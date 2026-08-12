//! The console, and RNDP, over the board's `D0`/`D1` header pins.
//!
//! ## Why this exists
//!
//! USB is the nicer link and it is not the one to depend on while it is the
//! thing being debugged. A port whose only console is USB cannot say why USB
//! is broken — the RP2040 port called that "a loop with no exit", and this one
//! walked into it too. A serial adapter on two header pins ends it: the board
//! can talk before, during and after any USB trouble.
//!
//! ## Which UART, from the schematic and not from the silkscreen
//!
//! The Meadow's `D0`/`D1` are labelled `COM1` on the board. `COM1` is **UART4**,
//! not USART1 — sheet 1 of `MeadowF7Micro_REVD.pdf` wires `COM1_RX`/`COM1_TX`
//! to `UART4_RX`/`UART4_TX`, and it is `COM2` that carries USART1. Guessing
//! from the name would have put this on the wrong peripheral and the wrong
//! pins, with nothing to show for it.
//!
//! - `UART4_TX` → **PH13** (ball E12), alternate function 8
//! - `UART4_RX` → **PI9**, alternate function 8
//!
//! Both recovered from the schematic by matching net labels to pin labels by
//! position, the same method that placed the LED — and checked the same way,
//! against nets whose answers are already known (`ADC1_IN7` on PA7,
//! `UART4_RX` on PI9, both of which match the part's own alternate-function
//! map).
//!
//! ## The peripheral is not the F4's
//!
//! An F7's USART is the v2 design: status in `ISR`, data split across `RDR`
//! and `TDR`, where the F4 has `SR` and a single `DR`. Driving it through the
//! F4 offsets writes into reserved space and reads status bits that are not
//! there — which is why `rustnet-hal-stm32` refuses `uart()` outright when
//! built for the F7 rather than appearing to work.

use crate::{rd, wr};

/// The two UARTs this board uses, both USARTv2 (RM0410 §34.8).
///
/// UART4 is the human console on `D0`/`D1`; UART5 is wired to the ESP32
/// coprocessor and never leaves the module.
const UART4: usize = 0x4000_4C00;
const UART5: usize = 0x4000_5000;
const CR1: usize = 0x00;
const BRR: usize = 0x0C;
const ISR: usize = 0x1C;
const RDR: usize = 0x24;
const TDR: usize = 0x28;

const CR1_UE: u32 = 1 << 0;
const CR1_RE: u32 = 1 << 2;
const CR1_TE: u32 = 1 << 3;

const ICR: usize = 0x20;
const ISR_ORE: u32 = 1 << 3;
const ISR_RXNE: u32 = 1 << 5;
const ISR_TXE: u32 = 1 << 7;
/// Writing `ORECF` is the only way to clear an overrun on a USARTv2. Reading
/// `RDR` does not do it, and the flag left standing is not cosmetic — see
/// [`Uart::read`].
const ICR_ORECF: u32 = 1 << 3;

const RCC_BASE: usize = 0x4002_3800;
const RCC_AHB1ENR: usize = RCC_BASE + 0x30;
const RCC_APB1ENR: usize = RCC_BASE + 0x40;
/// UART4 sits at bit 19 of APB1ENR, UART5 at bit 20.
const APB1_UART4EN: u32 = 1 << 19;
const APB1_UART5EN: u32 = 1 << 20;

const GPIOH: usize = 0x4002_1C00;
const GPIOI: usize = 0x4002_2000;
const MODER: usize = 0x00;
const AFRL: usize = 0x20;
const AFRH: usize = 0x24;

/// The console speed every other RustNet target uses, so the tools need no
/// special case.
pub const BAUD: u32 = 115_200;

pub struct Uart {
    base: usize,
}

impl Uart {
    /// Bring UART4 up on PH13/PI9 at [`BAUD`].
    ///
    /// `pclk1_hz` is the APB1 clock the peripheral is actually fed — 54 MHz
    /// with the core at 216 MHz. Passing what the clock tree *was asked for*
    /// rather than what it settled on is how baud rates end up subtly wrong,
    /// so the caller hands over the figure it programmed.
    pub fn new(pclk1_hz: u32) -> Self {
        // Port H for TX, port I for RX.
        wr(RCC_AHB1ENR, rd(RCC_AHB1ENR) | (1 << 7) | (1 << 8));
        wr(RCC_APB1ENR, rd(RCC_APB1ENR) | APB1_UART4EN);

        // PH13 to alternate function 8.
        let moder = rd(GPIOH + MODER);
        wr(GPIOH + MODER, (moder & !(0b11 << 26)) | (0b10 << 26));
        let afrh = rd(GPIOH + AFRH);
        // AFRH covers pins 8..15; pin 13 is at bits 20..23.
        wr(GPIOH + AFRH, (afrh & !(0xF << 20)) | (8 << 20));

        // PI9 to alternate function 8.
        let moder = rd(GPIOI + MODER);
        wr(GPIOI + MODER, (moder & !(0b11 << 18)) | (0b10 << 18));
        let afrh = rd(GPIOI + AFRH);
        // Pin 9 is at bits 4..7 of AFRH.
        wr(GPIOI + AFRH, (afrh & !(0xF << 4)) | (8 << 4));
        let _ = AFRL;

        // The peripheral must be disabled while it is configured; most of its
        // control bits are ignored while `UE` is set.
        wr(UART4 + CR1, 0);
        // Oversampling by 16, which is the reset default, makes the divisor
        // simply the clock over the baud rate. Rounded rather than truncated:
        // at 54 MHz and 115200 the exact figure is 468.75, and truncating
        // costs 0.16% of the bit clock for no reason.
        wr(UART4 + BRR, (pclk1_hz + BAUD / 2) / BAUD);
        wr(UART4 + CR1, CR1_UE | CR1_TE | CR1_RE);

        Uart { base: UART4 }
    }

    /// Send one byte, waiting for room in the transmit register.
    ///
    /// Bounded, because a console that can hang the firmware is worse than a
    /// console that drops a line — and an unbounded wait here would do exactly
    /// that if the peripheral were misconfigured.
    pub fn put(&mut self, byte: u8) {
        for _ in 0..200_000u32 {
            if rd(self.base + ISR) & ISR_TXE != 0 {
                wr(self.base + TDR, byte as u32);
                return;
            }
        }
    }

    pub fn write(&mut self, bytes: &[u8]) {
        for &b in bytes {
            self.put(b);
        }
    }

    /// Take whatever has arrived, without waiting for anything.
    ///
    /// Overruns are cleared as they are noticed. There is no receive FIFO on
    /// this peripheral — one byte in `RDR` and one in the shift register — so
    /// at 115200 baud a caller that looks away for a millisecond loses about
    /// eleven bytes, and an ESP32 answering `AT+CWLAP` sends thousands. The
    /// loss is unavoidable; leaving `ORE` standing afterwards is not, and it
    /// is the more expensive half: an uncleared overrun means every later
    /// read is read against a stale error state rather than a clean line.
    pub fn read(&mut self, buf: &mut [u8]) -> usize {
        let mut n = 0;
        while n < buf.len() {
            let isr = rd(self.base + ISR);
            if isr & ISR_ORE != 0 {
                wr(self.base + ICR, ICR_ORECF);
            }
            if isr & ISR_RXNE == 0 {
                break;
            }
            buf[n] = (rd(self.base + RDR) & 0xFF) as u8;
            n += 1;
        }
        n
    }
}

/// The ESP32-PICO-D4 coprocessor's link and its control lines.
///
/// The module carries an ESP32 for WiFi and BLE, wired to the STM32 two ways:
/// SPI2 — which is how **Meadow OS** talks to it, using firmware and a
/// protocol of Wilderness Labs' own — and UART5, which is the one anything
/// else can use.
///
/// From schematic sheet 2, by net → BGA ball → pin, with the column rule
/// checked against nets whose answers are already known (`I2C1_SCL` on PB6,
/// `FMC_D5` on PE8):
///
/// | Net | Ball | Pin |
/// |---|---|---|
/// | `UART5_ESP32` (STM32 → ESP32) | P13 | PB13, `UART5_TX`, AF8 |
/// | `ESP32_UART5` (ESP32 → STM32) | D12 | PD2, `UART5_RX`, AF8 |
/// | `ESP32_RESET_L` | K1 | PF7 |
/// | `ESP32_BOOT_L` | E3 | PI10 |
///
/// That PB13 and PD2 are a *matched* UART5 pair on this part is the strongest
/// check available: a wrong derivation would be very unlikely to land on both
/// halves of one peripheral.
pub struct Esp32 {
    pub uart: Uart,
}

const GPIOF: usize = 0x4002_1400;
/// `ESP32_RESET_L` on PF7 and `ESP32_BOOT_L` on PI10 — both active low, as
/// their `_L` suffixes say.
const RESET_PIN: u32 = 7;
const BOOT_PIN: u32 = 10;

impl Esp32 {
    pub fn new(pclk1_hz: u32) -> Self {
        // Ports B, D, F and I.
        wr(RCC_AHB1ENR, rd(RCC_AHB1ENR) | (1 << 1) | (1 << 3) | (1 << 5) | (1 << 8));
        wr(RCC_APB1ENR, rd(RCC_APB1ENR) | APB1_UART5EN);

        // PB13 -> UART5_TX, PD2 -> UART5_RX, both AF8.
        Self::mux(0x4002_0400, 13, 8);
        Self::mux(0x4002_0C00, 2, 8);

        // The control lines are ordinary outputs, and both are released
        // (driven high) to start: holding BOOT low is what puts the ESP32 into
        // its own ROM loader, and that is a request, not a default.
        Self::output(GPIOF, RESET_PIN, true);
        Self::output(GPIOI, BOOT_PIN, true);

        wr(UART5 + CR1, 0);
        wr(UART5 + BRR, (pclk1_hz + BAUD / 2) / BAUD);
        wr(UART5 + CR1, CR1_UE | CR1_TE | CR1_RE);

        Esp32 { uart: Uart { base: UART5 } }
    }

    fn mux(base: usize, pin: u32, af: u32) {
        let sh = pin * 2;
        let moder = rd(base + MODER);
        wr(base + MODER, (moder & !(0b11 << sh)) | (0b10 << sh));
        let (reg, shift) = if pin < 8 { (base + AFRL, pin * 4) } else { (base + AFRH, (pin - 8) * 4) };
        let v = rd(reg);
        wr(reg, (v & !(0xF << shift)) | (af << shift));
    }

    fn output(base: usize, pin: u32, high: bool) {
        let sh = pin * 2;
        let moder = rd(base + MODER);
        wr(base + MODER, (moder & !(0b11 << sh)) | (0b01 << sh));
        Self::write_pin(base, pin, high);
    }

    fn write_pin(base: usize, pin: u32, high: bool) {
        // BSRR: low half sets, high half clears.
        wr(base + 0x18, if high { 1 << pin } else { 1 << (pin + 16) });
    }

    /// Hold the ESP32 in reset, release it, and let it start.
    ///
    /// `into_loader` holds `BOOT` low across the release, which is what asks
    /// the ESP32's mask ROM for its serial loader instead of the application
    /// in its flash — the same gesture as holding BOOT on the STM32 itself.
    /// Returns with the ESP32 just out of reset and **nothing yet read**.
    ///
    /// The caller must start draining immediately. A USARTv2 holds one byte in
    /// `RDR`, so anything not taken before the next arrives is lost to
    /// overrun — and an ESP32's ROM says its whole piece within the first
    /// hundred milliseconds. An earlier version waited 100 ms here before
    /// handing back, and caught exactly one byte of the banner: the last one.
    ///
    /// `BOOT` is therefore set before the reset is released, which is when the
    /// ESP32 samples it, and left alone afterwards — holding it low across the
    /// release is what asks the mask ROM for its serial loader.
    pub fn reset(&mut self, into_loader: bool, delay: &mut dyn rustnet_hal::delay::Delay) {
        Self::write_pin(GPIOI, BOOT_PIN, !into_loader);
        Self::write_pin(GPIOF, RESET_PIN, false);
        delay.delay_us(20_000);
        // Drain anything the line collected while the part was held down, so
        // the first byte read afterwards is genuinely the first byte sent.
        let mut sink = [0u8; 64];
        while self.uart.read(&mut sink) > 0 {}
        Self::write_pin(GPIOF, RESET_PIN, true);
    }

    /// Drive `EN` (reset) and `GPIO0` (boot) directly.
    ///
    /// Both nets are active low, so `true` here means *asserted* — held down —
    /// which is the opposite of the pin level.
    ///
    /// The three methods below belong to the bridge build, where `esptool`
    /// drives them from the host's control lines. Kept in the normal build so
    /// there is one ESP32 driver rather than two that can drift apart.
    #[cfg_attr(not(feature = "esp-bridge"), allow(dead_code))]
    pub fn set_lines(&mut self, reset_asserted: bool, boot_asserted: bool) {
        Self::write_pin(GPIOF, RESET_PIN, !reset_asserted);
        Self::write_pin(GPIOI, BOOT_PIN, !boot_asserted);
    }

    /// Let go of `BOOT` once the part has had time to sample it.
    #[cfg_attr(not(feature = "esp-bridge"), allow(dead_code))]
    pub fn release_boot(&mut self) {
        Self::write_pin(GPIOI, BOOT_PIN, true);
    }

    /// Set the link speed. The ESP32's ROM talks at 115200 with the 40 MHz
    /// crystal a PICO-D4 carries, but application firmware chooses its own.
    #[cfg_attr(not(feature = "esp-bridge"), allow(dead_code))]
    pub fn set_baud(&mut self, pclk1_hz: u32, baud: u32) {
        wr(UART5 + CR1, 0);
        wr(UART5 + BRR, (pclk1_hz + baud / 2) / baud);
        wr(UART5 + CR1, CR1_UE | CR1_TE | CR1_RE);
    }
}
