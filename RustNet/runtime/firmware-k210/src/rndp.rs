//! RNDP served over UARTHS.
//!
//! A cooperative, single-threaded service: [`Rndp::poll`] takes whatever the
//! receive ring holds, answers any complete frames, and returns. The main loop
//! calls it between interpreter fuel slices, so the device stays responsive to
//! the tools without threads, an RTOS, or an executor.
//!
//! ## Receive: a FIFO, a deadline, and two producers
//!
//! UARTHS has an 8-entry receive FIFO. At 115200 baud that is 694 µs of traffic
//! — generous next to the STM32's single byte and 87 µs, but still a hard
//! deadline: a gap longer than that between two reads *loses* bytes rather than
//! delaying them, and a lost byte in the middle of a multi-kilobyte `rustnet
//! flash` payload fails the whole upload.
//!
//! So the FIFO is emptied from two places, and [`drain_fifo`] is the same code
//! either way:
//!
//! - the UARTHS interrupt, via the PLIC (feature `rx-interrupt`, on by default);
//! - polling — before every fuel slice, and every 100 µs while an application
//!   sleeps.
//!
//! Belt and braces on purpose. The interrupt gives margin no polling interval
//! can guarantee; the polled drain means that if the PLIC setup turns out to be
//! wrong on real silicon, the port still talks to the tools instead of appearing
//! dead. `info` reports `rx_dropped` and `max_poll_gap_us`, so which one is
//! actually carrying the traffic is a measurement rather than a hope.
//!
//! Having two producers is why the drain sits in a critical section. The
//! consumer — the main loop — needs none: one producer at a time and one
//! consumer is the single-producer/single-consumer case the plain atomics
//! already order correctly.
//!
//! Only commands needing neither a filesystem nor a full crypto stack are
//! answered here. `runtime/firmware`'s `DeviceService` is the complete
//! implementation, and it is `std`-bound.

use alloc::format;
use alloc::string::{String, ToString};
use alloc::vec;
use alloc::vec::Vec;
use core::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

use riscv::interrupt::machine as interrupt;
use rustnet_core::Module;
use rustnet_hal::Board as _;
use rustnet_hal_k210::{delay, uart::Uarths};
use rustnet_rndp::*;
use rustnet_secureboot::{verify, ChipFamily, ImageKind};

use crate::{board, FirmwareHost, APP_NAME};

// ---------------------------------------------------------------------------
// Receive ring
// ---------------------------------------------------------------------------

/// The ring has to absorb everything that arrives between two *consumer* passes
/// — the drain only moves bytes from the FIFO into here, and an overflow
/// corrupts a frame rather than merely delaying it.
///
/// 128 KB is eleven seconds of continuous traffic at 115200. That is far more
/// than it sounds like it needs, and the size was raised from 16 KB after an
/// upload failed once against a graphics-heavy application: the consumer only
/// runs between interpreter fuel slices, and a scene spending 200 ms a frame
/// leaves the ring filling for that whole time. 16 KB covered 1.4 seconds,
/// which is fine until something stalls, and a stall shows up as a `rustnet
/// flash` that times out with nothing in the logs to say why.
///
/// The frame ceiling is [`board::MAX_RNDP_FRAME`] — half a megabyte — so this
/// still does not guarantee a whole payload fits unread. It does not need to:
/// what it has to cover is the gap between two polls, not the whole transfer.
/// On a chip with 6 MB of SRAM there is no reason to be tight about it.
const RX_CAP: usize = 128 * 1024;

static mut RX_BUF: [u8; RX_CAP] = [0; RX_CAP];
/// Written only by [`drain_fifo`], which holds a critical section.
static RX_HEAD: AtomicUsize = AtomicUsize::new(0);
/// Written only by the main loop.
static RX_TAIL: AtomicUsize = AtomicUsize::new(0);
/// Bytes the ring had no room for. Reported over RNDP rather than hidden.
static RX_DROPPED: AtomicUsize = AtomicUsize::new(0);
/// Times the UARTHS interrupt has been serviced.
///
/// Reported by `info` because "is the PLIC setup actually working?" is
/// otherwise unanswerable: the polled drain covers for a dead interrupt so
/// completely that a board with no working interrupt at all looks fine until an
/// application gets busy enough to widen the polling gap past the receive
/// FIFO's 694 µs, and then uploads start failing for no visible reason.
pub(crate) static RX_IRQS: AtomicUsize = AtomicUsize::new(0);

/// Move everything the UARTHS FIFO holds into the ring.
///
/// Called from the interrupt handler and from the polled paths, hence the
/// critical section: two producers writing `RX_HEAD` would otherwise be able to
/// hand out the same slot twice.
pub fn drain_fifo() {
    interrupt::free(|| {
        while let Some(byte) = Uarths::take_byte() {
            let head = RX_HEAD.load(Ordering::Relaxed);
            let next = (head + 1) % RX_CAP;
            if next == RX_TAIL.load(Ordering::Acquire) {
                RX_DROPPED.fetch_add(1, Ordering::Relaxed);
                continue;
            }
            // SAFETY: inside the critical section this is the only writer of
            // RX_BUF[head], and `head` is only published to the consumer after
            // the write.
            unsafe {
                core::ptr::addr_of_mut!(RX_BUF).cast::<u8>().add(head).write_volatile(byte)
            };
            RX_HEAD.store(next, Ordering::Release);
        }
    });
}

fn rx_take(out: &mut [u8]) -> usize {
    let head = RX_HEAD.load(Ordering::Acquire);
    let mut tail = RX_TAIL.load(Ordering::Relaxed);
    let mut n = 0;
    while tail != head && n < out.len() {
        // SAFETY: the consumer is the only reader, and this slot was published
        // by a producer before `head` advanced past it.
        out[n] = unsafe { core::ptr::addr_of!(RX_BUF).cast::<u8>().add(tail).read_volatile() };
        tail = (tail + 1) % RX_CAP;
        n += 1;
    }
    RX_TAIL.store(tail, Ordering::Release);
    n
}

// ---------------------------------------------------------------------------
// PLIC
// ---------------------------------------------------------------------------

#[cfg(feature = "rx-interrupt")]
mod plic {
    use core::sync::atomic::Ordering;

    use super::{drain_fifo, interrupt, RX_IRQS};
    use rustnet_hal_k210::{reg, uart::Uarths};

    const BASE: usize = 0x0C00_0000;
    /// Per-source priority, one word each. 0 means "never interrupt".
    const PRIORITY: usize = BASE;
    /// Per-context enable bitmaps, 32 words (1024 sources) per context.
    const ENABLE: usize = BASE + 0x2000;
    /// Per-context configuration: threshold, then claim/complete four bytes on.
    const CONTEXT_CONFIG: usize = BASE + 0x0020_0000;
    const CONTEXT_STRIDE: usize = 0x1000;
    /// The K210's PLIC gives each hart a machine-mode context, and hart 0's is
    /// 0. There is no supervisor context on this part, so a context number is
    /// just a hart id.
    const CONTEXT: usize = 0;

    const THRESHOLD: usize = CONTEXT_CONFIG + CONTEXT * CONTEXT_STRIDE;
    const CLAIM: usize = THRESHOLD + 4;

    const IRQ_UARTHS: u32 = rustnet_hal_k210::uart::IRQ_UARTHS;
    /// The companion radio's port. Its FIFO is sixteen bytes and the module
    /// speaks unbidden, so it needs the same treatment the console gets.
    const IRQ_UART1: u32 = rustnet_hal_k210::uart::IRQ_UART1;

    /// Route the UARTHS source to this hart and unmask machine external
    /// interrupts.
    pub fn arm() {
        // Any priority above the threshold will do; 1 is the lowest that is not
        // "masked".
        reg::write(THRESHOLD, 0);
        for source in [IRQ_UARTHS, IRQ_UART1] {
            // Any priority above the threshold will do; 1 is the lowest that
            // is not "masked".
            reg::write(PRIORITY + 4 * source as usize, 1);
            reg::modify(
                ENABLE + CONTEXT * 0x80 + 4 * (source as usize / 32),
                0,
                1 << (source % 32),
            );
        }

        // Retire anything the ROM left claimed. A claim that is never completed
        // leaves its source masked for good, which would look exactly like the
        // interrupt never having been enabled.
        for _ in 0..8 {
            let pending = reg::read(CLAIM);
            if pending == 0 {
                break;
            }
            reg::write(CLAIM, pending);
        }

        Uarths::set_rx_interrupt(true);
        // SAFETY: not inside a critical section, and the only source enabled
        // above is one this firmware serves. riscv-rt cleared `mie` at reset, so
        // the machine timer and software interrupts stay masked and cannot land
        // in DefaultHandler.
        unsafe {
            riscv::register::mie::set_mext();
            interrupt::enable();
        }
    }

    /// The PLIC's machine-external handler.
    ///
    /// Claims in a loop and **always completes**, including for a source it does
    /// not recognise: an unacknowledged claim masks that source permanently, and
    /// a handler that returns without completing is how an interrupt storm
    /// starts.
    #[export_name = "MachineExternal"]
    unsafe extern "C" fn machine_external() {
        loop {
            let source = reg::read(CLAIM);
            if source == 0 {
                // Spurious, or nothing left pending.
                break;
            }
            if source == IRQ_UARTHS {
                RX_IRQS.fetch_add(1, Ordering::Relaxed);
                drain_fifo();
            }
            if source == IRQ_UART1 {
                crate::espat::drain_fifo();
            }
            reg::write(CLAIM, source);
        }
    }
}

/// Start pulling received bytes in. Call once, after the HAL has configured the
/// console.
pub fn start_receiving() {
    #[cfg(feature = "rx-interrupt")]
    plic::arm();

    // Whether or not the interrupt is armed, take whatever is already waiting.
    drain_fifo();
    crate::espat::drain_fifo();
}

// ---------------------------------------------------------------------------
// Instrumentation
// ---------------------------------------------------------------------------
//
// The FIFO can only cover as much latency as the consumer actually has. Rather
// than guess at what stalls it, measure: these record the worst gap between two
// polls and how long an RSA verify takes, both straight off `mcycle`. Reported
// by `info`, and the gap is reset when it is read so a measurement can be scoped
// to one operation.
//
// No wrap handling here, unlike the Cortex-M port: `mcycle` is 64 bits wide on
// RV64 and takes 1400 years to overflow at 400 MHz.

static LAST_POLL_CYC: AtomicU64 = AtomicU64::new(0);
static MAX_POLL_GAP_CYC: AtomicU64 = AtomicU64::new(0);
static LAST_VERIFY_CYC: AtomicU64 = AtomicU64::new(0);

/// Note that a poll happened, folding the gap since the previous one into the
/// running maximum.
fn mark_poll() {
    let now = delay::cycles();
    let previous = LAST_POLL_CYC.swap(now, Ordering::Relaxed);
    if previous != 0 {
        MAX_POLL_GAP_CYC.fetch_max(now.saturating_sub(previous), Ordering::Relaxed);
    }
}

// ---------------------------------------------------------------------------
// Service
// ---------------------------------------------------------------------------

/// Refuse to buffer more than this while hunting for a frame. It has to hold a
/// whole signed application container, so it is the ceiling on what `rustnet
/// flash` can deliver here.
const MAX_FRAME: usize = board::MAX_RNDP_FRAME;

const FW_VERSION: &str = env!("CARGO_PKG_VERSION");

pub struct Rndp {
    rx: Vec<u8>,
    /// Whether the application gets fuel. `start`/`stop` flip it.
    pub app_running: bool,
    /// PKCS#1 DER public key from `rustnet provision`, mirrored into flash so it
    /// survives a power cycle.
    pub_key: Option<Vec<u8>>,
    /// An application accepted by `rustnet flash`, waiting for the main loop to
    /// rebuild the interpreter around it.
    pending_app: Option<Vec<u8>>,
    /// Name of whatever is loaded, for `info` and `apps list`.
    pub app_name: String,
    /// Size of the loaded RNX, likewise.
    pub app_size: usize,
}

impl Default for Rndp {
    fn default() -> Self {
        Self::new()
    }
}

impl Rndp {
    pub fn new() -> Self {
        Self {
            rx: Vec::new(),
            app_running: true,
            pub_key: None,
            pending_app: None,
            app_name: String::from(APP_NAME),
            app_size: crate::APP_RNX.len(),
        }
    }

    pub fn set_pub_key(&mut self, key: Vec<u8>) {
        self.pub_key = Some(key);
    }

    pub fn set_app_name(&mut self, name: &str) {
        self.app_name = String::from(name);
    }

    pub fn set_app_size(&mut self, size: usize) {
        self.app_size = size;
    }

    pub fn has_pending_app(&self) -> bool {
        self.pending_app.is_some()
    }

    pub fn take_pending_app(&mut self) -> Option<Vec<u8>> {
        self.pending_app.take()
    }

    /// Take whatever the ring holds and answer any complete frames.
    pub fn poll(&mut self, host: &mut FirmwareHost) {
        mark_poll();
        // Anything still sitting in the FIFO belongs in the ring before we
        // decide the stream is exhausted.
        drain_fifo();

        let mut chunk = [0u8; 256];
        loop {
            let n = rx_take(&mut chunk);
            if n == 0 {
                break;
            }
            self.rx.extend_from_slice(&chunk[..n]);
            if self.rx.len() > MAX_FRAME {
                let excess = self.rx.len() - MAX_FRAME / 2;
                self.rx.drain(..excess);
            }
        }

        loop {
            self.skip_to_magic();

            // A corrupted stream can contain a stray "RN". Without this the
            // decoder reads the bogus length that follows, waits forever for a
            // frame that will never complete, and every real frame queues up
            // behind it — the port goes dead until reset. Anything claiming more
            // than we would ever accept is a false start, not a frame.
            if self.rx.len() >= 7 {
                let claimed = u32::from_le_bytes([self.rx[3], self.rx[4], self.rx[5], self.rx[6]]);
                if claimed as usize > MAX_FRAME {
                    self.rx.drain(..1);
                    continue;
                }
            }

            match Frame::decode(&self.rx) {
                Ok(Some((frame, used))) => {
                    self.rx.drain(..used);
                    // `drain` keeps the capacity, and an application container
                    // is kilobytes; hand it back before it accumulates.
                    if self.rx.capacity() > 4096 && self.rx.len() < 512 {
                        self.rx.shrink_to_fit();
                    }
                    let response = match self.dispatch(&frame, host) {
                        Ok(payload) => Frame::ok(payload),
                        Err(message) => Frame::err(message),
                    };
                    host.write_raw(&response.encode());
                }
                Ok(None) => break,
                Err(_) => {
                    // Corrupt frame: step past this magic and resynchronise on
                    // the next, rather than discarding the whole buffer.
                    if self.rx.len() < 2 {
                        break;
                    }
                    self.rx.drain(..1);
                }
            }
        }
    }

    /// Drop everything before the next frame start. The tools do the same in the
    /// other direction, because a serial line carries log output and boot
    /// banners between frames.
    fn skip_to_magic(&mut self) {
        if self.rx.len() < 2 {
            return;
        }
        match self.rx.windows(2).position(|w| w == MAGIC) {
            Some(0) => {}
            Some(start) => {
                self.rx.drain(..start);
            }
            // No magic anywhere: keep the last byte, which may be the first half
            // of a magic split across two reads.
            None => {
                let keep = self.rx.len() - 1;
                self.rx.drain(..keep);
            }
        }
    }

    fn dispatch(&mut self, frame: &Frame, host: &mut FirmwareHost) -> Result<Vec<u8>, String> {
        match frame.code {
            CMD_PING => Ok(vec![PROTOCOL_VERSION]),

            CMD_INFO => {
                let per_us = (host.cpu_hz() as u64 / 1_000_000).max(1);
                let max_gap_us = MAX_POLL_GAP_CYC.swap(0, Ordering::Relaxed) / per_us;
                let verify_us = LAST_VERIFY_CYC.load(Ordering::Relaxed) / per_us;
                Ok(format!(
                r#"{{"chip":"k210","board":"{}","version":"{}","protocol":{},"uptime_ms":{},"heap_used":{},"apps":1,"wifi":false,"active_app":"{}","running":{},"autostart":null,"transport":"{}","cpu_hz":{},"rx_dropped":{},"esp_rx_dropped":{},"rx_irqs":{},"max_poll_gap_us":{},"last_verify_us":{},"storage_used":{}}}"#,
                board::NAME,
                FW_VERSION,
                PROTOCOL_VERSION,
                host.uptime_ms(),
                crate::heap_used(),
                self.app_name,
                self.app_running,
                host.transport_name(),
                host.cpu_hz(),
                RX_DROPPED.load(Ordering::Relaxed),
                crate::espat::dropped(),
                RX_IRQS.load(Ordering::Relaxed),
                max_gap_us,
                verify_us,
                host.storage_used(),
            )
            .into_bytes())
            }

            // One slot, holding whatever is loaded — which is the compiled-in
            // application until `rustnet flash` replaces it, so this has to read
            // the live name, not the build-time constant.
            CMD_LIST_APPS => Ok(format!("{}\t{}\n", self.app_name, self.app_size).into_bytes()),

            CMD_START_APP => {
                self.app_running = true;
                Ok(Vec::new())
            }

            CMD_STOP_APP => {
                self.app_running = false;
                Ok(Vec::new())
            }

            CMD_GET_LOGS => {
                let max = if frame.payload.len() >= 4 {
                    u32::from_le_bytes(frame.payload[..4].try_into().unwrap()) as usize
                } else {
                    100
                };
                Ok(host.log_tail(max).into_bytes())
            }

            // SYSCTL's soft reset, which restarts the SoC — so the mask ROM runs
            // again and reloads the image from flash, rather than merely
            // re-entering `main` with every peripheral as we left it.
            CMD_REBOOT => host.board.power().reset(),

            CMD_FLASH_DATA => {
                // `u16` path length, the path, then the bytes. The std
                // firmware puts these under `/data/`; this port's filesystem is
                // a flat namespace of named blobs, so the name arrives as given
                // and an application opens it by the same name.
                if frame.payload.len() < 2 {
                    return Err(String::from("empty payload"));
                }
                let path_len = u16::from_le_bytes([frame.payload[0], frame.payload[1]]) as usize;
                if frame.payload.len() < 2 + path_len {
                    return Err(String::from("truncated payload"));
                }
                let path = String::from_utf8_lossy(&frame.payload[2..2 + path_len]).to_string();
                let data = &frame.payload[2 + path_len..];
                let files = host
                    .board
                    .extmem(1)
                    .map_err(|e| format!("no filesystem on this board: {e}"))?;
                rustnet_flashfs::write(files, &path, data)?;
                let _ = core::fmt::Write::write_fmt(
                    host,
                    format_args!("[fs] wrote {path} ({} bytes)
", data.len()),
                );
                Ok(Vec::new())
            }
            CMD_WIFI_CONFIG => {
                // A newline-separated SSID and PSK, per docs/protocol.md. This
                // is the only way credentials reach the board: they are never
                // compiled into an application.
                let text = String::from_utf8_lossy(&frame.payload);
                let (ssid, psk) = text.split_once('\n').ok_or("expected ssid\\npsk")?;
                host.wifi.ssid = ssid.to_string();
                host.wifi.psk = psk.to_string();
                // To flash, not just to RAM — see espat::CREDENTIALS_FILE for
                // why RAM alone cannot work on a board whose reset line is
                // wired to its serial handshake.
                if let Err(e) = host.store_wifi() {
                    let _ = core::fmt::Write::write_fmt(
                        host,
                        format_args!("[wifi] credentials not saved: {e}\n"),
                    );
                }
                // The SSID is logged and the PSK is not, deliberately: `logs`
                // is readable by anything that can reach the port.
                let _ = core::fmt::Write::write_fmt(
                    host,
                    format_args!("[wifi] configured for '{ssid}'
"),
                );
                Ok(Vec::new())
            }
            CMD_PROVISION_KEY => {
                if frame.payload.is_empty() {
                    return Err("empty key".to_string());
                }
                self.pub_key = Some(frame.payload.clone());
                host.persist(crate::storage::KIND_PUB_KEY, &frame.payload);
                Ok(Vec::new())
            }

            CMD_FLASH_APP => {
                // [name_len:u8][name][RNSB container] — the same payload the std
                // service takes, so the tools need no special case.
                let p = &frame.payload;
                if p.is_empty() {
                    return Err("empty payload".to_string());
                }
                let name_len = p[0] as usize;
                if p.len() < 1 + name_len {
                    return Err("truncated payload".to_string());
                }
                let name = core::str::from_utf8(&p[1..1 + name_len])
                    .map_err(|_| "app name is not UTF-8".to_string())?;
                let container = &p[1 + name_len..];

                let key = self
                    .pub_key
                    .as_ref()
                    .ok_or_else(|| "device not provisioned: flash a key first".to_string())?;

                let started = delay::cycles();
                let verified = verify(container, key, ChipFamily::K210);
                LAST_VERIFY_CYC
                    .store(delay::cycles().saturating_sub(started), Ordering::Relaxed);
                let image = verified.map_err(|e| format!("signature check failed: {e}"))?;
                if image.kind != ImageKind::App {
                    return Err("container is not an app image".to_string());
                }
                // Refuse anything the interpreter could not load, before it
                // replaces a working application.
                Module::from_bytes(image.payload).map_err(|e| format!("invalid RNX: {e}"))?;

                self.app_name = String::from(name);
                self.app_size = image.payload.len();
                host.persist(crate::storage::KIND_APP, image.payload);
                host.persist(crate::storage::KIND_APP_NAME, name.as_bytes());
                self.pending_app = Some(image.payload.to_vec());
                Ok(Vec::new())
            }

            other => Err(format!("command {other:#04x} is not implemented on this target")),
        }
    }
}
