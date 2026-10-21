//! ADC — one 12-bit SAR converter, five inputs.
//!
//! | HAL channel | Input |
//! |---|---|
//! | 0..3 | GPIO26..GPIO29 (on a Pico, GP29 is VSYS / 3 and GP28..26 are free) |
//! | 4 | the on-die temperature sensor |
//!
//! Three things have to be true before a conversion means anything:
//!
//! - **`clk_adc` must run at 48 MHz.** It is its own clock-tree output with
//!   its own enable, normally fed from `PLL_USB`. A converter without it
//!   never raises `READY`, which looks like a hung peripheral.
//! - **The pin must be analogue.** Digital input buffer off, pulls off — see
//!   [`crate::gpio::set_analog`]. Leaving the input buffer on draws current at
//!   mid-rail and adds noise to the very voltage being measured.
//! - **The temperature sensor has its own bias enable** (`TS_EN`), off at
//!   reset. Channel 4 without it reads a meaningless floor.
//!
//! The reference on a Pico is the 3.3 V rail through a filter, so millivolts
//! are `raw * 3300 / 4095` — the trait's default. The temperature sensor reads
//! about 0.706 V at 27 °C and falls 1.721 mV/°C (datasheet 4.9.5); the
//! conversion is left to the application, which knows whether it wants it.

use rustnet_hal::adc::AdcChannel;
use rustnet_hal::{HalError, HalResult};

use crate::base::{ADC, CLOCKS};
use crate::{gpio, pins, reg, resets};

const CS: usize = ADC + 0x00;
const RESULT: usize = ADC + 0x04;

/// `CS` fields (datasheet 4.9.6).
const CS_EN: u32 = 1 << 0;
const CS_TS_EN: u32 = 1 << 1;
const CS_START_ONCE: u32 = 1 << 2;
const CS_READY: u32 = 1 << 8;
const CS_ERR: u32 = 1 << 9;
const CS_AINSEL_SHIFT: u32 = 12;

/// `CLK_ADC_CTRL` / `CLK_ADC_DIV` (datasheet 2.15.7).
const CLK_ADC_CTRL: usize = CLOCKS + 0x60;
const CLK_ADC_DIV: usize = CLOCKS + 0x64;
const CLK_ENABLE: u32 = 1 << 11;
const CLK_AUXSRC_MASK: u32 = 7 << 5;
/// AUXSRC 0 is `clksrc_pll_usb`.
const CLK_AUXSRC_PLL_USB: u32 = 0 << 5;
/// `DIV.INT` at bits 9:8; 1 is divide-by-one.
const CLK_DIV_1: u32 = 1 << 8;

/// The converter's required clock.
pub const ADC_CLOCK_HZ: u32 = 48_000_000;

/// A conversion is 96 `clk_adc` cycles, 2 µs; this is thousands of times that.
const SPIN_LIMIT: u32 = 100_000;

/// The `CS` word that starts one conversion on `channel`.
///
/// `TS_EN` is kept on in every word rather than only for channel 4: the
/// sensor's bias needs time to settle after it is switched on, and turning it
/// on just before the conversion that reads it gives a low first reading.
pub const fn start_word(channel: u8) -> u32 {
    CS_EN | CS_TS_EN | CS_START_ONCE | ((channel as u32 & 7) << CS_AINSEL_SHIFT)
}

pub struct Rp2040Adc {
    channel: u8,
    /// `clk_usb`'s rate as the clock tree reported it; the ADC clock is
    /// taken from the same PLL, so zero here means there is nothing to feed
    /// the converter from.
    usb_hz: u32,
    pin_ready: bool,
}

impl Rp2040Adc {
    pub const fn new(channel: u8, usb_hz: u32) -> Self {
        Self { channel, usb_hz, pin_ready: false }
    }

    /// Make sure `clk_adc` is running from `PLL_USB` at 48 MHz.
    ///
    /// The firmware's clock bring-up may already have done this; if it has,
    /// it is left alone. Otherwise the clock is stopped, retargeted and
    /// restarted — changing `AUXSRC` under a running clock can glitch it.
    fn ensure_clock(&self) -> HalResult<()> {
        let ctrl = reg::read(CLK_ADC_CTRL);
        if ctrl & CLK_ENABLE != 0 && ctrl & CLK_AUXSRC_MASK == CLK_AUXSRC_PLL_USB {
            return Ok(());
        }
        if self.usb_hz != ADC_CLOCK_HZ {
            return Err(HalError::NotSupported);
        }
        reg::clear_bits(CLK_ADC_CTRL, CLK_ENABLE);
        // Two cycles of the old clock for the disable to land (2.15.3.2);
        // a few dozen system cycles is ample at any setting.
        for _ in 0..64 {
            core::hint::spin_loop();
        }
        reg::write(CLK_ADC_DIV, CLK_DIV_1);
        reg::write(CLK_ADC_CTRL, CLK_AUXSRC_PLL_USB);
        reg::set_bits(CLK_ADC_CTRL, CLK_ENABLE);
        Ok(())
    }

    fn ensure_ready(&mut self) -> HalResult<()> {
        if !pins::adc_channel_valid(self.channel) {
            return Err(HalError::InvalidArgument("RP2040 ADC channels are 0..=4"));
        }
        self.ensure_clock()?;
        if !resets::is_released(resets::ADC) && !resets::unreset(resets::ADC) {
            return Err(HalError::Timeout);
        }
        if reg::read(CS) & CS_EN == 0 {
            reg::write(CS, CS_EN | CS_TS_EN);
            // READY rises once the converter has powered up.
            if !reg::wait_until(SPIN_LIMIT, || reg::read(CS) & CS_READY != 0) {
                return Err(HalError::Timeout);
            }
        }
        if !self.pin_ready {
            if let Some(pin) = pins::adc_pin(self.channel) {
                gpio::set_analog(pin);
            }
            self.pin_ready = true;
        }
        Ok(())
    }
}

impl AdcChannel for Rp2040Adc {
    fn read_raw(&mut self) -> HalResult<u16> {
        self.ensure_ready()?;
        // Select the input first, then start: AINSEL is sampled when the
        // conversion begins, and one write carrying both is exactly that.
        reg::write(CS, start_word(self.channel) & !CS_START_ONCE);
        reg::write(CS, start_word(self.channel));
        if !reg::wait_until(SPIN_LIMIT, || reg::read(CS) & CS_READY != 0) {
            return Err(HalError::Timeout);
        }
        if reg::read(CS) & CS_ERR != 0 {
            return Err(HalError::Bus("ADC conversion error"));
        }
        Ok((reg::read(RESULT) & 0xFFF) as u16)
    }

    fn resolution_bits(&self) -> u8 {
        12
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_start_word_selects_the_input_and_keeps_the_sensor_biased() {
        assert_eq!(start_word(0), 0b0111);
        assert_eq!(start_word(2), 0x2007);
        // Channel 4, the temperature sensor: AINSEL = 4 at bits 14:12.
        assert_eq!(start_word(4), 0x4007);
        assert_eq!(start_word(4) & CS_TS_EN, CS_TS_EN);
    }

    #[test]
    fn registers_sit_where_the_datasheet_puts_them() {
        assert_eq!(CS, 0x4004_C000);
        assert_eq!(RESULT, 0x4004_C004);
        assert_eq!(CLK_ADC_CTRL, 0x4000_8060);
        assert_eq!(CLK_ADC_DIV, 0x4000_8064);
    }

    /// The trait's default conversion, at this converter's 12 bits and the
    /// Pico's 3.3 V reference.
    #[test]
    fn millivolts_at_full_scale_and_mid_scale() {
        struct Fixed(u16);
        impl AdcChannel for Fixed {
            fn read_raw(&mut self) -> HalResult<u16> {
                Ok(self.0)
            }
            fn resolution_bits(&self) -> u8 {
                12
            }
        }
        assert_eq!(Fixed(4095).read_millivolts(), Ok(3300));
        assert_eq!(Fixed(2048).read_millivolts(), Ok(1650));
        // The temperature sensor at 27 °C reads ~0.706 V: raw 876.
        assert_eq!(Fixed(876).read_millivolts(), Ok(705));
    }
}
