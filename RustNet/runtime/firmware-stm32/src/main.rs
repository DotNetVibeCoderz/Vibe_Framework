//! STM32F4 firmware for RustNet: runs an embedded C# application on-chip.
//!
//! The IL interpreter (`rustnet-core`) is `no_std + alloc`, so it links here
//! directly. What does *not* link is `runtime/firmware` — the RNDP server,
//! filesystem, OTA and secure boot are still `std`-bound. So the application
//! is compiled to an RNX module on the PC and embedded in this image with
//! `include_bytes!` rather than flashed over the wire. Changing the app means
//! rebuilding and reflashing the firmware.
//!
//! The firmware supplies the interpreter's whole `RuntimeHost` surface — four
//! methods — backed by `rustnet-hal-stm32`: console to the UART, the clock and
//! sleeps to the DWT delay, and `RustNet.Hal.Gpio::*` to the board's pins.
//!
//! Verified on a Nucleo-F401RE (over SWD) and a Netduino 3 WiFi (over DFU).
//!
//! ```text
//! # Nucleo-F401RE (default)
//! cargo build --release
//!
//! # Netduino 3 WiFi
//! cargo build --release --no-default-features --features board-netduino3-wifi
//! ```

#![no_std]
#![no_main]

extern crate alloc;

use alloc::format;
use alloc::string::String;
use alloc::vec::Vec;
use core::fmt::Write as _;
use core::mem::MaybeUninit;
use core::sync::atomic::{AtomicU32, Ordering};

use cortex_m_rt::{entry, exception, ExceptionFrame};
use embedded_alloc::LlffHeap;
use rustnet_core::{HostValue, Interpreter, Module, RunExit, RuntimeHost};
use rustnet_hal::gpio::{Level, PinMode};
use rustnet_hal::uart::UartConfig;
use rustnet_hal::{Board as _, HalError};
use rustnet_hal_stm32::{Clocks, Stm32F4Board};
use stm32f4xx_hal::{pac, prelude::*};

#[cfg(all(feature = "board-nucleo-f401re", feature = "board-netduino3-wifi"))]
compile_error!("select exactly one board feature");

// The application, compiled from C# by the MetadataProcessor. Rebuild with:
//   dotnet build demo/<App>/<App>.csproj -c Debug
//   rustnet build demo/<App>/bin/Debug/net10.0/<App>.dll -o demo/<App>.rnx

#[cfg(feature = "app-blink")]
pub(crate) static APP_RNX: &[u8] = include_bytes!("../demo/Blink.rnx");
#[cfg(feature = "app-blink")]
pub(crate) const APP_NAME: &str = "blink";

#[cfg(feature = "app-language-tour")]
pub(crate) static APP_RNX: &[u8] = include_bytes!("../demo/LanguageTour.rnx");
#[cfg(feature = "app-language-tour")]
pub(crate) const APP_NAME: &str = "languagetour";

#[cfg(all(feature = "app-blink", feature = "app-language-tour"))]
compile_error!("select exactly one app feature");

#[cfg(not(any(feature = "app-blink", feature = "app-language-tour")))]
compile_error!("select an app feature (app-blink or app-language-tour)");

// Measured with `cargo run -p rustnet-core --example heap_probe`: the tour
// peaks at 49 KB, above what the F401RE build reserves. Catching that here
// costs nothing; catching it on hardware costs a flash cycle to diagnose.
#[cfg(all(feature = "app-language-tour", feature = "board-nucleo-f401re"))]
compile_error!(
    "app-language-tour needs ~49 KB of heap; the F401RE build reserves 48 KB \
     of its 96 KB RAM. Use board-netduino3-wifi, or raise HEAP_SIZE and \
     re-measure with the heap_probe example."
);

// ---------------------------------------------------------------------------
// Board specifics
// ---------------------------------------------------------------------------

#[cfg(feature = "board-nucleo-f401re")]
mod board {
    use super::*;

    pub const NAME: &str = "Nucleo-F401RE";
    /// LD2 = PA5 -> port 0, index 5.
    pub const LED: u32 = 5;
    /// HAL uart index 1 = USART2 (PA2 TX / PA3 RX) — wired to the on-board
    /// ST-LINK's virtual COM port, so RNDP needs no extra adapter here.
    pub const CONSOLE: u8 = 1;
    pub const CONSOLE_BASE: usize = 0x4000_4400;
    pub const CONSOLE_IRQ: pac::Interrupt = pac::Interrupt::USART2;
    /// Frame ceiling for `rustnet flash`. Modest, because a verify already
    /// wants ~17 KB of the same heap the running application is using.
    pub const MAX_RNDP_FRAME: usize = 12 * 1024;
    /// 64 KB out of 96 KB, leaving ~30 KB of stack. Sized by measurement, not
    /// taste: a running application wants ~21 KB, an inbound container ~6.5 KB
    /// twice over, and an RSA verify peaks at 17 KB on top — about 51 KB, which
    /// the previous 48 KB could not hold. `rustnet-core`'s heap_probe example
    /// gives the first number; the scratch verify probe gave the last.
    pub const HEAP_SIZE: usize = 64 * 1024;

    /// HSI, not HSE: this board's 8 MHz HSE is fed from the on-board ST-LINK's
    /// MCO, which stops when CN2 is removed for an external probe — asking for
    /// HSE there hangs in `freeze()`.
    pub fn clocks(rcc: pac::RCC) -> stm32f4xx_hal::rcc::Clocks {
        rcc.constrain().cfgr.sysclk(84.MHz()).freeze()
    }
}

#[cfg(feature = "board-netduino3-wifi")]
mod board {
    use super::*;

    pub const NAME: &str = "Netduino 3 WiFi";
    /// USR_LED = PA10 -> port 0, index 10 (nanoFramework's `LINE_LED1`).
    pub const LED: u32 = 10;
    /// HAL uart index 1 = USART2 (PA2 TX / PA3 RX), which this board brings
    /// out on the ordinary digital header as **D3 and D2**. UART7 would also
    /// work electrically, but only on the goPort2 GoBus connector, which needs
    /// a special cable and has its own power and TX-pull-up enables
    /// (PD10, PE10) to assert first. D2/D3 take jumper wires.
    ///
    /// This board has no virtual COM port of its own — nanoFramework reached
    /// it over native USB CDC instead — so a USB-serial adapter on D2/D3/GND
    /// is what RNDP needs here.
    pub const CONSOLE: u8 = 1;
    pub const CONSOLE_BASE: usize = 0x4000_4400;
    pub const CONSOLE_IRQ: pac::Interrupt = pac::Interrupt::USART2;
    /// microSD chip select, PB0 (`MICROSD_CS`) -> port 1, index 0. The card's
    /// data lines are SPI3 on PC10/11/12, and PB11 is its insert switch.
    pub const SD_CS: u32 = 16;
    /// The slot's enable line, PB1 (`MICROSD_CTRL`). nanoFramework drives it
    /// high at board init; left floating, the card never answers.
    pub const SD_CTRL: u32 = 17;
    /// The slot's insert switch, PB11 (`MICROSD_INS`).
    pub const SD_DETECT: u32 = 27;
    /// Frame ceiling for `rustnet flash`; roomier, matching the bigger heap.
    pub const MAX_RNDP_FRAME: usize = 48 * 1024;
    /// 96 KB out of 192 KB contiguous SRAM, leaving the rest for stack.
    pub const HEAP_SIZE: usize = 96 * 1024;

    /// Real 25 MHz crystal on PH0/PH1 — no ST-LINK MCO involved here.
    pub fn clocks(rcc: pac::RCC) -> stm32f4xx_hal::rcc::Clocks {
        // One PLL, both clocks: VCO 336 MHz, /2 for the core and /7 for USB.
        rcc.constrain()
            .cfgr
            .use_hse(25.MHz())
            .sysclk(168.MHz())
            .require_pll48clk()
            .freeze()
    }
}

mod rndp;
#[cfg(feature = "board-netduino3-wifi")]
mod sdcard;
mod storage;
#[cfg(feature = "board-netduino3-wifi")]
mod usb;

// ---------------------------------------------------------------------------
// Persistent storage
// ---------------------------------------------------------------------------
//
// Sector 7 of the internal flash, which `memory.x` deliberately leaves out of
// the FLASH region so the linker can never place code here. Both supported
// boards use the same address: the F401RE has nothing beyond it, and the
// F427's remaining 1.6 MB is simply unused for now.

pub(crate) const STORAGE_BASE: u32 = 0x0806_0000;
pub(crate) const STORAGE_LEN: u32 = 128 * 1024;
pub(crate) const STORAGE_SECTOR: u32 = 7;

// The image must end before storage begins, or erasing storage would erase
// code. `memory.x` already enforces this by not describing the sector, but
// stating the intent here keeps the two from drifting apart silently.
const _: () = assert!(STORAGE_BASE == 0x0800_0000 + 384 * 1024);

#[global_allocator]
static HEAP: LlffHeap = LlffHeap::empty();

pub(crate) fn heap_used() -> usize {
    HEAP.used()
}

// Direct access to the console USART's registers, for the two things the HAL
// deliberately does not expose: enabling the receive interrupt, and reading
// the data register from inside the ISR.

pub(crate) fn usart_read(offset: usize) -> u32 {
    // SAFETY: fixed peripheral address for the console port on this board.
    unsafe { core::ptr::read_volatile((board::CONSOLE_BASE + offset) as *const u32) }
}

pub(crate) fn usart_modify(offset: usize, set: u32) {
    let addr = (board::CONSOLE_BASE + offset) as *mut u32;
    // SAFETY: see `usart_read`. Single-threaded; the ISR only touches SR/DR.
    unsafe { addr.write_volatile(addr.read_volatile() | set) }
}

// ---------------------------------------------------------------------------
// Signalling with the LED
// ---------------------------------------------------------------------------
//
// This board has no debug probe attached, so the LED is the only channel that
// works everywhere — including a panic handler, where no `Board` value is in
// scope. These helpers therefore poke the registers directly instead of going
// through the HAL. Both supported boards put their user LED on port A.

const _: () = assert!(board::LED < 16, "the raw signalling helpers assume port A");

const RCC_AHB1ENR: *mut u32 = 0x4002_3830 as *mut u32;
const GPIOA_MODER: *mut u32 = 0x4002_0000 as *mut u32;
const GPIOA_BSRR: *mut u32 = 0x4002_0018 as *mut u32;

fn led_init() {
    // SAFETY: fixed peripheral addresses; single-threaded, runs on the chip.
    unsafe {
        RCC_AHB1ENR.write_volatile(RCC_AHB1ENR.read_volatile() | 1);
        let shift = board::LED * 2;
        let moder = GPIOA_MODER.read_volatile() & !(0b11 << shift);
        GPIOA_MODER.write_volatile(moder | (0b01 << shift));
    }
}

fn led(on: bool) {
    let bit = if on { 1 << board::LED } else { 1 << (board::LED + 16) };
    // SAFETY: BSRR is write-only and atomic.
    unsafe { GPIOA_BSRR.write_volatile(bit) }
}

/// The core clock the spin delay below assumes. Starts at the 16 MHz HSI the
/// chip resets to, and is corrected once the PLL is up — without this, every
/// signal after the clock change runs 10x fast and blinks too briefly to see.
static CPU_HZ: AtomicU32 = AtomicU32::new(16_000_000);

/// Counts calls from the application into the host, so the first one can be
/// signalled without the rest flooding the LED.
static HOST_CALLS: AtomicU32 = AtomicU32::new(0);

/// Crude spin delay — deliberately not the DWT, so it still works before the
/// cycle counter is started and inside fault handlers. Roughly four cycles per
/// iteration; precision does not matter, visibility does.
fn spin_ms(ms: u32) {
    let per_ms = CPU_HZ.load(Ordering::Relaxed) / 1000 / 4;
    for _ in 0..per_ms.saturating_mul(ms) {
        core::hint::spin_loop();
    }
}

/// Flash `n` times, then pause. Used to mark how far boot got: execution
/// stopping after `n` blinks localises the fault to the step after `n`.
/// The gap between groups is far longer than the gap within one, so the
/// groups stay countable by eye.
fn signal(n: u32) {
    for _ in 0..n {
        led(true);
        spin_ms(120);
        led(false);
        spin_ms(200);
    }
    spin_ms(900);
}

/// Repeat a blink group forever. Distinct counts distinguish failure kinds.
fn signal_forever(n: u32) -> ! {
    loop {
        signal(n);
    }
}

/// Rust panic — including an allocation failure, which panics by default.
#[panic_handler]
fn on_panic(_: &core::panic::PanicInfo) -> ! {
    led_init();
    signal_forever(5)
}

/// Bad memory access or, most likely here, a stack overflow.
#[exception]
unsafe fn HardFault(_: &ExceptionFrame) -> ! {
    led_init();
    signal_forever(8)
}

// ---------------------------------------------------------------------------
// The interpreter's view of this board
// ---------------------------------------------------------------------------

/// Interpreter instructions per service-loop turn. Small on purpose — see the
/// note where it is used.
const FUEL_SLICE: u64 = 2_000;

/// How many console lines to keep for `rustnet logs`.
const LOG_LINES: usize = 64;

pub(crate) struct FirmwareHost {
    pub(crate) board: Stm32F4Board,
    /// Completed console lines, oldest first, capped at [`LOG_LINES`].
    logs: Vec<String>,
    /// The line being assembled; the interpreter writes in fragments.
    partial: String,
    /// The board's own USB, when it has one wired to the MCU. Present on the
    /// Netduino, absent on the Nucleo, whose USB socket only reaches its
    /// ST-LINK.
    #[cfg(feature = "board-netduino3-wifi")]
    usb: Option<usb::UsbConsole>,
    /// The RNDP service lives here so `sleep_ms` can keep serving it. Fuel is
    /// counted in instructions, but an application spends its time asleep — a
    /// slice of 250k instructions can take minutes of wall clock, and nothing
    /// would answer the tools for all of it.
    rndp: rndp::Rndp,
}

impl FirmwareHost {
    pub(crate) fn new(board: Stm32F4Board) -> Self {
        Self {
            board,
            logs: Vec::new(),
            partial: String::new(),
            #[cfg(feature = "board-netduino3-wifi")]
            usb: None,
            rndp: rndp::Rndp::new(),
        }
    }

    /// Take over the console with the board's USB, once it is enumerating.
    #[cfg(feature = "board-netduino3-wifi")]
    pub(crate) fn attach_usb(&mut self, console: usb::UsbConsole) {
        self.usb = Some(console);
    }

    /// Which transport is actually carrying RNDP, for `info`.
    pub(crate) fn transport_name(&self) -> &'static str {
        #[cfg(feature = "board-netduino3-wifi")]
        if self.usb.is_some() {
            return "usb-cdc";
        }
        "uart"
    }

    /// Read from whichever transport is serving RNDP. Never blocks.
    pub(crate) fn console_read(&mut self, buf: &mut [u8]) -> usize {
        #[cfg(feature = "board-netduino3-wifi")]
        if let Some(usb) = &mut self.usb {
            return usb.read(buf);
        }
        rndp::rx_take(buf)
    }

    /// Keep the transport's own housekeeping running. USB answers the host's
    /// control transfers from here; the UART needs nothing.
    pub(crate) fn console_service(&mut self) {
        #[cfg(feature = "board-netduino3-wifi")]
        if let Some(usb) = &mut self.usb {
            usb.service();
        }
    }

    fn out(&mut self, bytes: &[u8]) {
        #[cfg(feature = "board-netduino3-wifi")]
        if let Some(usb) = &mut self.usb {
            usb.write(bytes);
            return;
        }
        if let Ok(uart) = self.board.uart(board::CONSOLE) {
            let _ = uart.write(bytes);
        }
    }

    /// Answer any pending RNDP traffic. Takes the service out of `self` for
    /// the call so it can borrow the rest of the host; the move is a couple of
    /// pointers, not an allocation.
    pub(crate) fn poll_rndp(&mut self) {
        self.console_service();
        let mut service = core::mem::take(&mut self.rndp);
        service.poll(self);
        self.rndp = service;
    }

    /// Load the provisioned key and any stored application out of flash.
    /// Returns the application, if one was kept.
    pub(crate) fn restore_persisted(&mut self) -> Option<Vec<u8>> {
        if let Some(key) = storage::load(&mut self.board, storage::KIND_PUB_KEY) {
            let _ = writeln!(self, "[storage] restored provisioning key");
            self.rndp.set_pub_key(key);
        }
        if let Some(name) = storage::load(&mut self.board, storage::KIND_APP_NAME) {
            if let Ok(text) = core::str::from_utf8(&name) {
                self.rndp.set_app_name(text);
            }
        }
        let app = storage::load(&mut self.board, storage::KIND_APP);
        if let Some(bytes) = &app {
            // The size has to come across too, or `apps list` keeps reporting
            // the compiled-in application's length for the restored one.
            self.rndp.set_app_size(bytes.len());
            let _ = writeln!(self, "[storage] restored app, {} bytes", bytes.len());
        }
        app
    }

    /// Persist a value, reporting failure to the log rather than to the tool:
    /// losing persistence is worth knowing about but should not fail the
    /// command that already succeeded in RAM.
    pub(crate) fn persist(&mut self, kind: u32, data: &[u8]) {
        if let Err(e) = storage::store(&mut self.board, kind, data) {
            let _ = writeln!(self, "[storage] could not persist: {e}");
        }
    }

    pub(crate) fn storage_used(&mut self) -> u32 {
        storage::used(&mut self.board)
    }

    pub(crate) fn app_running(&self) -> bool {
        self.rndp.app_running
    }

    pub(crate) fn stop_app(&mut self) {
        self.rndp.app_running = false;
    }

    pub(crate) fn has_pending_app(&self) -> bool {
        self.rndp.has_pending_app()
    }

    /// Take an application delivered by `rustnet flash`, and let it run.
    pub(crate) fn take_pending_app(&mut self) -> Option<Vec<u8>> {
        let next = self.rndp.take_pending_app();
        if next.is_some() {
            self.rndp.app_running = true;
        }
        next
    }

    /// Console output. Goes to the UART live *and* into the log ring: the
    /// tools skip non-frame bytes on the wire, so both can share the line.
    fn print(&mut self, text: &str) {
        self.write_raw_crlf(text);

        for ch in text.chars() {
            if ch == '\n' {
                if self.logs.len() == LOG_LINES {
                    self.logs.remove(0);
                }
                let line = core::mem::take(&mut self.partial);
                self.logs.push(line);
            } else if ch != '\r' {
                self.partial.push(ch);
            }
        }
    }

    /// Translate bare LF into CRLF so a serial terminal renders lines rather
    /// than a staircase.
    fn write_raw_crlf(&mut self, text: &str) {
        let Ok(uart) = self.board.uart(board::CONSOLE) else { return };
        for piece in text.split_inclusive('\n') {
            match piece.strip_suffix('\n') {
                Some(body) => {
                    let _ = uart.write(body.as_bytes());
                    let _ = uart.write(b"\r\n");
                }
                None => {
                    let _ = uart.write(piece.as_bytes());
                }
            }
        }
    }

    /// Write bytes verbatim — RNDP frames must not be line-ending mangled.
    pub(crate) fn write_raw(&mut self, bytes: &[u8]) {
        self.out(bytes);
        if let Ok(uart) = self.board.uart(board::CONSOLE) {
            let _ = uart.flush();
        }
    }

    /// Core clock, for turning cycle counts into microseconds.
    pub(crate) fn cpu_hz(&mut self) -> u32 {
        self.board.power().cpu_frequency_hz()
    }

    pub(crate) fn uptime_ms(&mut self) -> u64 {
        self.board.delay().now_us() / 1000
    }

    /// The last `max` console lines, newest last, newline separated.
    pub(crate) fn log_tail(&self, max: usize) -> String {
        let start = self.logs.len().saturating_sub(max);
        let mut out = String::new();
        for line in &self.logs[start..] {
            out.push_str(line);
            out.push('\n');
        }
        out
    }
}

impl core::fmt::Write for FirmwareHost {
    fn write_str(&mut self, s: &str) -> core::fmt::Result {
        self.print(s);
        Ok(())
    }
}

fn hal_err(e: HalError) -> String {
    format!("{e}")
}

impl RuntimeHost for FirmwareHost {
    fn console_write(&mut self, text: &str) {
        self.print(text);
    }

    fn now_ms(&mut self) -> u64 {
        self.board.delay().now_us() / 1000
    }

    fn sleep_ms(&mut self, ms: u64) {
        // Serve RNDP while the application sleeps. An app that blinks spends
        // nearly all its wall-clock time here while burning almost no fuel, so
        // waiting it out in one blocking delay leaves the tools unanswered for
        // as long as the sleep lasts. Receive is interrupt-driven, so nothing
        // is lost meanwhile — but nothing is *answered* either.
        // Short steps: this interval is the consumer latency the receive ring
        // has to cover, and a multi-kilobyte upload arrives as one burst.
        const STEP_MS: u64 = 2;
        let mut left = ms;
        while left > 0 {
            let step = left.min(STEP_MS);
            self.board.delay().delay_ms(step);
            left -= step;
            self.poll_rndp();
        }
    }

    fn invoke(&mut self, name: &str, args: Vec<HostValue>) -> Result<HostValue, String> {
        // Diagnostic: flash 9 once, on the very first call the application
        // makes into the host. Seeing it means the interpreter really is
        // executing managed code; not seeing it means it never got that far.
        if HOST_CALLS.fetch_add(1, Ordering::Relaxed) == 0 {
            signal(9);
        }

        // Widen exactly as `runtime/firmware/src/apphost.rs` does. Numeric
        // arguments arrive in whatever form the evaluation stack held, and a
        // C# `bool` arrives as an integer, not `HostValue::Bool` — demanding
        // the narrow type here fails every call that takes one.
        let int = |n: usize| match args.get(n) {
            Some(HostValue::I32(v)) => Ok(*v),
            Some(HostValue::I64(v)) => Ok(*v as i32),
            Some(HostValue::F64(v)) => Ok(*v as i32),
            Some(HostValue::Bool(v)) => Ok(i32::from(*v)),
            other => Err(format!("{name}: argument {n} is not an int: {other:?}")),
        };
        let flag = |n: usize| int(n).map(|v| v != 0);

        match name {
            // The board tells the app where its LED is, so one compiled
            // module runs on every board rather than one per LED position.
            // Matched by shape, not by full name: the namespace belongs to
            // whichever application is embedded.
            n if n.ends_with("Board::UserLed()") => Ok(HostValue::I32(board::LED as i32)),

            "RustNet.Hal.Gpio::SetMode(i4,i4)" => {
                let pin = int(0)? as u32;
                let mode = match int(1)? {
                    0 => PinMode::Input,
                    1 => PinMode::InputPullUp,
                    2 => PinMode::InputPullDown,
                    3 => PinMode::Output,
                    _ => PinMode::OutputOpenDrain,
                };
                self.board.gpio(pin).and_then(|p| p.set_mode(mode)).map_err(hal_err)?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Gpio::Write(i4,bool)" => {
                let pin = int(0)? as u32;
                let level = if flag(1)? { Level::High } else { Level::Low };
                self.board.gpio(pin).and_then(|p| p.write(level)).map_err(hal_err)?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Gpio::Read(i4)" => {
                let pin = int(0)? as u32;
                let level = self.board.gpio(pin).and_then(|p| p.read()).map_err(hal_err)?;
                Ok(HostValue::Bool(level == Level::High))
            }
            "RustNet.Hal.Gpio::Toggle(i4)" => {
                let pin = int(0)? as u32;
                self.board.gpio(pin).and_then(|p| p.toggle()).map_err(hal_err)?;
                Ok(HostValue::Void)
            }

            other => Err(format!("unsupported internal call: {other}")),
        }
    }
}

// ---------------------------------------------------------------------------

#[entry]
fn main() -> ! {
    // Boot progress is blinked out as 1, 2, 3, 4 then 6 flashes with pauses
    // between. Whichever count the sequence stops at names the step that hung
    // — the only debugging channel available with no probe on this board.
    led_init();
    signal(1); // reached the entry point

    {
        static mut HEAP_MEM: [MaybeUninit<u8>; board::HEAP_SIZE] =
            [MaybeUninit::uninit(); board::HEAP_SIZE];
        // SAFETY: runs once, before anything can allocate.
        unsafe { HEAP.init(core::ptr::addr_of_mut!(HEAP_MEM) as usize, board::HEAP_SIZE) }
    }

    let dp = pac::Peripherals::take().unwrap();

    // The only job stm32f4xx-hal has here: bring up the PLL and report what it
    // actually achieved. Those numbers feed the baud divisor and the delay
    // scaling, so read them back rather than assuming the requested values.
    let clocks = board::clocks(dp.RCC);
    // Correct the spin-delay reference before the next signal, or everything
    // from here on blinks 10x too fast to see.
    CPU_HZ.store(clocks.sysclk().raw(), Ordering::Relaxed);
    signal(2); // PLL locked — a missing HSE hangs above this line

    let mut hw = Stm32F4Board::new(Clocks {
        sysclk_hz: clocks.sysclk().raw(),
        pclk1_hz: clocks.pclk1().raw(),
        pclk2_hz: clocks.pclk2().raw(),
    });
    hw.init();
    hw.attach_storage(storage::region());
    hw.uart(board::CONSOLE)
        .and_then(|u| u.configure(UartConfig::default()))
        .expect("console configure");
    signal(3); // board and console configured

    let mut host = FirmwareHost::new(hw);

    // The Netduino's USB socket reaches the MCU, so RNDP can ride it and no
    // adapter is needed on the header. The UART stays configured either way,
    // as a fallback that needs no enumeration to work.
    #[cfg(feature = "board-netduino3-wifi")]
    {
        let gpioa = dp.GPIOA.split();
        let peripheral = stm32f4xx_hal::otg_fs::USB::new(
            (dp.OTG_FS_GLOBAL, dp.OTG_FS_DEVICE, dp.OTG_FS_PWRCLK),
            (gpioa.pa11, gpioa.pa12),
            &clocks,
        );
        host.attach_usb(usb::UsbConsole::new(peripheral));
    }
    let _ = writeln!(
        host,
        "\nRustNet on {} @ {} MHz, heap {} KB",
        board::NAME,
        clocks.sysclk().raw() / 1_000_000,
        board::HEAP_SIZE / 1024
    );
    signal(4); // banner sent — UART transmit completes, so it is not stuck on TXE

    rndp::start_receiving();
    signal(6); // console up and RNDP listening; the application runs next

    // The application starts as the one compiled in, and is replaced whenever
    // `rustnet flash` lands a new one. Swapping means rebuilding the
    // interpreter, because it borrows the module for its lifetime — which is
    // why the run lives in a function: the borrow of `app` has to end before
    // `app` can be reassigned.
    // Anything a previous session provisioned or uploaded lives in flash, so
    // pick that up before falling back to the application built into the
    // image. This is what makes a power cycle not undo your work.
    #[cfg(feature = "board-netduino3-wifi")]
    probe_sd(&mut host);

    let restored = host.restore_persisted();
    let mut app: Vec<u8> = restored.unwrap_or_else(|| APP_RNX.to_vec());
    loop {
        let (returned, next) = run_app(&app, host);
        host = returned;
        if let Some(replacement) = next {
            let _ = writeln!(host, "[flash] switching to the newly flashed app");
            app = replacement;
        }
    }
}

/// Bring up the microSD card and say what came back. Reading block 0 and
/// finding its signature proves the whole chain — wiring, SPI timing, the
/// card's own state machine — rather than just that init did not error.
/// Bring up the microSD card and report what came back.
///
/// The slot's enable line is active low, which `board.h` does not say —
/// nanoFramework leaves it high and never drives a card over SPI on this
/// target, so its idle level is not evidence of the active one. Both are
/// tried rather than assumed.
///
/// Reading the CSD separates a failing card from a failing driver without the
/// card leaving the slot: the CSD comes from the controller, a block comes
/// from the flash array, and both arrive through the same data-token path.
#[cfg(feature = "board-netduino3-wifi")]
fn probe_sd(host: &mut FirmwareHost) {
    for level in [Level::Low, Level::High] {
        let Ok(mut card) =
            sdcard::SdCard::init(&mut host.board, board::SD_CS, Some((board::SD_CTRL, level)))
        else {
            continue;
        };

        let mut csd = [0u8; 16];
        let identity = match card.read_csd(&mut host.board, &mut csd) {
            Ok(()) => match sdcard::capacity_mb(&csd) {
                Some(mb) => format!("{mb} MB"),
                None => String::from("unknown size"),
            },
            Err(e) => format!("CSD unreadable: {e}"),
        };

        let mut block = alloc::vec![0u8; sdcard::BLOCK_LEN];
        match card.read_block(&mut host.board, 0, &mut block) {
            Ok(()) => {
                let signature = u16::from_le_bytes([block[510], block[511]]);
                let _ = writeln!(host, "[sd] {identity}, block 0 read ({signature:#06x})");
            }
            Err(e) => {
                // A card that identifies but will not hand over a block has a
                // working controller in front of unreadable storage. Say that,
                // rather than reporting it as an absent card.
                let status = card
                    .status(&mut host.board)
                    .map(|r2| format!("{r2:02x?}"))
                    .unwrap_or_else(|_| String::from("unavailable"));
                let _ = writeln!(
                    host,
                    "[sd] {identity} identifies but blocks are unreadable ({e}, status {status})                      — controller healthy, storage is not"
                );
            }
        }
        return;
    }
    let _ = writeln!(host, "[sd] no card");
}

/// Run one application until it ends or a new one is flashed. Returns the host
/// and, if `rustnet flash` delivered one, the module to run next.
fn run_app(app: &[u8], host: FirmwareHost) -> (FirmwareHost, Option<Vec<u8>>) {
    let mut host = host;

    let module = match Module::from_bytes(app) {
        Ok(module) => module,
        Err(_) => {
            // Only reachable for the compiled-in app: an uploaded one is
            // parsed before it is accepted.
            let _ = writeln!(host, "[fatal] module is not a valid RNX");
            signal_forever(7);
        }
    };
    let _ = writeln!(
        host,
        "app: {} methods, {} types, {} strings",
        module.methods.len(),
        module.types.len(),
        module.strings.len()
    );

    let mut interp = Interpreter::new(&module, host);

    // The bare-metal service loop: answer the tools, then hand the application
    // a slice of fuel. Cooperative and single-threaded — no RTOS, no executor.
    // Receive is interrupt-driven, so a long slice costs response latency but
    // never a dropped byte.
    loop {
        interp.host.poll_rndp();

        if interp.host.has_pending_app() {
            break;
        }
        if !interp.host.app_running() {
            continue;
        }

        // Measured, not chosen: with 250k the worst gap between two polls was
        // 3.8 s, because the interpreter burns fuel waiting out an
        // application's sleep rather than always handing it to `sleep_ms`. The
        // receive ring only covers ~355 ms at 115200, so a slice that long
        // overruns it many times over and corrupts whatever is arriving.
        // `info` reports `max_poll_gap_us`; re-measure if this changes.
        match interp.run(FUEL_SLICE) {
            RunExit::OutOfFuel => {}
            // An application that ends, faults or pauses used to halt the
            // firmware. Now that the tools can reach us, stay reachable and
            // report instead — otherwise a bad app takes the device with it.
            RunExit::Completed => {
                let n = interp.instructions;
                let _ = writeln!(interp.host, "[exit] completed after {n} instructions");
                interp.host.stop_app();
            }
            RunExit::Paused { method, il_offset } => {
                let _ = writeln!(interp.host, "[exit] paused at method {method} il {il_offset}");
                interp.host.stop_app();
            }
            RunExit::Error(msg) => {
                let _ = writeln!(interp.host, "[exit] error: {msg}");
                interp.host.stop_app();
            }
        }
    }

    let mut host = interp.host;
    let next = host.take_pending_app();
    (host, next)
}
