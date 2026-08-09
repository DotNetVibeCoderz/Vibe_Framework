//! RNDP over the board's own USB port.
//!
//! The same protocol every other target speaks, so `rustnet` talks to a Pico
//! with no adapter and no special case — `rustnet info --device serial:COM12`
//! and it answers.
//!
//! Cooperative, like the K210 port: [`Rndp::poll`] takes whatever the USB
//! endpoint holds, answers any complete frames, and returns. The main loop
//! calls it between interpreter fuel slices, so the device stays responsive to
//! the tools without threads or an executor.
//!
//! ## What this port answers
//!
//! `ping`, `info`, `logs`, `reboot`, the data commands, and — now that there
//! is flash to put them in — provisioning, app flashing, start/stop and
//! autostart. Every command it still does not implement is refused by name
//! rather than ignored, because a tool that gets silence cannot tell a
//! missing feature from a broken link.
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

use crate::storage::sys;
use crate::{board, FirmwareHost};

/// Bytes received and not yet parsed into a frame.
///
/// A flashed application arrives as one frame, so this has to hold the largest
/// signed container the board will take. 128 KB of the RP2040's 264 KB is a
/// lot to reserve permanently, so the buffer is a `Vec` that grows to what a
/// transfer actually needs and this is only the ceiling.
const RX_CAP: usize = 128 * 1024;

#[derive(Default)]
pub struct Rndp {
    rx: Vec<u8>,
    /// Set by a reboot request and acted on by the main loop, after the reply
    /// has gone out — a device that resets before answering leaves the tool
    /// waiting for a frame that will never arrive.
    pub reboot_requested: bool,
    /// Reboot into the ROM bootloader rather than into the image, so the next
    /// firmware goes on without anyone reaching for the BOOTSEL button. Every
    /// other target in this tree can be reflashed over its own link; this is
    /// how the Pico joins them.
    pub reboot_to_bootloader: bool,
    /// The provisioning key, cached from flash at boot so a signature check
    /// does not re-read it.
    pub pub_key: Option<Vec<u8>>,
    /// The name of the flashed application, or the compiled-in one.
    pub app_name: String,
    pub app_size: usize,
    /// Whether the interpreter should be given fuel. `stop` clears it, and a
    /// faulted app clears it too, so the port stays reachable either way.
    pub app_running: bool,
    /// An application accepted but not yet running: the main loop takes it,
    /// drops the current module and starts this one.
    pub pending_app: Option<Vec<u8>>,
}

impl Rndp {
    pub const fn new() -> Self {
        Self {
            rx: Vec::new(),
            reboot_requested: false,
            reboot_to_bootloader: false,
            pub_key: None,
            app_name: String::new(),
            app_size: 0,
            app_running: true,
            pending_app: None,
        }
    }

    /// Answer whatever the tools have sent.
    pub fn poll(&mut self, host: &mut FirmwareHost) {
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

        loop {
            match Frame::decode(&self.rx) {
                Ok(Some((frame, used))) => {
                    self.rx.drain(..used);
                    let response = match self.dispatch(&frame, host) {
                        Ok(payload) => Frame::new(ST_OK, payload),
                        Err(message) => Frame::new(ST_ERR, message.into_bytes()),
                    };
                    if let Some(usb) = host.usb.as_mut() {
                        usb.write(&response.encode());
                    }
                }
                Ok(None) => break,
                Err(_) => {
                    // Not a frame. Resynchronise on the next magic rather than
                    // discarding everything: a tool that opened the port
                    // mid-transfer leaves a partial frame in front of good
                    // ones.
                    self.resync();
                    break;
                }
            }
        }
    }

    /// Drop everything before the next frame start.
    fn resync(&mut self) {
        match self.rx.windows(2).position(|w| w == [0x52, 0x4E]) {
            Some(0) => self.rx.clear(),
            Some(start) => {
                self.rx.drain(..start);
            }
            None => self.rx.clear(),
        }
    }

    fn dispatch(&mut self, frame: &Frame, host: &mut FirmwareHost) -> Result<Vec<u8>, String> {
        match frame.code {
            CMD_PING => Ok(Vec::new()),

            CMD_INFO => {
                // The same shape every other target reports, so the tools and
                // the Workbench need no special case.
                let uptime = host.board_uptime_ms();
                let autostart = match rustnet_flashfs::read(&mut host.flash, sys::AUTOSTART) {
                    Ok(name) => format!("\"{}\"", String::from_utf8_lossy(&name)),
                    Err(_) => String::from("null"),
                };
                Ok(format!(
                    r#"{{"chip":"rp2040","board":"{}","version":"{}","protocol":{},"uptime_ms":{},"heap_used":{},"apps":1,"wifi":false,"active_app":"{}","running":{},"autostart":{},"provisioned":{},"storage_used":{},"transport":"usb-cdc","cpu_hz":{}}}"#,
                    board::NAME,
                    env!("CARGO_PKG_VERSION"),
                    rustnet_rndp::PROTOCOL_VERSION,
                    uptime,
                    crate::heap_used(),
                    self.app_name,
                    self.app_running,
                    autostart,
                    self.pub_key.is_some(),
                    rustnet_flashfs::used(&mut host.flash),
                    host.cpu_hz(),
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

            CMD_FLASH_DATA => {
                // `u16` path length, the path, then the bytes — the shape
                // docs/protocol.md gives. This port's filesystem is a flat
                // namespace of named blobs, so the name arrives as given.
                if frame.payload.len() < 2 {
                    return Err(String::from("empty payload"));
                }
                let path_len = u16::from_le_bytes([frame.payload[0], frame.payload[1]]) as usize;
                if frame.payload.len() < 2 + path_len {
                    return Err(String::from("truncated payload"));
                }
                let path = String::from_utf8_lossy(&frame.payload[2..2 + path_len]).to_string();
                let data = &frame.payload[2 + path_len..];
                rustnet_flashfs::write(&mut host.flash, &path, data)?;
                Ok(Vec::new())
            }

            CMD_READ_DATA => {
                let path = String::from_utf8_lossy(&frame.payload).to_string();
                rustnet_flashfs::read(&mut host.flash, &path)
            }

            // One slot, holding whatever is loaded — the compiled-in
            // application until `rustnet flash` replaces it, so this reads the
            // live name rather than the build-time constant.
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
                // The compiled-in application comes back, because something
                // has to run and a board with nothing loaded is a board that
                // looks broken.
                let _ = rustnet_flashfs::delete(&mut host.flash, sys::APP);
                let _ = rustnet_flashfs::delete(&mut host.flash, sys::APP_NAME);
                let _ = rustnet_flashfs::delete(&mut host.flash, sys::AUTOSTART);
                self.app_name = String::from(board::APP_NAME);
                self.app_size = 0;
                Ok(Vec::new())
            }

            CMD_SET_AUTOSTART => {
                if frame.payload.is_empty() {
                    let _ = rustnet_flashfs::delete(&mut host.flash, sys::AUTOSTART);
                } else {
                    rustnet_flashfs::write(&mut host.flash, sys::AUTOSTART, &frame.payload)?;
                }
                Ok(Vec::new())
            }

            CMD_PROVISION_KEY => {
                if frame.payload.is_empty() {
                    return Err(String::from("empty key"));
                }
                // Write-once in spirit rather than in silicon: a device whose
                // key can be replaced accepts anything its new owner signs, so
                // the second attempt is refused. Recovering from a lost key
                // means erasing storage from the bootloader, which needs
                // physical access — that is the point.
                if self.pub_key.is_some() {
                    return Err(String::from("already provisioned"));
                }
                rustnet_flashfs::write(&mut host.flash, sys::PUB_KEY, &frame.payload)?;
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
                let image = verify(container, key, ChipFamily::Rp2040)
                    .map_err(|e| format!("signature check failed: {e}"))?;
                if image.kind != ImageKind::App {
                    return Err(String::from("container is not an app image"));
                }
                // Refuse anything the interpreter could not load, before it
                // replaces a working application.
                Module::from_bytes(image.payload).map_err(|e| format!("invalid RNX: {e}"))?;

                rustnet_flashfs::write(&mut host.flash, sys::APP, image.payload)?;
                rustnet_flashfs::write(&mut host.flash, sys::APP_NAME, name.as_bytes())?;
                self.app_name = String::from(name);
                self.app_size = image.payload.len();
                self.pending_app = Some(image.payload.to_vec());
                Ok(Vec::new())
            }

            CMD_REBOOT => {
                // Recorded, not done. The reply has to reach the tool first.
                self.reboot_requested = true;
                // A payload of `01` asks for the bootloader instead of a plain
                // reset. `rustnet reboot` sends nothing, so the ordinary path
                // is unaffected and only a tool that means it lands in BOOTSEL.
                self.reboot_to_bootloader = frame.payload.first() == Some(&1);
                Ok(Vec::new())
            }

            other => Err(format!(
                "command {other:#04x} is not implemented on this target (no storage yet)"
            )),
        }
    }
}
