//! Kendryte K210 firmware for RustNet: runs a C# application on bare-metal
//! RISC-V and serves RNDP over UARTHS.
//!
//! The IL interpreter (`rustnet-core`) is `no_std + alloc`, so it links here
//! directly. What does *not* link is `runtime/firmware` — the filesystem, OTA
//! and on-device debugger are still `std`-bound — so an application is either
//! compiled into the image with `include_bytes!` or delivered over the wire by
//! `rustnet flash`, which this firmware verifies, parses and keeps in the
//! board's SPI flash.
//!
//! **Not yet run on hardware.** Everything here is written against the K210
//! datasheet and Kendryte's own SDK, and it builds; nothing has been executed.
//! `README.md` lists what to check first, and why each item is where the risk
//! actually sits.
//!
//! ```text
//! cargo build --release                     # Maix Go, blink demo
//! cargo build --release --no-default-features \
//!     --features board-maix-go,app-language-tour,rx-interrupt
//! ```
//!
//! ## Two things that make this port different
//!
//! **The FPU has to be switched on.** `mstatus.FS` comes out of reset as `Off`
//! on this core, and every floating-point instruction then traps as an illegal
//! instruction. `rustnet-core` does `f64` arithmetic, so without the `csrs
//! mstatus` in [`enable_fpu_and_accelerator`] the interpreter dies on the first
//! `double` a program touches — which presents as a hard fault in the middle of
//! working code rather than as anything to do with the FPU. Kendryte's `crt.S`
//! does the same thing on the same line of reasoning; riscv-rt does not do it
//! for you.
//!
//! **There is memory to spare.** 6 MB of SRAM against the F401RE's 96 KB, so
//! the heap here is 4 MB and `rustnet flash` accepts half-megabyte containers.
//! The STM32 port's constant negotiation over kilobytes simply does not apply.

#![no_std]
#![no_main]

extern crate alloc;

use alloc::format;
use alloc::string::String;
use alloc::vec::Vec;
use core::fmt::Write as _;
use core::mem::MaybeUninit;
use core::sync::atomic::{AtomicU32, Ordering};

use embedded_alloc::LlffHeap;
use riscv_rt::{entry, pre_init};
use rustnet_core::{HostValue, Interpreter, Module, RunExit, RuntimeHost};
use rustnet_hal::gpio::{Level, PinMode};
use rustnet_hal::uart::UartConfig;
use rustnet_hal::{Board as _, HalError};
use rustnet_gfx::{Color, Framebuffer};
use rustnet_hal::extmem::ExtMemory;
use rustnet_hal_k210::{camera, fpioa, lcd, reg, Clocks, K210Board, SpiFlash};
use rustnet_flashfs as flashfs;

mod espat;
mod mqtt;
mod imaging;

// The application, compiled from C# by the MetadataProcessor.
//
// Deliberately the *same files* the STM32 port carries rather than copies of
// them: two ports running the same demo is the point of having a demo, and a
// duplicated binary is a duplicate that can drift. Rebuild with:
//   dotnet build ../firmware-stm32/demo/<App>/<App>.csproj -c Debug
//   rustnet build .../<App>.dll -o ../firmware-stm32/demo/<App>.rnx

#[cfg(feature = "app-blink")]
pub(crate) static APP_RNX: &[u8] = include_bytes!("../../firmware-stm32/demo/Blink.rnx");
#[cfg(feature = "app-blink")]
pub(crate) const APP_NAME: &str = "blink";

#[cfg(feature = "app-language-tour")]
pub(crate) static APP_RNX: &[u8] = include_bytes!("../../firmware-stm32/demo/LanguageTour.rnx");
#[cfg(feature = "app-language-tour")]
pub(crate) const APP_NAME: &str = "languagetour";

#[cfg(all(feature = "app-blink", feature = "app-language-tour"))]
compile_error!("select exactly one app feature");

#[cfg(not(any(feature = "app-blink", feature = "app-language-tour")))]
compile_error!("select an app feature (app-blink or app-language-tour)");

#[cfg(not(feature = "board-maix-go"))]
compile_error!("select a board feature (board-maix-go)");

// ---------------------------------------------------------------------------
// Board specifics
// ---------------------------------------------------------------------------

#[cfg(feature = "board-maix-go")]
mod board {
    pub const NAME: &str = "Sipeed Maix Go";

    // FPIOA pads. Mostly from Sipeed's own `config_maix_go.py` — but the LED
    // colours are **not**, because that file is wrong about them for this
    // board.
    //
    // It lists green on IO12 and blue on IO13. Lighting each pad on its own and
    // looking at the board gives red, **blue**, green for IO14, IO12, IO13 — so
    // the pads are the other way round, which is what several third-party
    // pinouts claimed and this port originally dismissed. Confirmed by eye on
    // 2026-07-31; a vendor's own config file being wrong is worth remembering
    // the next time one is treated as authoritative.
    pub const LED_R: u8 = 14;
    pub const LED_G: u8 = 13;
    pub const LED_B: u8 = 12;
    /// The on-board ESP8285's UART, **named from the K210's side**.
    ///
    /// The datasheet labels IO6 `WIFI_TX` and IO7 `WIFI_RX`, which are the
    /// *module's* directions; the board schematic spells the other half out —
    /// IO6 is also `MCU_RX` and IO7 also `MCU_TX`. Taking the datasheet's names
    /// at face value wires this UART backwards, which is silent: the port
    /// configures, transmits into the module's own transmit pin, and nothing
    /// ever answers.
    pub const WIFI_UART_TX: u8 = 7;
    pub const WIFI_UART_RX: u8 = 6;
    /// Active-high enable. The module is held in reset until this is driven.
    pub const WIFI_EN: u8 = 8;

    // The three buttons, all 10K pulled up and shorting to ground — so a press
    // reads **low**. IO16 doubles as the ROM's boot select, which is why it is
    // the one to avoid holding at reset.
    pub const BUTTON_UP: u8 = 17;
    pub const BUTTON_MIDDLE: u8 = 15;
    pub const BUTTON_DOWN: u8 = 16;

    /// UARTHS pads as `(tx, rx)`. The board's STM32F103 bridges USB to these,
    /// and the mask ROM's ISP already uses them — so this is where `rustnet
    /// --device serial:COMn` lands with no extra adapter.
    pub const CONSOLE_PINS: (u8, u8) = (5, 4);

    /// The RGB LED is common-anode: pulling a pad **low** lights it.
    ///
    /// If this is ever backwards the diagnostics still work — the LED would sit
    /// lit and blink dark, and a group of blinks stays just as countable
    /// inverted. So it is worth getting right, but it is not load-bearing.
    pub const LED_ACTIVE: super::Level = super::Level::Low;

    // GPIOHS channels the LEDs are pinned to. Bound explicitly rather than
    // allocated on demand, because the panic handler has to reach a pin with no
    // `Board` in scope: it knows a channel number, not a pad.
    pub const CH_LED_R: u8 = 0;
    pub const CH_LED_G: u8 = 1;
    pub const CH_LED_B: u8 = 2;

    /// Which LED the application gets from `Board::UserLed()`. Blue, so an app
    /// blinking away cannot be confused with the green boot progress or the red
    /// failure signal.
    pub const USER_LED: u8 = LED_B;

    /// 4 MB of the 6 MB SRAM. Generous on purpose: the interpreter, an inbound
    /// signed container and an RSA verify all want heap at the same moment, and
    /// on this chip there is no reason to make them compete. Leaves the image
    /// plus over a megabyte of stack.
    pub const HEAP_SIZE: usize = 4 * 1024 * 1024;

    /// Frame ceiling for `rustnet flash`.
    pub const MAX_RNDP_FRAME: usize = 512 * 1024;

    /// Where persistence lives in the board's 16 MB SPI NOR flash: the top
    /// 256 KB, far above the few hundred kilobytes the image occupies at offset
    /// 0. That distance *is* the safety argument — see `storage.rs`.
    pub const STORAGE_BASE: u32 = 0x00FC_0000;
    pub const STORAGE_LEN: u32 = 0x0004_0000;

    /// The filesystem's window: everything between the image and the record
    /// window. It starts at 1 MB rather than immediately after the image so a
    /// firmware that grows — the panel driver and graphics added a chunk — never
    /// creeps into stored files, and it stops where `STORAGE_BASE` begins.
    ///
    /// Two windows rather than one shared region because they fail differently:
    /// a filesystem that fills up compacts by erasing its whole window, and the
    /// provisioned key must not be inside the blast radius of an application
    /// writing log files.
    pub const FS_BASE: u32 = 0x0010_0000;
    pub const FS_LEN: u32 = STORAGE_BASE - FS_BASE;

    /// Camera geometry. Matched to the panel so a frame blits without
    /// scaling, and a width in whole bursts of eight because that is what the
    /// DVP counts a line in.
    pub const CAMERA_WIDTH: u16 = 320;
    pub const CAMERA_HEIGHT: u16 = 240;

    /// Panel geometry. The Maix panel is 320x240 in landscape.
    pub const PANEL_WIDTH: u32 = 320;
    pub const PANEL_HEIGHT: u32 = 240;
}

mod rndp;
mod storage;

#[global_allocator]
static HEAP: LlffHeap = LlffHeap::empty();

pub(crate) fn heap_used() -> usize {
    HEAP.used()
}

// ---------------------------------------------------------------------------
// The FPU
// ---------------------------------------------------------------------------

/// Turn on the floating-point unit (and the KPU's accelerator state) before
/// anything can execute an FP instruction.
///
/// `mstatus.FS` is `Off` at reset, and in that state every `f`/`d` instruction
/// — and every access to `fcsr` — raises an illegal-instruction exception. The
/// interpreter's numeric tower is built on `f64`, so this is not an
/// optimisation: without it, a C# program dies the moment it multiplies two
/// doubles, and the trap points at whatever ordinary-looking arithmetic happened
/// to be first.
///
/// `csrs` rather than a read-modify-write: `XS` is read-only on this core, so
/// setting bits is the only operation that is well defined for both fields.
#[pre_init]
unsafe fn enable_fpu_and_accelerator() {
    // FS = mstatus[14:13], XS = mstatus[16:15].
    core::arch::asm!("csrs mstatus, {}", in(reg) 0x0001_E000usize, options(nomem, nostack));
}

// ---------------------------------------------------------------------------
// Signalling with the LEDs
// ---------------------------------------------------------------------------
//
// A Maix Go has an on-board debug probe, but the LED path works in places the
// probe does not reach — a panic handler, a trap handler, and the window before
// the console is configured. These helpers therefore poke FPIOA and GPIOHS
// directly rather than going through the HAL, exactly as the STM32 port does,
// and for the same reason: no `Board` value is in scope where they are needed
// most.
//
// Having three LEDs is a genuine improvement over that port. Boot progress goes
// out on green and failures on red, so "it stopped somewhere" and "it failed"
// are different colours instead of different counts.

const GPIOHS_BASE: usize = 0x3800_1000;
const GPIOHS_OUTPUT_EN: usize = GPIOHS_BASE + 0x08;
const GPIOHS_OUTPUT_VAL: usize = GPIOHS_BASE + 0x0C;

const LED_MASK: u32 =
    (1 << board::CH_LED_R) | (1 << board::CH_LED_G) | (1 << board::CH_LED_B);

/// Route the three LED pads to their channels and drive them all dark.
///
/// Idempotent, and safe to call from a fault handler that has no idea whether
/// bring-up got this far — which is why it ungates FPIOA itself rather than
/// assuming `K210Board::init` has run. The ROM must already have that clock on,
/// since its own ISP talks over UARTHS and those pads reach the peripheral
/// through FPIOA like everything else; but the very first diagnostic blink is
/// the last place to depend on a chain of reasoning, and the insurance is one
/// register write.
fn led_init() {
    rustnet_hal_k210::sysctl::clock_enable(rustnet_hal_k210::sysctl::Peripheral::Fpioa);
    fpioa::set_function(board::LED_R, fpioa::gpiohs(board::CH_LED_R));
    fpioa::set_function(board::LED_G, fpioa::gpiohs(board::CH_LED_G));
    fpioa::set_function(board::LED_B, fpioa::gpiohs(board::CH_LED_B));
    reg::modify(GPIOHS_OUTPUT_EN, 0, LED_MASK);
    led_write(LED_MASK, false);
}

/// Light or extinguish a set of LED channels, honouring the board's polarity.
fn led_write(mask: u32, on: bool) {
    let drive_high = (board::LED_ACTIVE == Level::High) == on;
    if drive_high {
        reg::modify(GPIOHS_OUTPUT_VAL, 0, mask);
    } else {
        reg::modify(GPIOHS_OUTPUT_VAL, mask, 0);
    }
}

/// The core clock the spin delay below assumes.
///
/// Starts at the crystal — the slowest the core can possibly be running — and
/// is corrected once SYSCTL has been read. Erring slow means the first signals
/// blink *longer* than intended, which is still visible; erring fast would make
/// them too brief to see, which reads exactly like a hang.
static CPU_HZ: AtomicU32 = AtomicU32::new(rustnet_hal_k210::sysctl::IN0_HZ);

/// Counts calls from the application into the host, so the first one can be
/// signalled without the rest flooding the LED.
static HOST_CALLS: AtomicU32 = AtomicU32::new(0);

/// Crude spin delay — deliberately not the cycle counter, so it still works
/// inside a fault handler where reading a CSR is the least of the worries.
/// Roughly four cycles per iteration; precision does not matter, visibility
/// does.
fn spin_ms(ms: u32) {
    let per_ms = CPU_HZ.load(Ordering::Relaxed) / 1000 / 4;
    for _ in 0..per_ms.saturating_mul(ms) {
        core::hint::spin_loop();
    }
}

/// Flash `mask` `n` times, then pause. Used to mark how far boot got:
/// execution stopping after `n` blinks localises the fault to the step after
/// `n`. The gap between groups is far longer than the gap within one, so the
/// groups stay countable by eye.
fn signal_on(mask: u32, n: u32) {
    for _ in 0..n {
        led_write(mask, true);
        spin_ms(120);
        led_write(mask, false);
        spin_ms(200);
    }
    spin_ms(900);
}

/// Boot progress, on green.
fn signal(n: u32) {
    signal_on(1 << board::CH_LED_G, n);
}







/// Ask the on-board ESP8285 whether it speaks AT, and at what rate.
///
/// Everything about the WiFi path depends on this one answer, and none of it
/// needs a person watching a board: if the module has AT firmware it replies
/// `OK`, and `RustNet.Net.Wifi` can be built on AT commands; if it does not,
/// the radio needs reflashing first and no amount of driver work will help.
///
/// Rates are tried in order because AT builds differ — 115200 is the modern
/// default, 74880 is the ESP8285 ROM's own odd rate, and 9600 turns up on
/// older factory images.
#[allow(dead_code)]
fn probe_esp8285(host: &mut FirmwareHost) {
    // 115200 first — the rate AT firmware ships with, and the rate this module
    // uses once it has actually been reset.
    //
    // 74880 stays in the list, and it is worth knowing why it is not first.
    // This port swept for a rate, found the module answering at 74880, and
    // wrote that down as a finding about the board: "the ESP8285 ROM's own odd
    // rate rather than the 115200 most AT builds ship with". It was nothing of
    // the kind. 74880 is the ESP8266 *bootloader's* rate, and the module was
    // answering there because it had never been properly reset — the K210's
    // reset does not reach it, so a module left wedged by one session came
    // back wedged in the next, across as many reflashes as you like. Pulsing
    // the enable line above fixed the baud rate and the joins at the same
    // time. A rate that only appears on a stuck module is a symptom, not a
    // specification.
    const RATES: [u32; 3] = [115_200, 74_880, 9_600];

    // Power-cycle the module, do not merely enable it.
    //
    // Resetting the K210 does not reset the ESP8285 — they share a board and
    // nothing else. So a module left mid-command by the previous session comes
    // back answering `busy p...` to everything, across as many K210 resets as
    // you like, and the probe reads that as a module at the wrong baud rate.
    // This port chased that for a while: `AT+CWJAP` leaves the module in
    // exactly that state, so every boot after a failed join started from a
    // wedged radio. The enable line is the only way to clear it.
    if let Ok(pin) = host.board.gpio(board::WIFI_EN as u32) {
        let _ = pin.set_mode(PinMode::Output);
        let _ = pin.write(Level::Low);
    }
    host.board.delay().delay_ms(100);
    if let Ok(pin) = host.board.gpio(board::WIFI_EN as u32) {
        let _ = pin.write(Level::High);
    }
    // The module prints its own boot banner at this rate and takes most of a
    // second to be ready for AT.
    host.board.delay().delay_ms(1_000);

    for rate in RATES {
        let configured = host
            .board
            .uart(1)
            .and_then(|u| u.configure(UartConfig { baud: rate, ..UartConfig::default() }));
        if configured.is_err() {
            let _ = writeln!(host, "[wifi] UART1 will not configure at {rate}");
            continue;
        }

        // Flush first. A previous attempt at the wrong rate leaves partial
        // bytes in the module's line buffer, and the next command then comes
        // back `ERROR` for reasons that have nothing to do with the command —
        // which is exactly how this probe first misread a working module.
        esp_send(host, b"\r\n");
        esp_collect(host, 30);

        let mut reply = esp_command(host, b"AT\r\n", 50);

        // `busy p...` means the module is still chewing on something from
        // before the reset — and, more usefully, that this rate is the right
        // one: garbage comes back at a wrong rate, and `busy` does not. So it
        // is a reason to wait, not a reason to try the next rate. Reading it
        // as a failure is how this probe walked past a working module and left
        // UART1 configured at 9600, which then failed every join the
        // application attempted.
        for _ in 0..10 {
            if !contains(&reply, b"busy") {
                break;
            }
            host.board.delay().delay_ms(200);
            reply = esp_command(host, b"AT\r\n", 50);
        }

        let _ = writeln!(host, "[wifi] {rate}: {}", esp_printable(&reply));
        if !contains(&reply, b"OK") {
            continue;
        }

        let version = esp_command(host, b"AT+GMR\r\n", 80);
        let _ = writeln!(host, "[wifi] AT at {rate} baud — {}", esp_printable(&version));
        return;
    }

    // Nothing answered. Leave the port at the rate this board is known to use
    // rather than at whichever one the sweep happened to try last — an
    // application joining later has no way to reconfigure it, and a UART left
    // at 9600 turns a temporarily busy module into a permanently absent one.
    let _ = host
        .board
        .uart(1)
        .and_then(|u| u.configure(UartConfig { baud: RATES[0], ..UartConfig::default() }));
    let _ = writeln!(host, "[wifi] no AT response at any rate; UART1 left at {}", RATES[0]);
}

fn contains(haystack: &[u8], needle: &[u8]) -> bool {
    haystack.windows(needle.len()).any(|w| w == needle)
}

fn esp_send(host: &mut FirmwareHost, bytes: &[u8]) {
    if let Ok(uart) = host.board.uart(1) {
        let _ = uart.write(bytes);
    }
}

/// Read for roughly `ticks` × 10 ms, stopping early once the line goes quiet.
///
/// Polled every 200 µs, not every 10 ms. UART1 is a 16550 with a **16-byte**
/// receive FIFO, and at 74880 baud a 10 ms gap lets about 75 bytes arrive —
/// so a leisurely reader silently loses four bytes in five. That is what
/// turned this module's version string into `AT versi97e9)`, which looks like
/// a wrong baud rate and is not one.
fn esp_collect(host: &mut FirmwareHost, ticks: u32) -> Vec<u8> {
    let mut seen: Vec<u8> = Vec::new();
    let mut idle = 0u32;
    let budget = ticks * 50; // 50 polls of 200 µs make up each 10 ms tick
    for _ in 0..budget {
        let mut buf = [0u8; 32];
        let got = match host.board.uart(1) {
            Ok(uart) => uart.read(&mut buf).unwrap_or(0),
            Err(_) => 0,
        };
        if got > 0 {
            seen.extend_from_slice(&buf[..got]);
            idle = 0;
        } else {
            idle += 1;
            // 250 idle polls is 50 ms of silence — well past the gap between
            // an AT echo and its response.
            if !seen.is_empty() && idle >= 250 {
                break;
            }
            host.board.delay().delay_us(200);
        }
    }
    seen
}

fn esp_command(host: &mut FirmwareHost, cmd: &[u8], ticks: u32) -> Vec<u8> {
    esp_send(host, cmd);
    esp_collect(host, ticks)
}

/// Bytes as text with the unprintables shown, plus the count.
///
/// Both halves matter: a wrong baud rate answers with plausible-looking
/// characters, and only the length and the dots tell that apart from a real
/// reply.
fn esp_printable(bytes: &[u8]) -> String {
    let text: String = bytes
        .iter()
        .map(|b| if b.is_ascii_graphic() || *b == b' ' { *b as char } else { '.' })
        .collect();
    format!("{} bytes \"{text}\"", bytes.len())
}

/// Ask whether a camera is on the board, and take one frame from it.
///
/// The sensor's control channel is **I²C2**, not the SCCB master inside the
/// DVP block — that is what a running MaixPy uses, and it announces as much on
/// the way up (`init i2c:2 freq:100000`). This port spent a while on the SCCB
/// master first, which acknowledged the address and then returned `0xff` from
/// the id register on every boot but one.
///
/// The DVP block still has to be brought up before any of it means anything:
/// `XCLK` comes from there, and the sensor's own logic — including the part
/// that acknowledges an address — runs off it.
///
/// The capture is a self-test rather than a photograph anyone looks at, and it
/// is reported as statistics for a reason: a camera that is wired but not
/// capturing hands back a buffer that is entirely one value, and a photograph
/// of anything at all does not. That distinction survives a serial line. "Does
/// the screen look right?" does not, and asking it repeatedly is how this port
/// spent a day on the panel.
#[allow(dead_code)]
fn probe_camera(host: &mut FirmwareHost, clocks: Clocks) {
    let mut dvp = camera::Dvp::new(camera::MAIX_CAMERA, clocks.cpu_hz);
    dvp.init();

    let found = match host.board.i2c(2) {
        Ok(bus) => {
            // 100 kHz, which is what MaixPy uses on this bus.
            let _ = bus.set_frequency(100_000);
            camera::probe(bus)
        }
        Err(_) => None,
    };

    let Some((address, pid)) = found else {
        let _ = writeln!(host, "[camera] no sensor answered on I2C2");
        return;
    };
    let name = camera::identify(pid).unwrap_or("unrecognised");
    let _ = writeln!(host, "[camera] {name} on I2C2 at {address:#04x}, id {pid:#06x}");

    let cpu_hz = clocks.cpu_hz;
    let opened = match host.board.i2c(2) {
        // Sixteen settling frames, not the forty a photograph wants. Two is
        // not enough to be useful: the sensor is still at its opening exposure
        // guess, so a dark room reads as an unlit frame with almost no noise,
        // and the liveness check has nothing to measure.
        Ok(bus) => imaging::Sensor::open_with_settle(
            bus,
            cpu_hz,
            board::CAMERA_WIDTH,
            board::CAMERA_HEIGHT,
            16,
        ),
        Err(e) => Err(format!("no I2C2 on this board: {e}")),
    };
    let mut sensor = match opened {
        Ok(sensor) => sensor,
        Err(e) => {
            let _ = writeln!(host, "[camera] {e}");
            return;
        }
    };
    let (w, h) = (sensor.width(), sensor.height());
    // Two frames, because the question worth answering at boot is not what one
    // frame looks like but whether the next one differs. A live sensor never
    // repeats itself, even pointed at a blank wall; a stuck data bus does.
    let first = match sensor.capture() {
        Ok(frame) => frame.to_vec(),
        Err(e) => {
            let _ = writeln!(host, "[camera] {e}");
            return;
        }
    };
    match sensor.capture() {
        Ok(second) => {
            let moved = imaging::difference(&first, second);
            let _ = writeln!(
                host,
                "[camera] {w}x{h}: {}, {moved} bytes moved between frames",
                imaging::describe(second, w)
            );
        }
        Err(e) => {
            let _ = writeln!(host, "[camera] {e}");
        }
    }
}

/// Repeat a red blink group forever. Distinct counts distinguish failure kinds.
fn fail_forever(n: u32) -> ! {
    loop {
        signal_on(1 << board::CH_LED_R, n);
    }
}

/// Rust panic — including an allocation failure, which panics by default.
#[panic_handler]
fn on_panic(_: &core::panic::PanicInfo) -> ! {
    led_init();
    fail_forever(5)
}

/// Any CPU exception: a bad access, a misaligned load, or — the most likely
/// cause of a surprise here — an illegal instruction from floating-point code
/// running with `mstatus.FS` still `Off`.
#[export_name = "ExceptionHandler"]
fn on_exception(_frame: &riscv_rt::TrapFrame) -> ! {
    led_init();
    fail_forever(8)
}

/// An interrupt arrived that nothing claimed. Only reachable if something
/// enabled a source this firmware does not serve.
#[export_name = "DefaultHandler"]
fn on_unexpected_interrupt() {
    led_init();
    fail_forever(6)
}

// ---------------------------------------------------------------------------
// The interpreter's view of this board
// ---------------------------------------------------------------------------

/// Interpreter instructions per service-loop turn.
///
/// Small on purpose. UARTHS has an 8-byte receive FIFO, which is 694 µs of
/// traffic at 115200 — so the gap between two drains has to stay well under
/// that or an inbound frame loses bytes. A slice this size is a fraction of it
/// at 400 MHz, and `info` reports `max_poll_gap_us` so the assumption can be
/// checked rather than trusted.
const FUEL_SLICE: u64 = 2_000;

/// How many console lines to keep for `rustnet logs`.
const LOG_LINES: usize = 128;

pub(crate) struct FirmwareHost {
    pub(crate) board: K210Board,
    /// Completed console lines, oldest first, capped at [`LOG_LINES`].
    logs: Vec<String>,
    /// The line being assembled; the interpreter writes in fragments.
    partial: String,
    /// The RNDP service lives here so `sleep_ms` can keep serving it. Fuel is
    /// counted in instructions, but an application spends its wall-clock time
    /// asleep — a blink demo burns a few hundred instructions per second — so
    /// waiting a slice out in one blocking delay would leave the tools
    /// unanswered for as long as the sleep lasts.
    rndp: rndp::Rndp,
    /// The drawing surface, once an application calls `Display.Init`. Not
    /// created up front: a 320x240 RGB565 frame is 150 KB, and an app that
    /// never draws should not pay for it.
    display: Option<Framebuffer>,
    /// The camera, once an application calls `Camera.Configure`. Like the
    /// framebuffer, not created up front: bringing the sensor up costs most of
    /// a second in mandated settling delays, and an app that never
    /// photographs anything should not wait for it.
    camera: Option<imaging::Sensor>,
    /// Clocks, kept because the camera needs the CPU rate for its delays and
    /// is opened long after `main` has finished with them.
    clocks: Clocks,
    /// The radio's credentials and its current address.
    pub(crate) wifi: espat::WifiState,
    /// The broker connection, once an application calls `Mqtt.Connect`. One at
    /// a time: the module is in single-connection mode, which is the only mode
    /// whose `+IPD` announcements have the shape this port parses.
    broker: Option<mqtt::MqttSession>,
}

impl FirmwareHost {
    pub(crate) fn new(board: K210Board, clocks: Clocks) -> Self {
        Self {
            board,
            logs: Vec::new(),
            partial: String::new(),
            rndp: rndp::Rndp::new(),
            display: None,
            camera: None,
            clocks,
            wifi: espat::WifiState::default(),
            broker: None,
        }
    }

    /// Write the provisioned credentials where a reset cannot reach them.
    pub(crate) fn store_wifi(&mut self) -> Result<(), String> {
        let mut blob = self.wifi.ssid.clone();
        blob.push('\n');
        blob.push_str(&self.wifi.psk);
        flashfs::write(self.files()?, espat::CREDENTIALS_FILE, blob.as_bytes())
    }

    /// Read them back at boot. A board with none is the ordinary case, not an
    /// error, so a failure here is silent.
    pub(crate) fn restore_wifi(&mut self) {
        let Ok(files) = self.board.extmem(1) else {
            return;
        };
        let Ok(blob) = flashfs::read(files, espat::CREDENTIALS_FILE) else {
            return;
        };
        let text = String::from_utf8_lossy(&blob);
        if let Some((ssid, psk)) = text.split_once('\n') {
            self.wifi.ssid = String::from(ssid);
            self.wifi.psk = String::from(psk);
        }
    }

    /// The filesystem's flash window, as the error a managed caller should see
    /// if the firmware never attached one.
    fn files(&mut self) -> Result<&mut dyn ExtMemory, String> {
        self.board.extmem(1).map_err(|e| format!("no filesystem on this board: {e}"))
    }

    /// Which transport is carrying RNDP, for `info`. One, here: the K210 has no
    /// USB device controller, so unlike the Netduino there is no alternative to
    /// the console UART.
    pub(crate) fn transport_name(&self) -> &'static str {
        "uarths"
    }

    /// Answer any pending RNDP traffic. Takes the service out of `self` for the
    /// call so it can borrow the rest of the host; the move is a couple of
    /// pointers, not an allocation.
    pub(crate) fn poll_rndp(&mut self) {
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

    /// Console output. Goes to the UART live *and* into the log ring: the tools
    /// skip non-frame bytes on the wire, so both can share the line.
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
        for piece in text.split_inclusive('\n') {
            match piece.strip_suffix('\n') {
                Some(body) => {
                    for byte in body.as_bytes() {
                        rustnet_hal_k210::Uarths::put_byte(*byte);
                    }
                    rustnet_hal_k210::Uarths::put_byte(b'\r');
                    rustnet_hal_k210::Uarths::put_byte(b'\n');
                }
                None => {
                    for byte in piece.as_bytes() {
                        rustnet_hal_k210::Uarths::put_byte(*byte);
                    }
                }
            }
        }
    }

    /// Write bytes verbatim — RNDP frames must not be line-ending mangled.
    pub(crate) fn write_raw(&mut self, bytes: &[u8]) {
        for byte in bytes {
            rustnet_hal_k210::Uarths::put_byte(*byte);
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
        // Two different intervals, because two different things are at stake.
        //
        // Draining the receive FIFO has a hard deadline: 8 bytes at 115200 is
        // 694 µs, and a gap longer than that loses bytes rather than delaying
        // them. Servicing a frame does not — it only costs response latency.
        // So the FIFO is emptied every 100 µs, and RNDP is polled every 2 ms.
        const POLL_STEP_MS: u64 = 2;
        const DRAIN_STEP_US: u64 = 100;

        let mut left = ms;
        while left > 0 {
            let step = left.min(POLL_STEP_MS);
            let mut waited = 0;
            while waited < step * 1000 {
                self.board.delay().delay_us(DRAIN_STEP_US);
                rndp::drain_fifo();
                // The radio's port too: it talks unbidden, and its FIFO is
                // twice the console's but its traffic is bulkier.
                espat::drain_fifo();
                waited += DRAIN_STEP_US;
            }
            left -= step;
            self.poll_rndp();
        }
    }

    fn invoke(&mut self, name: &str, args: Vec<HostValue>) -> Result<HostValue, String> {
        // Diagnostic: flash green nine times, once, on the very first call the
        // application makes into the host. Seeing it means the interpreter
        // really is executing managed code; not seeing it means it never got
        // that far.
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
            // The board tells the app where its LED is, so one compiled module
            // runs on every board rather than one per LED position. Matched by
            // shape, not by full name: the namespace belongs to whichever
            // application is embedded.
            //
            // On this board the LED is common-anode, so a demo that writes
            // `true` for "lit" will blink inverted — dark where it means light.
            // Left alone rather than silently flipped: an app that drives a
            // relay off the same call would not thank us for the surprise.
            n if n.ends_with("Board::UserLed()") => {
                Ok(HostValue::I32(board::USER_LED as i32))
            }
            // The buttons, by the same rule as the LED: the board knows where
            // they are, so one compiled module runs everywhere. All three are
            // pulled up and short to ground, so `Gpio.Read` is *false* while a
            // button is held.
            n if n.ends_with("Board::ButtonUp()") => {
                Ok(HostValue::I32(board::BUTTON_UP as i32))
            }
            n if n.ends_with("Board::ButtonDown()") => {
                Ok(HostValue::I32(board::BUTTON_DOWN as i32))
            }
            n if n.ends_with("Board::ButtonMiddle()") => {
                Ok(HostValue::I32(board::BUTTON_MIDDLE as i32))
            }

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

            // --- Graphics ------------------------------------------------
            //
            // The canonical names have to match `runtime/firmware/src/apphost.rs`
            // character for character: the same compiled module runs against
            // both hosts, and the interpreter dispatches on the string.
            //
            // Every draw call is a no-op until `Init`, rather than an error.
            // An app that draws before initialising is buggy, but failing the
            // call turns a blank screen into an unhandled exception, and a
            // blank screen is the more diagnosable of the two.
            "RustNet.Graphics.Display::Init(i4,i4)" => {
                let w = int(0)?.clamp(1, 1024) as u32;
                let h = int(1)?.clamp(1, 1024) as u32;
                self.display = Some(Framebuffer::new(w, h));
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::ConfigurePanel(i4,i4,i4,i4)" => {
                let w = int(1)?.clamp(1, 1024) as u32;
                let h = int(2)?.clamp(1, 1024) as u32;
                let rotation = int(3)?.rem_euclid(360) as u16;
                let mut fb = Framebuffer::new(w, h);
                fb.set_rotation(rotation);
                self.display = Some(fb);
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::Width()" => Ok(HostValue::I32(
                self.display.as_ref().map(|fb| fb.logical_size().0 as i32).unwrap_or(0),
            )),
            "RustNet.Graphics.Display::Height()" => Ok(HostValue::I32(
                self.display.as_ref().map(|fb| fb.logical_size().1 as i32).unwrap_or(0),
            )),
            "RustNet.Graphics.Display::SetClip(i4,i4,i4,i4)" => {
                let (x, y, w, h) = (int(0)?, int(1)?, int(2)?, int(3)?);
                if let Some(fb) = self.display.as_mut() {
                    fb.set_clip(x, y, w, h);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::ClearClip()" => {
                if let Some(fb) = self.display.as_mut() {
                    fb.clear_clip();
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::Clear(i4)" => {
                let c = Color(int(0)? as u16);
                if let Some(fb) = self.display.as_mut() {
                    fb.clear(c);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::SetPixel(i4,i4,i4)" => {
                let (x, y) = (int(0)?, int(1)?);
                let c = Color(int(2)? as u16);
                if let Some(fb) = self.display.as_mut() {
                    fb.set_pixel(x, y, c);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::FillRect(i4,i4,i4,i4,i4)" => {
                let (x, y, w, h) = (int(0)?, int(1)?, int(2)?, int(3)?);
                let c = Color(int(4)? as u16);
                if let Some(fb) = self.display.as_mut() {
                    fb.fill_rect(x, y, w, h, c);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::FillCircle(i4,i4,i4,i4)" => {
                let (cx, cy, r) = (int(0)?, int(1)?, int(2)?);
                let c = Color(int(3)? as u16);
                if let Some(fb) = self.display.as_mut() {
                    fb.fill_circle(cx, cy, r, c);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::DrawText(i4,i4,string,i4,i4)" => {
                let (x, y) = (int(0)?, int(1)?);
                let text = text_arg(&args, 2, name)?;
                let c = Color(int(3)? as u16);
                let scale = int(4)?;
                if let Some(fb) = self.display.as_mut() {
                    fb.draw_text(x, y, &text, c, scale);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::DrawLine(i4,i4,i4,i4,i4)" => {
                let (x0, y0, x1, y1) = (int(0)?, int(1)?, int(2)?, int(3)?);
                let c = Color(int(4)? as u16);
                if let Some(fb) = self.display.as_mut() {
                    fb.draw_line(x0, y0, x1, y1, c);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::DrawImage(i4,i4,i4,i4,u1[])" => {
                let (x, y) = (int(0)?, int(1)?);
                let (w, h) = (int(2)?.max(0) as u32, int(3)?.max(0) as u32);
                let src = rgb565_arg(&args, 4, name)?;
                if let Some(fb) = self.display.as_mut() {
                    fb.draw_image(x, y, w, h, &src);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::BlendImage(i4,i4,i4,i4,u1[],i4)" => {
                let (x, y) = (int(0)?, int(1)?);
                let (w, h) = (int(2)?.max(0) as u32, int(3)?.max(0) as u32);
                let src = rgb565_arg(&args, 4, name)?;
                let alpha = int(5)?.clamp(0, 255) as u8;
                if let Some(fb) = self.display.as_mut() {
                    fb.blend_image(x, y, w, h, &src, alpha);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::FillGradient(i4,i4,i4,i4,i4,i4,bool)" => {
                let (x, y, w, h) = (int(0)?, int(1)?, int(2)?, int(3)?);
                let c0 = Color(int(4)? as u16);
                let c1 = Color(int(5)? as u16);
                let vertical = flag(6)?;
                if let Some(fb) = self.display.as_mut() {
                    fb.fill_gradient(x, y, w, h, c0, c1, vertical);
                }
                Ok(HostValue::Void)
            }
            "RustNet.Graphics.Display::Present()" => {
                // No clone: 150 KB per frame would double peak heap and cost
                // more time than the blit itself.
                if let Some(fb) = self.display.as_ref() {
                    let (w, h) = (fb.width, fb.height);
                    self.board.present_frame(&fb.pixels, w, h).map_err(hal_err)?;
                }
                Ok(HostValue::Void)
            }

            // --- WiFi -----------------------------------------------------
            "RustNet.Net.Wifi::Connect(string,string)" => {
                // Empty arguments mean "use what was provisioned", so an app
                // can ship without credentials in it and still join. That is
                // the intended shape: the SSID and PSK arrive over RNDP and
                // live in RAM.
                let ssid = text_arg(&args, 0, name)?;
                let psk = text_arg(&args, 1, name)?;
                if !ssid.is_empty() {
                    self.wifi.ssid = ssid;
                    self.wifi.psk = psk;
                }
                let (ssid, psk) = (self.wifi.ssid.clone(), self.wifi.psk.clone());
                match espat::join(self, &ssid, &psk) {
                    Ok(ip) => {
                        let _ = writeln!(self, "[wifi] joined '{ssid}' as {ip}");
                        self.wifi.ip = ip;
                        self.wifi.joined = true;
                    }
                    Err(e) => {
                        let _ = writeln!(self, "[wifi] {e}");
                        self.wifi.ip.clear();
                        self.wifi.joined = false;
                    }
                }
                Ok(HostValue::Bool(self.wifi.joined))
            }
            "RustNet.Net.Wifi::IsConnected()" => Ok(HostValue::Bool(self.wifi.joined)),
            "RustNet.Net.Wifi::GetSsid()" => Ok(HostValue::Str(self.wifi.ssid.clone())),
            "RustNet.Net.Wifi::GetIp()" => Ok(HostValue::Str(self.wifi.ip.clone())),
            "RustNet.Net.Wifi::Disconnect()" => {
                espat::leave(self);
                self.wifi.ip.clear();
                self.wifi.joined = false;
                Ok(HostValue::Void)
            }

            // --- MQTT -----------------------------------------------------
            "RustNet.Net.Mqtt::Connect(string,string)" => {
                let address = text_arg(&args, 0, name)?;
                let client_id = text_arg(&args, 1, name)?;
                self.broker = None;
                self.broker = Some(mqtt::MqttSession::open(self, &address, &client_id, None)?);
                Ok(HostValue::Bool(true))
            }
            "RustNet.Net.Mqtt::ConnectAuth(string,string,string,string)" => {
                let address = text_arg(&args, 0, name)?;
                let client_id = text_arg(&args, 1, name)?;
                let user = text_arg(&args, 2, name)?;
                let password = text_arg(&args, 3, name)?;
                self.broker = None;
                self.broker =
                    Some(mqtt::MqttSession::open(self, &address, &client_id, Some((&user, &password)))?);
                Ok(HostValue::Bool(true))
            }
            "RustNet.Net.Mqtt::Publish(string,string,i4)" => {
                let topic = text_arg(&args, 0, name)?;
                let payload = text_arg(&args, 1, name)?;
                let qos = int(2)?.clamp(0, 1) as u8;
                // Taken out of `self` for the call: the session needs the host
                // to reach the UART, and it cannot borrow it while it is a
                // field of it. The move is a couple of pointers.
                let mut session = self.broker.take().ok_or_else(mqtt::not_connected)?;
                let outcome = session.publish(self, &topic, payload.as_bytes(), qos);
                self.broker = Some(session);
                outcome.map_err(|e| mqtt::describe_failure(self, &e))?;
                Ok(HostValue::Void)
            }
            "RustNet.Net.Mqtt::Subscribe(string)" => {
                let topic = text_arg(&args, 0, name)?;
                let mut session = self.broker.take().ok_or_else(mqtt::not_connected)?;
                let outcome = session.subscribe(self, &topic);
                self.broker = Some(session);
                outcome.map_err(|e| mqtt::describe_failure(self, &e))?;
                Ok(HostValue::Void)
            }
            "RustNet.Net.Mqtt::Poll()" => {
                // A short budget on purpose. This is called from a UI loop, and
                // a poll that waits seconds for a broker with nothing to say
                // stops the screen instead.
                let mut session = self.broker.take().ok_or_else(mqtt::not_connected)?;
                let outcome = session.poll(self, mqtt::POLL_BUDGET_MS);
                self.broker = Some(session);
                match outcome? {
                    // "topic\0payload", which is what the managed wrapper
                    // splits: the host boundary carries strings and byte
                    // arrays, not tuples.
                    Some((topic, payload)) => {
                        let mut joined = topic;
                        joined.push('\0');
                        joined.push_str(&String::from_utf8_lossy(&payload));
                        Ok(HostValue::Str(joined))
                    }
                    // Nothing waiting. The managed doc comment says this call
                    // blocks until a message arrives, and on this board it
                    // does not — an application drawing a screen between polls
                    // cannot afford to stop for a broker with nothing to say.
                    // An empty string means "nothing yet", and the demo loops.
                    None => Ok(HostValue::Str(String::new())),
                }
            }

            // --- Camera ---------------------------------------------------
            "RustNet.Media.Camera::ConfigureRaw(i4,i4,i4)" => {
                let (w, h) = (int(0)?, int(1)?);
                // The format argument is RGB565 or grayscale in the managed
                // API; this path produces RGB565 either way, and silently
                // handing back the wrong format would be worse than saying so.
                if int(2)? != 0 {
                    return Err(String::from(
                        "this board's camera only delivers RGB565",
                    ));
                }
                let clamp = |v: i32| v.clamp(0, u16::MAX as i32) as u16;
                let cpu_hz = self.clocks.cpu_hz;
                // Dropped before the new one is built: two sensors would mean
                // two sets of frame buffers alive at once, and at 320x240 that
                // is 375 KB of heap for no reason.
                self.camera = None;
                let bus = self.board.i2c(2).map_err(hal_err)?;
                self.camera = Some(imaging::Sensor::open(bus, cpu_hz, clamp(w), clamp(h))?);
                Ok(HostValue::Void)
            }
            "RustNet.Media.Camera::Capture()" => {
                let sensor = self
                    .camera
                    .as_mut()
                    .ok_or_else(|| String::from("call Camera.Configure before Camera.Capture"))?;
                let frame = sensor.capture()?;
                Ok(HostValue::Bytes(frame.to_vec()))
            }
            "RustNet.Media.Camera::Width()" => Ok(HostValue::I32(
                self.camera.as_ref().map(|c| c.width()).unwrap_or(0) as i32,
            )),
            "RustNet.Media.Camera::Height()" => Ok(HostValue::I32(
                self.camera.as_ref().map(|c| c.height()).unwrap_or(0) as i32,
            )),

            // --- Filesystem ----------------------------------------------
            "RustNet.IO.FileSystem::WriteAllText(string,string)" => {
                let path = text_arg(&args, 0, name)?;
                let body = text_arg(&args, 1, name)?;
                flashfs::write(self.files()?, &path, body.as_bytes())?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::AppendText(string,string)" => {
                let path = text_arg(&args, 0, name)?;
                let body = text_arg(&args, 1, name)?;
                flashfs::append_bytes(self.files()?, &path, body.as_bytes())?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::ReadAllText(string)" => {
                let path = text_arg(&args, 0, name)?;
                let bytes = flashfs::read(self.files()?, &path)?;
                let text = String::from_utf8(bytes).map_err(|_| {
                    format!("{path} is not UTF-8; read it with ReadAllBytes")
                })?;
                Ok(HostValue::Str(text))
            }
            "RustNet.IO.FileSystem::WriteAllBytes(string,u1[])" => {
                let path = text_arg(&args, 0, name)?;
                let bytes = bytes_arg(&args, 1, name)?;
                flashfs::write(self.files()?, &path, &bytes)?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::AppendBytes(string,u1[])" => {
                let path = text_arg(&args, 0, name)?;
                let bytes = bytes_arg(&args, 1, name)?;
                flashfs::append_bytes(self.files()?, &path, &bytes)?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::ReadAllBytes(string)" => {
                let path = text_arg(&args, 0, name)?;
                Ok(HostValue::Bytes(flashfs::read(self.files()?, &path)?))
            }
            "RustNet.IO.FileSystem::Exists(string)" => {
                let path = text_arg(&args, 0, name)?;
                Ok(HostValue::Bool(flashfs::exists(self.files()?, &path)?))
            }
            "RustNet.IO.FileSystem::Delete(string)" => {
                let path = text_arg(&args, 0, name)?;
                flashfs::delete(self.files()?, &path)?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::CreateDirectory(string)" => {
                let path = text_arg(&args, 0, name)?;
                flashfs::create_directory(self.files()?, &path)?;
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::List(string)" => {
                let path = text_arg(&args, 0, name)?;
                Ok(HostValue::Str(flashfs::list(self.files()?, &path)?.join("\n")))
            }

            other => Err(format!("unsupported internal call: {other}")),
        }
    }
}

/// A string argument, by position.
fn text_arg(args: &[HostValue], n: usize, name: &str) -> Result<String, String> {
    match args.get(n) {
        Some(HostValue::Str(s)) => Ok(s.clone()),
        other => Err(format!("{name}: argument {n} is not a string: {other:?}")),
    }
}

/// A byte-array argument, by position. Byte arrays are the only array channel
/// across this boundary, which is why wider data is packed little-endian by the
/// C# side rather than passed as a typed array.
fn bytes_arg(args: &[HostValue], n: usize, name: &str) -> Result<Vec<u8>, String> {
    match args.get(n) {
        Some(HostValue::Bytes(b)) => Ok(b.clone()),
        other => Err(format!("{name}: argument {n} is not a byte array: {other:?}")),
    }
}

/// A byte-array argument decoded as little-endian RGB565 pixels.
fn rgb565_arg(args: &[HostValue], n: usize, name: &str) -> Result<Vec<u16>, String> {
    Ok(bytes_arg(args, n, name)?
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect())
}

// ---------------------------------------------------------------------------

#[entry]
fn main() -> ! {
    // Boot progress is blinked out on green as 1, 2, 3, 4 then 6 flashes with
    // pauses between. Whichever count the sequence stops at names the step that
    // hung; a repeating *red* group is a failure rather than a stall.
    led_init();
    signal(1); // reached the entry point

    {
        static mut HEAP_MEM: [MaybeUninit<u8>; board::HEAP_SIZE] =
            [MaybeUninit::uninit(); board::HEAP_SIZE];
        // SAFETY: runs once, before anything can allocate.
        unsafe { HEAP.init(core::ptr::addr_of_mut!(HEAP_MEM) as usize, board::HEAP_SIZE) }
    }

    // Read the clock tree rather than programming it: the mask ROM has already
    // brought PLL0 up (its own ISP talks over UARTHS at speed), and
    // re-programming a PLL that feeds the executing core is a change that
    // either works or hangs with nothing on the console to say which.
    let clocks = Clocks::detect();
    // Correct the spin-delay reference before the next signal. It started at
    // the crystal, so until now every blink has been up to 15x too long.
    CPU_HZ.store(clocks.cpu_hz.max(1_000_000), Ordering::Relaxed);
    signal(2); // clock tree read

    let mut hw = K210Board::new(board::NAME, clocks, Some(board::CONSOLE_PINS));
    hw.init();
    // Pin the LEDs to the channels the raw helpers above assume, before any
    // on-demand allocation can take those channels for something else.
    for (pad, channel) in [
        (board::LED_R, board::CH_LED_R),
        (board::LED_G, board::CH_LED_G),
        (board::LED_B, board::CH_LED_B),
    ] {
        let _ = hw.bind_gpio(pad, channel);
    }
    // The ESP8285's UART, so an AT-command netif has somewhere to land later.
    // Muxing costs nothing and documents the wiring in one place.
    let _ = hw.set_uart_pins(1, board::WIFI_UART_TX, board::WIFI_UART_RX);
    hw.attach_storage(SpiFlash::new(
        board::STORAGE_BASE,
        board::STORAGE_LEN,
        clocks.spi_hz(3),
    ));
    hw.attach_files(SpiFlash::new(board::FS_BASE, board::FS_LEN, clocks.spi_hz(3)));
    hw.uart(0)
        .and_then(|u| u.configure(UartConfig::default()))
        .expect("console configure");
    signal(3); // board and console configured

    let mut host = FirmwareHost::new(hw, clocks);
    let _ = writeln!(
        host,
        "\nRustNet on {} @ {} MHz (APB0 {} MHz), heap {} MB",
        board::NAME,
        clocks.cpu_hz / 1_000_000,
        clocks.apb0_hz / 1_000_000,
        board::HEAP_SIZE / (1024 * 1024)
    );
    signal(4); // banner sent — UARTHS transmit completes, so it is not stuck

    // Say what the flash answered. On new hardware this is the single most
    // informative line: a plausible JEDEC id means SPI3, the pad routing and
    // the clock divisor are all right, and storage can be trusted.
    match storage::identify(&mut host.board) {
        Ok(text) => {
            let _ = writeln!(host, "[storage] {text}");
        }
        Err(e) => {
            let _ = writeln!(host, "[storage] flash did not answer: {e}");
        }
    }
    let fs_used = host.board.extmem(1).map(flashfs::used).unwrap_or(0);
    let _ = writeln!(
        host,
        "[fs] {} KB used of {} KB at {:#08x}",
        fs_used / 1024,
        board::FS_LEN / 1024,
        board::FS_BASE
    );

    // Bring the panel up last of the hardware, because it is the newest and
    // least proven part: everything above has already reported on the console
    // by the time a wrong register write here could hang the boot.
    #[cfg(feature = "no-panel")]
    {
        // Hold the panel in reset rather than merely skipping its init. An
        // uninitialised ST7789 is not an unpowered one, and the point of this
        // build is to take current off the rail, not to save a few register
        // writes.
        if let Ok(pin) = host.board.gpio(lcd::MAIX_PANEL.rst_pad as u32) {
            let _ = pin.set_mode(PinMode::Output);
            let _ = pin.write(Level::Low);
        }
        let _ = writeln!(host, "[panel] held in reset (feature no-panel)");
    }
    #[cfg(not(feature = "no-panel"))]
    let mut panel = lcd::St7789::new(lcd::MAIX_PANEL, clocks.spi_hz(0), clocks.cpu_hz);
    #[cfg(not(feature = "no-panel"))]
    match panel.init(lcd::DEFAULT_CLOCK_HZ) {
        Ok(()) => {
            // Red, green, blue, white, black before anything else draws. On a
            // board this young the two failure modes — "the bus is dead" and
            // "the colours or orientation are wrong" — look identical from a
            // still photograph of a running app, and are told apart in two
            // seconds by whether the screen changes at all.
            let _ = writeln!(
                host,
                "[panel] ST7789V {}x{} on SPI0 octal @ {} MHz (SPI0 source {} MHz)",
                board::PANEL_WIDTH,
                board::PANEL_HEIGHT,
                lcd::DEFAULT_CLOCK_HZ / 1_000_000,
                clocks.spi_hz(0) / 1_000_000
            );
            // LEDs first, then the panel. If the LEDs are dark the panel test
            // below cannot mean anything: both depend on GPIOHS driving an
            // output pad, and the panel additionally needs its reset released.
            // The sweeps and `blink_backlight` are diagnostics, not boot
            // behaviour — they cost a minute each and the application is what
            // should be running. Call them from here when a question comes up;
            // what each one has already settled is in their doc comments.
            let _ = panel.fill(0x0000);
            host.board.attach_panel(panel);
        }
        Err(e) => {
            let _ = writeln!(host, "[panel] not brought up: {e}");
        }
    }

    probe_esp8285(&mut host);
    probe_camera(&mut host, clocks);

    // Before the application starts: it may try to join immediately, and on
    // this board it will have been restarted by whatever tool is talking to
    // the port.
    host.restore_wifi();
    let provisioned = host.wifi.ssid.clone();
    if !provisioned.is_empty() {
        let _ = writeln!(host, "[wifi] provisioned for '{provisioned}'");
    }

    rndp::start_receiving();
    // The radio speaks without being asked, so its port is armed too.
    espat::start_receiving();
    signal(6); // console up and RNDP listening; the application runs next

    // Anything a previous session provisioned or uploaded lives in flash, so
    // pick that up before falling back to the application built into the image.
    // This is what makes a power cycle not undo your work.
    //
    // The application starts as the one compiled in, and is replaced whenever
    // `rustnet flash` lands a new one. Swapping means rebuilding the
    // interpreter, because it borrows the module for its lifetime — which is
    // why the run lives in a function: the borrow of `app` has to end before
    // `app` can be reassigned.
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

/// Run one application until it ends or a new one is flashed. Returns the host
/// and, if `rustnet flash` delivered one, the module to run next.
fn run_app(app: &[u8], host: FirmwareHost) -> (FirmwareHost, Option<Vec<u8>>) {
    let mut host = host;

    let module = match Module::from_bytes(app) {
        Ok(module) => module,
        Err(_) => {
            // Only reachable for the compiled-in app: an uploaded one is parsed
            // before it is accepted.
            let _ = writeln!(host, "[fatal] module is not a valid RNX");
            fail_forever(7);
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
    loop {
        rndp::drain_fifo();
        espat::drain_fifo();
        interp.host.poll_rndp();

        if interp.host.has_pending_app() {
            break;
        }
        if !interp.host.app_running() {
            continue;
        }

        match interp.run(FUEL_SLICE) {
            RunExit::OutOfFuel => {}
            // An application that ends, faults or pauses must not take the
            // firmware with it: stay reachable and report, so a bad app can be
            // replaced over the wire rather than needing a reflash.
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
