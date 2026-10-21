//! The board: what this port implements, and honest refusals for the rest.
//!
//! GPIO, UART, time, PWM, ADC, I2C, SPI and PIO signal control are
//! implemented; everything else in the [`rustnet_hal::Board`] surface answers
//! [`HalError::NotSupported`] rather than pretending. A stub that silently
//! succeeds is worse than one that refuses — an application gets no reading
//! and no reason.
//!
//! Bus pins are muxed the first time a bus is asked for, on the defaults in
//! [`crate::pins`], so an application that never touches I2C1 leaves GP6/GP7
//! free for `Gpio`.

use alloc::vec::Vec;
use rustnet_hal::delay::Delay;
use rustnet_hal::power::{BatteryStatus, PowerManager, SleepMode, WakeReason, WakeSource};
use rustnet_hal::rtc::{DateTime, Rtc};
use rustnet_hal::watchdog::Watchdog;
use rustnet_hal::{Board, HalError, HalResult};

use crate::adc::Rp2040Adc;
use crate::clocks::Clocks;
use crate::gpio::{self, func, Rp2040Pin, PIN_COUNT};
use crate::i2c::{self, Rp2040I2c};
use crate::pins;
use crate::pwm::Rp2040Pwm;
use crate::signal::Rp2040Signal;
use crate::spi::{self, Rp2040Spi};
use crate::timer::{self, TimerDelay};
use crate::uart::{Rp2040Uart, UART0, UART1};

/// Bits in [`Rp2040Board::muxed`]: which buses have had their pins routed.
/// Bus 1 of each kind is the bus-0 bit shifted left by one.
const MUX_I2C0: u8 = 1 << 0;
const MUX_SPI0: u8 = 1 << 2;
const MUX_UART1: u8 = 1 << 4;

/// Nothing here resets the chip yet; the watchdog is the way in when it does.
struct Rp2040Power {
    sys_hz: u32,
}

impl PowerManager for Rp2040Power {
    fn sleep(&mut self, _mode: SleepMode, _duration_ms: Option<u64>) -> HalResult<()> {
        Err(HalError::NotSupported)
    }
    fn battery(&mut self) -> HalResult<BatteryStatus> {
        // A Pico has no fuel gauge and no battery input; reporting a made-up
        // percentage would be worse than saying there is nothing to read.
        Err(HalError::NotSupported)
    }
    fn cpu_frequency_hz(&self) -> u32 {
        self.sys_hz
    }
    fn set_cpu_frequency_hz(&mut self, _hz: u32) -> HalResult<()> {
        // Retuning the PLL under a running system means every UART divisor
        // derived from it is now wrong. Refused until something recomputes
        // them.
        Err(HalError::NotSupported)
    }
    fn reset(&mut self) -> ! {
        // The documented software reset is a watchdog armed to fire at once.
        // Until that is wired up, spin: the signature promises never to
        // return, and returning would be a lie the caller cannot detect.
        loop {
            core::hint::spin_loop();
        }
    }
    fn shutdown(&mut self) -> ! {
        loop {
            core::hint::spin_loop();
        }
    }
    fn arm_wake(&mut self, _source: WakeSource) -> HalResult<()> {
        Err(HalError::NotSupported)
    }
    fn clear_wake_sources(&mut self) {}
    fn wake_reason(&self) -> WakeReason {
        WakeReason::PowerOn
    }
}

/// The RP2040 has no battery-backed clock, so this is uptime with an offset —
/// enough to timestamp a log line, not enough to trust across a power cut.
struct Rp2040Rtc {
    offset_s: u64,
}

impl Rtc for Rp2040Rtc {
    fn now(&mut self) -> HalResult<DateTime> {
        Ok(DateTime::from_epoch(
            self.offset_s + timer::now_us() / 1_000_000,
        ))
    }
    fn set(&mut self, dt: DateTime) -> HalResult<()> {
        self.offset_s = dt
            .to_epoch()
            .saturating_sub(timer::now_us() / 1_000_000);
        Ok(())
    }
    fn set_alarm(&mut self, _epoch: u64) -> HalResult<()> {
        Err(HalError::NotSupported)
    }
    fn clear_alarm(&mut self) -> HalResult<()> {
        Err(HalError::NotSupported)
    }
    fn alarm(&self) -> Option<u64> {
        None
    }
}

struct Rp2040Watchdog;

impl Watchdog for Rp2040Watchdog {
    fn start(&mut self, _timeout_ms: u32) -> HalResult<()> {
        Err(HalError::NotSupported)
    }
    fn feed(&mut self) -> HalResult<()> {
        Err(HalError::NotSupported)
    }
    fn stop(&mut self) -> HalResult<()> {
        Err(HalError::NotSupported)
    }
    fn is_running(&self) -> bool {
        false
    }
    fn timeout_ms(&self) -> u32 {
        0
    }
}

pub struct Rp2040Board {
    name: &'static str,
    clocks: Clocks,
    pins: Vec<Rp2040Pin>,
    uarts: [Rp2040Uart; 2],
    i2cs: [Rp2040I2c; 2],
    spis: [Rp2040Spi; 2],
    /// One per GPIO: the PWM channel number is the pin number.
    pwms: Vec<Rp2040Pwm>,
    adcs: [Rp2040Adc; 5],
    signal: Rp2040Signal,
    muxed: u8,
    delay: TimerDelay,
    power: Rp2040Power,
    rtc: Rp2040Rtc,
    watchdog: Rp2040Watchdog,
}

impl Rp2040Board {
    pub fn new(name: &'static str, clocks: Clocks) -> Self {
        Self {
            name,
            clocks,
            pins: (0..PIN_COUNT).map(Rp2040Pin::new).collect(),
            uarts: [
                Rp2040Uart::new(UART0, clocks.peri_hz),
                Rp2040Uart::new(UART1, clocks.peri_hz),
            ],
            // I2C, PWM and PIO run from clk_sys; SPI and UART from clk_peri.
            i2cs: [
                Rp2040I2c::new(i2c::I2C0, clocks.sys_hz),
                Rp2040I2c::new(i2c::I2C1, clocks.sys_hz),
            ],
            spis: [
                Rp2040Spi::new(spi::SPI0, clocks.peri_hz),
                Rp2040Spi::new(spi::SPI1, clocks.peri_hz),
            ],
            pwms: (0..PIN_COUNT).map(|p| Rp2040Pwm::new(p, clocks.sys_hz)).collect(),
            adcs: [
                Rp2040Adc::new(0, clocks.usb_hz),
                Rp2040Adc::new(1, clocks.usb_hz),
                Rp2040Adc::new(2, clocks.usb_hz),
                Rp2040Adc::new(3, clocks.usb_hz),
                Rp2040Adc::new(4, clocks.usb_hz),
            ],
            signal: Rp2040Signal::new(0, clocks.sys_hz),
            muxed: 0,
            delay: TimerDelay,
            power: Rp2040Power { sys_hz: clocks.sys_hz },
            rtc: Rp2040Rtc { offset_s: 0 },
            watchdog: Rp2040Watchdog,
        }
    }

    /// Release the pin blocks and start the timer's tick. Call once, before
    /// anything else touches a peripheral.
    ///
    /// Returns whether time is actually running. A caller that ignores this
    /// gets delays that do not delay, and every timeout above it fires at
    /// once — which presents as a hung peripheral somewhere else entirely.
    pub fn init(&mut self) -> bool {
        crate::gpio::init();
        timer::start_tick(self.clocks.xosc_hz)
    }

    pub fn clocks(&self) -> Clocks {
        self.clocks
    }

    /// Route a bus's pins the first time it is used. `bit` is that bus's
    /// [`Self::muxed`] flag; `route` does the pad and function work.
    fn mux_once(&mut self, bit: u8, route: impl FnOnce()) {
        if self.muxed & bit == 0 {
            route();
            self.muxed |= bit;
        }
    }
}

impl Board for Rp2040Board {
    fn name(&self) -> &str {
        self.name
    }

    fn gpio(&mut self, pin: u32) -> HalResult<&mut dyn rustnet_hal::gpio::GpioPin> {
        self.pins
            .get_mut(pin as usize)
            .map(|p| p as &mut dyn rustnet_hal::gpio::GpioPin)
            .ok_or(HalError::InvalidArgument("RP2040 has GPIO0..=29"))
    }

    fn uart(&mut self, port: u8) -> HalResult<&mut dyn rustnet_hal::uart::Uart> {
        // UART0's pins are the console's and the firmware routes them at boot.
        if port == 1 {
            self.mux_once(MUX_UART1, || {
                let (tx, rx) = pins::UART[1];
                gpio::set_function(tx, func::UART);
                gpio::set_function(rx, func::UART);
                // An unconnected RX floats and reads as a stream of garbage
                // bytes; idle-high is what a UART line is anyway.
                gpio::set_pulls(rx, true, false);
            });
        }
        self.uarts
            .get_mut(port as usize)
            .map(|u| u as &mut dyn rustnet_hal::uart::Uart)
            .ok_or(HalError::InvalidArgument("RP2040 has UART0 and UART1"))
    }

    fn delay(&mut self) -> &mut dyn Delay {
        &mut self.delay
    }

    fn power(&mut self) -> &mut dyn PowerManager {
        &mut self.power
    }

    fn rtc(&mut self) -> &mut dyn Rtc {
        &mut self.rtc
    }

    fn watchdog(&mut self) -> &mut dyn Watchdog {
        &mut self.watchdog
    }

    fn i2c(&mut self, bus: u8) -> HalResult<&mut dyn rustnet_hal::i2c::I2cBus> {
        if bus > 1 {
            return Err(HalError::InvalidArgument("RP2040 has I2C0 and I2C1"));
        }
        self.mux_once(MUX_I2C0 << bus, || {
            let (sda, scl) = pins::I2C[bus as usize];
            for pin in [sda, scl] {
                gpio::set_function(pin, func::I2C);
                gpio::set_pulls(pin, true, false);
            }
        });
        Ok(&mut self.i2cs[bus as usize])
    }

    fn spi(&mut self, bus: u8) -> HalResult<&mut dyn rustnet_hal::spi::SpiBus> {
        if bus > 1 {
            return Err(HalError::InvalidArgument("RP2040 has SPI0 and SPI1"));
        }
        self.mux_once(MUX_SPI0 << bus, || {
            let p = pins::SPI[bus as usize];
            for pin in [p.sck, p.mosi, p.miso] {
                gpio::set_function(pin, func::SPI);
            }
        });
        Ok(&mut self.spis[bus as usize])
    }

    fn pwm(&mut self, channel: u8) -> HalResult<&mut dyn rustnet_hal::pwm::PwmChannel> {
        self.pwms
            .get_mut(channel as usize)
            .map(|p| p as &mut dyn rustnet_hal::pwm::PwmChannel)
            .ok_or(HalError::InvalidArgument("PWM channel is a GPIO number, 0..=29"))
    }

    fn adc(&mut self, channel: u8) -> HalResult<&mut dyn rustnet_hal::adc::AdcChannel> {
        self.adcs
            .get_mut(channel as usize)
            .map(|a| a as &mut dyn rustnet_hal::adc::AdcChannel)
            .ok_or(HalError::InvalidArgument(
                "RP2040 ADC channels are 0..=3 (GP26..GP29) and 4 (temperature)",
            ))
    }

    fn signal(&mut self, pin: u32) -> HalResult<&mut dyn rustnet_hal::signal::SignalControl> {
        if pin >= PIN_COUNT {
            return Err(HalError::InvalidArgument("RP2040 has GPIO0..=29"));
        }
        self.signal.set_pin(pin);
        Ok(&mut self.signal)
    }

    // --- not implemented on this port ------------------------------------
    //
    // Real peripherals or integration points rather than gaps in the silicon:
    // I2S and one-wire would come from PIO, the way signal control does.

    fn i2s(&mut self, _port: u8) -> HalResult<&mut dyn rustnet_hal::i2s::I2sBus> {
        Err(HalError::NotSupported)
    }
    fn can(&mut self, _bus: u8) -> HalResult<&mut dyn rustnet_hal::can::CanBus> {
        Err(HalError::NotSupported)
    }
    fn onewire(&mut self, _bus: u8) -> HalResult<&mut dyn rustnet_hal::onewire::OneWireBus> {
        Err(HalError::NotSupported)
    }
    fn extmem(&mut self, _index: u8) -> HalResult<&mut dyn rustnet_hal::extmem::ExtMemory> {
        Err(HalError::NotSupported)
    }
    fn netif(
        &mut self,
        _kind: rustnet_hal::netif::NetIfKind,
    ) -> HalResult<&mut dyn rustnet_hal::netif::NetInterface> {
        Err(HalError::NotSupported)
    }
}
