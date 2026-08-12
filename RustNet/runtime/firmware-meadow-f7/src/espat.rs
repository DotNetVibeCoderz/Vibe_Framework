//! WiFi, by talking AT to the ESP32 coprocessor on UART5.
//!
//! The Meadow carries an ESP32-PICO-D4 whose stock firmware is Wilderness
//! Labs' own — an unpublished protocol over SPI2, useless to anything but
//! Meadow OS. What runs there now is **ESP-AT v4.1.1.0 built from source for
//! this module** (`C:\esp-at`, `module_config/module_pico-d4`), for two
//! reasons no prebuilt binary can satisfy:
//!
//! * Espressif's published ESP32 image defaults the AT port to UART1 on
//!   GPIO16/17. On a PICO-D4 those two pins are the module's *embedded flash*,
//!   which is why the schematic draws them with no nets. Muxing them cuts the
//!   chip off from the flash it executes from. The manufacturing NVS can be
//!   repointed at UART0 — but that is only half of it, because
//! * ESP-IDF puts `printf` and the log on UART0, and a console sharing the
//!   wire with AT drops log lines inside AT responses. `ESP_CONSOLE_NONE` is
//!   what makes UART0 exclusively the AT port, and it is a **build-time**
//!   choice. That is the reason this needed a source build and not a patch.
//!
//! The Bluetooth stack is out of that build: with it the image is 0xd010
//! bytes larger than the 1.5 MB OTA partitions the module's table defines, so
//! the stock configuration does not fit its own partition layout. This board
//! wants a WiFi radio, so dropping it was free.
//!
//! **This module owns the link, no one else.** The ESP32's UART is also how
//! `esptool` reaches it, through the separate `--features esp-bridge` image;
//! the two never run at once.

use alloc::format;
use alloc::string::{String, ToString};

use rustnet_hal::delay::Delay;

use crate::uart::Esp32;

/// How often the receive path is looked at while waiting for a reply.
///
/// A USARTv2 has no receive FIFO, so a byte survives only until the next one
/// lands — 87 µs apart at 115200 baud. Polling faster than that is what makes
/// the difference between reading a reply and reading a quarter of one. It is
/// also why waiting here cannot use `serviced_delay`: servicing USB takes long
/// enough to lose bytes, which is a bug that looks like an ESP32 answering
/// garbage.
const POLL_US: u32 = 20;

/// Beyond this a reply is treated as runaway rather than buffered. `AT+CWLAP`
/// in a dense neighbourhood is the realistic worst case and stays well inside.
const MAX_REPLY: usize = 4096;

/// A conversation with ESP-AT.
pub struct EspAt {
    esp: Esp32,
    /// Whether AT answered at boot. Everything else is only meaningful when
    /// this is true, and a board whose coprocessor is silent should say so
    /// once rather than fail every call with the same timeout.
    pub present: bool,
    /// The firmware banner from `AT+GMR`, for the log and `rustnet info`.
    pub version: String,
    pub ssid: String,
    pub ip: String,
    pub connected: bool,
}

impl EspAt {
    pub fn new(pclk1_hz: u32) -> Self {
        EspAt {
            esp: Esp32::new(pclk1_hz),
            present: false,
            version: String::new(),
            ssid: String::new(),
            ip: String::new(),
            connected: false,
        }
    }

    /// Reset the coprocessor and get it into a known state.
    ///
    /// Returns the `AT+GMR` banner on success. Failure is reported rather than
    /// panicked on: a Meadow with a dead or differently-flashed coprocessor is
    /// still a working RustNet board for everything that is not WiFi.
    pub fn begin(&mut self, delay: &mut dyn Delay) -> Result<String, String> {
        self.esp.reset(false, delay);
        // ESP-AT is ready in well under a second, but the reset also produces
        // the mask ROM's own chatter at 74880 baud, which arrives here as
        // noise. Drain it rather than try to parse it.
        self.drain(1_500, delay);

        // `AT` up to five times. The first one after a reset is routinely lost
        // to that noise, and a single failed attempt would report a missing
        // coprocessor when the truth is a mistimed hello.
        let mut last = String::new();
        let mut answered = false;
        for _ in 0..5 {
            match self.exchange("AT", 1_000, delay) {
                Ok(_) => {
                    answered = true;
                    break;
                }
                Err(e) => last = e,
            }
        }
        if !answered {
            return Err(last);
        }

        // Echo off: every command otherwise comes back before its answer, and
        // the parsing below would have to know which is which.
        let _ = self.exchange("ATE0", 1_000, delay);
        // Station mode. The image boots in softAP mode (`+CWMODE:2`), which
        // joins nothing.
        self.exchange("AT+CWMODE=1", 2_000, delay)?;
        // Single connection: the multiplexed form prefixes every payload with
        // a link id, and nothing here wants five sockets.
        let _ = self.exchange("AT+CIPMUX=0", 1_000, delay);

        let banner = self.exchange("AT+GMR", 2_000, delay)?;
        self.version = banner
            .lines()
            .find(|l| l.starts_with("AT version:"))
            .unwrap_or("")
            .trim()
            .to_string();
        self.present = true;

        // A coprocessor that kept a join across the reset is already on a
        // network; ask rather than assume it is idle.
        self.refresh_status(delay);
        Ok(banner)
    }

    /// Join a network. Blocks until the ESP32 says yes or gives up.
    pub fn connect(&mut self, ssid: &str, psk: &str, delay: &mut dyn Delay) -> Result<(), String> {
        if !self.present {
            return Err(String::from("no ESP-AT coprocessor answered at boot"));
        }
        let mut cmd = String::from("AT+CWJAP=\"");
        escape_into(&mut cmd, ssid);
        cmd.push_str("\",\"");
        escape_into(&mut cmd, psk);
        cmd.push('"');

        // 20 seconds: a join is DHCP plus an association plus whatever the
        // access point feels like, and ESP-AT reports `FAIL` itself when it
        // gives up. A shorter timeout here reports a failure the radio has
        // not actually reached yet.
        let reply = self.exchange(&cmd, 20_000, delay)?;
        if reply.contains("FAIL") || reply.contains("ERROR") {
            self.connected = false;
            return Err(join_error(&reply));
        }
        self.ssid = ssid.to_string();
        self.refresh_status(delay);
        if !self.connected {
            return Err(String::from("joined but no address was assigned"));
        }
        Ok(())
    }

    pub fn disconnect(&mut self, delay: &mut dyn Delay) -> Result<(), String> {
        self.exchange("AT+CWQAP", 5_000, delay)?;
        self.connected = false;
        self.ip.clear();
        self.ssid.clear();
        Ok(())
    }

    /// Re-read SSID and address from the coprocessor.
    ///
    /// The radio, not this firmware, is the authority: it can lose an
    /// association at any moment and nothing here would be told.
    pub fn refresh_status(&mut self, delay: &mut dyn Delay) {
        if !self.present {
            return;
        }
        if let Ok(reply) = self.exchange("AT+CIPSTA?", 2_000, delay) {
            self.ip = field(&reply, "+CIPSTA:ip:").unwrap_or_default();
            // `0.0.0.0` is what an unassociated station reports, and it is not
            // an address anything can be reached on.
            self.connected = !self.ip.is_empty() && self.ip != "0.0.0.0";
        }
        if let Ok(reply) = self.exchange("AT+CWJAP?", 2_000, delay) {
            if let Some(s) = field(&reply, "+CWJAP:") {
                // The reply is `+CWJAP:"ssid",...`; the helper already
                // unquoted the first field.
                self.ssid = s;
            }
        }
    }

    /// Send one command and collect its reply.
    ///
    /// Ends on `OK`, `ERROR` or `FAIL` on a line of its own — the three ways
    /// ESP-AT finishes speaking — or on the timeout. The reply is returned
    /// with those terminators still in it, because callers like `connect`
    /// need to tell them apart.
    pub fn exchange(
        &mut self,
        cmd: &str,
        timeout_ms: u32,
        delay: &mut dyn Delay,
    ) -> Result<String, String> {
        // Anything still on the line belongs to the previous exchange, or is
        // an unsolicited message. Either way it would be read as part of this
        // reply, so it goes first.
        self.drain(2, delay);

        self.esp.uart.write(cmd.as_bytes());
        self.esp.uart.write(b"\r\n");

        let mut reply = String::new();
        let mut chunk = [0u8; 64];
        // A real clock, not a count of iterations: the poll below sometimes
        // reads and sometimes waits, so iterations and milliseconds are not
        // the same thing, and a 20-second join timeout derived from the wrong
        // one is off by whatever fraction of the wait had traffic in it.
        let deadline = delay.now_us() + timeout_ms as u64 * 1000;
        while delay.now_us() < deadline {
            let n = self.esp.uart.read(&mut chunk);
            if n > 0 {
                for &b in &chunk[..n] {
                    if reply.len() < MAX_REPLY {
                        // Non-ASCII cannot appear in an AT reply; if it does,
                        // the line speed is wrong and dropping it keeps the
                        // string printable for the log.
                        if b == b'\n' || b == b'\r' || (0x20..0x7F).contains(&b) {
                            reply.push(b as char);
                        }
                    }
                }
                if ends_reply(&reply) {
                    return Ok(reply);
                }
            } else {
                delay.delay_us(POLL_US as u64);
            }
        }
        Err(format!("'{cmd}' timed out after {timeout_ms} ms"))
    }

    /// Throw away whatever is on the line for a while.
    fn drain(&mut self, ms: u32, delay: &mut dyn Delay) {
        let mut sink = [0u8; 64];
        let deadline = delay.now_us() + ms as u64 * 1000;
        while delay.now_us() < deadline {
            if self.esp.uart.read(&mut sink) == 0 {
                delay.delay_us(POLL_US as u64);
            }
        }
    }
}

/// Whether the buffer ends in one of ESP-AT's three terminators.
///
/// Checked against the tail rather than searched for anywhere, because `OK`
/// also appears inside `AT+GMR` output and inside SSIDs.
fn ends_reply(reply: &str) -> bool {
    let tail = reply.trim_end();
    tail.ends_with("OK") || tail.ends_with("ERROR") || tail.ends_with("FAIL")
}

/// Pull the first quoted or bare value that follows `key`.
fn field(reply: &str, key: &str) -> Option<String> {
    let line = reply.lines().find(|l| l.trim_start().starts_with(key))?;
    let rest = line.trim_start().strip_prefix(key)?.trim();
    let rest = rest.strip_prefix('"').unwrap_or(rest);
    let end = rest.find(['"', ',']).unwrap_or(rest.len());
    Some(rest[..end].to_string())
}

/// Turn ESP-AT's numeric join failure into something a log can be read from.
fn join_error(reply: &str) -> String {
    // `+CWJAP:<n>` accompanies the FAIL, and the number is the whole diagnosis.
    match field(reply, "+CWJAP:").as_deref() {
        Some("1") => String::from("join timed out"),
        Some("2") => String::from("wrong password"),
        Some("3") => String::from("no access point with that SSID was found"),
        Some("4") => String::from("the access point refused the join"),
        Some(other) => format!("join failed (code {other})"),
        None => String::from("join failed"),
    }
}

/// Quote a value for an AT command.
///
/// ESP-AT's parameter syntax is comma-separated and double-quoted, so a
/// password containing `"` or `,` — both legal in WPA — would otherwise be
/// read as the end of the argument and change which network is joined.
fn escape_into(out: &mut String, value: &str) {
    for ch in value.chars() {
        if ch == '"' || ch == ',' || ch == '\\' {
            out.push('\\');
        }
        out.push(ch);
    }
}
