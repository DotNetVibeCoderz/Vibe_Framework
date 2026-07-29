//! RNDP over the board's own USB socket, as a CDC serial device.
//!
//! On a board whose USB reaches the MCU — the Netduino, not the Nucleo —
//! this removes the USB-serial adapter from the picture entirely: one cable
//! carries DFU for the firmware and RNDP for everything after it.
//!
//! # Clocking
//!
//! USB needs 48 MHz alongside whatever the core runs at. On the Netduino one
//! PLL gives both from the single 25 MHz crystal — VCO 336 MHz, `/2` for the
//! 168 MHz core and `/7` for USB — which is why `require_pll48clk()` can be
//! asked for without changing the core clock.
//!
//! # Servicing
//!
//! `usb-device` is polled, not interrupt-driven here, so [`UsbConsole::service`]
//! has to run often: the host's control transfers are answered from it. The
//! firmware calls it wherever it already polls RNDP, including inside
//! `sleep_ms`. One place it cannot run is during a flash erase, which stalls
//! the core for about a second — long enough that a host may drop the port.

use stm32f4xx_hal::otg_fs::{UsbBus, UsbBusType, USB};
use usb_device::prelude::*;
use usbd_serial::SerialPort;

/// Endpoint buffers for the OTG FS core. Must outlive the bus, hence static.
static mut EP_MEMORY: [u32; 1024] = [0; 1024];

/// The community shared CDC identifier, not a registered one. Fine for a
/// development board — a product needs its own, since the pair is what a host
/// uses to tell devices apart.
const VID: u16 = 0x16C0;
const PID: u16 = 0x27DD;

/// How many times to re-poll while waiting for the host to take a write
/// before giving up on it. A host that has not opened the port never will,
/// and blocking forever there would take the whole service loop with it.
const WRITE_ATTEMPTS: u32 = 64;

pub struct UsbConsole {
    device: UsbDevice<'static, UsbBusType>,
    serial: SerialPort<'static, UsbBusType>,
}

impl UsbConsole {
    /// Claim the OTG FS peripheral and enumerate as a CDC serial device.
    pub fn new(usb: USB) -> Self {
        // `singleton!` hands out a `&'static mut` exactly once, which is what
        // the borrowed SerialPort and UsbDevice both need to live in a struct.
        let bus: &'static mut _ = cortex_m::singleton!(
            : usb_device::bus::UsbBusAllocator<UsbBusType> =
                UsbBus::new(usb, unsafe { &mut *core::ptr::addr_of_mut!(EP_MEMORY) })
        )
        .expect("the USB bus is built once");

        let serial = SerialPort::new(bus);
        let device = UsbDeviceBuilder::new(bus, UsbVidPid(VID, PID))
            .device_class(usbd_serial::USB_CLASS_CDC)
            .strings(&[StringDescriptors::default()
                .manufacturer("RustNet")
                .product("RustNet device (RNDP)")
                .serial_number("rustnet-stm32")])
            .expect("descriptor strings fit")
            .build();

        Self { device, serial }
    }

    /// Answer whatever the host is asking. Cheap, and safe to call often.
    pub fn service(&mut self) {
        self.device.poll(&mut [&mut self.serial]);
    }

    /// Non-blocking read; 0 when nothing is waiting.
    pub fn read(&mut self, buf: &mut [u8]) -> usize {
        self.service();
        self.serial.read(buf).unwrap_or(0)
    }

    /// Write, giving up rather than blocking if the host is not draining.
    /// Console output is worth losing; the service loop is not.
    pub fn write(&mut self, bytes: &[u8]) {
        let mut sent = 0;
        let mut attempts = 0;
        while sent < bytes.len() && attempts < WRITE_ATTEMPTS {
            match self.serial.write(&bytes[sent..]) {
                Ok(0) | Err(_) => {
                    attempts += 1;
                    self.service();
                }
                Ok(n) => {
                    sent += n;
                    attempts = 0;
                }
            }
        }
        // Push the last partial packet out rather than leaving it buffered
        // until the next write happens along.
        let _ = self.serial.flush();
        self.service();
    }
}
