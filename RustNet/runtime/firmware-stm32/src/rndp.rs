//! RNDP served over the console UART.
//!
//! A cooperative, single-threaded service: [`Rndp::poll`] takes whatever the
//! receive ring holds, answers any complete frames, and returns. The main loop
//! calls it between interpreter fuel slices, so the device stays responsive to
//! the tools without threads, an RTOS, or an executor.
//!
//! **Receive is interrupt-driven, and has to be.** The F4's USART has a
//! one-byte receive register and no FIFO, so at 115200 baud a byte must be
//! taken every ~87 µs. An interpreter fuel slice runs for tens of
//! milliseconds, so a polled reader would drop most of every frame. The ISR
//! below moves bytes into a ring the main loop drains at its leisure.
//!
//! Only commands needing neither a filesystem nor crypto are answered here.
//! Application upload needs both; `runtime/firmware`'s `DeviceService` is the
//! full implementation, and it is `std`-bound.

use alloc::format;
use alloc::string::{String, ToString};
use alloc::vec;
use alloc::vec::Vec;
use core::sync::atomic::{AtomicUsize, Ordering};

use rustnet_core::Module;
use rustnet_rndp::*;
use rustnet_secureboot::{verify, ChipFamily, ImageKind};
use stm32f4xx_hal::pac::interrupt;

use crate::{board, FirmwareHost, APP_NAME};

// ---------------------------------------------------------------------------
// Interrupt-driven receive ring
// ---------------------------------------------------------------------------

/// The receive ring has to absorb everything that arrives between two polls.
/// At 115200 baud that is ~115 bytes per 10 ms of consumer latency, but an
/// application container arrives as one continuous multi-kilobyte burst, and
/// a ring that overflows corrupts the frame rather than merely delaying it.
const RX_CAP: usize = 4096;

static mut RX_BUF: [u8; RX_CAP] = [0; RX_CAP];
/// Written only by the ISR.
static RX_HEAD: AtomicUsize = AtomicUsize::new(0);
/// Written only by the main loop. Single producer, single consumer, one core —
/// so plain atomics order this correctly with no critical section.
static RX_TAIL: AtomicUsize = AtomicUsize::new(0);
/// Bytes the ring had no room for. Reported over RNDP rather than hidden.
static RX_DROPPED: AtomicUsize = AtomicUsize::new(0);

/// Called from the USART interrupt: move the pending byte into the ring.
fn rx_isr() {
    let sr = crate::usart_read(USART_SR);
    if sr & SR_RXNE == 0 {
        return;
    }
    // Reading DR clears RXNE, and clears an overrun that SR just latched.
    let byte = (crate::usart_read(USART_DR) & 0xFF) as u8;

    let head = RX_HEAD.load(Ordering::Relaxed);
    let next = (head + 1) % RX_CAP;
    if next == RX_TAIL.load(Ordering::Acquire) {
        RX_DROPPED.fetch_add(1, Ordering::Relaxed);
        return;
    }
    // SAFETY: the ISR is the only writer of RX_BUF[head], and `head` is only
    // published to the consumer after the write.
    unsafe { core::ptr::addr_of_mut!(RX_BUF).cast::<u8>().add(head).write_volatile(byte) };
    RX_HEAD.store(next, Ordering::Release);
}

pub(crate) fn rx_take(out: &mut [u8]) -> usize {
    let head = RX_HEAD.load(Ordering::Acquire);
    let mut tail = RX_TAIL.load(Ordering::Relaxed);
    let mut n = 0;
    while tail != head && n < out.len() {
        // SAFETY: the consumer is the only reader, and this slot was
        // published by the ISR before `head` advanced past it.
        out[n] = unsafe { core::ptr::addr_of!(RX_BUF).cast::<u8>().add(tail).read_volatile() };
        tail = (tail + 1) % RX_CAP;
        n += 1;
    }
    RX_TAIL.store(tail, Ordering::Release);
    n
}

// ---------------------------------------------------------------------------
// Instrumentation
// ---------------------------------------------------------------------------
//
// The receive ring can only cover as much latency as the consumer actually
// has. Rather than guess at what stalls it, measure: these record the worst
// gap between two polls, and how long an RSA verify takes, both straight off
// the DWT cycle counter. Reported by `info`, and reset when it is read, so a
// measurement can be scoped to one operation.

const DWT_CYCCNT: usize = 0xE000_1004;

fn cycles() -> u32 {
    // SAFETY: the cycle counter, started by `Stm32F4Board::init`.
    unsafe { core::ptr::read_volatile(DWT_CYCCNT as *const u32) }
}

static LAST_POLL_CYC: AtomicUsize = AtomicUsize::new(0);
static MAX_POLL_GAP_CYC: AtomicUsize = AtomicUsize::new(0);
static LAST_VERIFY_CYC: AtomicUsize = AtomicUsize::new(0);

/// Note that a poll happened, folding the gap since the previous one into the
/// running maximum.
fn mark_poll() {
    let now = cycles() as usize;
    let previous = LAST_POLL_CYC.swap(now, Ordering::Relaxed);
    if previous != 0 {
        let gap = (now as u32).wrapping_sub(previous as u32) as usize;
        MAX_POLL_GAP_CYC.fetch_max(gap, Ordering::Relaxed);
    }
}

const USART_SR: usize = 0x00;
const USART_DR: usize = 0x04;
const USART_CR1: usize = 0x0C;
const SR_RXNE: u32 = 1 << 5;
const CR1_RXNEIE: u32 = 1 << 5;

/// Turn on receive interrupts for the console USART. Call once, after the HAL
/// has configured the port.
pub fn start_receiving() {
    crate::usart_modify(USART_CR1, CR1_RXNEIE);
    // SAFETY: enabling the console USART's interrupt line is the whole point.
    unsafe { cortex_m::peripheral::NVIC::unmask(board::CONSOLE_IRQ) };
}

// The ISR the vector table points at. `#[interrupt]` matches on the function
// name, so it names the console port — USART2 on both supported boards, which
// is why there is only one here.
#[interrupt]
#[allow(non_snake_case)]
fn USART2() {
    rx_isr();
}

// ---------------------------------------------------------------------------
// Service
// ---------------------------------------------------------------------------

/// Refuse to buffer more than this while hunting for a frame. It has to hold
/// a whole signed application container, so it is sized per board — this is
/// the ceiling on what `rustnet flash` can deliver here.
const MAX_FRAME: usize = board::MAX_RNDP_FRAME;

const FW_VERSION: &str = env!("CARGO_PKG_VERSION");

pub struct Rndp {
    rx: Vec<u8>,
    /// Whether the application gets fuel. `start`/`stop` flip it.
    pub app_running: bool,
    /// PKCS#1 DER public key from `rustnet provision`. Held in RAM only:
    /// there is no filesystem here, so provisioning does not survive a reset.
    pub_key: Option<Vec<u8>>,
    /// An application accepted by `rustnet flash`, waiting for the main loop
    /// to rebuild the interpreter around it.
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

        let mut chunk = [0u8; 128];
        loop {
            let n = host.console_read(&mut chunk);
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
            // behind it — the port goes dead until reset. Anything claiming
            // more than we would ever accept is a false start, not a frame.
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
                    // is kilobytes: hand it back before dispatch, which is
                    // where the heap is tightest (an RSA verify wants ~17 KB).
                    if self.rx.capacity() > 512 && self.rx.len() < 512 {
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
                    // Corrupt frame: step past this magic and resynchronise
                    // on the next, rather than discarding the whole buffer.
                    if self.rx.len() < 2 {
                        break;
                    }
                    self.rx.drain(..1);
                }
            }
        }
    }

    /// Drop everything before the next frame start. The tools do the same in
    /// the other direction, because a serial line carries log output and boot
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
            // No magic anywhere: keep the last byte, which may be the first
            // half of a magic split across two reads.
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
                let per_us = (host.cpu_hz() / 1_000_000).max(1) as usize;
                let max_gap_us = MAX_POLL_GAP_CYC.swap(0, Ordering::Relaxed) / per_us;
                let verify_us = LAST_VERIFY_CYC.load(Ordering::Relaxed) / per_us;
                Ok(format!(
                r#"{{"chip":"stm32","board":"{}","version":"{}","protocol":{},"uptime_ms":{},"heap_used":{},"apps":1,"wifi":false,"active_app":"{}","running":{},"autostart":null,"transport":"{}","rx_dropped":{},"max_poll_gap_us":{},"last_verify_us":{},"storage_used":{}}}"#,
                board::NAME,
                FW_VERSION,
                PROTOCOL_VERSION,
                host.uptime_ms(),
                crate::heap_used(),
                self.app_name,
                self.app_running,
                host.transport_name(),
                RX_DROPPED.load(Ordering::Relaxed),
                max_gap_us,
                verify_us,
                host.storage_used(),
            )
            .into_bytes())
            }

            // One slot: the application compiled into this image.
            // One slot, holding whatever is loaded — which is the compiled-in
            // application until `rustnet flash` replaces it, so this has to
            // read the live name, not the build-time constant.
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

            CMD_REBOOT => cortex_m::peripheral::SCB::sys_reset(),

            CMD_PROVISION_KEY => {
                if frame.payload.is_empty() {
                    return Err("empty key".to_string());
                }
                self.pub_key = Some(frame.payload.clone());
                host.persist(crate::storage::KIND_PUB_KEY, &frame.payload);
                Ok(Vec::new())
            }

            CMD_FLASH_APP => {
                // [name_len:u8][name][RNSB container] — the same payload the
                // std service takes, so the tools need no special case.
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

                let started = cycles();
                let verified = verify(container, key, ChipFamily::Stm32);
                LAST_VERIFY_CYC
                    .store(cycles().wrapping_sub(started) as usize, Ordering::Relaxed);
                let image = verified.map_err(|e| format!("signature check failed: {e}"))?;
                if image.kind != ImageKind::App {
                    return Err("container is not an app image".to_string());
                }
                // Refuse anything the interpreter could not load, before it
                // replaces a working application.
                Module::from_bytes(image.payload)
                    .map_err(|e| format!("invalid RNX: {e}"))?;

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
