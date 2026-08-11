//! RustNet firmware for the Wilderness Labs **Meadow F7 Micro v1.0**
//! (STM32F777, Cortex-M7 at 216 MHz).
//!
//! The board reaches its host over its own USB socket as a CDC serial port,
//! so one cable carries DFU for the firmware and RNDP for everything after
//! it — no adapter, no probe. That is the same arrangement the Netduino and
//! the Pico use, and for the same reason: a board you can only talk to
//! through extra hardware is a board that is awkward to fix.
//!
//! ## What this replaces
//!
//! Flashing this **overwrites Meadow OS** in the part's internal flash. That
//! is not incidental — DFU into internal flash is the only way in without a
//! probe. It is reversible: Wilderness Labs' own `meadow` CLI puts their OS
//! back.
//!
//! ## Clocking, and how the crystal is found rather than assumed
//!
//! The Meadow's HSE frequency is not published anywhere, and USB will not work
//! without it. This firmware first tried the internal HSI to sidestep the
//! unknown; the board answered with `Device Descriptor Request Failed`, which
//! is exactly right. **HSI is 1% accurate and USB full-speed allows 0.25%** -
//! four times out of spec, so the host's very first descriptor request fails
//! its CRC. An F7 has no HSI48 and no clock-recovery unit to rescue it, which
//! is why every F7 USB design uses a crystal.
//!
//! A crystal is certainly present: the part's own ROM bootloader speaks USB
//! DFU, and that needs HSE too. What is unknown is only its frequency.
//!
//! So the firmware **sweeps the standard crystal frequencies and lets the host
//! decide**, in one flash rather than one flash per guess. For each candidate
//! it programs the PLL for a 216 MHz core and exactly 48 MHz for USB, resets
//! the USB device, and waits to see whether a host configures it. A host that
//! completes enumeration is the only authority on whether the bit clock is
//! right, so it is the one being asked. The winner is reported in the boot log
//! and in `info`, so the answer is recorded rather than rediscovered.
//!
//! ## Diagnostics
//!
//! The part identifies itself over RNDP rather than being asserted here: the
//! `info` response carries `DBGMCU_IDCODE` and the flash-size word the chip
//! keeps in system memory. If the board is not the F777 this was built for,
//! it says so instead of misbehaving quietly.

#![no_std]
#![no_main]

extern crate alloc;

use alloc::format;
use alloc::string::String;
use alloc::vec::Vec;
use core::fmt::Write as _;
use core::mem::MaybeUninit;

use cortex_m_rt::{entry, exception, ExceptionFrame};
use embedded_alloc::LlffHeap;
use rustnet_core::{HostValue, Interpreter, Module, RunExit, RuntimeHost};
use rustnet_hal::gpio::{Level, PinMode};
use rustnet_hal::Board as _;
use rustnet_hal_stm32::{Clocks, Stm32F4Board};

mod chipid;
mod qspi;
mod rndp;
mod uart;
mod usb;

/// The application, compiled from C# by the MetadataProcessor and embedded in
/// the image. This port has no flash filesystem yet, so replacing the app
/// means rebuilding the firmware — the same place the STM32F4 port started.
static APP_RNX: &[u8] = include_bytes!("../../firmware-stm32/demo/Blink.rnx");
const APP_NAME: &str = "blink (embedded)";

pub mod board {
    /// What `rustnet info` calls this board.
    pub const NAME: &str = "Meadow F7 Micro";

    /// The onboard RGB LED, from Wilderness Labs' own schematic
    /// (`Meadow_Hardware_Designs`, `MeadowF7Micro_REVD.pdf`, sheet 2): the
    /// nets `BLINKY_R`, `BLINKY_G` and `BLINKY_B` land on PA2, PA1 and PA0.
    ///
    /// Derived rather than guessed, and checked against the same sheet's known
    /// nets before being believed — `ADC1_IN3` sits on PA3, `ADC1_IN10` on PC0
    /// and `ADC1_IN11` on PC1, all of which match the part's own ADC map. A
    /// method that gets those three right is a method worth trusting for the
    /// LED.
    ///
    /// `Board::gpio` numbers pins as `port * 16 + index`, and port A is 0.
    pub const LED_BLUE: u32 = 0; // PA0
    pub const LED_GREEN: u32 = 1; // PA1
    pub const LED_RED: u32 = 2; // PA2

    /// The one the embedded demo drives.
    pub const USER_LED: Option<u32> = Some(LED_GREEN);

    /// The crystal on PH0/PH1: `X401`, an Abracon **ABM12W-25**.
    ///
    /// 25 MHz, from the same schematic. This was previously searched for at
    /// runtime because no published document named it; it is now a fact, and a
    /// board that has to rediscover its own crystal on every boot is a board
    /// that takes twenty seconds to say hello.
    pub const HSE_HZ: u32 = 25_000_000;
}

// ---------------------------------------------------------------------------
// Heap
// ---------------------------------------------------------------------------

#[global_allocator]
static HEAP: LlffHeap = LlffHeap::empty();

/// The interpreter allocates for every object, boxed value and string, so this
/// is the ceiling on what an application can hold. 256 KB of the part's 384 KB
/// usable SRAM, leaving room for the stack and USB buffers.
const HEAP_SIZE: usize = 256 * 1024;
static mut HEAP_MEM: [MaybeUninit<u8>; HEAP_SIZE] = [MaybeUninit::uninit(); HEAP_SIZE];

pub(crate) fn heap_used() -> usize {
    HEAP.used()
}

// ---------------------------------------------------------------------------
// Clock tree
// ---------------------------------------------------------------------------

const RCC_BASE: usize = 0x4002_3800;
const RCC_CR: usize = RCC_BASE + 0x00;
const RCC_PLLCFGR: usize = RCC_BASE + 0x04;
const RCC_CFGR: usize = RCC_BASE + 0x08;
const RCC_APB1ENR: usize = RCC_BASE + 0x40;

const PWR_BASE: usize = 0x4000_7000;
const PWR_CR1: usize = PWR_BASE + 0x00;
const PWR_CSR1: usize = PWR_BASE + 0x04;

const FLASH_ACR: usize = 0x4002_3C00;

const RCC_AHB1ENR: usize = RCC_BASE + 0x30;
/// Port H, whose first two pins are OSC_IN and OSC_OUT.
const GPIOH_BASE: usize = 0x4002_1C00;

#[inline(always)]
fn rd(addr: usize) -> u32 {
    // SAFETY: fixed peripheral address, only reached on the chip.
    unsafe { core::ptr::read_volatile(addr as *const u32) }
}

#[inline(always)]
fn wr(addr: usize, value: u32) {
    // SAFETY: as above.
    unsafe { core::ptr::write_volatile(addr as *mut u32, value) }
}

/// A PLL setting that yields a 216 MHz core and exactly 48 MHz for USB from
/// one crystal frequency.
///
/// `P` is always /2 and the VCO always lands on 432 MHz, so `Q` is always 9.
/// What changes between crystals is only how the input is divided down to the
/// 1-2 MHz the PLL wants to see.
#[derive(Clone, Copy)]
pub struct PllPlan {
    pub hse_hz: u32,
    m: u32,
    n: u32,
}

/// The Meadow's crystal, and the divisors that take it to both clocks.
///
/// 25 MHz does not divide to the 2 MHz the PLL prefers, so it takes a 1 MHz
/// input and twice the multiplier to reach the same 432 MHz VCO. These are the
/// same numbers ST's own configurator picks for 25 MHz into 216 MHz.
const MEADOW_PLL: PllPlan = PllPlan { hse_hz: board::HSE_HZ, m: 25, n: 432 };

/// The VCO is always 432 MHz, and 432/9 is exactly 48.
const PLL_Q: u32 = 9;

/// Everything that must be true before the core may run at 216 MHz, and which
/// stays true whichever source ends up feeding the PLL.
///
/// The order is the part's, not a preference: the voltage scale and over-drive
/// have to be raised *before* the core is clocked past 180 MHz, and the flash
/// wait states before the clock is switched - a core running faster than its
/// flash can answer does not fault, it fetches rubbish.
fn prepare_for_216mhz() {
    // HSI is on out of reset and is what the core runs from until a crystal
    // is found, but say so rather than assume it.
    wr(RCC_CR, rd(RCC_CR) | (1 << 0)); // HSION
    while rd(RCC_CR) & (1 << 1) == 0 {} // HSIRDY

    // PWR on, then voltage scale 1 - the only scale that permits 216 MHz.
    wr(RCC_APB1ENR, rd(RCC_APB1ENR) | (1 << 28)); // PWREN
    wr(PWR_CR1, (rd(PWR_CR1) & !(0b11 << 14)) | (0b11 << 14)); // VOS = scale 1

    // Over-drive, which 216 MHz requires and 180 MHz does not. Both handshakes
    // have to complete before any switch.
    wr(PWR_CR1, rd(PWR_CR1) | (1 << 16)); // ODEN
    while rd(PWR_CSR1) & (1 << 16) == 0 {} // ODRDY
    wr(PWR_CR1, rd(PWR_CR1) | (1 << 17)); // ODSWEN
    while rd(PWR_CSR1) & (1 << 17) == 0 {} // ODSWRDY

    // 7 wait states with the ART accelerator and prefetch on: 216 MHz at
    // 3.3 V. Set before the switch, never after.
    wr(FLASH_ACR, (1 << 9) | (1 << 8) | 7); // ARTEN | PRFTEN | LATENCY=7

    // APB1 /4 = 54 MHz and APB2 /2 = 108 MHz - the family's bus ceilings.
    // AHB undivided.
    let cfgr = (rd(RCC_CFGR) & !0x0000_FCFF) | (0b101 << 10) | (0b100 << 13);
    wr(RCC_CFGR, cfgr);
}

/// The three LED pins live on port A, whose clock gate and registers are the
/// same ones the HAL uses — but this has to work before the heap, the board or
/// anything else exists, so it talks to them directly.
mod led {
    use super::{rd, wr};

    const RCC_AHB1ENR: usize = super::RCC_BASE + 0x30;
    const GPIOA: usize = 0x4002_0000;

    /// The level that lights a colour.
    ///
    /// The LED is common-anode — schematic sheet 2 shows `LTST-C19HE1WT` with
    /// its shared pin at VCC and each colour returning to the MCU through its
    /// own resistor — so a pin driven **low** lights it, which is the opposite
    /// of the obvious.
    const ON: bool = false;

    pub fn init() {
        wr(RCC_AHB1ENR, rd(RCC_AHB1ENR) | 1); // GPIOAEN
        let moder = rd(GPIOA);
        // PA0, PA1, PA2 to general-purpose output.
        let cleared = moder & !(0b11 | (0b11 << 2) | (0b11 << 4));
        wr(GPIOA, cleared | 0b01 | (0b01 << 2) | (0b01 << 4));
        all_off();
    }

    pub fn set(pin: u32, lit: bool) {
        // BSRR: the low half drives a pin high, the high half drives it low.
        //
        // `ON` says which level lights the LED, so `lit` has to be translated
        // through it rather than compared to it. The first version compared —
        // `lit == ON` — which inverted the whole module and made the
        // application's blinking invisible while still producing countable
        // stage codes, because a blink looks the same in either phase.
        let drive_high = lit != ON;
        let bit = if drive_high { 1 << pin } else { 1 << (pin + 16) };
        wr(GPIOA + 0x18, bit);
    }

    pub fn all_off() {
        for pin in 0..3 {
            set(pin, false);
        }
    }

    /// Blink `count` times on one colour, then pause.
    ///
    /// This is the console. Until USB enumerates there is no other way for
    /// this board to say where it got to, and a port that has no console until
    /// USB works is a loop with no exit — the RP2040 port paid four blind
    /// flash cycles to learn that, and this is the lesson applied rather than
    /// repeated.
    pub fn signal(pin: u32, count: u32) {
        for _ in 0..count {
            set(pin, true);
            spin(1_500_000);
            set(pin, false);
            spin(1_500_000);
        }
        spin(9_000_000);
    }

    /// A crude busy-wait, deliberately not the DWT delay: this runs before the
    /// cycle counter is started and at whatever clock the tree happens to be
    /// at, so it is measured in iterations rather than time.
    pub fn spin(n: u32) {
        for _ in 0..n {
            core::hint::spin_loop();
        }
    }
}

/// Spin until `ready`, or give up.
///
/// The bound is deliberately generous in time and tight in patience: half a
/// million iterations is on the order of 200 ms with the core on HSI, where an
/// oscillator that is going to start has long since started — crystals settle
/// in single-digit milliseconds. The first version of this waited eight
/// million spins, which made each of twelve failing candidates cost about
/// three and a half seconds and turned the whole search into two minutes of
/// looking like a dead board.
fn wait_for(ready: impl Fn() -> bool) -> bool {
    // ~2 seconds with the core on HSI. Generous on purpose: this is now a
    // diagnostic, and "it needed longer than I allowed" must not be one of the
    // answers still on the table.
    for _ in 0..4_000_000u32 {
        if ready() {
            return true;
        }
    }
    false
}

/// Run the core at 216 MHz from HSI, as a last resort.
///
/// USB will not enumerate on this - HSI is four times outside the bit-clock
/// tolerance - but the USB core still has to be *clocked* before it can be
/// reset and initialised at all. So this exists to give the peripheral a
/// 48 MHz input on a board where no crystal starts, which turns a hang into a
/// diagnosable failure.
fn use_hsi_pll() {
    wr(RCC_CFGR, rd(RCC_CFGR) & !0b11);
    while (rd(RCC_CFGR) >> 2) & 0b11 != 0 {}
    wr(RCC_CR, rd(RCC_CR) & !(1 << 24));
    while rd(RCC_CR) & (1 << 25) != 0 {}

    // 16 MHz /8 = 2 MHz, x216 = 432 MHz VCO, /2 = 216 MHz, /9 = 48 MHz.
    wr(RCC_PLLCFGR, 8 | (216 << 6) | (PLL_Q << 24)); // PLLSRC = 0 => HSI
    wr(RCC_CR, rd(RCC_CR) | (1 << 24));
    while rd(RCC_CR) & (1 << 25) == 0 {}
    wr(RCC_CFGR, (rd(RCC_CFGR) & !0b11) | 0b10);
    while (rd(RCC_CFGR) >> 2) & 0b11 != 0b10 {}
}

/// Run the core at 216 MHz from the external clock `plan` describes.
///
/// `bypass` picks how HSE is driven, and it is not a detail. A **crystal**
/// hangs across OSC_IN and OSC_OUT and needs the chip's oscillator amplifier;
/// a **clock input** is a finished square wave fed to OSC_IN alone, and with
/// the amplifier enabled it will never produce `HSERDY`. A board built the
/// second way looks exactly like a board with no HSE at all — which is what
/// this one looked like, until both modes were tried.
///
/// Returns `false` if HSE never becomes ready or the PLL will not lock on it,
/// rather than hanging with nothing to report.
#[derive(Clone, Copy, PartialEq)]
pub enum ClockResult {
    /// Running at 216 MHz from the external clock.
    Ok,
    /// `HSERDY` never asserted: the oscillator is not running at all.
    NoHse,
    /// The oscillator runs, but the PLL would not lock on it — which points at
    /// the divisors, and therefore at the frequency, rather than the crystal.
    NoPll,
    /// The PLL locked and the core would not switch to it.
    NoSwitch,
}

fn use_hse(plan: PllPlan, bypass: bool) -> ClockResult {
    // Hand PH0 and PH1 back to the oscillator before asking it to run.
    //
    // Out of reset those pins are analog, which is what OSC_IN/OSC_OUT need —
    // but this firmware is not reached from reset. It is jumped to by the ROM
    // bootloader, which has been using the chip for its own purposes, and a
    // pin left driven or pulled is a crystal that cannot swing. That would
    // look exactly like the failure seen here: a fitted, healthy crystal whose
    // HSERDY never arrives.
    //
    // Analog mode (0b11) is the reset state and the correct one; the pull
    // registers are cleared for the same reason.
    wr(RCC_AHB1ENR, rd(RCC_AHB1ENR) | (1 << 7)); // GPIOHEN
    let moder = rd(GPIOH_BASE);
    wr(GPIOH_BASE, moder | 0b11 | (0b11 << 2)); // PH0, PH1 analog
    let pupdr = rd(GPIOH_BASE + 0x0C);
    wr(GPIOH_BASE + 0x0C, pupdr & !(0b11 | (0b11 << 2))); // no pull

    // Back to HSI first: the PLL cannot be reconfigured while the core is
    // running from it, and the registers are simply ignored if it is.
    wr(RCC_CFGR, rd(RCC_CFGR) & !0b11); // SW = HSI
    while (rd(RCC_CFGR) >> 2) & 0b11 != 0 {}
    wr(RCC_CR, rd(RCC_CR) & !(1 << 24)); // PLLON = 0
    while rd(RCC_CR) & (1 << 25) != 0 {}

    // HSEBYP can only be changed while HSE is off, so the mode is selected
    // before the oscillator is started, never after.
    wr(RCC_CR, rd(RCC_CR) & !(1 << 16)); // HSEON = 0
    if bypass {
        wr(RCC_CR, rd(RCC_CR) | (1 << 18)); // HSEBYP
    } else {
        wr(RCC_CR, rd(RCC_CR) & !(1 << 18));
    }

    // Start it, but bounded.
    wr(RCC_CR, rd(RCC_CR) | (1 << 16)); // HSEON
    if !wait_for(|| rd(RCC_CR) & (1 << 17) != 0) {
        return ClockResult::NoHse;
    }

    // /M to the PLL's input, xN to a 432 MHz VCO, /P=2 for the core, /Q=9 for
    // exactly 48 MHz. Bit 22 (PLLSRC) selects HSE over HSI.
    wr(
        RCC_PLLCFGR,
        plan.m | (plan.n << 6) | (1 << 22) | (PLL_Q << 24),
    );
    wr(RCC_CR, rd(RCC_CR) | (1 << 24)); // PLLON

    // Bounded, because a candidate that is wrong about the crystal is also
    // wrong about the PLL input: dividing an 8 MHz crystal by 25 asks the PLL
    // for a 320 kHz input, well under its 1 MHz minimum, and it may simply
    // never lock. An unbounded wait there would hang the board on the first
    // wrong guess and take the whole search with it.
    if !wait_for(|| rd(RCC_CR) & (1 << 25) != 0) {
        return ClockResult::NoPll;
    }

    // Switch, then wait for the switch to be acknowledged rather than assuming
    // it took.
    wr(RCC_CFGR, (rd(RCC_CFGR) & !0b11) | 0b10); // SW = PLL
    if !wait_for(|| (rd(RCC_CFGR) >> 2) & 0b11 == 0b10) {
        return ClockResult::NoSwitch;
    }
    ClockResult::Ok
}

// ---------------------------------------------------------------------------
// Host
// ---------------------------------------------------------------------------

/// Everything the interpreter and the RNDP service share.
pub struct FirmwareHost {
    pub board: Stm32F4Board,
    /// The serial console on the `D0`/`D1` header pins. Brought up before USB
    /// and never taken down, because it is the link that still works when USB
    /// does not — which is exactly when a console is worth having.
    pub uart: Option<uart::Uart>,
    /// The module's 32 MB QSPI NOR, once it has identified itself. `None` if
    /// the JEDEC read came back wrong — better no storage than storage that
    /// silently is not there.
    pub flash: Option<qspi::Qspi>,
    /// The ESP32 coprocessor's UART5 link and its reset/boot lines.
    pub esp: Option<uart::Esp32>,
    /// What the clock tree was actually programmed to. `Stm32F4Board` takes
    /// this at construction and keeps it private, and the delay source needs
    /// the same number, so it is recorded once here rather than derived twice.
    pub sysclk_hz: u32,
    /// The external clock the host agreed with, and how it is driven. `None`
    /// while the sweep runs; if it stays `None` the board never enumerated on
    /// any candidate, which is worth reporting rather than inventing a number.
    pub hse_hz: Option<(u32, bool)>,
    pub usb: Option<usb::UsbConsole>,
    pub rndp: rndp::Rndp,
    /// Console output, kept for `rustnet logs`. A ring rather than a stream:
    /// a device that has been running for a week should still answer, and the
    /// oldest lines are the ones worth losing.
    logs: Vec<String>,
    partial: String,
    /// When the last console heartbeat went out.
    last_beat_ms: u64,
    /// Set once a valid RNDP frame arrives on the serial link.
    ///
    /// The wire cannot be a human console and a binary protocol at once: log
    /// lines land in the middle of frames and a tool sees noise. So the link
    /// picks a side the moment it is spoken to — console until something
    /// speaks RNDP at it, protocol thereafter. That is the right default too,
    /// because the console matters most before any tool can connect.
    pub uart_is_protocol: bool,
}

/// How many console lines to keep. Sized for a boot banner plus a few hundred
/// lines of application output.
const LOG_LINES: usize = 200;

impl FirmwareHost {
    fn board_uptime_ms(&mut self) -> u64 {
        self.board.delay().now_us() / 1000
    }

    fn cpu_hz(&mut self) -> u32 {
        self.sysclk_hz
    }

    pub(crate) fn tail_logs(&self, max: usize) -> String {
        let start = self.logs.len().saturating_sub(max);
        self.logs[start..].join("\n")
    }

    /// Answer any pending RNDP traffic.
    ///
    /// Takes the service out of `self` for the call so it can borrow the rest
    /// of the host; the move is a couple of pointers, not an allocation. The
    /// same shape the Pico and K210 ports use.
    pub(crate) fn poll_rndp(&mut self) {
        let mut service = core::mem::take(&mut self.rndp);
        service.poll(self);
        let reboot = service.reboot_requested;
        self.rndp = service;
        if reboot {
            // The reply has gone out by now; resetting before answering leaves
            // the tool waiting for a frame that never comes.
            cortex_m::peripheral::SCB::sys_reset();
        }
    }

    /// Say something on the serial console once a second, forever.
    ///
    /// The banner alone was printed once at boot, which makes reading it a
    /// race: the port has to be open before the board starts, and a capture
    /// that misses the moment sees an empty port and cannot tell that from a
    /// dead one. A heartbeat removes the timing from the question — attach
    /// whenever, and if the wiring is right something arrives within a second.
    fn heartbeat(&mut self) {
        let now = self.board_uptime_ms();
        if now.saturating_sub(self.last_beat_ms) < 1000 {
            return;
        }
        self.last_beat_ms = now;
        let configured = self.usb.as_ref().is_some_and(|u| u.is_configured());
        // Out of the serial port and **not** into the log ring.
        //
        // A line a second fills a two-hundred-line ring in three minutes, and
        // what it evicts first is the boot banner — the part anyone reading
        // `logs` actually wants. The first version of this heartbeat destroyed
        // the very diagnostic it was added to provide.
        let quiet = self.uart_is_protocol;
        let seconds = now / 1000;
        if let Some(u) = self.uart.as_mut().filter(|_| !quiet) {
            let mut line = String::new();
            let _ = write!(line, "[{seconds}s] alive, usb configured: {configured}\r\n");
            u.write(line.as_bytes());
        }
    }

    /// Blink the LED *without* going deaf to the host.
    ///
    /// The raw `led::signal` stops the world for as long as it blinks, which
    /// is fine before USB exists and ruinous afterwards: the host asks for the
    /// device descriptor the moment the pull-up appears, and a few hundred
    /// milliseconds of not answering is an enumeration it abandons. That is
    /// the same rule the RP2040 port paid four blind flash cycles for —
    /// **every wait has to serve the bus** — and this is it being broken
    /// again, one port later, by diagnostics of all things.
    fn blink(&mut self, pin: u32, count: u32) {
        for _ in 0..count {
            led::set(pin, true);
            self.serviced_delay(90);
            led::set(pin, false);
            self.serviced_delay(180);
        }
        self.serviced_delay(500);
    }

    /// Wait, while still serving the bus and the protocol.
    ///
    /// Every wait on this port goes through here. A plain delay is a hole in
    /// the USB schedule, and a hole long enough is an enumeration the host
    /// abandons — the rule the RP2040 port paid four blind flash cycles to
    /// learn, and it is the same silicon-independent truth here.
    fn serviced_delay(&mut self, ms: u64) {
        for _ in 0..ms {
            if let Some(usb) = self.usb.as_mut() {
                usb.service();
            }
            self.poll_rndp();
            self.heartbeat();
            // Abandon the wait if a new application has arrived.
            //
            // An application's own `Sleep.Ms` lands here, and a fuel slice
            // does not end until the instructions are spent — so an app that
            // sleeps a second per loop can hold the interpreter for a minute
            // of wall time, and `rustnet flash` reports success while nothing
            // visibly changes for that whole minute. Cutting the sleep short
            // lets the slice finish promptly and the swap happen when it was
            // asked for. Replacing a running application must never depend on
            // that application's cooperation.
            if self.rndp.pending_app.is_some() {
                return;
            }
            self.board.delay().delay_us(1000);
        }
    }
}

impl core::fmt::Write for FirmwareHost {
    fn write_str(&mut self, s: &str) -> core::fmt::Result {
        // Out of the serial port as it is written, as well as into the ring.
        // A log you can only read by asking for it is no use while the thing
        // being debugged is the asking — and a terminal wants CR before LF.
        let quiet = self.uart_is_protocol;
        if let Some(u) = self.uart.as_mut().filter(|_| !quiet) {
            for byte in s.bytes() {
                if byte == b'\n' {
                    u.put(b'\r');
                }
                u.put(byte);
            }
        }
        for ch in s.chars() {
            if ch == '\n' {
                let line = core::mem::take(&mut self.partial);
                if self.logs.len() == LOG_LINES {
                    self.logs.remove(0);
                }
                self.logs.push(line);
            } else if ch != '\r' {
                self.partial.push(ch);
            }
        }
        Ok(())
    }
}

impl RuntimeHost for FirmwareHost {
    fn console_write(&mut self, text: &str) {
        let _ = self.write_str(text);
    }

    fn now_ms(&mut self) -> u64 {
        self.board_uptime_ms()
    }

    fn sleep_ms(&mut self, ms: u64) {
        self.serviced_delay(ms);
    }

    fn invoke(&mut self, name: &str, args: Vec<HostValue>) -> Result<HostValue, String> {
        let args = args.as_slice();
        match name {
            "RustNet.Hal.Gpio::SetMode(i4,i4)" => {
                let (pin, mode) = (int_arg(args, 0)?, int_arg(args, 1)?);
                let mode = match mode {
                    0 => PinMode::Input,
                    1 => PinMode::Output,
                    2 => PinMode::InputPullUp,
                    3 => PinMode::InputPullDown,
                    _ => return Err(format!("unknown pin mode {mode}")),
                };
                self.board
                    .gpio(pin as u32)
                    .and_then(|p| p.set_mode(mode))
                    .map_err(|e| format!("{e:?}"))?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Gpio::Write(i4,bool)" => {
                let (pin, level) = (int_arg(args, 0)?, int_arg(args, 1)?);
                let level = if level != 0 { Level::High } else { Level::Low };
                self.board
                    .gpio(pin as u32)
                    .and_then(|p| p.write(level))
                    .map_err(|e| format!("{e:?}"))?;
                Ok(HostValue::Void)
            }
            "RustNet.Hal.Gpio::Read(i4)" => {
                let pin = int_arg(args, 0)?;
                let level = self
                    .board
                    .gpio(pin as u32)
                    .and_then(|p| p.read())
                    .map_err(|e| format!("{e:?}"))?;
                Ok(HostValue::Bool(level == Level::High))
            }
            // The demo asks the firmware which pin its LED is on, because that
            // is a board fact and not an application one. This board has no
            // answer yet — see `board::USER_LED`.
            "RustNet.Hal.Gpio::Toggle(i4)" => {
                let pin = int_arg(args, 0)?;
                let level = self
                    .board
                    .gpio(pin as u32)
                    .and_then(|p| p.read())
                    .map_err(|e| format!("{e:?}"))?;
                let flipped = if level == Level::High { Level::Low } else { Level::High };
                self.board
                    .gpio(pin as u32)
                    .and_then(|p| p.write(flipped))
                    .map_err(|e| format!("{e:?}"))?;
                Ok(HostValue::Void)
            }
            // Which pin the user LED is on is a board fact, not an
            // application one, so the demos ask rather than assume. Matched by
            // suffix because each demo declares its own `Board` type in its
            // own namespace — `Blink.Board`, `LanguageTour.Board` — and the
            // question they are asking is identical.
            name if name.ends_with("Board::UserLed()") => match board::USER_LED {
                Some(pin) => Ok(HostValue::I32(pin as i32)),
                None => Err(String::from(
                    "this board's user LED pin is not known yet; see board::USER_LED",
                )),
            },
            other => Err(format!("unknown internal call: {other}")),
        }
    }
}

fn int_arg(args: &[HostValue], i: usize) -> Result<i64, String> {
    match args.get(i) {
        Some(HostValue::I32(v)) => Ok(*v as i64),
        Some(HostValue::I64(v)) => Ok(*v),
        Some(HostValue::Bool(b)) => Ok(*b as i64),
        _ => Err(format!("argument {i} is not an integer")),
    }
}

// ---------------------------------------------------------------------------
// Entry
// ---------------------------------------------------------------------------

#[entry]
fn main() -> ! {
    // Read the clock state the ROM bootloader handed over, *before* anything
    // is touched. This is the measurement that settles the argument.
    //
    // The board reports that HSE never starts, and the schematic plainly shows
    // a 25 MHz crystal on PH0/PH1. Both cannot be true of working hardware —
    // but the bootloader has just been running USB itself, so whatever clock
    // it used demonstrably works on this unit. Asking what that was is one
    // register read, and it decides whether to keep fighting for the crystal
    // or to accept that this part runs from HSI.
    let entry_cr = rd(RCC_CR);
    let entry_hse_on = entry_cr & (1 << 16) != 0;
    let entry_hse_ready = entry_cr & (1 << 17) != 0;

    prepare_for_216mhz();
    led::init();

    // Blue, before anything else, so it cannot be confused with the stage
    // codes that follow:
    //   1  the bootloader was not using HSE at all
    //   2  HSE was switched on but never became ready — a crystal that is
    //      fitted and not oscillating
    //   3  HSE was on and ready: the crystal works, and the fault is mine
    led::signal(
        board::LED_BLUE,
        match (entry_hse_on, entry_hse_ready) {
            (false, _) => 1,
            (true, false) => 2,
            (true, true) => 3,
        },
    );

    // SAFETY: called once, before anything allocates.
    unsafe {
        HEAP.init(core::ptr::addr_of_mut!(HEAP_MEM) as usize, HEAP_SIZE);
    }

    let mut board = Stm32F4Board::new(Clocks::MEADOW_F7);
    board.init();

    let mut host = FirmwareHost {
        board,
        uart: None,
        flash: None,
        esp: None,
        sysclk_hz: Clocks::MEADOW_F7.sysclk_hz,
        hse_hz: None,
        usb: None,
        rndp: rndp::Rndp::new(),
        logs: Vec::new(),
        partial: String::new(),
        last_beat_ms: 0,
        uart_is_protocol: false,
    };

    // The serial console, before anything that might go wrong. It is the one
    // link that keeps working while USB is the thing being debugged, so it
    // comes up first and is never taken down.
    host.uart = Some(uart::Uart::new(Clocks::MEADOW_F7.pclk1_hz));
    let _ = writeln!(host, "");
    let _ = writeln!(host, "--- RustNet on Meadow F7 ---");

    // Stage 1. The schematic says a 25 MHz crystal sits on PH0/PH1, and the
    // board says otherwise: on the first build to carry this LED console it
    // reported failure. One blink could not say *which* failure, so this
    // reports the step that gave way, and tries the other way of driving HSE
    // before giving up.
    //
    //   1 green  crystal, 216 MHz — everything as the schematic describes
    //   2 green  the same, but only as a clock input (HSEBYP)
    //   1 red    HSERDY never asserted in either mode: nothing is oscillating
    //   2 red    the oscillator runs, but the PLL will not lock on it —
    //            which accuses the divisors, and so the frequency, not the part
    //   3 red    the PLL locked and the core refused to switch to it
    let crystal = use_hse(MEADOW_PLL, false);
    let outcome = if crystal == ClockResult::Ok {
        led::signal(board::LED_GREEN, 1);
        crystal
    } else {
        let input = use_hse(MEADOW_PLL, true);
        if input == ClockResult::Ok {
            led::signal(board::LED_GREEN, 2);
            input
        } else {
            use_hsi_pll();
            // Report the crystal attempt: it is the one the schematic predicts,
            // so it is the one whose failure is informative.
            led::signal(
                board::LED_RED,
                match crystal {
                    ClockResult::NoHse => 1,
                    ClockResult::NoPll => 2,
                    _ => 3,
                },
            );
            crystal
        }
    };
    let _ = outcome;

    // USB before anything slow. The host starts asking for descriptors as soon
    // as the pull-up appears, and a device that is busy elsewhere at that
    // moment enumerates as "Device Descriptor Request Failed".
    let ahb_hz = host.cpu_hz();
    host.usb = Some(usb::UsbConsole::new(ahb_hz));
    if let Some(usb) = host.usb.as_mut() {
        usb.force_session_valid();
    }

    // Stage 2: the USB core initialised and the pull-up is presented. From
    // here on every blink goes through `blink`, which keeps polling — see the
    // note there.
    host.blink(board::LED_GREEN, 2);

    // The core's revision, in two groups of green blinks: the nibble at bits
    // 15..12, then the one at 11..8, each offset by one so that zero is still
    // a blink and cannot be mistaken for silence. 0x3000 reads as 4 then 1.
    //
    // This is here because the driver picks its VBUS handling from this number
    // and silently does nothing for a revision it does not know — and "the
    // driver has no branch for this core" deserves to be a fact rather than a
    // suspicion.
    let id = usb::UsbConsole::core_id();
    host.blink(board::LED_GREEN, ((id >> 12) & 0xF) + 1);
    host.blink(board::LED_GREEN, ((id >> 8) & 0xF) + 1);

    let id = chipid::identify();
    let mhz = host.cpu_hz() / 1_000_000;
    let _ = writeln!(
        host,
        "RustNet on {} @ {mhz} MHz, heap {} KB",
        board::NAME,
        HEAP_SIZE / 1024
    );
    let _ = writeln!(host, "hse: {} MHz crystal (X401, ABM12W-25)", board::HSE_HZ / 1_000_000);
    // The QSPI NOR, and its identity rather than an assumption. A board whose
    // flash is absent, miswired or a different part says so here instead of
    // corrupting storage quietly later.
    {
        let mut flash = qspi::Qspi::new();
        match flash.read_id() {
            Ok(jid) if jid == qspi::EXPECTED_ID => {
                let _ = writeln!(
                    host,
                    "qspi: S25FL256L ({:02x}{:02x}{:02x}), {} MB",
                    jid[0], jid[1], jid[2],
                    qspi::CAPACITY / (1024 * 1024)
                );
                host.flash = Some(flash);
            }
            Ok(jid) => {
                let _ = writeln!(
                    host,
                    "[warn] qspi answered {:02x}{:02x}{:02x}, expected {:02x}{:02x}{:02x} - storage off",
                    jid[0], jid[1], jid[2],
                    qspi::EXPECTED_ID[0], qspi::EXPECTED_ID[1], qspi::EXPECTED_ID[2]
                );
            }
            Err(e) => {
                let _ = writeln!(host, "[warn] qspi did not answer ({e:?}) - storage off");
            }
        }
    }

    let _ = writeln!(host, "{}", id.describe());

    // A transparent bridge to the ESP32, when built for it.
    //
    // The coprocessor boots into Wilderness Labs' own firmware, which talks a
    // protocol of theirs over SPI2 and says nothing at all on UART5 — the
    // probe below found two bytes of reset noise and no banner. So using it
    // for WiFi means putting different firmware on it, and the right tool for
    // that is `esptool`, not something hand-rolled here.
    //
    // `esptool` expects a USB-serial chip whose `DTR` and `RTS` lines are
    // wired to the ESP32's `GPIO0` and `EN`. This board has no such chip: the
    // STM32 *is* the USB device. So this mode makes it behave like one —
    // bytes are forwarded both ways verbatim, and the host's control lines are
    // driven onto the reset and boot pins with the polarity `esptool` assumes.
    //
    // Built separately rather than switched at runtime because it is not a
    // RustNet device while it is doing this: it speaks no RNDP, runs no
    // application, and answers no tool. Pretending otherwise would be worse
    // than being a different image.
    //
    //     cargo build --release --features esp-bridge
    //     esptool --port COM17 --chip esp32 write_flash ... AT firmware ...
    #[cfg(feature = "esp-bridge")]
    {
        let mut esp = uart::Esp32::new(Clocks::MEADOW_F7.pclk1_hz);
        let _ = writeln!(host, "[bridge] USB <-> ESP32 on UART5; RNDP is off in this build");

        let mut to_esp = [0u8; 64];
        let mut from_esp = [0u8; 64];
        let (mut last_dtr, mut last_rts) = (false, false);
        loop {
            if let Some(usb) = host.usb.as_mut() {
                usb.service();

                // Control lines first: `esptool` toggles them and then talks
                // immediately, so a late reset loses the sync attempt.
                let (dtr, rts) = (usb.dtr(), usb.rts());
                if dtr != last_dtr || rts != last_rts {
                    // The convention every ESP32 board follows: RTS drives EN,
                    // DTR drives GPIO0, both asserted when the host raises the
                    // signal.
                    esp.set_lines(rts, dtr);
                    last_dtr = dtr;
                    last_rts = rts;
                }

                let n = usb.read(&mut to_esp);
                if n > 0 {
                    esp.uart.write(&to_esp[..n]);
                }
            }
            let n = esp.uart.read(&mut from_esp);
            if n > 0 {
                if let Some(usb) = host.usb.as_mut() {
                    usb.write(&from_esp[..n]);
                }
            }
        }
    }

    #[cfg(not(feature = "esp-bridge"))]
    // Ask the ESP32 what it is running.
    //
    // The module carries an ESP32-PICO-D4 for WiFi and BLE, and **Meadow OS
    // drives it over SPI2 with firmware and a protocol of Wilderness Labs'
    // own** — neither of which is published. Before writing a line of WiFi
    // code it is worth knowing what is actually in that part's flash, and the
    // cheapest way to ask is to reset it and listen: an ESP32's mask ROM
    // announces itself, and application firmware usually says something too.
    //
    // Whatever comes back is printed verbatim rather than parsed. This is a
    // probe, and a probe that interprets is a probe that can be wrong twice.
    {
        let mut esp = uart::Esp32::new(Clocks::MEADOW_F7.pclk1_hz);
        let _ = writeln!(host, "[esp] resetting coprocessor, listening on UART5...");
        {
            let delay = host.board.delay();
            esp.reset(false, delay);
        }
        host.esp = Some(esp);

        // Read from the very first instant, and keep reading faster than the
        // line can fill. A USARTv2 holds one byte in `RDR`; anything not taken
        // before the next arrives is lost to overrun, and an ESP32's ROM says
        // its whole piece in the first hundred milliseconds. The previous
        // version waited before it started and caught exactly one byte of the
        // banner — the last one.
        let mut seen = 0usize;
        let mut chunk = [0u8; 64];
        let mut boot_released = false;
        for tick in 0..8000u32 {
            if !boot_released && tick > 1000 {
                // Long enough for the part to have sampled BOOT.
                if let Some(e) = host.esp.as_mut() {
                    e.release_boot();
                }
                boot_released = true;
            }
            let n = host.esp.as_mut().map(|e| e.uart.read(&mut chunk)).unwrap_or(0);
            if n > 0 {
                seen += n;
                // Byte by byte, printable or escaped. Firmware that is not
                // sending text still says something by its shape, and a lossy
                // decode would hide exactly that.
                for &b in &chunk[..n] {
                    if b == b'\n' || b == b'\r' || (0x20..0x7F).contains(&b) {
                        let _ = write!(host, "{}", b as char);
                    } else {
                        let _ = write!(host, "<{b:02x}>");
                    }
                }
            }
            // A plain delay, not `serviced_delay`: servicing USB in here takes
            // long enough to let bytes slip past.
            host.board.delay().delay_us(250);
        }
        if seen == 0 {
            let _ = writeln!(
                host,
                "[esp] silent at {} baud - either its firmware does not use UART5, or the link is not what the schematic says",
                uart::BAUD
            );
        } else {
            let _ = writeln!(host, "
[esp] {seen} bytes");
        }
    }
    if !id.is_expected() {
        let _ = writeln!(
            host,
            "[warn] this image was built for an STM32F777; the memory map may not fit this part"
        );
    }

    // Give the host its moment to enumerate before the interpreter takes the
    // core, and prove the service loop runs while nothing else does.
    host.serviced_delay(500);

    // Stage 3, and the one that matters: **blue only once a host has actually
    // configured the device**. Every earlier signal says "the firmware got
    // here"; this one says the other end agreed, which is the only claim worth
    // making about USB.
    for _ in 0..40 {
        if host.usb.as_ref().is_some_and(|u| u.is_configured()) {
            host.blink(board::LED_BLUE, 3);
            break;
        }
        host.serviced_delay(100);
    }

    // What runs is whatever was flashed, if anything, and the compiled-in demo
    // otherwise — a board with nothing loaded is a board that looks broken.
    if let Some(flash) = host.flash.as_mut() {
        host.rndp.pub_key = rustnet_flashfs::read(flash, qspi::sys::PUB_KEY).ok();
    }
    if host.rndp.pub_key.is_some() {
        let _ = writeln!(host, "[sec] provisioned");
    }
    let stored = host
        .flash
        .as_mut()
        .and_then(|f| rustnet_flashfs::read(f, qspi::sys::APP).ok());
    host.rndp.app_name = match host
        .flash
        .as_mut()
        .and_then(|f| rustnet_flashfs::read(f, qspi::sys::APP_NAME).ok())
    {
        Some(name) if stored.is_some() => String::from_utf8_lossy(&name).into_owned(),
        _ => String::from(APP_NAME),
    };
    let mut app: Vec<u8> = match stored {
        Some(bytes) => {
            let name = host.rndp.app_name.clone();
            let _ = writeln!(host, "[app] {name} from flash ({} bytes)", bytes.len());
            bytes
        }
        None => APP_RNX.to_vec(),
    };
    host.rndp.app_size = app.len();
    // A flashed app runs on power-up only if it was told to. The compiled-in
    // one always does: it is the demo, and there is nothing to protect.
    let has_flashed = host
        .flash
        .as_mut()
        .map(|f| rustnet_flashfs::exists(f, qspi::sys::APP).unwrap_or(false))
        .unwrap_or(false);
    let autostart = host
        .flash
        .as_mut()
        .map(|f| rustnet_flashfs::exists(f, qspi::sys::AUTOSTART).unwrap_or(false))
        .unwrap_or(false);
    host.rndp.app_running = autostart || !has_flashed;
    if !host.rndp.app_running {
        let _ = writeln!(host, "[app] loaded but not started (no autostart)");
    }

    // Outer loop: `rustnet flash` replaces the running application without a
    // reboot, and the module borrows the bytes, so the borrow has to end
    // before the bytes can be replaced.
    loop {
        let (returned, next) = run_app(&app, host);
        host = returned;
        match next {
            Some(replacement) => {
                let name = host.rndp.app_name.clone();
                let _ = writeln!(host, "[app] switching to {name}");
                app = replacement;
            }
            None => serve_forever(host),
        }
    }
}

/// Run one application until a new one is flashed.
fn run_app(app: &[u8], host: FirmwareHost) -> (FirmwareHost, Option<Vec<u8>>) {
    let mut host = host;

    let module = match Module::from_bytes(app) {
        Ok(m) => m,
        Err(e) => {
            // Only reachable for the compiled-in app: an uploaded one is
            // parsed before it is accepted.
            let _ = writeln!(host, "[app] will not load: {e}");
            serve_forever(host);
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
    loop {
        if let Some(usb) = interp.host.usb.as_mut() {
            usb.service();
        }
        interp.host.poll_rndp();
        interp.host.heartbeat();

        if interp.host.rndp.pending_app.is_some() {
            break;
        }
        if !interp.host.rndp.app_running {
            continue;
        }

        match interp.run(2_000) {
            RunExit::OutOfFuel => {}
            // An application that ends, faults or pauses must not take the
            // firmware with it: stay reachable and report it, so a bad app can
            // be replaced over the wire rather than needing a reflash.
            RunExit::Completed => {
                let _ = writeln!(interp.host, "[app] returned");
                interp.host.rndp.app_running = false;
            }
            RunExit::Paused { method, il_offset } => {
                let _ = writeln!(interp.host, "[app] paused at method {method} il {il_offset}");
                interp.host.rndp.app_running = false;
            }
            RunExit::Error(e) => {
                let _ = writeln!(interp.host, "[app] fault: {e}");
                interp.host.rndp.app_running = false;
            }
        }
    }

    let mut host = interp.host;
    let next = host.rndp.pending_app.take();
    if next.is_some() {
        host.rndp.app_running = true;
    }
    (host, next)
}

/// Keep answering the tools with no application running.
///
/// The one thing this must never do is stop: a device that goes quiet cannot
/// be told what went wrong, and cannot be given a working application.
fn serve_forever(host: FirmwareHost) -> ! {
    let mut host = host;
    loop {
        host.serviced_delay(100);
        host.heartbeat();
    }
}

#[exception]
unsafe fn HardFault(ef: &ExceptionFrame) -> ! {
    // Nothing can be reported from here — the console needs the USB stack that
    // this fault interrupted. Reset instead of spinning, so the board comes
    // back reachable rather than staying dark.
    let _ = ef;
    cortex_m::peripheral::SCB::sys_reset();
}

#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! {
    cortex_m::peripheral::SCB::sys_reset();
}
