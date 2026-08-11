//! RNDP over the board's own USB port.
//!
//! The same protocol every other target speaks, so `rustnet` talks to a Meadow
//! with no adapter and no special case — `rustnet info --device serial:COM15`
//! and it answers.
//!
//! Cooperative, like the Pico and K210 ports: [`Rndp::poll`] takes whatever
//! the USB endpoint holds, answers any complete frames, and returns. The main
//! loop calls it between interpreter fuel slices, so the device stays
//! responsive to the tools without threads or an executor.
//!
//! ## What this port answers, and what it does not
//!
//! `ping`, `info`, `logs` and `reboot`. Not flashing, not provisioning, not
//! apps — those need storage, and this port has none yet: the application is
//! embedded in the image. Every unimplemented command is refused **by name**
//! rather than ignored, because a tool that gets silence cannot tell a missing
//! feature from a broken link.
//!
//! ## Why the receive side is a ring rather than a read
//!
//! A USB bulk endpoint holds one 64-byte packet. A `rustnet` frame is usually
//! larger, and the endpoint has to be drained promptly or the host is NAKed
//! and the transfer stalls — so bytes are taken out on every poll and
//! accumulated here, where a partial frame can wait for the rest of itself.

use alloc::format;
use alloc::string::{String, ToString};
use alloc::vec::Vec;

use rustnet_core::Module;
use rustnet_rndp::{
    Frame, CMD_ERASE_APP, CMD_FLASH_APP, CMD_FLASH_DATA, CMD_GET_LOGS, CMD_INFO, CMD_LIST_APPS,
    CMD_PING, CMD_PROVISION_KEY, CMD_READ_DATA, CMD_REBOOT, CMD_SET_AUTOSTART, CMD_START_APP,
    CMD_STOP_APP, ST_ERR, ST_OK,
};
use rustnet_secureboot::{verify, ChipFamily, ImageKind};

use crate::qspi::sys;

use crate::{board, chipid, FirmwareHost, APP_NAME};

/// Bytes received and not yet parsed into a frame.
///
/// A flashed application arrives as one frame, so this has to hold the largest
/// signed container the board will take. It is a ceiling, not a reservation —
/// the buffer is a `Vec` that grows to what a transfer actually needs — but
/// the ceiling has to be above the largest real payload or the frame is
/// dropped whole and the tool simply times out with nothing to say why. Two
/// kilobytes was left over from before this port had storage, and a 13 KB
/// application hit it immediately.
const RX_CAP: usize = 128 * 1024;

/// Which pipe a frame arrived on, so the answer goes back the same way.
#[derive(Clone, Copy, PartialEq)]
enum Link {
    Usb,
    Uart,
}

#[derive(Default)]
pub struct Rndp {
    rx: Vec<u8>,
    rx_uart: Vec<u8>,
    /// Set by a reboot request and acted on by the main loop, after the reply
    /// has gone out — a device that resets before answering leaves the tool
    /// waiting for a frame that will never arrive.
    pub reboot_requested: bool,
    /// The provisioning key, cached from flash at boot.
    pub pub_key: Option<Vec<u8>>,
    /// The name of the flashed application, or the compiled-in one.
    pub app_name: String,
    pub app_size: usize,
    /// Whether the interpreter should be given fuel.
    pub app_running: bool,
    /// An application accepted but not yet running: the main loop takes it,
    /// drops the current module and starts this one.
    pub pending_app: Option<Vec<u8>>,
}

impl Rndp {
    pub const fn new() -> Self {
        Self {
            rx: Vec::new(),
            rx_uart: Vec::new(),
            reboot_requested: false,
            pub_key: None,
            app_name: String::new(),
            app_size: 0,
            app_running: true,
            pending_app: None,
        }
    }

    /// Answer whatever the tools have sent, on either link.
    ///
    /// Two independent buffers rather than one: a partial frame on the serial
    /// adapter must not be spliced into a partial frame on USB, and a reply
    /// has to go back where its request came from.
    pub fn poll(&mut self, host: &mut FirmwareHost) {
        self.poll_usb(host);
        self.poll_uart(host);
    }

    fn poll_uart(&mut self, host: &mut FirmwareHost) {
        let mut chunk = [0u8; 64];
        loop {
            let n = match host.uart.as_mut() {
                Some(u) => u.read(&mut chunk),
                None => 0,
            };
            if n == 0 {
                break;
            }
            if self.rx_uart.len() + n <= RX_CAP {
                self.rx_uart.extend_from_slice(&chunk[..n]);
            } else {
                self.rx_uart.clear();
            }
            if n < chunk.len() {
                break;
            }
        }
        let mut buf = core::mem::take(&mut self.rx_uart);
        self.drain(&mut buf, host, Link::Uart);
        self.rx_uart = buf;
    }

    fn poll_usb(&mut self, host: &mut FirmwareHost) {
        let mut chunk = [0u8; 64];
        loop {
            let n = match host.usb.as_mut() {
                Some(usb) => usb.read(&mut chunk),
                None => 0,
            };
            if n == 0 {
                break;
            }
            if self.rx.len() + n <= RX_CAP {
                self.rx.extend_from_slice(&chunk[..n]);
            } else {
                // A frame that cannot fit is dropped whole rather than
                // truncated: half a frame desynchronises the stream and every
                // command after it fails for no visible reason.
                self.rx.clear();
            }
            if n < chunk.len() {
                break;
            }
        }

        let mut buf = core::mem::take(&mut self.rx);
        self.drain(&mut buf, host, Link::Usb);
        self.rx = buf;
    }

    /// Answer every complete frame in `buf`, replying on `link`.
    fn drain(&mut self, buf: &mut Vec<u8>, host: &mut FirmwareHost, link: Link) {
        loop {
            match Frame::decode(buf) {
                Ok(Some((frame, used))) => {
                    buf.drain(..used);
                    if link == Link::Uart {
                        // Something speaks the protocol here now; stop writing
                        // console text into its frames.
                        host.uart_is_protocol = true;
                    }
                    let response = match self.dispatch(&frame, host) {
                        Ok(payload) => Frame::new(ST_OK, payload),
                        Err(message) => Frame::new(ST_ERR, message.into_bytes()),
                    };
                    let bytes = response.encode();
                    match link {
                        Link::Usb => {
                            if let Some(usb) = host.usb.as_mut() {
                                usb.write(&bytes);
                            }
                        }
                        Link::Uart => {
                            if let Some(u) = host.uart.as_mut() {
                                u.write(&bytes);
                            }
                        }
                    }
                }
                Ok(None) => break,
                Err(_) => {
                    // Not a frame. Resynchronise on the next magic rather than
                    // discarding everything: a tool that opened the port
                    // mid-transfer leaves a partial frame in front of good ones.
                    match buf.windows(2).position(|w| w == [0x52, 0x4E]) {
                        Some(0) => buf.clear(),
                        Some(start) => {
                            buf.drain(..start);
                        }
                        None => buf.clear(),
                    }
                    break;
                }
            }
        }
    }

    fn dispatch(&mut self, frame: &Frame, host: &mut FirmwareHost) -> Result<Vec<u8>, String> {
        match frame.code {
            CMD_PING => Ok(Vec::new()),

            CMD_INFO => {
                // The same shape every other target reports, so the tools and
                // the Workbench need no special case. Fields this port does not
                // have are reported honestly rather than invented: there is no
                // storage, so no apps and no autostart.
                //
                // `chip_id` is the extra one, and it is here because the exact
                // part is not in the vendor's documentation — the board says
                // what it is instead of this firmware asserting it.
                let uptime = host.board_uptime_ms();
                let id = chipid::identify();
                Ok(format!(
                    r#"{{"chip":"stm32f7","board":"{}","version":"{}","protocol":{},"uptime_ms":{},"heap_used":{},"apps":{},"wifi":false,"active_app":"{}","running":{},"autostart":{},"provisioned":{},"storage_used":{},"transport":"usb-cdc+uart4","cpu_hz":{},"hse_hz":{},"chip_id":"{}","chip_expected":{}}}"#,
                    board::NAME,
                    env!("CARGO_PKG_VERSION"),
                    rustnet_rndp::PROTOCOL_VERSION,
                    uptime,
                    crate::heap_used(),
                    if self.app_size > 0 { 1 } else { 0 },
                    self.app_name,
                    self.app_running,
                    // The name it will start with, or null. Read from flash so
                    // it reports what the device will actually do rather than
                    // what this session happened to set.
                    match host
                        .flash
                        .as_mut()
                        .and_then(|f| rustnet_flashfs::read(f, sys::AUTOSTART).ok())
                    {
                        Some(name) => format!("\"{}\"", String::from_utf8_lossy(&name)),
                        None => String::from("null"),
                    },
                    self.pub_key.is_some(),
                    host.flash
                        .as_mut()
                        .map(|f| rustnet_flashfs::used(f))
                        .unwrap_or(0),
                    host.cpu_hz(),
                    board::HSE_HZ,
                    id.describe(),
                    id.is_expected(),
                )
                .into_bytes())
            }

            CMD_GET_LOGS => {
                let max = if frame.payload.len() >= 4 {
                    u32::from_le_bytes(frame.payload[..4].try_into().unwrap()) as usize
                } else {
                    100
                };
                Ok(host.tail_logs(max).into_bytes())
            }

            // One slot, holding whatever is loaded.
            CMD_LIST_APPS => Ok(format!("{}	{}
", self.app_name, self.app_size).into_bytes()),

            CMD_START_APP => {
                self.app_running = true;
                Ok(Vec::new())
            }

            CMD_STOP_APP => {
                self.app_running = false;
                Ok(Vec::new())
            }

            CMD_ERASE_APP => {
                let flash = host.flash.as_mut().ok_or_else(no_storage)?;
                let _ = rustnet_flashfs::delete(flash, sys::APP);
                let _ = rustnet_flashfs::delete(flash, sys::APP_NAME);
                let _ = rustnet_flashfs::delete(flash, sys::AUTOSTART);
                self.app_name = String::from(APP_NAME);
                self.app_size = 0;
                Ok(Vec::new())
            }

            CMD_SET_AUTOSTART => {
                let flash = host.flash.as_mut().ok_or_else(no_storage)?;
                if frame.payload.is_empty() {
                    let _ = rustnet_flashfs::delete(flash, sys::AUTOSTART);
                } else {
                    rustnet_flashfs::write(flash, sys::AUTOSTART, &frame.payload)?;
                }
                Ok(Vec::new())
            }

            CMD_PROVISION_KEY => {
                if frame.payload.is_empty() {
                    return Err(String::from("empty key"));
                }
                // Write-once in spirit: a device whose key can be replaced
                // accepts anything its new owner signs. Recovering from a lost
                // key means erasing storage, which needs the board in hand.
                if self.pub_key.is_some() {
                    return Err(String::from("already provisioned"));
                }
                let flash = host.flash.as_mut().ok_or_else(no_storage)?;
                rustnet_flashfs::write(flash, sys::PUB_KEY, &frame.payload)?;
                self.pub_key = Some(frame.payload.clone());
                Ok(Vec::new())
            }

            CMD_FLASH_APP => {
                // [name_len:u8][name][RNSB container] — the same payload the
                // std service takes, so the tools need no special case.
                let p = &frame.payload;
                if p.is_empty() {
                    return Err(String::from("empty payload"));
                }
                let name_len = p[0] as usize;
                if p.len() < 1 + name_len {
                    return Err(String::from("truncated payload"));
                }
                let name = core::str::from_utf8(&p[1..1 + name_len])
                    .map_err(|_| String::from("app name is not UTF-8"))?;
                let container = &p[1 + name_len..];

                let key = self
                    .pub_key
                    .as_ref()
                    .ok_or_else(|| String::from("device not provisioned: flash a key first"))?;
                let image = verify(container, key, ChipFamily::Stm32)
                    .map_err(|e| format!("signature check failed: {e}"))?;
                if image.kind != ImageKind::App {
                    return Err(String::from("container is not an app image"));
                }
                // Refuse anything the interpreter could not load, before it
                // replaces a working application.
                Module::from_bytes(image.payload).map_err(|e| format!("invalid RNX: {e}"))?;

                let flash = host.flash.as_mut().ok_or_else(no_storage)?;
                rustnet_flashfs::write(flash, sys::APP, image.payload)?;
                rustnet_flashfs::write(flash, sys::APP_NAME, name.as_bytes())?;
                self.app_name = String::from(name);
                self.app_size = image.payload.len();
                self.pending_app = Some(image.payload.to_vec());
                Ok(Vec::new())
            }

            CMD_FLASH_DATA => {
                // `u16` path length, the path, then the bytes — the shape
                // docs/protocol.md gives.
                if frame.payload.len() < 2 {
                    return Err(String::from("empty payload"));
                }
                let n = u16::from_le_bytes([frame.payload[0], frame.payload[1]]) as usize;
                if frame.payload.len() < 2 + n {
                    return Err(String::from("truncated payload"));
                }
                let path = String::from_utf8_lossy(&frame.payload[2..2 + n]).to_string();
                let data = &frame.payload[2 + n..];
                let flash = host.flash.as_mut().ok_or_else(no_storage)?;
                rustnet_flashfs::write(flash, &path, data)?;
                Ok(Vec::new())
            }

            CMD_READ_DATA => {
                let path = String::from_utf8_lossy(&frame.payload).to_string();
                let flash = host.flash.as_mut().ok_or_else(no_storage)?;
                rustnet_flashfs::read(flash, &path)
            }

            CMD_REBOOT => {
                // Recorded, not done. The reply has to reach the tool first.
                self.reboot_requested = true;
                Ok(Vec::new())
            }

            other => Err(format!(
                "command {other:#04x} is not implemented on this target (no storage yet)"
            )),
        }
    }
}

fn no_storage() -> String {
    String::from("no QSPI storage on this board")
}
