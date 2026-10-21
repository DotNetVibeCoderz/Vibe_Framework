//! GPIO — three blocks for one pin.
//!
//! A pin on this chip is configured in three places and all three have to
//! agree, which is the usual way an RP2040 GPIO ends up doing nothing:
//!
//! - **IO_BANK0** picks which peripheral drives the pin. `SIO` (function 5) is
//!   the one that makes it a plain GPIO.
//! - **PADS_BANK0** is the analogue pad: input enable, output disable, pulls.
//!   A pad whose input enable is off reads zero forever no matter what is on
//!   the wire.
//! - **SIO** is the register the core actually writes. It sits on the
//!   single-cycle bus rather than the APB, so it has its own `SET`/`CLR`/`XOR`
//!   registers instead of the `+0x2000` aliases every other peripheral uses.

use alloc::boxed::Box;
use rustnet_hal::gpio::{Edge, GpioPin, Level, PinMode};
use rustnet_hal::{HalError, HalResult};

use crate::base::{IO_BANK0, PADS_BANK0, SIO};
use crate::{reg, resets};

/// The RP2040 brings out 30 GPIOs; a Pico exposes 0..=28.
pub const PIN_COUNT: u32 = 30;

const SIO_GPIO_IN: usize = SIO + 0x04;
const SIO_GPIO_OUT_SET: usize = SIO + 0x14;
const SIO_GPIO_OUT_CLR: usize = SIO + 0x18;
const SIO_GPIO_OUT_XOR: usize = SIO + 0x1C;
const SIO_GPIO_OE_SET: usize = SIO + 0x24;
const SIO_GPIO_OE_CLR: usize = SIO + 0x28;

/// `PADS_BANK0` per-pin control.
const PAD_OD: u32 = 1 << 7;
const PAD_IE: u32 = 1 << 6;
const PAD_PUE: u32 = 1 << 3;
const PAD_PDE: u32 = 1 << 2;

/// `IO_BANK0` function select values (datasheet 2.19.2, the F1..F9 columns).
/// The function *number* is the same on every pin; which instance and which
/// signal it carries depends on the pin — see [`crate::pins`].
pub mod func {
    pub const SPI: u32 = 1;
    pub const UART: u32 = 2;
    pub const I2C: u32 = 3;
    pub const PWM: u32 = 4;
    /// The core's own GPIO.
    pub const SIO: u32 = 5;
    pub const PIO0: u32 = 6;
    pub const PIO1: u32 = 7;
    /// Nothing drives the pin. What an analogue input wants.
    pub const NULL: u32 = 0x1F;
}

const FUNC_SIO: u32 = func::SIO;

/// `PADS_BANK0.GPIOn`: one word per pin, after the `VOLTAGE_SELECT` word.
pub(crate) fn pad_ctrl(pin: u32) -> usize {
    PADS_BANK0 + 0x04 + 4 * pin as usize
}

/// `IO_BANK0.GPIOn_CTRL`: the second word of each pin's STATUS/CTRL pair.
pub(crate) fn io_ctrl(pin: u32) -> usize {
    IO_BANK0 + 0x04 + 8 * pin as usize
}

/// The pin's input level as SIO sees it — valid whichever peripheral the pin
/// is routed to, because inputs fan out to every function at once.
pub fn level(pin: u32) -> bool {
    pin < PIN_COUNT && reg::read(SIO_GPIO_IN) & (1 << pin) != 0
}

/// Set the pad's pull resistors. Both may be off; asking for both is refused
/// by turning both off, since the pad would otherwise sit at mid-rail as a
/// resistive divider — a level every input reads differently.
pub fn set_pulls(pin: u32, up: bool, down: bool) {
    if pin >= PIN_COUNT {
        return;
    }
    let pad = pad_ctrl(pin);
    reg::clear_bits(pad, PAD_PUE | PAD_PDE);
    match (up, down) {
        (true, false) => reg::set_bits(pad, PAD_PUE),
        (false, true) => reg::set_bits(pad, PAD_PDE),
        _ => {}
    }
}

/// Prepare a pin for the ADC: digital input off, output off, no pulls, no
/// function. The SDK's `adc_gpio_init` does the same, and for the same reason:
/// a digital input buffer on a pin sitting at mid-rail draws current and adds
/// noise to the very voltage being measured, and a pull is a resistor in
/// parallel with the source.
pub fn set_analog(pin: u32) {
    if pin >= PIN_COUNT {
        return;
    }
    let pad = pad_ctrl(pin);
    reg::clear_bits(pad, PAD_IE | PAD_PUE | PAD_PDE);
    reg::set_bits(pad, PAD_OD);
    reg::write(io_ctrl(pin), func::NULL);
}

/// Release the GPIO blocks. Called once before any pin is touched — writes to
/// a held peripheral are discarded silently.
pub fn init() {
    resets::unreset(resets::IO_BANK0 | resets::PADS_BANK0);
}

/// Route `pin` to a peripheral function other than SIO (UART, SPI, ...).
pub fn set_function(pin: u32, function: u32) {
    if pin >= PIN_COUNT {
        return;
    }
    // The pad has to be able to carry a signal in both directions: a UART's
    // RX pin needs the input enabled, and its TX pin needs the output driver
    // left on. One pad setting serves both because the peripheral, not the
    // pad, decides which way the line is driven.
    reg::clear_bits(pad_ctrl(pin), PAD_OD);
    reg::set_bits(pad_ctrl(pin), PAD_IE);
    reg::write(io_ctrl(pin), function);
}

pub struct Rp2040Pin {
    pin: u32,
}

impl Rp2040Pin {
    pub const fn new(pin: u32) -> Self {
        Self { pin }
    }
}

impl GpioPin for Rp2040Pin {
    fn set_mode(&mut self, mode: PinMode) -> HalResult<()> {
        if self.pin >= PIN_COUNT {
            return Err(HalError::InvalidArgument("RP2040 has GPIO0..=29"));
        }
        let pad = pad_ctrl(self.pin);
        // Input enabled in every mode, including output: reading back a pin
        // the core is driving is how open-drain emulation and bus arbitration
        // are checked, and a pad with IE off reads zero regardless.
        reg::set_bits(pad, PAD_IE);
        reg::clear_bits(pad, PAD_OD | PAD_PUE | PAD_PDE);
        reg::write(io_ctrl(self.pin), FUNC_SIO);

        let bit = 1 << self.pin;
        match mode {
            PinMode::Output => reg::write(SIO_GPIO_OE_SET, bit),
            PinMode::Input => reg::write(SIO_GPIO_OE_CLR, bit),
            PinMode::InputPullUp => {
                reg::write(SIO_GPIO_OE_CLR, bit);
                reg::set_bits(pad, PAD_PUE);
            }
            PinMode::InputPullDown => {
                reg::write(SIO_GPIO_OE_CLR, bit);
                reg::set_bits(pad, PAD_PDE);
            }
            PinMode::OutputOpenDrain => {
                // The pad has no open-drain mode, so it is emulated the way
                // every other port in this repo emulates it: the pin is only
                // ever driven low, and "high" means releasing it to whatever
                // pulls the line up. `write` cannot know that, so the output
                // enable is toggled there via the same OE registers.
                reg::write(SIO_GPIO_OUT_CLR, bit);
                reg::write(SIO_GPIO_OE_CLR, bit);
                reg::set_bits(pad, PAD_PUE);
            }
        }
        Ok(())
    }

    fn write(&mut self, level: Level) -> HalResult<()> {
        let bit = 1 << self.pin;
        // SIO is on the single-cycle bus, so it carries its own set and clear
        // registers rather than the +0x2000 aliases the APB peripherals have.
        match level {
            Level::High => reg::write(SIO_GPIO_OUT_SET, bit),
            Level::Low => reg::write(SIO_GPIO_OUT_CLR, bit),
        }
        Ok(())
    }

    fn read(&mut self) -> HalResult<Level> {
        let bit = 1 << self.pin;
        Ok(if reg::read(SIO_GPIO_IN) & bit != 0 {
            Level::High
        } else {
            Level::Low
        })
    }

    fn toggle(&mut self) -> HalResult<()> {
        reg::write(SIO_GPIO_OUT_XOR, 1 << self.pin);
        Ok(())
    }

    fn on_edge(&mut self, _edge: Edge, _handler: Box<dyn FnMut(Level) + Send>) -> HalResult<()> {
        // IO_BANK0 has per-pin edge interrupts; nothing in this port has
        // needed one yet. Refused rather than accepted, because a handler
        // that is registered and never fires is worse than one that says so.
        Err(HalError::NotSupported)
    }

    fn clear_interrupt(&mut self) -> HalResult<()> {
        Err(HalError::NotSupported)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Register addresses per pin. IO_BANK0 interleaves STATUS and CTRL, so a
    /// stride of 4 instead of 8 lands on the *next pin's status* — writes
    /// that vanish without a trace.
    #[test]
    fn per_pin_register_addresses() {
        assert_eq!(io_ctrl(0), 0x4001_4004);
        assert_eq!(io_ctrl(25), 0x4001_40CC);
        assert_eq!(pad_ctrl(0), 0x4001_C004);
        assert_eq!(pad_ctrl(26), 0x4001_C06C);
    }

    #[test]
    fn function_numbers_match_the_datasheet() {
        assert_eq!(func::SPI, 1);
        assert_eq!(func::UART, 2);
        assert_eq!(func::I2C, 3);
        assert_eq!(func::PWM, 4);
        assert_eq!(func::SIO, 5);
        assert_eq!(func::PIO0, 6);
        assert_eq!(func::PIO1, 7);
        assert_eq!(func::NULL, 0x1F);
    }
}
