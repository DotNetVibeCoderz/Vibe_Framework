//! Which pins each bus uses on a Pico, and the datasheet rules that say so.
//!
//! Any RP2040 peripheral can come out on several pins, but not on any pin: the
//! function table (datasheet 2.19.2) fixes, per GPIO, *which instance* and
//! *which signal* a function number carries. Getting it wrong is silent — a
//! pin set to "I2C" that the table says is I2C1 SCL drives I2C1, not I2C0 —
//! so the defaults below are checked against the table's rules in the tests
//! rather than trusted.
//!
//! | `RustNet.Hal` call | Number means | Pico pins (default) |
//! |---|---|---|
//! | `I2c.*(bus, …)` | 0 = I2C0, 1 = I2C1 | I2C0: SDA GP4, SCL GP5 · I2C1: SDA GP6, SCL GP7 |
//! | `Spi.*(bus, …)` | 0 = SPI0, 1 = SPI1 | SPI0: SCK GP18, MOSI GP19, MISO GP16 (CS GP17 by `Gpio`) · SPI1: SCK GP10, MOSI GP11, MISO GP12 (CS GP13) |
//! | `Uart.*(port, …)` | 1 = UART1 | UART1: TX GP8, RX GP9 (UART0 on GP0/GP1 is the console) |
//! | `Adc.*(channel)` | 0..3 = GP26..GP29, 4 = temperature sensor | GP26, GP27, GP28 (GP29 is VSYS/3 on a Pico) |
//! | `Pwm.Configure(channel, …)` | the GPIO number | any pin; GP25 is the on-board LED |
//! | `Signal.*(pin, …)` | the GPIO number | any pin, through PIO0 |
//!
//! None of these overlap, so every bus can be in use at once. Chip select is
//! deliberately not muxed to the SPI block: `Hal.cs` asks applications to drive
//! it with `Gpio` around each transaction, and a hardware CSn that the PL022
//! pulses between frames in mode 0 would fight that.

/// SDA, SCL for I2C0 and I2C1 — pico-sdk's `PICO_DEFAULT_I2C_*` for bus 0.
pub const I2C: [(u32, u32); 2] = [(4, 5), (6, 7)];

/// One SPI block's pins.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SpiPins {
    pub sck: u32,
    pub mosi: u32,
    pub miso: u32,
    /// Not muxed; free for the application's `Gpio` chip select.
    pub cs: u32,
}

/// SPI0 on the pico-sdk default pins, SPI1 on the next free group.
pub const SPI: [SpiPins; 2] = [
    SpiPins { sck: 18, mosi: 19, miso: 16, cs: 17 },
    SpiPins { sck: 10, mosi: 11, miso: 12, cs: 13 },
];

/// TX, RX. UART0 is the console and is muxed by the firmware at boot; UART1
/// is the application's.
pub const UART: [(u32, u32); 2] = [(0, 1), (8, 9)];

/// ADC inputs 0..3 are fixed in silicon to GPIO26..29.
pub const ADC_FIRST_PIN: u32 = 26;
/// Input 4 is the on-die temperature sensor and has no pin.
pub const ADC_TEMPERATURE: u8 = 4;

/// What an SPI function pin carries, in the datasheet's names.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SpiSignal {
    Rx,
    Csn,
    Sck,
    Tx,
}

/// What a UART function pin carries.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UartSignal {
    Tx,
    Rx,
    Cts,
    Rts,
}

/// Function 3 on `gpio`: `(instance, is_scl)`. SDA on even pins, SCL on odd,
/// and the instance alternates every pair: GP0/1 I2C0, GP2/3 I2C1, GP4/5 I2C0…
pub const fn i2c_role(gpio: u32) -> (u8, bool) {
    (((gpio >> 1) & 1) as u8, gpio & 1 == 1)
}

/// Function 1 on `gpio`: `(instance, signal)`. Groups of four
/// (RX, CSn, SCK, TX), with the instance alternating every eight pins.
pub const fn spi_role(gpio: u32) -> (u8, SpiSignal) {
    let signal = match gpio & 3 {
        0 => SpiSignal::Rx,
        1 => SpiSignal::Csn,
        2 => SpiSignal::Sck,
        _ => SpiSignal::Tx,
    };
    (((gpio >> 3) & 1) as u8, signal)
}

/// Function 2 on `gpio`: `(instance, signal)`. Groups of four (TX, RX, CTS,
/// RTS); the instance runs 0,1,1,0,0,1,1,0 over groups of four pins — the one
/// mapping on this chip that is not a plain bit of the pin number.
pub const fn uart_role(gpio: u32) -> (u8, UartSignal) {
    let signal = match gpio & 3 {
        0 => UartSignal::Tx,
        1 => UartSignal::Rx,
        2 => UartSignal::Cts,
        _ => UartSignal::Rts,
    };
    ((((gpio + 4) >> 3) & 1) as u8, signal)
}

/// The GPIO behind ADC input `channel`, or `None` for the temperature sensor
/// (and for a channel that does not exist — see [`adc_channel_valid`]).
pub const fn adc_pin(channel: u8) -> Option<u32> {
    if channel < ADC_TEMPERATURE {
        Some(ADC_FIRST_PIN + channel as u32)
    } else {
        None
    }
}

pub const fn adc_channel_valid(channel: u8) -> bool {
    channel <= ADC_TEMPERATURE
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Spot checks against the function table itself, so the rules above are
    /// not only checked against themselves.
    #[test]
    fn the_rules_reproduce_the_datasheet_table() {
        assert_eq!(i2c_role(0), (0, false)); // GPIO0  F3 = I2C0 SDA
        assert_eq!(i2c_role(3), (1, true)); //  GPIO3  F3 = I2C1 SCL
        assert_eq!(i2c_role(26), (1, false)); // GPIO26 F3 = I2C1 SDA
        assert_eq!(spi_role(0), (0, SpiSignal::Rx)); //  GPIO0  F1 = SPI0 RX
        assert_eq!(spi_role(8), (1, SpiSignal::Rx)); //  GPIO8  F1 = SPI1 RX
        assert_eq!(spi_role(27), (1, SpiSignal::Tx)); // GPIO27 F1 = SPI1 TX
        assert_eq!(uart_role(0), (0, UartSignal::Tx)); //  GPIO0  F2 = UART0 TX
        assert_eq!(uart_role(4), (1, UartSignal::Tx)); //  GPIO4  F2 = UART1 TX
        assert_eq!(uart_role(12), (0, UartSignal::Tx)); // GPIO12 F2 = UART0 TX
        assert_eq!(uart_role(20), (1, UartSignal::Tx)); // GPIO20 F2 = UART1 TX
        assert_eq!(uart_role(29), (0, UartSignal::Rx)); // GPIO29 F2 = UART0 RX
    }

    #[test]
    fn default_i2c_pins_are_the_bus_they_claim_to_be() {
        for (bus, (sda, scl)) in I2C.iter().enumerate() {
            assert_eq!(i2c_role(*sda), (bus as u8, false), "I2C{bus} SDA on GP{sda}");
            assert_eq!(i2c_role(*scl), (bus as u8, true), "I2C{bus} SCL on GP{scl}");
        }
    }

    #[test]
    fn default_spi_pins_are_the_bus_they_claim_to_be() {
        for (bus, p) in SPI.iter().enumerate() {
            let bus = bus as u8;
            assert_eq!(spi_role(p.sck), (bus, SpiSignal::Sck));
            assert_eq!(spi_role(p.mosi), (bus, SpiSignal::Tx));
            assert_eq!(spi_role(p.miso), (bus, SpiSignal::Rx));
            assert_eq!(spi_role(p.cs), (bus, SpiSignal::Csn));
        }
    }

    #[test]
    fn default_uart_pins_are_the_port_they_claim_to_be() {
        for (port, (tx, rx)) in UART.iter().enumerate() {
            assert_eq!(uart_role(*tx), (port as u8, UartSignal::Tx));
            assert_eq!(uart_role(*rx), (port as u8, UartSignal::Rx));
        }
    }

    #[test]
    fn adc_channels_map_to_gpio26_and_up() {
        assert_eq!(adc_pin(0), Some(26));
        assert_eq!(adc_pin(3), Some(29));
        assert_eq!(adc_pin(ADC_TEMPERATURE), None);
        assert!(adc_channel_valid(4));
        assert!(!adc_channel_valid(5));
    }

    /// Every bus at once: no default pin is claimed twice, and none of them is
    /// the console, the LED or an ADC input.
    #[test]
    fn no_two_defaults_share_a_pin() {
        let mut used: alloc::vec::Vec<u32> = alloc::vec![25, 26, 27, 28];
        for (a, b) in I2C.iter().chain(UART.iter()) {
            used.push(*a);
            used.push(*b);
        }
        for p in SPI.iter() {
            used.extend_from_slice(&[p.sck, p.mosi, p.miso, p.cs]);
        }
        let mut sorted = used.clone();
        sorted.sort();
        sorted.dedup();
        assert_eq!(sorted.len(), used.len(), "a pin is claimed twice: {used:?}");
    }
}
