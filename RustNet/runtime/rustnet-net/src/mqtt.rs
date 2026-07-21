//! MQTT 3.1.1 client: packet codec + blocking client over TCP.

use std::io::{Read, Write};
use std::net::TcpStream;
use std::time::Duration;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Packet {
    Connect {
        client_id: String,
        keep_alive_s: u16,
        username: Option<String>,
        password: Option<String>,
    },
    ConnAck { session_present: bool, code: u8 },
    Publish { topic: String, payload: Vec<u8>, qos: u8, packet_id: Option<u16> },
    PubAck { packet_id: u16 },
    Subscribe { packet_id: u16, topics: Vec<(String, u8)> },
    SubAck { packet_id: u16, codes: Vec<u8> },
    PingReq,
    PingResp,
    Disconnect,
}

fn encode_remaining_length(mut len: usize, out: &mut Vec<u8>) {
    loop {
        let mut byte = (len % 128) as u8;
        len /= 128;
        if len > 0 {
            byte |= 0x80;
        }
        out.push(byte);
        if len == 0 {
            break;
        }
    }
}

fn decode_remaining_length(data: &[u8]) -> Option<(usize, usize)> {
    let mut value = 0usize;
    let mut multiplier = 1usize;
    for (i, byte) in data.iter().enumerate().take(4) {
        value += ((byte & 0x7F) as usize) * multiplier;
        if byte & 0x80 == 0 {
            return Some((value, i + 1));
        }
        multiplier *= 128;
    }
    None
}

fn put_str(s: &str, out: &mut Vec<u8>) {
    out.extend_from_slice(&(s.len() as u16).to_be_bytes());
    out.extend_from_slice(s.as_bytes());
}

fn get_str(data: &[u8], pos: &mut usize) -> Option<String> {
    if *pos + 2 > data.len() {
        return None;
    }
    let len = u16::from_be_bytes([data[*pos], data[*pos + 1]]) as usize;
    *pos += 2;
    if *pos + len > data.len() {
        return None;
    }
    let s = String::from_utf8_lossy(&data[*pos..*pos + len]).to_string();
    *pos += len;
    Some(s)
}

impl Packet {
    pub fn encode(&self) -> Vec<u8> {
        let (header, var): (u8, Vec<u8>) = match self {
            Packet::Connect { client_id, keep_alive_s, username, password } => {
                let mut v = Vec::new();
                put_str("MQTT", &mut v);
                v.push(4); // protocol level 3.1.1
                let mut flags = 0b0000_0010u8; // clean session
                if username.is_some() {
                    flags |= 0b1000_0000;
                }
                if password.is_some() {
                    flags |= 0b0100_0000;
                }
                v.push(flags);
                v.extend_from_slice(&keep_alive_s.to_be_bytes());
                put_str(client_id, &mut v);
                if let Some(u) = username {
                    put_str(u, &mut v);
                }
                if let Some(p) = password {
                    put_str(p, &mut v);
                }
                (0x10, v)
            }
            Packet::ConnAck { session_present, code } => {
                (0x20, vec![*session_present as u8, *code])
            }
            Packet::Publish { topic, payload, qos, packet_id } => {
                let mut v = Vec::new();
                put_str(topic, &mut v);
                if *qos > 0 {
                    v.extend_from_slice(&packet_id.unwrap_or(1).to_be_bytes());
                }
                v.extend_from_slice(payload);
                (0x30 | (qos << 1), v)
            }
            Packet::PubAck { packet_id } => (0x40, packet_id.to_be_bytes().to_vec()),
            Packet::Subscribe { packet_id, topics } => {
                let mut v = packet_id.to_be_bytes().to_vec();
                for (t, qos) in topics {
                    put_str(t, &mut v);
                    v.push(*qos);
                }
                (0x82, v)
            }
            Packet::SubAck { packet_id, codes } => {
                let mut v = packet_id.to_be_bytes().to_vec();
                v.extend_from_slice(codes);
                (0x90, v)
            }
            Packet::PingReq => (0xC0, Vec::new()),
            Packet::PingResp => (0xD0, Vec::new()),
            Packet::Disconnect => (0xE0, Vec::new()),
        };
        let mut out = vec![header];
        encode_remaining_length(var.len(), &mut out);
        out.extend_from_slice(&var);
        out
    }

    /// Decode one packet; returns (packet, bytes consumed).
    pub fn decode(data: &[u8]) -> Option<(Packet, usize)> {
        if data.is_empty() {
            return None;
        }
        let header = data[0];
        let (len, len_bytes) = decode_remaining_length(&data[1..])?;
        let start = 1 + len_bytes;
        if data.len() < start + len {
            return None;
        }
        let var = &data[start..start + len];
        let total = start + len;
        let packet = match header >> 4 {
            1 => {
                let mut pos = 0;
                let _proto = get_str(var, &mut pos)?;
                let _level = *var.get(pos)?;
                let flags = *var.get(pos + 1)?;
                pos += 2; // level + flags
                let keep_alive_s = u16::from_be_bytes([*var.get(pos)?, *var.get(pos + 1)?]);
                pos += 2;
                let client_id = get_str(var, &mut pos)?;
                let username = if flags & 0b1000_0000 != 0 { Some(get_str(var, &mut pos)?) } else { None };
                let password = if flags & 0b0100_0000 != 0 { Some(get_str(var, &mut pos)?) } else { None };
                Packet::Connect { client_id, keep_alive_s, username, password }
            }
            2 => Packet::ConnAck { session_present: var[0] & 1 != 0, code: var[1] },
            3 => {
                let qos = (header >> 1) & 0x03;
                let mut pos = 0;
                let topic = get_str(var, &mut pos)?;
                let packet_id = if qos > 0 {
                    let id = u16::from_be_bytes([*var.get(pos)?, *var.get(pos + 1)?]);
                    pos += 2;
                    Some(id)
                } else {
                    None
                };
                Packet::Publish { topic, payload: var[pos..].to_vec(), qos, packet_id }
            }
            4 => Packet::PubAck { packet_id: u16::from_be_bytes([var[0], var[1]]) },
            8 => {
                let packet_id = u16::from_be_bytes([var[0], var[1]]);
                let mut topics = Vec::new();
                let mut pos = 2;
                while pos < var.len() {
                    let t = get_str(var, &mut pos)?;
                    let qos = *var.get(pos)?;
                    pos += 1;
                    topics.push((t, qos));
                }
                Packet::Subscribe { packet_id, topics }
            }
            9 => Packet::SubAck {
                packet_id: u16::from_be_bytes([var[0], var[1]]),
                codes: var[2..].to_vec(),
            },
            12 => Packet::PingReq,
            13 => Packet::PingResp,
            14 => Packet::Disconnect,
            _ => return None,
        };
        Some((packet, total))
    }
}

/// Blocking MQTT client (QoS 0/1 publish, subscribe, poll).
pub struct MqttClient {
    stream: TcpStream,
    rx_buf: Vec<u8>,
    next_packet_id: u16,
}

#[derive(Debug)]
pub enum MqttError {
    Io(String),
    Refused(u8),
    Protocol(String),
}

impl From<std::io::Error> for MqttError {
    fn from(e: std::io::Error) -> Self {
        MqttError::Io(e.to_string())
    }
}

impl MqttClient {
    pub fn connect(addr: &str, client_id: &str) -> Result<MqttClient, MqttError> {
        Self::connect_auth(addr, client_id, None, None)
    }

    /// Connect with optional username/password — the auth shape cloud IoT
    /// hubs (Azure SAS, GCP JWT, AWS custom auth) expect.
    pub fn connect_auth(
        addr: &str,
        client_id: &str,
        username: Option<&str>,
        password: Option<&str>,
    ) -> Result<MqttClient, MqttError> {
        let mut stream = TcpStream::connect(addr)?;
        stream.set_read_timeout(Some(Duration::from_secs(10)))?;
        stream.write_all(
            &Packet::Connect {
                client_id: client_id.into(),
                keep_alive_s: 60,
                username: username.map(String::from),
                password: password.map(String::from),
            }
            .encode(),
        )?;
        let mut client = MqttClient { stream, rx_buf: Vec::new(), next_packet_id: 1 };
        match client.read_packet()? {
            Packet::ConnAck { code: 0, .. } => Ok(client),
            Packet::ConnAck { code, .. } => Err(MqttError::Refused(code)),
            other => Err(MqttError::Protocol(format!("expected CONNACK, got {other:?}"))),
        }
    }

    pub fn publish(&mut self, topic: &str, payload: &[u8], qos: u8) -> Result<(), MqttError> {
        let packet_id = if qos > 0 {
            let id = self.next_packet_id;
            self.next_packet_id = self.next_packet_id.wrapping_add(1).max(1);
            Some(id)
        } else {
            None
        };
        self.stream.write_all(
            &Packet::Publish { topic: topic.into(), payload: payload.to_vec(), qos, packet_id }
                .encode(),
        )?;
        if qos > 0 {
            match self.read_packet()? {
                Packet::PubAck { .. } => {}
                other => return Err(MqttError::Protocol(format!("expected PUBACK, got {other:?}"))),
            }
        }
        Ok(())
    }

    pub fn subscribe(&mut self, topic: &str) -> Result<(), MqttError> {
        let id = self.next_packet_id;
        self.next_packet_id = self.next_packet_id.wrapping_add(1).max(1);
        self.stream
            .write_all(&Packet::Subscribe { packet_id: id, topics: vec![(topic.into(), 0)] }.encode())?;
        match self.read_packet()? {
            Packet::SubAck { .. } => Ok(()),
            other => Err(MqttError::Protocol(format!("expected SUBACK, got {other:?}"))),
        }
    }

    /// Block until the next PUBLISH arrives (answers pings transparently).
    pub fn poll_message(&mut self) -> Result<(String, Vec<u8>), MqttError> {
        loop {
            match self.read_packet()? {
                Packet::Publish { topic, payload, qos, packet_id } => {
                    if qos > 0 {
                        if let Some(id) = packet_id {
                            self.stream.write_all(&Packet::PubAck { packet_id: id }.encode())?;
                        }
                    }
                    return Ok((topic, payload));
                }
                Packet::PingReq => self.stream.write_all(&Packet::PingResp.encode())?,
                _ => {}
            }
        }
    }

    pub fn ping(&mut self) -> Result<(), MqttError> {
        self.stream.write_all(&Packet::PingReq.encode())?;
        match self.read_packet()? {
            Packet::PingResp => Ok(()),
            other => Err(MqttError::Protocol(format!("expected PINGRESP, got {other:?}"))),
        }
    }

    pub fn disconnect(mut self) -> Result<(), MqttError> {
        self.stream.write_all(&Packet::Disconnect.encode())?;
        Ok(())
    }

    fn read_packet(&mut self) -> Result<Packet, MqttError> {
        loop {
            if let Some((packet, used)) = Packet::decode(&self.rx_buf) {
                self.rx_buf.drain(..used);
                return Ok(packet);
            }
            let mut chunk = [0u8; 1024];
            let n = self.stream.read(&mut chunk)?;
            if n == 0 {
                return Err(MqttError::Io("connection closed".into()));
            }
            self.rx_buf.extend_from_slice(&chunk[..n]);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;

    #[test]
    fn packet_roundtrips() {
        let packets = vec![
            Packet::Connect { client_id: "dev-01".into(), keep_alive_s: 30, username: None, password: None },
            Packet::ConnAck { session_present: false, code: 0 },
            Packet::Publish { topic: "sensors/temp".into(), payload: b"25.5".to_vec(), qos: 0, packet_id: None },
            Packet::Publish { topic: "cmd".into(), payload: b"on".to_vec(), qos: 1, packet_id: Some(7) },
            Packet::PubAck { packet_id: 7 },
            Packet::Subscribe { packet_id: 2, topics: vec![("a/b".into(), 0), ("c/#".into(), 1)] },
            Packet::SubAck { packet_id: 2, codes: vec![0, 1] },
            Packet::PingReq,
            Packet::PingResp,
            Packet::Disconnect,
        ];
        for p in packets {
            let bytes = p.encode();
            let (decoded, used) = Packet::decode(&bytes).expect("decode");
            assert_eq!(used, bytes.len());
            assert_eq!(decoded, p);
        }
    }

    /// Tiny in-process broker good enough to exercise the client.
    fn mini_broker(listener: TcpListener) {
        let (mut stream, _) = listener.accept().unwrap();
        let mut buf = Vec::new();
        let mut retained: Option<(String, Vec<u8>)> = None;
        let mut subscribed = false;
        loop {
            let mut chunk = [0u8; 1024];
            let n = match stream.read(&mut chunk) {
                Ok(0) | Err(_) => return,
                Ok(n) => n,
            };
            buf.extend_from_slice(&chunk[..n]);
            while let Some((packet, used)) = Packet::decode(&buf) {
                buf.drain(..used);
                match packet {
                    Packet::Connect { .. } => {
                        stream
                            .write_all(&Packet::ConnAck { session_present: false, code: 0 }.encode())
                            .unwrap();
                    }
                    Packet::Subscribe { packet_id, topics } => {
                        subscribed = true;
                        stream
                            .write_all(
                                &Packet::SubAck { packet_id, codes: vec![0; topics.len()] }.encode(),
                            )
                            .unwrap();
                        if let Some((t, p)) = retained.take() {
                            stream
                                .write_all(
                                    &Packet::Publish { topic: t, payload: p, qos: 0, packet_id: None }
                                        .encode(),
                                )
                                .unwrap();
                        }
                    }
                    Packet::Publish { topic, payload, qos, packet_id } => {
                        if qos > 0 {
                            stream
                                .write_all(
                                    &Packet::PubAck { packet_id: packet_id.unwrap() }.encode(),
                                )
                                .unwrap();
                        }
                        if subscribed {
                            stream
                                .write_all(
                                    &Packet::Publish { topic, payload, qos: 0, packet_id: None }
                                        .encode(),
                                )
                                .unwrap();
                        } else {
                            retained = Some((topic, payload));
                        }
                    }
                    Packet::PingReq => stream.write_all(&Packet::PingResp.encode()).unwrap(),
                    Packet::Disconnect => return,
                    _ => {}
                }
            }
        }
    }

    #[test]
    fn client_against_mini_broker() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = format!("127.0.0.1:{}", listener.local_addr().unwrap().port());
        let broker = std::thread::spawn(move || mini_broker(listener));

        let mut client = MqttClient::connect(&addr, "test-device").unwrap();
        client.ping().unwrap();
        client.publish("telemetry/temp", b"26.1", 1).unwrap();
        client.subscribe("telemetry/#").unwrap();
        let (topic, payload) = client.poll_message().unwrap();
        assert_eq!(topic, "telemetry/temp");
        assert_eq!(payload, b"26.1");
        client.disconnect().unwrap();
        broker.join().unwrap();
    }
}
