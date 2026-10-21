//! PWM — eight slices, two outputs each, every GPIO reachable.
//!
//! The RP2040's PWM is not a pool of channels to allocate: the pin decides.
//! GPIO *n* is always slice `(n >> 1) & 7`, output A when *n* is even and B
//! when it is odd. So the HAL channel number **is the GPIO number** — channel
//! 25 is the Pico's LED — and the table below is arithmetic, not a choice.
//!
//! **Two pins share a slice, and a slice has one frequency.** GP24 and GP25
//! are both slice 4; configuring one at 1 kHz and the other at 50 Hz leaves
//! both at 50 Hz with their own duty cycles. That is the silicon, not a driver
//! limit, and it is documented rather than hidden.
//!
//! ## The arithmetic
//!
//! A slice counts `clk_sys` divided by `DIV` (8.4 fixed point, 1.0 ..< 256.0)
//! from 0 to `TOP` and wraps, so
//!
//! ```text
//! f = clk_sys / (DIV * (TOP + 1))
//! ```
//!
//! For the finest duty resolution `TOP` should be as large as possible, so
//! the divider is the *smallest* that brings `TOP + 1` under the limit, and
//! `TOP` then takes up the rest. The output is high while the counter is below
//! the compare level, so `CC = (TOP + 1) * duty` and `CC = TOP + 1` is a
//! steady 100% — which is why `TOP` stops at 65534: `TOP + 1` has to fit the
//! 16-bit compare field, or full duty is unreachable.

use rustnet_hal::pwm::PwmChannel;
use rustnet_hal::{HalError, HalResult};

use crate::gpio::{self, func, PIN_COUNT};
use crate::{reg, resets};

const SLICE_STRIDE: usize = 0x14;
const CSR: usize = 0x00;
const DIV: usize = 0x04;
const CTR: usize = 0x08;
const CC: usize = 0x0C;
const TOP: usize = 0x10;

/// `CHn_CSR`: enable; everything else (phase-correct, inversion, gated
/// divider modes) left at zero — free-running, trailing-edge.
const CSR_EN: u32 = 1 << 0;

/// Largest `TOP` this driver uses. One below the field's maximum so that
/// `TOP + 1`, the compare value for 100% duty, still fits in 16 bits.
pub const MAX_TOP: u32 = 0xFFFE;

/// `DIV` in sixteenths: `INT` (8 bits, at 11:4) and `FRAC` (4 bits, at 3:0)
/// together are exactly the register value. 1.0 is the smallest legal
/// divider; `INT = 0` would mean 256, which this never asks for.
const DIV16_MIN: u32 = 16;
const DIV16_MAX: u32 = 0xFFF;

/// Duty is passed in hundredths of a percent, the `rustnet_hal` convention.
pub const DUTY_FULL: u32 = 10_000;

pub const fn slice_of(gpio: u32) -> u32 {
    (gpio >> 1) & 7
}

/// Output B is on odd pins.
pub const fn is_b(gpio: u32) -> bool {
    gpio & 1 == 1
}

fn slice_reg(slice: u32, offset: usize) -> usize {
    crate::base::PWM + slice as usize * SLICE_STRIDE + offset
}

/// One slice's divider and wrap, as the registers want them.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PwmTiming {
    /// The `DIV` register: the divider in sixteenths.
    pub div16: u32,
    pub top: u32,
}

impl PwmTiming {
    /// The frequency these settings really produce, in millihertz — the
    /// requested one rounded through two integer registers.
    pub fn actual_mhz(&self, sys_hz: u32) -> u64 {
        sys_hz as u64 * 16 * 1000 / (self.div16 as u64 * (self.top as u64 + 1))
    }

    /// The compare level for `duty` (0..=10000). Truncating, so a duty never
    /// rounds up into "always on" until it is asked for.
    pub fn compare(&self, duty: u16) -> u32 {
        let duty = (duty as u32).min(DUTY_FULL);
        (self.top + 1) * duty / DUTY_FULL
    }
}

/// Divider and wrap for `hz` on a `sys_hz` clock, or `None` if the slice
/// cannot produce it.
///
/// The slowest reachable rate is `sys / (255.9375 * 65535)` — 7.45 Hz at 125
/// MHz. The fastest is `sys / 2`, which leaves a duty resolution of exactly
/// on, off, or half; anything faster has no second compare level at all and
/// is refused rather than delivered as a constant.
pub fn timing(sys_hz: u32, hz: u32) -> Option<PwmTiming> {
    if sys_hz == 0 || hz == 0 {
        return None;
    }
    let ticks16 = sys_hz as u64 * 16; // clk_sys in sixteenths of a divider step
    let span = hz as u64 * (MAX_TOP as u64 + 1);
    let div16 = ticks16.div_ceil(span).max(DIV16_MIN as u64);
    if div16 > DIV16_MAX as u64 {
        return None;
    }
    let per_period = div16 * hz as u64;
    let top_plus_1 = ((ticks16 + per_period / 2) / per_period).min(MAX_TOP as u64 + 1);
    if top_plus_1 < 2 {
        return None;
    }
    Some(PwmTiming { div16: div16 as u32, top: (top_plus_1 - 1) as u32 })
}

/// `CC` with one half replaced: A is the low 16 bits, B the high.
pub const fn cc_with(cc: u32, b: bool, level: u32) -> u32 {
    let level = level & 0xFFFF;
    if b {
        (cc & 0x0000_FFFF) | (level << 16)
    } else {
        (cc & 0xFFFF_0000) | level
    }
}

/// One PWM output, named by its GPIO.
pub struct Rp2040Pwm {
    gpio: u32,
    sys_hz: u32,
    timing: Option<PwmTiming>,
    duty: u16,
}

impl Rp2040Pwm {
    pub const fn new(gpio: u32, sys_hz: u32) -> Self {
        Self { gpio, sys_hz, timing: None, duty: 0 }
    }

    fn slice(&self) -> u32 {
        slice_of(self.gpio)
    }

    fn ensure_released(&self) -> HalResult<()> {
        if resets::is_released(resets::PWM) || resets::unreset(resets::PWM) {
            Ok(())
        } else {
            Err(HalError::Timeout)
        }
    }

    fn write_compare(&self) {
        if let Some(t) = self.timing {
            let addr = slice_reg(self.slice(), CC);
            // A plain read-modify-write: the other half belongs to the sibling
            // pin, and the alias pair (clear, then set) would leave this
            // output at zero for a moment in between — a visible glitch on a
            // dimmed LED and a real one on a servo.
            let cc = reg::read(addr);
            reg::write(addr, cc_with(cc, is_b(self.gpio), t.compare(self.duty)));
        }
    }
}

impl PwmChannel for Rp2040Pwm {
    fn set_frequency(&mut self, hz: u32) -> HalResult<()> {
        if self.gpio >= PIN_COUNT {
            return Err(HalError::InvalidArgument("PWM channel is a GPIO number, 0..=29"));
        }
        let t = timing(self.sys_hz, hz).ok_or(HalError::InvalidArgument(
            "PWM frequency out of range (about 8 Hz up to clk_sys / 2)",
        ))?;
        self.ensure_released()?;
        let slice = self.slice();
        // Keep the enable bit, clear every mode bit: free-running,
        // trailing-edge, no inversion.
        let csr = reg::read(slice_reg(slice, CSR)) & CSR_EN;
        reg::write(slice_reg(slice, CSR), csr);
        reg::write(slice_reg(slice, DIV), t.div16);
        reg::write(slice_reg(slice, TOP), t.top);
        // Restart the count. A counter already past the new TOP would run on to
        // 0xFFFF before wrapping — one long, wrong period.
        reg::write(slice_reg(slice, CTR), 0);
        self.timing = Some(t);
        self.write_compare();
        Ok(())
    }

    fn set_duty(&mut self, duty_permyriad: u16) -> HalResult<()> {
        if duty_permyriad as u32 > DUTY_FULL {
            return Err(HalError::InvalidArgument("PWM duty is 0..=10000"));
        }
        self.duty = duty_permyriad;
        self.write_compare();
        Ok(())
    }

    fn enable(&mut self) -> HalResult<()> {
        if self.timing.is_none() {
            return Err(HalError::InvalidArgument("set a PWM frequency before enabling"));
        }
        self.ensure_released()?;
        gpio::set_function(self.gpio, func::PWM);
        reg::set_bits(slice_reg(self.slice(), CSR), CSR_EN);
        Ok(())
    }

    /// Hold this output low. The slice keeps running, because its other
    /// output belongs to another pin that may still be in use.
    fn disable(&mut self) -> HalResult<()> {
        self.duty = 0;
        self.write_compare();
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn gpio_to_slice_and_output() {
        assert_eq!((slice_of(0), is_b(0)), (0, false));
        assert_eq!((slice_of(15), is_b(15)), (7, true));
        // GPIO16 wraps round to slice 0 again.
        assert_eq!((slice_of(16), is_b(16)), (0, false));
        // The Pico's LED is slice 4, output B.
        assert_eq!((slice_of(25), is_b(25)), (4, true));
    }

    #[test]
    fn slice_registers_sit_at_a_0x14_stride() {
        assert_eq!(slice_reg(0, CSR), 0x4005_0000);
        assert_eq!(slice_reg(4, CC), 0x4005_005C);
        // Slice 7's TOP is the last slice register; the global EN follows at 0xA0.
        assert_eq!(slice_reg(7, TOP), 0x4005_009C);
    }

    /// 1 kHz at 125 MHz: divider 1.9375 (INT 1, FRAC 15), TOP 64515 —
    /// 1000.002 Hz with almost the full 16 bits of duty resolution.
    #[test]
    fn one_kilohertz_on_the_stock_clock() {
        let t = timing(125_000_000, 1_000).expect("reachable");
        assert_eq!(t.div16, 31);
        assert_eq!((t.div16 >> 4, t.div16 & 0xF), (1, 15));
        assert_eq!(t.top, 64_515);
        assert_eq!(t.actual_mhz(125_000_000), 1_000_002);
    }

    /// The servo rate: 50 Hz needs a divider of 38.1875.
    #[test]
    fn fifty_hertz_for_a_servo() {
        let t = timing(125_000_000, 50).expect("reachable");
        assert_eq!(t.div16, 611);
        assert_eq!(t.top, 65_465);
        let mhz = t.actual_mhz(125_000_000);
        assert!((49_990..=50_010).contains(&mhz), "{mhz} mHz");
    }

    /// Fast rates use divider 1.0 and give up resolution, not accuracy.
    #[test]
    fn fast_rates_keep_the_divider_at_one() {
        let t = timing(125_000_000, 1_000_000).expect("reachable");
        assert_eq!(t.div16, 16);
        assert_eq!(t.top, 124);
        assert_eq!(t.actual_mhz(125_000_000), 1_000_000_000);
        // clk_sys / 2 is the last rate with two distinct levels.
        assert_eq!(timing(125_000_000, 62_500_000).map(|t| t.top), Some(1));
        assert_eq!(timing(125_000_000, 100_000_000), None);
    }

    #[test]
    fn rates_below_the_dividers_reach_are_refused() {
        assert!(timing(125_000_000, 8).is_some());
        assert_eq!(timing(125_000_000, 7), None);
        assert_eq!(timing(125_000_000, 0), None);
        assert_eq!(timing(0, 1_000), None);
    }

    /// Whatever the request, the registers stay in their fields.
    #[test]
    fn every_timing_fits_its_registers() {
        for hz in [8u32, 10, 50, 333, 1_000, 20_000, 440_000, 10_000_000, 62_500_000] {
            let t = timing(125_000_000, hz).expect("reachable");
            assert!((DIV16_MIN..=DIV16_MAX).contains(&t.div16), "{hz} Hz div {}", t.div16);
            assert!(t.top >= 1 && t.top <= MAX_TOP, "{hz} Hz top {}", t.top);
        }
        // Up to the audio range the rate is within 0.1% — rounding, not a
        // wrong formula. (Above it, TOP is small and the steps are coarse:
        // 10 MHz comes out as 9.6 MHz, which is the silicon's nearest.)
        for hz in [8u32, 10, 50, 333, 1_000, 20_000, 440_000] {
            let t = timing(125_000_000, hz).expect("reachable");
            let got = t.actual_mhz(125_000_000) as i64;
            let want = hz as i64 * 1000;
            assert!((got - want).abs() * 1000 <= want, "{hz} Hz gave {got} mHz");
        }
    }

    #[test]
    fn duty_maps_onto_the_compare_level() {
        let t = PwmTiming { div16: 31, top: 64_515 };
        assert_eq!(t.compare(0), 0);
        assert_eq!(t.compare(5_000), 32_258);
        // Full duty is TOP + 1: the counter never reaches it, so always high.
        assert_eq!(t.compare(10_000), 64_516);
        // Out-of-range duty saturates rather than wrapping into a low level.
        assert_eq!(t.compare(u16::MAX), 64_516);
        // At the largest TOP, full duty still fits the 16-bit field.
        let widest = PwmTiming { div16: 16, top: MAX_TOP };
        assert_eq!(widest.compare(10_000), 0xFFFF);
    }

    #[test]
    fn compare_halves_do_not_disturb_each_other() {
        assert_eq!(cc_with(0xAAAA_BBBB, false, 0x1234), 0xAAAA_1234);
        assert_eq!(cc_with(0xAAAA_BBBB, true, 0x1234), 0x1234_BBBB);
        assert_eq!(cc_with(0, true, 0x1_FFFF), 0xFFFF_0000);
    }
}
