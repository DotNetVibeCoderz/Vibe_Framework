//! RP2040 — a register-level HAL for RustNet, no PAC and no vendor SDK.
//!
//! Same shape as [`rustnet_hal_k210`] and `rustnet-hal-stm32`: a small crate
//! that talks to the chip through its memory map and implements the
//! [`rustnet_hal`] traits, so the interpreter and the firmware above it do not
//! know which silicon they are on.
//!
//! Three things about this part decide most of what follows.
//!
//! **Every peripheral comes out of reset held.** The `RESETS` block gates all
//! of them at power-up and a register write to a held peripheral is simply
//! lost — no fault, no hint. So [`resets::unreset`] comes before every driver
//! here, and it waits for the acknowledge rather than assuming it.
//!
//! **The chip boots at about 6 MHz on a ring oscillator**, not on its crystal.
//! Nothing in the datasheet's timing tables is true until the crystal and the
//! PLLs are running, so a UART configured before [`clocks::init`] is a UART at
//! whatever rate the ring oscillator drifted to that day.
//!
//! **Atomic register aliases replace read-modify-write.** Every peripheral
//! register is mirrored at `+0x1000` (XOR), `+0x2000` (set) and `+0x3000`
//! (clear). Using them is not an optimisation: a read-modify-write on a
//! register another core or an interrupt also touches loses whichever update
//! landed in between, and the RP2040 has two cores.
//!
//! Clocks, GPIO, UART, the timer and flash have run on a Pico. PWM, ADC, I2C,
//! SPI and the PIO-based signal engine are written against the datasheet and
//! their arithmetic is host-tested; they await their first on-chip pass — see
//! the port's README for the checklist.

#![no_std]

extern crate alloc;

pub mod adc;
pub mod ota;
pub mod clocks;
pub mod gpio;
pub mod i2c;
pub mod pins;
pub mod pio;
pub mod pwm;
pub mod reg;
pub mod resets;
pub mod signal;
pub mod spi;
pub mod timer;
pub mod uart;

mod board;

pub use board::Rp2040Board;
pub use clocks::Clocks;
pub use uart::Rp2040Uart;

/// Peripheral base addresses, from the RP2040 datasheet's memory map (2.2).
pub mod base {
    pub const XOSC: usize = 0x4002_4000;
    pub const PLL_SYS: usize = 0x4002_8000;
    pub const PLL_USB: usize = 0x4002_C000;
    pub const RESETS: usize = 0x4000_C000;
    pub const CLOCKS: usize = 0x4000_8000;
    pub const IO_BANK0: usize = 0x4001_4000;
    pub const PADS_BANK0: usize = 0x4001_C000;
    pub const SIO: usize = 0xD000_0000;
    pub const TIMER: usize = 0x4005_4000;
    pub const UART0: usize = 0x4003_4000;
    pub const UART1: usize = 0x4003_8000;
    pub const SPI0: usize = 0x4003_C000;
    pub const SPI1: usize = 0x4004_0000;
    pub const I2C0: usize = 0x4004_4000;
    pub const I2C1: usize = 0x4004_8000;
    pub const ADC: usize = 0x4004_C000;
    pub const PWM: usize = 0x4005_0000;
    pub const WATCHDOG: usize = 0x4005_8000;
    pub const PIO0: usize = 0x5020_0000;
    pub const PIO1: usize = 0x5030_0000;
}

#[cfg(test)]
mod tests {
    use super::base;

    /// The memory map (datasheet 2.2). Every driver is a base plus an offset,
    /// so a wrong base is a driver writing into some other peripheral.
    #[test]
    fn peripheral_bases_match_the_memory_map() {
        assert_eq!(base::UART0, 0x4003_4000);
        assert_eq!(base::SPI0, 0x4003_C000);
        assert_eq!(base::SPI1, 0x4004_0000);
        assert_eq!(base::I2C0, 0x4004_4000);
        assert_eq!(base::I2C1, 0x4004_8000);
        assert_eq!(base::ADC, 0x4004_C000);
        assert_eq!(base::PWM, 0x4005_0000);
        assert_eq!(base::PIO0, 0x5020_0000);
        assert_eq!(base::PIO1, 0x5030_0000);
    }
}
