//! SPI — the RP2040's two ARM PL022 controllers, as masters.
//!
//! | HAL bus | Controller | Pico pins |
//! |---|---|---|
//! | 0 | SPI0 at `0x4003_C000` | SCK GP18, MOSI GP19, MISO GP16 |
//! | 1 | SPI1 at `0x4004_0000` | SCK GP10, MOSI GP11, MISO GP12 |
//!
//! Chip select is not muxed: the application drives it with `Gpio` (GP17 /
//! GP13 are the conventional pins), which is what `RustNet.Hal.Spi` asks of
//! every port. The PL022's own CSn pulses high between frames in mode 0, which
//! breaks every device that needs CS held across a command and its data.
//!
//! The clock is `clk_peri` divided twice: an even prescaler `CPSDVSR`
//! (2..=254) and a serial clock rate `SCR` (0..=255),
//!
//! ```text
//! f_sck = clk_peri / (CPSDVSR * (1 + SCR))
//! ```
//!
//! and [`dividers`] picks the smallest prescaler that lets the post-divider
//! reach the request, then the smallest post-divider that does not exceed it.
//! Never faster than asked — a device clocked above its rating misreads,
//! while one clocked slower merely takes longer. (The vendor SDK's search
//! agrees at every common rate but can overshoot at the slow end — it gives
//! 10.17 kHz for 10 kHz — which is why it is not copied verbatim.)
//!
//! Modes are the usual CPOL/CPHA pairs, which the PL022 calls SPO and SPH.

use rustnet_hal::spi::{SpiBus, SpiMode};
use rustnet_hal::{HalError, HalResult};

use crate::{reg, resets};

const SSPCR0: usize = 0x00;
const SSPCR1: usize = 0x04;
const SSPDR: usize = 0x08;
const SSPSR: usize = 0x0C;
const SSPCPSR: usize = 0x10;

/// `SSPCR0`: data size minus one in 3:0, frame format 5:4 (0 = Motorola),
/// SPO 6, SPH 7, SCR 15:8.
const CR0_DSS_8BIT: u32 = 7;
const CR0_SPO: u32 = 1 << 6;
const CR0_SPH: u32 = 1 << 7;
const CR0_SCR_SHIFT: u32 = 8;

/// `SSPCR1`: synchronous serial port enable; master mode is MS = 0.
const CR1_SSE: u32 = 1 << 1;

/// `SSPSR`.
const SR_TNF: u32 = 1 << 1;
const SR_RNE: u32 = 1 << 2;
const SR_BSY: u32 = 1 << 4;

/// Both FIFOs are eight frames deep.
const FIFO_DEPTH: usize = 8;

/// Iterations without progress before a transfer is declared stuck.
const SPIN_LIMIT: u32 = 1_000_000;

#[derive(Clone, Copy)]
pub struct SpiDef {
    pub base: usize,
    pub reset: u32,
}

pub const SPI0: SpiDef = SpiDef { base: crate::base::SPI0, reset: resets::SPI0 };
pub const SPI1: SpiDef = SpiDef { base: crate::base::SPI1, reset: resets::SPI1 };

/// The two dividers: `prescale` is `CPSDVSR`, `postdiv` is `SCR + 1`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SpiDividers {
    pub prescale: u32,
    pub postdiv: u32,
}

impl SpiDividers {
    pub fn actual_hz(&self, peri_hz: u32) -> u32 {
        peri_hz / (self.prescale * self.postdiv)
    }
}

/// The two dividers for `baud`, at or below it. `None` below the slowest rate
/// they reach (`clk_peri / (254 * 256)`, 1.92 kHz at 125 MHz).
pub fn dividers(peri_hz: u32, baud: u32) -> Option<SpiDividers> {
    if baud == 0 || peri_hz == 0 {
        return None;
    }
    let f = peri_hz as u64;
    let baud = baud as u64;
    // Smallest even prescaler for which a post-divider of 256 is slow enough.
    let mut prescale = 2u64;
    while prescale * 256 * baud < f {
        prescale += 2;
        if prescale > 254 {
            return None;
        }
    }
    // Smallest post-divider that is not faster than asked; at most 256 by
    // the choice of prescaler, at least 1 when the request exceeds f / 2.
    let postdiv = f.div_ceil(prescale * baud).clamp(1, 256);
    Some(SpiDividers { prescale: prescale as u32, postdiv: postdiv as u32 })
}

/// `SSPCR0` for 8-bit Motorola frames in `mode` at `postdiv`.
pub fn cr0(postdiv: u32, mode: SpiMode) -> u32 {
    let (cpol, cpha) = match mode {
        SpiMode::Mode0 => (false, false),
        SpiMode::Mode1 => (false, true),
        SpiMode::Mode2 => (true, false),
        SpiMode::Mode3 => (true, true),
    };
    ((postdiv - 1) << CR0_SCR_SHIFT)
        | if cpha { CR0_SPH } else { 0 }
        | if cpol { CR0_SPO } else { 0 }
        | CR0_DSS_8BIT
}

pub struct Rp2040Spi {
    def: SpiDef,
    peri_hz: u32,
    configured: bool,
}

impl Rp2040Spi {
    pub const fn new(def: SpiDef, peri_hz: u32) -> Self {
        Self { def, peri_hz, configured: false }
    }

    #[inline]
    fn r(&self, offset: usize) -> u32 {
        reg::read(self.def.base + offset)
    }

    #[inline]
    fn w(&self, offset: usize, value: u32) {
        reg::write(self.def.base + offset, value);
    }
}

impl SpiBus for Rp2040Spi {
    fn configure(&mut self, hz: u32, mode: SpiMode) -> HalResult<()> {
        let d = dividers(self.peri_hz, hz).ok_or(HalError::InvalidArgument(
            "SPI rate not reachable from clk_peri (about 2 kHz and up)",
        ))?;
        if !resets::is_released(self.def.reset) && !resets::unreset(self.def.reset) {
            return Err(HalError::Timeout);
        }
        // Disabled while the format changes; the PL022 latches CR0 on enable.
        self.w(SSPCR1, 0);
        self.w(SSPCPSR, d.prescale);
        self.w(SSPCR0, cr0(d.postdiv, mode));
        self.w(SSPCR1, CR1_SSE);
        self.configured = true;
        Ok(())
    }

    /// Full duplex, with the transmit side kept at most a FIFO ahead of the
    /// receive side — the SDK's loop. Running further ahead overflows the
    /// receive FIFO and the PL022 drops the overflowing frames silently.
    fn transfer(&mut self, tx: &[u8], rx: &mut [u8]) -> HalResult<()> {
        if !self.configured {
            return Err(HalError::InvalidArgument("call Spi.Configure before transferring"));
        }
        if tx.len() != rx.len() {
            return Err(HalError::InvalidArgument("SPI transfer buffers differ in length"));
        }
        // Leftovers from an earlier transfer would be read as this one's.
        while self.r(SSPSR) & SR_RNE != 0 {
            let _ = self.r(SSPDR);
        }
        let (mut sent, mut got, mut idle) = (0usize, 0usize, 0u32);
        while got < rx.len() {
            let mut moved = false;
            if sent < tx.len() && sent - got < FIFO_DEPTH && self.r(SSPSR) & SR_TNF != 0 {
                self.w(SSPDR, tx[sent] as u32);
                sent += 1;
                moved = true;
            }
            if self.r(SSPSR) & SR_RNE != 0 {
                rx[got] = (self.r(SSPDR) & 0xFF) as u8;
                got += 1;
                moved = true;
            }
            if moved {
                idle = 0;
            } else {
                idle += 1;
                if idle == SPIN_LIMIT {
                    return Err(HalError::Timeout);
                }
            }
        }
        // Every frame received means every frame clocked; BSY is a formality,
        // but checking it keeps a caller's CS release after the last edge.
        if reg::wait_until(SPIN_LIMIT, || self.r(SSPSR) & SR_BSY == 0) {
            Ok(())
        } else {
            Err(HalError::Timeout)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn controllers_and_reset_bits() {
        assert_eq!(SPI0.base, 0x4003_C000);
        assert_eq!(SPI1.base, 0x4004_0000);
        assert_eq!(SPI0.reset, 1 << 16);
        assert_eq!(SPI1.reset, 1 << 17);
    }

    /// 1 MHz on 125 MHz: prescale 2, SCR 62 — 992 kHz, the same answer the
    /// SDK gives, and below the request rather than above it.
    #[test]
    fn one_megahertz_matches_the_vendor_sdk() {
        let d = dividers(125_000_000, 1_000_000).expect("reachable");
        assert_eq!(d, SpiDividers { prescale: 2, postdiv: 63 });
        assert_eq!(d.actual_hz(125_000_000), 992_063);
    }

    #[test]
    fn the_fastest_rate_is_half_of_clk_peri() {
        let d = dividers(125_000_000, 62_500_000).expect("reachable");
        assert_eq!(d, SpiDividers { prescale: 2, postdiv: 1 });
        // Asking for more than the controller can do gives its maximum.
        assert_eq!(dividers(125_000_000, 100_000_000), Some(d));
    }

    #[test]
    fn slow_rates_raise_the_prescaler() {
        // 10 kHz: the post-divider alone cannot reach it from prescale 2.
        // 10 kHz is exact: 125 MHz / (50 * 250). (The SDK's search returns
        // 48 * 256 here — 10.17 kHz, faster than asked.)
        let d = dividers(125_000_000, 10_000).expect("reachable");
        assert_eq!(d, SpiDividers { prescale: 50, postdiv: 250 });
        assert_eq!(d.actual_hz(125_000_000), 10_000);
        // The slow end: 2 kHz needs nearly the largest prescaler.
        let d = dividers(125_000_000, 2_000).expect("reachable");
        assert_eq!(d, SpiDividers { prescale: 246, postdiv: 255 });
        assert_eq!(d.actual_hz(125_000_000), 1_992);
    }

    #[test]
    fn the_rate_never_exceeds_the_request() {
        // Every rate from the slowest reachable up: never faster, and never
        // needlessly slow — the next smaller post-divider would overshoot.
        let mut hz = 1_923u32;
        while hz < 70_000_000 {
            let d = dividers(125_000_000, hz).expect("reachable");
            assert_eq!(d.prescale % 2, 0);
            assert!((2..=254).contains(&d.prescale) && (1..=256).contains(&d.postdiv));
            assert!(d.actual_hz(125_000_000) <= hz, "{hz} Hz gave {}", d.actual_hz(125_000_000));
            if d.postdiv > 1 {
                // Exact: clk / (p * (postdiv - 1)) > hz, without rounding.
                let next = d.prescale as u64 * (d.postdiv as u64 - 1) * hz as u64;
                assert!(125_000_000 > next, "{hz} Hz: postdiv {} was not the smallest", d.postdiv);
            }
            hz = hz * 11 / 10 + 1;
        }
    }

    #[test]
    fn too_slow_is_refused() {
        assert_eq!(dividers(125_000_000, 1_000), None);
        assert_eq!(dividers(125_000_000, 0), None);
    }

    #[test]
    fn modes_set_cpol_and_cpha() {
        assert_eq!(cr0(1, SpiMode::Mode0), 0x0007);
        assert_eq!(cr0(1, SpiMode::Mode1), 0x0087);
        assert_eq!(cr0(1, SpiMode::Mode2), 0x0047);
        assert_eq!(cr0(1, SpiMode::Mode3), 0x00C7);
        // SCR is the post-divider minus one, at bits 15:8.
        assert_eq!(cr0(63, SpiMode::Mode0), 0x3E07);
        assert_eq!(cr0(256, SpiMode::Mode0), 0xFF07);
    }
}
