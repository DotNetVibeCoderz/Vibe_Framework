//! MQTT over a serial radio.
//!
//! The ESP8285 on this board runs AT 1.6.2, which has no MQTT client — that
//! arrived in AT 2.x. What it does have is a TCP socket reachable through
//! three commands: `AT+CIPSTART` to open one, `AT+CIPSEND` to write a fixed
//! number of bytes, and unsolicited `+IPD,<n>:` lines carrying whatever came
//! back. So MQTT is framed here and handed over a byte at a time.
//!
//! The packets themselves are [`rustnet_mqtt`]'s, the same ones the host's
//! `TcpStream` client encodes. A second implementation of the remaining-length
//! varint is a second place for it to be wrong.
//!
//! Three things about `AT+CIPSEND` decide the shape of this module.
//!
//! **It is a two-step handshake, and the second step has no line ending.**
//! `AT+CIPSEND=<len>` is answered with `> ` — a prompt, not a line — and the
//! payload that follows is raw bytes with no terminator. Waiting for a newline
//! there waits forever.
//!
//! **The length is declared in advance and must be exact.** Send fewer bytes
//! than promised and the module sits waiting for the rest, swallowing the next
//! command as payload; send more and the excess is interpreted as AT input.
//! Either way the failure surfaces later, as a session that has silently
//! desynchronised.
//!
//! **`+IPD` arrives whenever the broker feels like it**, interleaved with
//! command replies rather than in answer to them. A read that assumes the next
//! line belongs to the command it just sent will eventually eat a message.

use alloc::format;
use alloc::string::String;
use alloc::vec::Vec;

use rustnet_mqtt::Packet;

use crate::espat;
use crate::FirmwareHost;

/// MQTT's default keep-alive, in seconds. Nothing here sends `PINGREQ` on a
/// timer yet, so this is a promise the firmware does not keep on an idle
/// connection — brokers drop the session after one and a half times this. The
/// demo publishes far more often than that; a long-lived connection will need
/// a ping.
const KEEP_ALIVE_S: u16 = 60;

/// How long `Mqtt.Poll` waits for the broker.
///
/// Short on purpose: an application calls this from its frame loop, and a poll
/// that waits seconds for a broker with nothing to say stops the screen rather
/// than the network. Anything that has already arrived is read regardless — the
/// budget bounds the wait, not the reading.
pub(crate) const POLL_BUDGET_MS: u32 = 60;

/// How long one read slice waits while looking for a packet.
///
/// Short, and repeated: the point is to keep asking rather than to wait once.
/// A quiet serial line does not mean a quiet broker.
const SLICE_MS: u32 = 100;

/// A connected broker, and whatever it has said that has not been read yet.
pub(crate) struct MqttSession {
    /// Bytes received and not yet decoded into a packet. MQTT is a stream
    /// protocol over a link that hands over arbitrary chunks, so a packet can
    /// arrive in pieces and two can arrive together.
    pending: Vec<u8>,
    next_packet_id: u16,
    pub(crate) connected: bool,
    /// The raw AT traffic seen while waiting for the last packet.
    ///
    /// Kept so that "the broker did not answer" can say what *did* arrive.
    /// The two ways that error happens — nothing on the wire at all, and a
    /// module reporting `CLOSED` because the connection was refused — look
    /// identical without it, and they call for opposite fixes.
    last_raw: String,
}

impl MqttSession {
    /// Open a TCP connection and complete the MQTT handshake.
    pub(crate) fn open(
        host: &mut FirmwareHost,
        address: &str,
        client_id: &str,
        credentials: Option<(&str, &str)>,
    ) -> Result<Self, String> {
        let (server, port) = split_address(address);

        // Single-connection mode. In multi-connection mode every command and
        // every `+IPD` carries a link id, and the two shapes are not
        // compatible — a module left in the other mode answers ERROR to the
        // CIPSTART below for no visible reason.
        let _ = espat::command(host, "AT+CIPMUX=0", 2_000);
        let _ = espat::command(host, "AT+CIPCLOSE", 2_000);

        let start = format!("AT+CIPSTART=\"TCP\",\"{server}\",{port}");
        espat::command(host, &start, 10_000).map_err(|e| format!("connect to {address}: {e}"))?;

        let mut session = Self { pending: Vec::new(), next_packet_id: 1, connected: false, last_raw: String::new() };
        let connect = Packet::Connect {
            client_id: String::from(client_id),
            keep_alive_s: KEEP_ALIVE_S,
            username: credentials.map(|(u, _)| String::from(u)),
            password: credentials.map(|(_, p)| String::from(p)),
        };
        session.send(host, &connect)?;

        let first = session.wait_for_packet(host, 10_000)?;
        match first {
            Packet::ConnAck { code: 0, .. } => {
                session.connected = true;
                Ok(session)
            }
            // The broker's own refusal codes: 1 unacceptable protocol version,
            // 2 client id rejected, 3 server unavailable, 4 bad credentials,
            // 5 not authorised. Reported as the number because the broker
            // means something specific by it.
            Packet::ConnAck { code, .. } => Err(format!("broker refused the connection ({code})")),
            other => Err(format!("expected CONNACK, got {other:?}")),
        }
    }

    /// Write one packet to the broker.
    ///
    /// Whatever the radio said while acknowledging the send is kept, not
    /// dropped: on a local network the broker's answer arrives inside the same
    /// read as `SEND OK`, and discarding it loses the reply to the very packet
    /// just sent.
    pub(crate) fn send(&mut self, host: &mut FirmwareHost, packet: &Packet) -> Result<(), String> {
        let bytes = packet.encode();
        let overheard = espat::send_raw(host, &bytes)?;
        if !overheard.is_empty() {
            self.last_raw.push_str(&String::from_utf8_lossy(&overheard));
            self.pending.extend_from_slice(&espat::extract_ipd(&overheard));
        }
        Ok(())
    }

    pub(crate) fn publish(
        &mut self,
        host: &mut FirmwareHost,
        topic: &str,
        payload: &[u8],
        qos: u8,
    ) -> Result<(), String> {
        // QoS above 0 needs a packet id and an acknowledgement to match it
        // against; only 0 and 1 exist in this codec, and 1 is where the id
        // matters.
        let packet_id = if qos > 0 {
            let id = self.next_packet_id;
            self.next_packet_id = self.next_packet_id.wrapping_add(1).max(1);
            Some(id)
        } else {
            None
        };
        let packet = Packet::Publish {
            topic: String::from(topic),
            payload: payload.to_vec(),
            qos,
            packet_id,
        };
        self.send(host, &packet)
    }

    pub(crate) fn subscribe(&mut self, host: &mut FirmwareHost, topic: &str) -> Result<(), String> {
        let id = self.next_packet_id;
        self.next_packet_id = self.next_packet_id.wrapping_add(1).max(1);
        let packet = Packet::Subscribe {
            packet_id: id,
            topics: alloc::vec![(String::from(topic), 0)],
        };
        self.send(host, &packet)?;
        match self.wait_for_packet(host, 5_000)? {
            Packet::SubAck { codes, .. } if codes.iter().all(|c| *c < 0x80) => Ok(()),
            Packet::SubAck { .. } => Err(format!("broker refused the subscription to '{topic}'")),
            other => Err(format!("expected SUBACK, got {other:?}")),
        }
    }

    /// The next message the broker has published to us, if one has arrived.
    ///
    /// Returns immediately when there is nothing: an application polling this
    /// from a UI loop must not stall the frame waiting for a broker that has
    /// nothing to say.
    pub(crate) fn poll(
        &mut self,
        host: &mut FirmwareHost,
        budget_ms: u32,
    ) -> Result<Option<(String, Vec<u8>)>, String> {
        self.absorb(host, budget_ms);
        while let Some((packet, used)) = Packet::decode(&self.pending) {
            self.pending.drain(..used);
            match packet {
                Packet::Publish { topic, payload, .. } => return Ok(Some((topic, payload))),
                // Keep-alive and acknowledgements are the broker holding up its
                // end; nothing here waits on them, but they must be consumed or
                // they sit in front of the next real message forever.
                Packet::PingReq => {
                    let _ = self.send(host, &Packet::PingResp);
                }
                _ => {}
            }
        }
        Ok(None)
    }

    /// Read until one whole packet is available, or the budget runs out.
    ///
    /// In slices, and looping. A single long read would stop at the first
    /// quiet moment on the serial line — which arrives long before a broker
    /// several network hops away has answered, and reports "the broker did not
    /// answer" for a broker that answers a second later. Silence on this link
    /// means nothing; only a decodable packet does.
    fn wait_for_packet(
        &mut self,
        host: &mut FirmwareHost,
        budget_ms: u32,
    ) -> Result<Packet, String> {
        let mut waited = 0u32;
        loop {
            if let Some((packet, used)) = Packet::decode(&self.pending) {
                self.pending.drain(..used);
                return Ok(packet);
            }
            if waited >= budget_ms {
                return Err(if self.last_raw.trim().is_empty() {
                    String::from("broker did not answer (nothing arrived on the radio)")
                } else {
                    format!("broker did not answer; radio said: {}", trim_to(&self.last_raw, 90))
                });
            }
            self.absorb(host, SLICE_MS);
            waited += SLICE_MS;
        }
    }

    /// Move whatever the radio has delivered into the pending buffer.
    fn absorb(&mut self, host: &mut FirmwareHost, budget_ms: u32) {
        let text = espat::drain(host, budget_ms);
        if !text.is_empty() {
            self.last_raw.push_str(&String::from_utf8_lossy(&text));
        }
        self.pending.extend_from_slice(&espat::extract_ipd(&text));
    }
}

/// Split `host:port`, defaulting to MQTT's own port.
///
/// The managed API takes one string, and a caller who writes `broker.local`
/// means 1883 — not port zero, and not an error at the far end of a connect
/// that was never going to work.
pub(crate) fn split_address(address: &str) -> (&str, u16) {
    match address.rsplit_once(':') {
        Some((host, port)) => (host, port.parse().unwrap_or(1883)),
        None => (address, 1883),
    }
}

/// The error a managed caller gets for using the broker before opening it.
pub(crate) fn not_connected() -> String {
    String::from("call Mqtt.Connect before using the broker")
}

/// Add the radio's state to a broker error.
///
/// "The broker did not answer" and "the radio dropped the network" are the
/// same sentence from inside an application, and the difference decides
/// whether to retry the publish or rejoin the access point.
pub(crate) fn describe_failure(host: &mut FirmwareHost, what: &str) -> String {
    if host.wifi.ip.is_empty() {
        format!("{what} (no network: the radio has no address)")
    } else {
        format!("{what} (radio still at {})", host.wifi.ip)
    }
}

/// A snippet of AT traffic short enough for a log line, with the unprintable
/// bytes shown. Both halves matter: MQTT payloads are binary, and a reply full
/// of dots is how a wrong baud rate looks.
fn trim_to(text: &str, limit: usize) -> String {
    let mut out = String::new();
    for c in text.chars() {
        if out.len() >= limit {
            break;
        }
        out.push(if c.is_ascii_graphic() || c == ' ' { c } else { '.' });
    }
    out
}
