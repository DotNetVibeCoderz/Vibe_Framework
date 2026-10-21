//! RNDP over Bluetooth LE — the radio-independent half.
//!
//! A BLE link is not a byte pipe: the host writes packets to an RX
//! characteristic and the device answers with notifications on a TX
//! characteristic, each bounded by the connection's negotiated ATT MTU. RNDP
//! frames are carried across them unchanged — a frame may span many packets
//! and one packet may end mid-frame — so the device needs exactly two things a
//! UART does not: a reassembly buffer that turns packets back into frames, and
//! a chunker that cuts a response into notification-sized pieces.
//!
//! Both live here, in the host-tested crate, so the ESP32 GATT server
//! (`firmware-esp32/src/ble.rs`) is left with nothing but the radio calls. The
//! host side's mirror is `dotnet/tools/RustNet.Deploy/BleTransport.cs`.
//!
//! The service is Nordic-UART-shaped and uses the NUS UUIDs verbatim, so a
//! generic BLE console (nRF Connect, Web Bluetooth UART pages) can talk to a
//! device as well as the RustNet tools can.

use crate::proto::Frame;

/// The GATT service carrying RNDP (Nordic UART Service).
pub const SERVICE_UUID: &str = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
/// Host → device: write / write-without-response.
pub const RX_CHAR_UUID: &str = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
/// Device → host: notify.
pub const TX_CHAR_UUID: &str = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";

/// What every RustNet device's advertised name starts with; `rustnet ble scan`
/// and `--device ble:<name>` match on it.
pub const NAME_PREFIX: &str = "RustNet-";

/// The ATT MTU every BLE link starts with, before any exchange.
pub const ATT_MTU_MIN: u16 = 23;

/// ATT's own header in a notification / write: opcode + attribute handle.
const ATT_HEADER: usize = 3;

/// Bytes of application data one notification can carry at `att_mtu`.
///
/// A link always supports at least the spec minimum of 23, whatever a stack
/// reports while the exchange is still in flight (NimBLE says 0 for a handle it
/// does not know), so the result is never below 20.
pub fn notify_payload_len(att_mtu: u16) -> usize {
    att_mtu.max(ATT_MTU_MIN) as usize - ATT_HEADER
}

/// Split `data` into notification-sized pieces for a link at `att_mtu`.
pub fn chunks(data: &[u8], att_mtu: u16) -> std::slice::Chunks<'_, u8> {
    data.chunks(notify_payload_len(att_mtu))
}

/// The advertised name for a device whose Bluetooth MAC is `mac` (most
/// significant byte first, as printed): `RustNet-` plus its last two bytes, so
/// several boards on one desk can be told apart without reading full addresses.
pub fn advertised_name(mac: [u8; 6]) -> String {
    format!("{NAME_PREFIX}{:02X}{:02X}", mac[4], mac[5])
}

/// Reassembles RNDP frames from BLE packets.
///
/// The same discipline as the UART and TCP session loops: append what arrived,
/// decode as many whole frames as there are, and on a framing error drop the
/// buffer and carry on — a byte stream that has lost sync cannot be repaired
/// by guessing, and the host retries.
#[derive(Default)]
pub struct RndpReassembler {
    buf: Vec<u8>,
}

impl RndpReassembler {
    pub fn new() -> Self {
        Self::default()
    }

    /// Append one packet's bytes.
    pub fn push(&mut self, packet: &[u8]) {
        self.buf.extend_from_slice(packet);
    }

    /// The next complete frame, if one has fully arrived.
    pub fn next_frame(&mut self) -> Option<Frame> {
        match Frame::decode(&self.buf) {
            Ok(Some((frame, used))) => {
                self.buf.drain(..used);
                Some(frame)
            }
            Ok(None) => None,
            Err(_) => {
                self.buf.clear();
                None
            }
        }
    }

    /// Forget any partial frame — a new connection starts a new stream.
    pub fn reset(&mut self) {
        self.buf.clear();
    }

    /// Bytes waiting for the rest of their frame.
    pub fn pending(&self) -> usize {
        self.buf.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::proto::{CMD_FLASH_APP, CMD_INFO, CMD_PING};

    #[test]
    fn payload_len_is_mtu_minus_att_header_with_the_spec_floor() {
        assert_eq!(notify_payload_len(23), 20);
        assert_eq!(notify_payload_len(247), 244);
        assert_eq!(notify_payload_len(517), 514);
        // An unknown/zero MTU is treated as the spec minimum, never as 0.
        assert_eq!(notify_payload_len(0), 20);
        assert_eq!(notify_payload_len(10), 20);
    }

    #[test]
    fn chunks_cover_the_response_exactly_and_respect_the_mtu() {
        let response = Frame::ok(vec![0xA5; 1000]).encode();
        for mtu in [23u16, 100, 185, 247, 517] {
            let pieces: Vec<&[u8]> = chunks(&response, mtu).collect();
            assert!(pieces.iter().all(|p| p.len() <= mtu as usize - 3 && !p.is_empty()));
            assert_eq!(pieces.len(), response.len().div_ceil(mtu as usize - 3));
            assert_eq!(pieces.concat(), response);
        }
    }

    #[test]
    fn empty_response_needs_no_packets() {
        assert_eq!(chunks(&[], 23).count(), 0);
    }

    #[test]
    fn name_uses_the_last_two_mac_bytes() {
        assert_eq!(advertised_name([0x24, 0x0A, 0xC4, 0x12, 0xAB, 0x0F]), "RustNet-AB0F");
        assert!(advertised_name([0; 6]).starts_with(NAME_PREFIX));
    }

    #[test]
    fn reassembles_frames_split_across_packets() {
        let a = Frame::new(CMD_FLASH_APP, vec![7u8; 300]);
        let b = Frame::new(CMD_PING, Vec::new());
        let mut stream = a.encode();
        stream.extend(b.encode());

        let mut r = RndpReassembler::new();
        let mut got = Vec::new();
        // 20-byte packets: the boundary between the two frames falls mid-packet.
        for packet in chunks(&stream, 23) {
            r.push(packet);
            while let Some(f) = r.next_frame() {
                got.push(f);
            }
        }
        assert_eq!(got, vec![a, b]);
        assert_eq!(r.pending(), 0);
    }

    #[test]
    fn holds_a_partial_frame_until_it_completes() {
        let f = Frame::new(CMD_INFO, vec![1, 2, 3]).encode();
        let mut r = RndpReassembler::new();
        r.push(&f[..4]);
        assert!(r.next_frame().is_none());
        assert_eq!(r.pending(), 4);
        r.push(&f[4..]);
        assert_eq!(r.next_frame().unwrap().code, CMD_INFO);
    }

    #[test]
    fn garbage_is_dropped_and_the_next_frame_still_decodes() {
        let mut r = RndpReassembler::new();
        r.push(b"not an rndp frame at all");
        assert!(r.next_frame().is_none());
        assert_eq!(r.pending(), 0);
        r.push(&Frame::new(CMD_PING, Vec::new()).encode());
        assert_eq!(r.next_frame().unwrap().code, CMD_PING);
    }

    #[test]
    fn reset_discards_a_half_received_frame() {
        let f = Frame::new(CMD_PING, vec![9; 50]).encode();
        let mut r = RndpReassembler::new();
        r.push(&f[..30]);
        r.reset();
        r.push(&f);
        assert_eq!(r.next_frame().unwrap().payload, vec![9; 50]);
    }
}
