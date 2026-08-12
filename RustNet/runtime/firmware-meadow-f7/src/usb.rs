//! RNDP over the board's own USB socket, as a CDC serial device.
//!
//! ## Why the OTG driver directly, and not a vendor HAL
//!
//! `stm32f7xx-hal` and `stm32f4xx-hal` both wrap the same crate for this —
//! `synopsys-usb-otg`, the driver for the Synopsys core ST licenses. Taking it
//! directly is fewer moving parts than pulling a fifteen-device PAC for one
//! peripheral, and it matches how the rest of this port talks to the chip:
//! at the register level, with the reference manual open.
//!
//! What a vendor HAL would otherwise supply is four constants and a clock
//! gate, all of them below.
//!
//! ## Pins
//!
//! OTG_FS is fixed to **PA11 (D−) and PA12 (D+)** on every STM32F7; the pins
//! are not remappable, so there is no board fact to be wrong about here. Both
//! must be in alternate function 10, which is the one thing this module sets
//! up that a HAL would otherwise have done.
//!
//! ## Servicing
//!
//! Polled, not interrupt-driven: the host's control transfers are answered
//! from [`UsbConsole::service`], so it has to run often. Every wait in this
//! firmware goes through `FirmwareHost::serviced_delay` for that reason.

use synopsys_usb_otg::{UsbBus, UsbPeripheral};
use usb_device::class_prelude::UsbBusAllocator;
use usb_device::prelude::*;
use usbd_serial::SerialPort;

/// The community shared CDC identifier, not a registered one. Fine for a
/// development board — a product needs its own, since the pair is what a host
/// uses to tell devices apart. The same pair the STM32F4 port uses.
const VID: u16 = 0x16C0;
const PID: u16 = 0x27DD;

/// How many times to re-poll while waiting for the host to take a write before
/// giving up. A host that has not opened the port never will, and blocking
/// there would take the whole service loop with it.
const WRITE_ATTEMPTS: u32 = 64;

const RCC_BASE: usize = 0x4002_3800;
const RCC_AHB1ENR: usize = RCC_BASE + 0x30;
const RCC_AHB2ENR: usize = RCC_BASE + 0x34;
const RCC_AHB2RSTR: usize = RCC_BASE + 0x14;
/// OTG_FS occupies bit 7 in both the AHB2 enable and reset registers.
const AHB2_OTGFS: u32 = 1 << 7;

const GPIOA_BASE: usize = 0x4002_0000;
const GPIO_MODER: usize = 0x00;
const GPIO_OSPEEDR: usize = 0x08;
const GPIO_AFRH: usize = 0x24;

/// The OTG_FS core's register block (RM0410 §42).
const OTG_FS_BASE: *const () = 0x5000_0000 as *const ();

/// The device-mode register bank sits 0x800 into the core, and `DCTL` is the
/// second register in it. Bit 1 is `SDIS`, the soft disconnect that drives the
/// D+ pull-up.
const OTG_FS_DCTL: usize = 0x5000_0804;
const DCTL_SDIS: u32 = 1 << 1;

/// Core registers used directly, for the things the driver decides by core
/// revision and can therefore decide wrongly.
const OTG_GOTGCTL: usize = 0x5000_0000;
const OTG_GCCFG: usize = 0x5000_0038;
const OTG_GSNPSID: usize = 0x5000_0040;

/// The full-speed core's own packet memory, in 32-bit words: 1.25 KB.
const FS_FIFO_WORDS: usize = 320;

/// Six bidirectional endpoints, which is what the F7's full-speed core has.
/// (The F405's four is a different part; `stm32f7xx-hal` says six here.)
const FS_ENDPOINTS: usize = 6;

/// The STM32F7's full-speed OTG core.
pub struct OtgFs {
    ahb_hz: u32,
}

// SAFETY: `REGISTERS` is the address the reference manual gives for the OTG_FS
// core on this family, the FIFO and endpoint counts are that core's, and
// `enable` performs exactly the clock gating the driver requires before it
// touches those registers.
unsafe impl UsbPeripheral for OtgFs {
    const REGISTERS: *const () = OTG_FS_BASE;
    const HIGH_SPEED: bool = false;
    const FIFO_DEPTH_WORDS: usize = FS_FIFO_WORDS;
    const ENDPOINT_COUNT: usize = FS_ENDPOINTS;

    /// Clock the OTG core **and reset it**.
    ///
    /// The reset is not belt-and-braces. This firmware is reached from the
    /// part's ROM bootloader, which has itself just been running USB to accept
    /// the DFU download — and `dfu-util`'s `:leave` makes the bootloader jump
    /// to the application rather than putting the chip through a power-on
    /// reset. So the OTG core arrives already configured for somebody else's
    /// session, with endpoints allocated and a device state that no longer
    /// matches anything. The driver's own soft reset clears the core's
    /// internal logic but not that; only the peripheral reset does.
    ///
    /// `stm32f7xx-hal` does exactly this in its own `enable`, which is where
    /// the omission showed up: comparing against an implementation known to
    /// work on this silicon is cheaper than reasoning about why mine did not.
    fn enable() {
        cortex_m::interrupt::free(|_| {
            // SAFETY: fixed peripheral addresses, and the critical section
            // makes each read-modify-write of a shared RCC register atomic.
            unsafe {
                let rstr = core::ptr::read_volatile(RCC_AHB2RSTR as *const u32);
                core::ptr::write_volatile(RCC_AHB2RSTR as *mut u32, rstr | AHB2_OTGFS);
                // A couple of cycles of settling; the reset is level-driven
                // and the release must not race the assertion.
                for _ in 0..64 {
                    core::hint::spin_loop();
                }
                core::ptr::write_volatile(RCC_AHB2RSTR as *mut u32, rstr & !AHB2_OTGFS);

                let ahb2 = core::ptr::read_volatile(RCC_AHB2ENR as *const u32);
                core::ptr::write_volatile(RCC_AHB2ENR as *mut u32, ahb2 | AHB2_OTGFS); // OTGFSEN
            }
        });
    }

    fn ahb_frequency_hz(&self) -> u32 {
        self.ahb_hz
    }
}

pub struct UsbConsole {
    device: UsbDevice<'static, UsbBus<OtgFs>>,
    serial: SerialPort<'static, UsbBus<OtgFs>>,
}

impl UsbConsole {
    /// Bring up PA11/PA12 and enumerate as a CDC serial device.
    ///
    /// `ahb_hz` is what the core is actually running at, not what it was asked
    /// for: the driver uses it to time the turnaround the host expects, and a
    /// wrong value is a device that enumerates intermittently.
    pub fn new(ahb_hz: u32) -> Self {
        Self::setup_pins();

        // `singleton!` hands out a `&'static mut` exactly once, which is what
        // the borrowed SerialPort and UsbDevice both need to live in a struct.
        let bus: &'static mut _ = cortex_m::singleton!(
            : UsbBusAllocator<UsbBus<OtgFs>> = UsbBus::new(OtgFs { ahb_hz }, unsafe {
                // SAFETY: handed to the allocator once and borrowed for the
                // program's life; nothing else refers to it.
                &mut *core::ptr::addr_of_mut!(EP_MEMORY)
            })
        )
        .expect("the USB bus is built once");

        let serial = SerialPort::new(bus);
        let device = UsbDeviceBuilder::new(bus, UsbVidPid(VID, PID))
            // Declared at device level so a host binds its CDC driver rather
            // than leaving the device unclaimed.
            .device_class(usbd_serial::USB_CLASS_CDC)
            .strings(&[StringDescriptors::default()
                .manufacturer("RustNet")
                .product("RustNet Meadow F7")
                .serial_number("meadow-f7")])
            .expect("descriptor strings fit")
            .build();

        Self { device, serial }
    }

    /// PA11 and PA12 to alternate function 10, at the highest slew rate.
    ///
    /// Full-speed USB is 12 Mbit/s and its edges will not meet the eye diagram
    /// at a lower drive setting — a pin left at the reset speed gives a device
    /// that enumerates sometimes, or only on some hosts, which is far harder
    /// to diagnose than one that never does.
    fn setup_pins() {
        cortex_m::interrupt::free(|_| unsafe {
            let ahb1 = core::ptr::read_volatile(RCC_AHB1ENR as *const u32);
            core::ptr::write_volatile(RCC_AHB1ENR as *mut u32, ahb1 | 1); // GPIOAEN

            let moder = (GPIOA_BASE + GPIO_MODER) as *mut u32;
            let v = core::ptr::read_volatile(moder);
            // Pins 11 and 12 to 0b10 (alternate function).
            let v = (v & !((0b11 << 22) | (0b11 << 24))) | (0b10 << 22) | (0b10 << 24);
            core::ptr::write_volatile(moder, v);

            let ospeedr = (GPIOA_BASE + GPIO_OSPEEDR) as *mut u32;
            let v = core::ptr::read_volatile(ospeedr);
            let v = v | (0b11 << 22) | (0b11 << 24); // very high speed
            core::ptr::write_volatile(ospeedr, v);

            // AFRH covers pins 8..15, four bits each: pin 11 at bits 12..15,
            // pin 12 at 16..19. AF10 is OTG_FS.
            let afrh = (GPIOA_BASE + GPIO_AFRH) as *mut u32;
            let v = core::ptr::read_volatile(afrh);
            let v = (v & !(0xF << 12) & !(0xF << 16)) | (10 << 12) | (10 << 16);
            core::ptr::write_volatile(afrh, v);
        });
    }

    /// Tell the core the USB session is valid, whatever it thinks of VBUS.
    ///
    /// The driver configures VBUS sensing from the core's revision id, and
    /// only for the revisions it has a branch for; anything else is left at
    /// reset. On this board VBUS reaches the MCU through a power switch and a
    /// FET (schematic sheet 3), so a core that is waiting to see VBUS before
    /// it will service a session can attach — the pull-up is independent — and
    /// then answer nothing. That is exactly the failure here: a device the
    /// host sees and cannot read a descriptor from.
    ///
    /// Forcing B-session valid removes the question. It is what the driver
    /// itself does for the revisions it recognises, so this only fills the gap
    /// where it does not.
    pub fn force_session_valid(&mut self) {
        // SAFETY: fixed peripheral addresses; the core is initialised by now.
        unsafe {
            let cfg = core::ptr::read_volatile(OTG_GCCFG as *const u32);
            // PWRDWN on (transceiver powered), VBDEN off (do not gate on VBUS).
            core::ptr::write_volatile(OTG_GCCFG as *mut u32, (cfg | (1 << 16)) & !(1 << 21));

            let otg = core::ptr::read_volatile(OTG_GOTGCTL as *const u32);
            // BVALOEN | BVALOVAL: override the B-session signal, and say valid.
            core::ptr::write_volatile(OTG_GOTGCTL as *mut u32, otg | (1 << 6) | (1 << 7));
        }
    }

    /// The Synopsys core revision, which is what the driver branches on.
    ///
    /// Reported so that "the driver has no branch for this core" stops being a
    /// hypothesis and becomes a number.
    pub fn core_id() -> u32 {
        // SAFETY: fixed peripheral address, read-only.
        unsafe { core::ptr::read_volatile(OTG_GSNPSID as *const u32) }
    }

    /// Drop off the bus, as if the cable had been pulled.
    ///
    /// The driver has a `force_reset`, and it is not usable for this: it holds
    /// the disconnect for **three milliseconds**. That is enough for a host
    /// that is merely idle, and not remotely enough for one that has already
    /// marked the port `Device Descriptor Request Failed` — such a host does
    /// not retry a three-millisecond blip, so a search that relied on it would
    /// silently test only its first candidate and report the rest as failures.
    ///
    /// So the pull-up is driven directly and the caller decides how long to
    /// stay away. Long enough looks like a replug, which is the one thing a
    /// host always responds to.
    pub fn detach(&mut self) {
        // SAFETY: fixed peripheral address; the core is initialised by now.
        unsafe {
            let v = core::ptr::read_volatile(OTG_FS_DCTL as *const u32);
            core::ptr::write_volatile(OTG_FS_DCTL as *mut u32, v | DCTL_SDIS);
        }
    }

    /// Present the pull-up again and let enumeration start from the top.
    pub fn attach(&mut self) {
        // SAFETY: as above.
        unsafe {
            let v = core::ptr::read_volatile(OTG_FS_DCTL as *const u32);
            core::ptr::write_volatile(OTG_FS_DCTL as *mut u32, v & !DCTL_SDIS);
        }
    }

    /// Answer whatever the host is asking. Cheap, and safe to call often.
    pub fn service(&mut self) {
        self.device.poll(&mut [&mut self.serial]);
    }

    /// Has a host configured this device? True once it is a usable port.
    pub fn is_configured(&self) -> bool {
        self.device.state() == UsbDeviceState::Configured
    }

    /// The host's `DTR` line, as a CDC control-line state.
    ///
    /// A USB-serial chip exposes these two signals, and `esptool` drives an
    /// ESP32's `GPIO0` and `EN` with them to put it into its ROM loader. This
    /// board has no such chip — the STM32 *is* the USB device — so the signals
    /// have to be forwarded by hand for the standard tooling to work.
    pub fn dtr(&self) -> bool {
        self.serial.dtr()
    }

    /// The baud rate the host has asked this port to run at.
    ///
    /// A real USB-serial chip reprograms its UART when the host sets the CDC
    /// line coding, and tools rely on it: `esptool --baud 921600` speaks to the
    /// *chip*, and the chip is expected to pass the rate on. A bridge that
    /// ignores it is stuck at whatever it was compiled with, which turns a
    /// four-megabyte flash read into ten minutes.
    pub fn baud(&self) -> u32 {
        self.serial.line_coding().data_rate()
    }

    /// The host's `RTS` line. See [`Self::dtr`].
    pub fn rts(&self) -> bool {
        self.serial.rts()
    }

    /// Take whatever the host has sent, if anything.
    pub fn read(&mut self, buf: &mut [u8]) -> usize {
        self.service();
        self.serial.read(buf).unwrap_or(0)
    }

    /// Write, giving up rather than blocking if the host is not draining.
    ///
    /// Console output is worth losing; the service loop is not.
    pub fn write(&mut self, bytes: &[u8]) {
        if !self.is_configured() {
            return;
        }
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

/// Endpoint buffers for the OTG core. Must outlive the bus, hence static.
static mut EP_MEMORY: [u32; FS_FIFO_WORDS] = [0; FS_FIFO_WORDS];
