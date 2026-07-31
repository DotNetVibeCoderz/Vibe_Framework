//! The ESP8285, spoken to in AT commands over UART1.
//!
//! The K210 has no radio. A Maix Go carries an ESP8285 wired to IO6/IO7 with
//! Espressif's AT firmware on it, so networking here is a conversation in
//! text: write a line, read until the module says `OK` or `ERROR`.
//!
//! Three things about this particular module shape the code.
//!
//! **It has to be power-cycled, not merely enabled.** Resetting the K210 does
//! not reset the ESP8285; nothing connects them but a board. A module left
//! mid-command comes back answering `busy p...` to everything and stays that
//! way across any number of K210 resets and reflashes — and `AT+CWJAP` is
//! exactly the command that leaves it there, so one failed join wedged every
//! session after it. The firmware pulses the enable line at boot now.
//!
//! **It answers at 115200, the ordinary AT rate.** This port swept for a rate
//! and found 74880, and wrote that down as a fact about the board. It was a
//! symptom: 74880 is the ESP8266 *bootloader's* rate, and the module was
//! sitting in it because it had never been reset. The power-cycle above fixed
//! the rate and the joins together. A rate that only appears on a stuck module
//! is not a specification.
//!
//! **The receive FIFO is sixteen bytes and there is no interrupt here.** At
//! 115200 baud that is under one and a half milliseconds of traffic, so a poll
//! every ten milliseconds loses most of a reply and what comes back looks like
//! a wrong baud rate. Polling every 200 µs keeps up. This was diagnosed once,
//! as `AT versi97e9)`, and is the reason the read loop looks the way it does.
//!
//! **It is AT 1.6.2, which has no MQTT client.** `AT+MQTT*` arrived in AT 2.x
//! and this module predates it, so anything above TCP has to be framed by hand
//! over `AT+CIPSEND`.
//!
//! The command vocabulary is Espressif's, cross-checked against MaixPy's own
//! `esp8285.c` — same commands, same order, same quoting.

use alloc::format;
use alloc::string::{String, ToString};
use alloc::vec::Vec;

use rustnet_espat as at;
pub(crate) use rustnet_espat::extract_ipd;
use rustnet_hal::Board as _;

use core::sync::atomic::{AtomicUsize, Ordering};

use crate::FirmwareHost;

// ---------------------------------------------------------------------------
// Receive ring
// ---------------------------------------------------------------------------
//
// UART1's FIFO is sixteen bytes — 1.4 ms at 115200 baud — and nothing empties
// it except code that is looking for a reply. That is fine for a command,
// where the reply is read immediately, and wrong for everything the module
// says on its own: a `+IPD` arriving while the application is capturing a
// camera frame and blitting it to the panel waits a hundred milliseconds for
// a reader, and by then it is gone.
//
// So bytes are moved out of the FIFO on sight — from the UART1 interrupt, and
// from every polling point that already drains UARTHS — into this ring, and
// the AT code reads the ring instead of the hardware. The protocol layer is
// unchanged; it just stops being the only thing keeping up.

const RING_CAP: usize = 8 * 1024;

static mut RING: [u8; RING_CAP] = [0; RING_CAP];
/// Written only by [`drain_fifo`].
static RING_HEAD: AtomicUsize = AtomicUsize::new(0);
/// Written only by the AT code.
static RING_TAIL: AtomicUsize = AtomicUsize::new(0);
/// Bytes the ring had no room for. Counted rather than hidden: a silent
/// overflow in the middle of an MQTT packet desynchronises the stream, and the
/// symptom appears much later as a broker that stopped answering.
static RING_DROPPED: AtomicUsize = AtomicUsize::new(0);

/// UART1's base address, for the interrupt handler.
const UART1_BASE: usize = 0x5021_0000;

/// Move everything UART1's FIFO holds into the ring.
///
/// Called from the interrupt handler and from the polled paths, so it runs
/// with interrupts masked: two producers advancing `RING_HEAD` would otherwise
/// be able to hand out the same slot twice.
pub(crate) fn drain_fifo() {
    riscv::interrupt::machine::free(|| {
        while let Some(byte) = rustnet_hal_k210::uart::take_byte_at(UART1_BASE) {
            let head = RING_HEAD.load(Ordering::Relaxed);
            let next = (head + 1) % RING_CAP;
            if next == RING_TAIL.load(Ordering::Acquire) {
                RING_DROPPED.fetch_add(1, Ordering::Relaxed);
                continue;
            }
            // SAFETY: inside the critical section this is the only writer of
            // RING[head], and `head` is published to the consumer only after
            // the write.
            unsafe {
                core::ptr::addr_of_mut!(RING).cast::<u8>().add(head).write_volatile(byte)
            };
            RING_HEAD.store(next, Ordering::Release);
        }
    });
}

/// Take up to `out.len()` bytes out of the ring.
fn ring_take(out: &mut [u8]) -> usize {
    let head = RING_HEAD.load(Ordering::Acquire);
    let mut tail = RING_TAIL.load(Ordering::Relaxed);
    let mut n = 0;
    while tail != head && n < out.len() {
        // SAFETY: the AT code is the only reader, and this slot was published
        // by a producer before `head` moved past it.
        out[n] = unsafe { core::ptr::addr_of!(RING).cast::<u8>().add(tail).read_volatile() };
        tail = (tail + 1) % RING_CAP;
        n += 1;
    }
    RING_TAIL.store(tail, Ordering::Release);
    n
}

/// How many bytes the ring has had to discard. Reported rather than hidden.
pub(crate) fn dropped() -> usize {
    RING_DROPPED.load(Ordering::Relaxed)
}

/// Arm UART1's receive interrupt. Call once, after the port is configured.
pub(crate) fn start_receiving() {
    rustnet_hal_k210::uart::enable_rx_interrupt(UART1_BASE);
    drain_fifo();
}

/// Polls between two chances for the tools to be answered.
///
/// A join takes twenty seconds and a broker handshake can take ten, and for
/// all of that the interpreter is inside one host call — so nothing else runs
/// unless this loop makes it. Without it `rustnet flash` and `rustnet logs`
/// time out against a device that is working perfectly, which is a poor way to
/// learn that the radio is busy. 50 polls is 10 ms, comfortably inside
/// UARTHS's 8-byte receive FIFO at 115200 baud.
const SERVICE_EVERY: u32 = 50;

/// Poll interval while waiting for the module, in microseconds. See the module
/// note — this is not a tuning knob, it is the FIFO's depth divided by the
/// baud rate.
const POLL_US: u32 = 200;

/// Polls of silence that end a reply *once it has terminated*. 250 is 50 ms,
/// which covers the trailing newline after `OK` without waiting on it.
///
/// Silence alone must never end a read. `AT+CWJAP` answers `WIFI DISCONNECT`,
/// then says nothing for several seconds while it associates, then finishes.
/// A read that stops at the first quiet moment sees only the disconnect and
/// reports a join failure for a join that was still in progress — which is
/// exactly what this port did first, and it looks like a wrong password.
const IDLE_POLLS: u32 = 250;

/// How much of a reply to keep when reporting one. A log line, not a
/// transcript.
const LOG_LIMIT: usize = 120;

/// How long to wait for `AT+CIPSEND`'s `> ` prompt. The module answers it in
/// milliseconds when the socket is open and never when it is not.
const PROMPT_BUDGET_MS: u32 = 3_000;

/// How long to wait for `SEND OK` after handing over a payload. Generous: it
/// covers a retransmit on a busy network, and the alternative to waiting is
/// reporting a failure for a message that then arrives.
const SEND_ACK_BUDGET_MS: u32 = 8_000;

/// Where provisioned credentials are kept between sessions.
///
/// Keeping them in RAM only was the first design, and it cannot work on this
/// board: opening the serial port asserts the K210's reset, so *every* tool
/// invocation power-cycles the device. `rustnet wifi` would set the SSID and
/// the next `rustnet logs` would wipe it, and the application would spend
/// forever retrying a join against credentials that were erased a moment after
/// they arrived.
///
/// So they go to flash, alongside the provisioning key and the application
/// that are already there. The tradeoff is real and worth naming: anyone
/// holding this board can read the PSK out of its NOR. That is the same
/// exposure as every other ESP-AT device with a stored network, and it is the
/// price of a board whose reset line is wired to its serial handshake.
pub(crate) const CREDENTIALS_FILE: &str = "wifi.cfg";

/// Everything the firmware knows about the radio.
///
/// Credentials arrive over RNDP (`rustnet wifi --ssid ... --psk ...`) or from a
/// managed `Wifi.Connect`, and are persisted to [`CREDENTIALS_FILE`].
#[derive(Default)]
pub(crate) struct WifiState {
    pub(crate) ssid: String,
    pub(crate) psk: String,
    pub(crate) ip: String,
    pub(crate) joined: bool,
}

/// Send a line, appending the terminator the module expects.
fn send(host: &mut FirmwareHost, line: &str) {
    if let Ok(uart) = host.board.uart(1) {
        let _ = uart.write(line.as_bytes());
        let _ = uart.write(b"\r\n");
    }
}

/// Read until the module goes quiet or the budget runs out.
///
/// `budget_ms` is a ceiling, not a delay: a reply that arrives and stops ends
/// the read immediately. Joining an access point can take ten seconds and
/// asking for the version takes ten milliseconds, so the two callers pass very
/// different ceilings and neither waits for the other's worst case.
fn collect(host: &mut FirmwareHost, budget_ms: u32) -> String {
    let mut seen: Vec<u8> = Vec::new();
    let mut idle = 0u32;
    let polls = budget_ms.saturating_mul(1_000 / POLL_US);
    for _ in 0..polls {
        let mut buf = [0u8; 64];
        drain_fifo();
        let got = ring_take(&mut buf);
        if got > 0 {
            seen.extend_from_slice(&buf[..got]);
            idle = 0;
            continue;
        }
        idle += 1;
        // Quiet *and* finished. The order matters: without the terminator
        // check this returns mid-join.
        if idle >= IDLE_POLLS && at::is_terminated(&String::from_utf8_lossy(&seen)) {
            break;
        }
        if idle % SERVICE_EVERY == 0 {
            host.poll_rndp();
        }
        host.board.delay().delay_us(POLL_US as u64);
    }
    String::from_utf8_lossy(&seen).into_owned()
}

/// Run one command and return its reply, or the module's own error text.
///
/// The module echoes the command back before answering, and its terminators
/// are whole lines — so `OK` is looked for as a line, not as a substring.
/// `AT+CWJAP` failing produces a reply containing both `FAIL` and, on some
/// firmware, a trailing `OK` from something earlier in the buffer; checking
/// failures first is what keeps a failed join from reading as a success.
pub(crate) fn command(host: &mut FirmwareHost, cmd: &str, budget_ms: u32) -> Result<String, String> {
    send(host, cmd);
    let reply = collect(host, budget_ms);
    for line in reply.lines().map(str::trim) {
        if line == "ERROR" || line.starts_with("FAIL") || line.starts_with("+CWJAP:") {
            return Err(short(&reply));
        }
    }
    if reply.lines().any(|l| l.trim() == "OK") {
        return Ok(reply);
    }
    Err(if reply.trim().is_empty() {
        format!("no answer to '{cmd}'")
    } else {
        short(&reply)
    })
}

/// A reply trimmed to something that fits in a log line, with the echo of the
/// command dropped — it is never the interesting part.
fn short(reply: &str) -> String {
    let body: Vec<&str> = reply
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty() && !l.starts_with("AT"))
        .collect();
    let joined = body.join("; ");
    if joined.len() > 120 {
        joined[..120].to_string()
    } else {
        joined
    }
}

/// Is the module there and answering?
pub(crate) fn ping(host: &mut FirmwareHost) -> bool {
    command(host, "AT", 200).is_ok()
}

/// Join an access point and return the address DHCP handed out.
///
/// Echo is turned off first. Leaving it on doubles every reply and makes the
/// parsing below depend on which half it lands in — cheap to disable, and it
/// only has to be done once per power cycle.
pub(crate) fn join(host: &mut FirmwareHost, ssid: &str, psk: &str) -> Result<String, String> {
    if ssid.is_empty() {
        return Err(String::from("no SSID: run `rustnet wifi --ssid ... --psk ...`"));
    }
    if !ping(host) {
        return Err(String::from("ESP8285 is not answering AT"));
    }
    let _ = command(host, "ATE0", 500);
    // Station mode. An ESP left in AP or AP+STA mode joins nothing and reports
    // no error about it.
    command(host, "AT+CWMODE=1", 1_000).map_err(|e| format!("CWMODE: {e}"))?;

    // The quoting is Espressif's and the escaping is real: an SSID containing
    // a quote or a backslash has to arrive escaped or the module parses the
    // command short and answers ERROR.
    let cmd = format!("AT+CWJAP=\"{}\",\"{}\"", escape(ssid), escape(psk));
    // Joining is the one slow command here — association, then DHCP.
    command(host, &cmd, 20_000).map_err(|e| format!("join failed: {e}"))?;

    let reply = command(host, "AT+CIFSR", 3_000).map_err(|e| format!("CIFSR: {e}"))?;
    Ok(parse_station_ip(&reply).unwrap_or_default())
}

/// Read whatever has arrived, without expecting a terminator.
///
/// [`command`] waits for `OK`; this does not, because `+IPD` announcements are
/// not answers to anything and never end in one. Used by the MQTT session,
/// which decides for itself when it has a whole packet.
pub(crate) fn drain(host: &mut FirmwareHost, budget_ms: u32) -> Vec<u8> {
    let mut seen: Vec<u8> = Vec::new();
    let mut idle = 0u32;
    let polls = budget_ms.saturating_mul(1_000 / POLL_US);
    for _ in 0..polls {
        let mut buf = [0u8; 64];
        drain_fifo();
        let got = ring_take(&mut buf);
        if got > 0 {
            seen.extend_from_slice(&buf[..got]);
            idle = 0;
            continue;
        }
        idle += 1;
        if !seen.is_empty() && idle >= IDLE_POLLS {
            break;
        }
        if idle % SERVICE_EVERY == 0 {
            host.poll_rndp();
        }
        host.board.delay().delay_us(POLL_US as u64);
    }
    seen
}

/// Write `data` to the open TCP connection.
///
/// `AT+CIPSEND` is a two-step handshake and neither step looks like the rest
/// of the AT protocol. The command is answered with `> ` — a prompt, with no
/// line ending — and the payload that follows is raw bytes, also with no
/// terminator. Waiting for a newline at either point waits forever.
///
/// The declared length must then be exact. Send fewer bytes than promised and
/// the module waits for the rest, swallowing the next command as payload; send
/// more and the excess is parsed as AT input. Both failures surface later, as
/// a session that has quietly desynchronised, so the length here is taken from
/// the slice rather than passed alongside it.
///
/// Returns everything read while waiting for the acknowledgement, because
/// discarding it loses messages. `SEND OK` and the broker's reply arrive in
/// the same breath on a local network — the round trip is a millisecond and
/// this loop reads for eight seconds — so a version of this that returned
/// `Ok(())` threw away every `+IPD` that answered quickly. That looked exactly
/// like a broker which never replied, and it is why nothing arrived on the
/// radio for a server sitting on the same switch.
pub(crate) fn send_raw(host: &mut FirmwareHost, data: &[u8]) -> Result<Vec<u8>, String> {
    if data.is_empty() {
        return Ok(Vec::new());
    }
    send(host, &format!("AT+CIPSEND={}", data.len()));

    // Wait for the prompt specifically. `OK` arrives first and means only that
    // the command parsed; writing on that would put the payload in front of
    // the module's own prompt.
    let mut waited = 0u32;
    let mut reply = Vec::new();
    while waited < PROMPT_BUDGET_MS * (1_000 / POLL_US) {
        reply.extend_from_slice(&drain(host, 20));
        if reply.contains(&b'>') {
            break;
        }
        if at::is_terminated(&String::from_utf8_lossy(&reply))
            && !String::from_utf8_lossy(&reply).contains("OK")
        {
            return Err(at::summarise(&String::from_utf8_lossy(&reply), LOG_LIMIT));
        }
        waited += 20 * (1_000 / POLL_US);
    }
    if !reply.contains(&b'>') {
        return Err(String::from("no send prompt from the radio"));
    }

    if let Ok(uart) = host.board.uart(1) {
        uart.write(data).map_err(|e| format!("send failed: {e}"))?;
    }

    // Wait for `SEND OK` specifically, not for silence.
    //
    // The module answers a payload in two parts — `Recv <n> bytes` when it has
    // taken the bytes, then `SEND OK` when it has put them on the wire — and
    // the gap between them is longer than any sensible idle threshold. Ending
    // the read at the first quiet moment sees only the first half and reports
    // a failed publish for a message that was about to go out; this port did
    // exactly that, and the error it printed was `Recv 26 bytes`.
    //
    // `SEND OK` means the radio transmitted, not that the broker received.
    // TCP acknowledgement is not application delivery and nothing here
    // pretends otherwise.
    let mut ack: Vec<u8> = Vec::new();
    let mut waited = 0u32;
    while waited < SEND_ACK_BUDGET_MS {
        ack.extend_from_slice(&drain(host, 100));
        let text = String::from_utf8_lossy(&ack).into_owned();
        if text.contains("SEND OK") {
            return Ok(ack);
        }
        if text.contains("SEND FAIL") || text.lines().any(|l| l.trim() == "ERROR") {
            return Err(at::summarise(&text, LOG_LIMIT));
        }
        waited += 100;
    }
    let text = String::from_utf8_lossy(&ack).into_owned();
    Err(if text.trim().is_empty() {
        String::from("radio did not acknowledge the send")
    } else {
        at::summarise(&text, LOG_LIMIT)
    })
}

pub(crate) fn leave(host: &mut FirmwareHost) {
    let _ = command(host, "AT+CWQAP", 3_000);
}

/// Escape the two characters Espressif's parser treats specially inside a
/// quoted argument.
fn escape(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for c in value.chars() {
        if c == '"' || c == '\\' || c == ',' {
            out.push('\\');
        }
        out.push(c);
    }
    out
}

/// The station address out of a `AT+CIFSR` reply.
///
/// The reply carries several lines — the soft-AP's address as well as the
/// station's, plus MAC addresses — and picking the wrong one reports an
/// address that is real, is the module's, and routes nowhere.
pub(crate) fn parse_station_ip(reply: &str) -> Option<String> {
    for line in reply.lines().map(str::trim) {
        let rest = line.strip_prefix("+CIFSR:STAIP,")?;
        let ip = rest.trim().trim_matches('"');
        if !ip.is_empty() && ip != "0.0.0.0" {
            return Some(ip.to_string());
        }
    }
    None
}
