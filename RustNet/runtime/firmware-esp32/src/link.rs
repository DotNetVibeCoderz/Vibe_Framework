//! The byte pipe RNDP runs over, which is not the same peripheral on every
//! ESP32.
//!
//! The classic ESP32 boards here — DevKit, WROOM, M5Stack — reach a host
//! through a USB-serial bridge chip wired to **UART0**. The ESP32-C3 has a USB
//! Serial/JTAG controller *inside the SoC*, and boards built around it (the
//! Seeed XIAO ESP32C3, for one) wire the USB socket straight to that and leave
//! UART0 on ordinary GPIOs with nothing attached. On such a board, a firmware
//! that talks to UART0 is talking to no one: the port enumerates, the tool
//! opens it, and every request times out.
//!
//! Both are byte pipes with the same shape, so this is the whole difference,
//! kept in one place and chosen at build time by the chip feature.
//!
//! ## Why the raw driver API, either way
//!
//! RNDP frames are binary. Going through the console VFS would CR/LF-translate
//! them, and a frame containing `0x0A` would arrive corrupt — intermittently,
//! depending on its payload, which is the worst kind of wrong. Both drivers
//! below are byte-exact in both directions.

use esp_idf_svc::sys;

/// How long a read waits before returning empty, in FreeRTOS ticks (~100 Hz).
///
/// Short enough that a reboot request is acted on promptly, long enough that
/// the loop is not a spin.
const READ_TIMEOUT_TICKS: u32 = 5;

#[cfg(not(feature = "chip-esp32c3"))]
mod imp {
    use super::*;

    /// What this port is called in the banner and in `info`.
    pub const NAME: &str = "uart0";

    pub fn install() {
        unsafe {
            sys::uart_driver_install(0, 4096, 4096, 0, std::ptr::null_mut(), 0);
        }
    }

    pub fn read(buf: &mut [u8]) -> usize {
        let n = unsafe {
            sys::uart_read_bytes(
                0,
                buf.as_mut_ptr() as *mut core::ffi::c_void,
                buf.len() as u32,
                READ_TIMEOUT_TICKS,
            )
        };
        n.max(0) as usize
    }

    pub fn write(data: &[u8]) {
        unsafe {
            sys::uart_write_bytes(0, data.as_ptr() as *const core::ffi::c_void, data.len());
            sys::uart_wait_tx_done(0, 100);
        }
    }
}

#[cfg(feature = "chip-esp32c3")]
mod imp {
    use super::*;

    pub const NAME: &str = "usb-serial-jtag";

    pub fn install() {
        // The buffers are the driver's own, not ours; 1 KB in each direction
        // is ample for RNDP, whose largest frame is an app upload that arrives
        // in chunks anyway.
        let mut config = sys::usb_serial_jtag_driver_config_t {
            tx_buffer_size: 1024,
            rx_buffer_size: 1024,
        };
        unsafe {
            sys::usb_serial_jtag_driver_install(&mut config);
        }
    }

    pub fn read(buf: &mut [u8]) -> usize {
        let n = unsafe {
            sys::usb_serial_jtag_read_bytes(
                buf.as_mut_ptr() as *mut core::ffi::c_void,
                buf.len() as u32,
                READ_TIMEOUT_TICKS,
            )
        };
        n.max(0) as usize
    }

    pub fn write(data: &[u8]) {
        unsafe {
            sys::usb_serial_jtag_write_bytes(
                data.as_ptr() as *const core::ffi::c_void,
                data.len(),
                // A host that is not draining the port must not stall the
                // firmware: RNDP is request/response, so a reply nobody
                // collects is a reply nobody needed.
                100,
            );
        }
    }
}

pub use imp::{install, read, write, NAME};
