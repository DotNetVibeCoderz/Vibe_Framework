//! The RESETS block: every peripheral starts held, and stays held until asked.
//!
//! This is the first thing that catches anyone coming to the RP2040 from
//! another Cortex-M part. There is no clock-enable register to forget — there
//! is a *reset* register, every peripheral is in it at power-up, and a write
//! to a held peripheral is discarded with no fault and no flag. A UART
//! configured before it is released reads back as zeros and transmits nothing,
//! which looks exactly like a wrong pin assignment.

use crate::base::RESETS;
use crate::reg;

const RESET: usize = RESETS + 0x00;
const RESET_DONE: usize = RESETS + 0x08;

/// Bits in `RESET`, from the datasheet (2.14.3). Only the ones this HAL
/// releases are named.
pub const ADC: u32 = 1 << 0;
pub const I2C0: u32 = 1 << 3;
pub const I2C1: u32 = 1 << 4;
pub const IO_BANK0: u32 = 1 << 5;
pub const PADS_BANK0: u32 = 1 << 8;
pub const PIO0: u32 = 1 << 10;
pub const PIO1: u32 = 1 << 11;
pub const PLL_SYS: u32 = 1 << 12;
pub const PLL_USB: u32 = 1 << 13;
pub const PWM: u32 = 1 << 14;
pub const SPI0: u32 = 1 << 16;
pub const SPI1: u32 = 1 << 17;
pub const TIMER: u32 = 1 << 21;
pub const UART0: u32 = 1 << 22;
pub const UART1: u32 = 1 << 23;

/// How long to wait for a peripheral to acknowledge coming out of reset.
/// Generous: this is a handful of clock cycles in practice, and the cost of
/// being wrong is a hang.
const SPIN_LIMIT: u32 = 1_000_000;

/// Release `mask` from reset and wait for the hardware to confirm it.
///
/// The acknowledge matters. Releasing reset and configuring immediately works
/// most of the time and fails when the peripheral has not caught up, which
/// produces a driver that is intermittently dead — the worst kind to chase.
pub fn unreset(mask: u32) -> bool {
    reg::clear_bits(RESET, mask);
    reg::wait_until(SPIN_LIMIT, || reg::read(RESET_DONE) & mask == mask)
}

/// Put `mask` back into reset.
pub fn hold(mask: u32) {
    reg::set_bits(RESET, mask);
}

/// Whether `mask` is out of reset already. Used to release a block once
/// rather than on every call: releasing is idempotent, but waiting for the
/// acknowledge on every ADC sample or PWM update is wasted bus traffic.
pub fn is_released(mask: u32) -> bool {
    reg::read(RESET) & mask == 0 && reg::read(RESET_DONE) & mask == mask
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The bit positions, straight from the datasheet's RESETS table (2.14.3).
    /// A wrong bit here releases some other peripheral and leaves this one
    /// held — which reads as a driver that configures and does nothing.
    #[test]
    fn reset_bits_match_the_datasheet() {
        assert_eq!(ADC, 1 << 0);
        assert_eq!(I2C0, 1 << 3);
        assert_eq!(I2C1, 1 << 4);
        assert_eq!(IO_BANK0, 1 << 5);
        assert_eq!(PADS_BANK0, 1 << 8);
        assert_eq!(PIO0, 1 << 10);
        assert_eq!(PIO1, 1 << 11);
        assert_eq!(PWM, 1 << 14);
        assert_eq!(SPI0, 1 << 16);
        assert_eq!(SPI1, 1 << 17);
        assert_eq!(TIMER, 1 << 21);
        assert_eq!(UART0, 1 << 22);
        assert_eq!(UART1, 1 << 23);
    }
}
