//! USB CDC-ACM over `usb-device`, so the board is its own COM port.
//!
//! This was a hand-written stack first. It got as far as a host seeing a
//! device attached and no further: the first descriptor request failed, and
//! four fixes went in — an unconfigured USB PLL, the DPRAM write hazard on
//! `AVAILABLE`, control transfers longer than one packet, and an unarmed EP0
//! status stage — each a genuine fault and none of them the last one.
//!
//! Every one of those was a hypothesis flashed blind, because this port has no
//! console until USB works. That is a loop with no exit, and the way out is to
//! stop hand-rolling the part that is a solved problem. `usb-device` and
//! `usbd-serial` over `rp2040-hal`'s bus are mature and widely used on this
//! part, and this crate already takes `rp2040-boot2` and `cortex-m-rt` rather
//! than writing them itself.
//!
//! The register-level work is not wasted: `rustnet-hal-rp2040` still drives
//! GPIO, the UART and the timer, and still implements the RustNet board
//! traits. What moved out is the clock tree and the USB device controller —
//! the same division the STM32 port makes with `stm32f4xx-hal`.

use rp2040_hal::usb::UsbBus;
use usb_device::class_prelude::UsbBusAllocator;
use usb_device::device::StringDescriptors;
use usb_device::prelude::{UsbDevice, UsbDeviceBuilder, UsbDeviceState, UsbVidPid};
use usbd_serial::SerialPort;

/// Raspberry Pi's vendor id and the SDK's CDC product id. Borrowed
/// deliberately: a made-up pair enumerates just as well and makes the board
/// indistinguishable from junk in a device list.
const VID_PID: UsbVidPid = UsbVidPid(0x2E8A, 0x000A);

pub struct UsbCdc {
    device: UsbDevice<'static, UsbBus>,
    serial: SerialPort<'static, UsbBus>,
}

impl UsbCdc {
    /// Build the device on an allocator that must outlive it.
    ///
    /// The allocator is `'static` because the descriptors and endpoint state
    /// are borrowed for as long as the device exists, and there is no scope in
    /// `main` long enough to own it otherwise.
    pub fn new(allocator: &'static UsbBusAllocator<UsbBus>) -> Self {
        let serial = SerialPort::new(allocator);
        let device = UsbDeviceBuilder::new(allocator, VID_PID)
            .strings(&[StringDescriptors::default()
                .manufacturer("RustNet")
                .product("RustNet Pico")
                .serial_number("0001")])
            .expect("string descriptors fit")
            // Declared at device level so a host binds its CDC driver rather
            // than leaving the device unclaimed.
            .device_class(usbd_serial::USB_CLASS_CDC)
            .build();
        Self { device, serial }
    }

    /// Service the bus. Call often; it never blocks.
    pub fn poll(&mut self) {
        // Only services the bus. Host input is left in the endpoint for
        // [`Self::read`] to take.
        //
        // An earlier version drained and discarded it here, from when nothing
        // consumed input — and once RNDP did, every frame was thrown away
        // before the service could see it. The port opened, accepted
        // everything, and answered nothing.
        let _ = self.device.poll(&mut [&mut self.serial]);
    }

    /// Has a host configured this device? True once it is a usable port.
    pub fn is_configured(&self) -> bool {
        self.device.state() == UsbDeviceState::Configured
    }

    /// Write to the port, giving up rather than blocking.
    ///
    /// A console that stalls the application because nothing is listening is
    /// worse than one that loses a line. The poll between attempts is what
    /// lets the host collect what has already been queued.
    pub fn write(&mut self, data: &[u8]) {
        if !self.is_configured() {
            return;
        }
        let mut sent = 0;
        let mut guard = 0u32;
        while sent < data.len() {
            match self.serial.write(&data[sent..]) {
                Ok(n) if n > 0 => {
                    sent += n;
                    guard = 0;
                }
                _ => {
                    guard += 1;
                    if guard == 20_000 {
                        return;
                    }
                }
            }
            self.poll();
        }
    }

    /// Take whatever the host has sent, if anything.
    pub fn read(&mut self, buf: &mut [u8]) -> usize {
        if !self.is_configured() {
            return 0;
        }
        self.serial.read(buf).unwrap_or(0)
    }
}
