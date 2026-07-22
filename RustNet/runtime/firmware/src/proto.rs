//! RNDP — RustNet Device Protocol.
//!
//! Frame (both directions):
//! ```text
//! 0x52 0x4E | u8 code | u32 len (LE) | payload[len] | u16 crc (LE)
//! ```
//! CRC-16/CCITT-FALSE over code+len+payload. Requests use command codes;
//! responses use status codes (0x00 OK, 0x01 ERR with UTF-8 message).

pub const MAGIC: [u8; 2] = [0x52, 0x4E];
pub const PROTOCOL_VERSION: u8 = 1;

// Command codes
pub const CMD_PING: u8 = 0x01;
pub const CMD_INFO: u8 = 0x02;
pub const CMD_PROVISION_KEY: u8 = 0x03;
pub const CMD_LIST_APPS: u8 = 0x10;
pub const CMD_FLASH_APP: u8 = 0x11;
pub const CMD_ERASE_APP: u8 = 0x12;
pub const CMD_START_APP: u8 = 0x13;
pub const CMD_STOP_APP: u8 = 0x14;
/// Set (payload = app name) or clear (empty payload) the app that auto-runs on
/// power-up / reboot.
pub const CMD_SET_AUTOSTART: u8 = 0x15;
pub const CMD_FLASH_DATA: u8 = 0x20;
pub const CMD_READ_DATA: u8 = 0x21;
pub const CMD_SET_CONFIG: u8 = 0x30;
pub const CMD_GET_CONFIG: u8 = 0x31;
pub const CMD_WIFI_CONFIG: u8 = 0x32;
pub const CMD_GET_LOGS: u8 = 0x40;
pub const CMD_GET_PERF: u8 = 0x41;
pub const CMD_SET_BOOT_IMAGE: u8 = 0x50;
pub const CMD_GET_BOOT_IMAGE: u8 = 0x51;
pub const CMD_GET_DISPLAY: u8 = 0x52;
pub const CMD_IO_STATE: u8 = 0x53;
pub const CMD_OTA_BEGIN: u8 = 0x60;
pub const CMD_OTA_DATA: u8 = 0x61;
pub const CMD_OTA_END: u8 = 0x62;
pub const CMD_OTA_CONFIRM: u8 = 0x63;
pub const CMD_OTA_ROLLBACK: u8 = 0x64;
pub const CMD_DEBUG_SET_BP: u8 = 0x70;
pub const CMD_DEBUG_CONTINUE: u8 = 0x71;
pub const CMD_DEBUG_STEP: u8 = 0x72;
pub const CMD_DEBUG_STACK: u8 = 0x73;
pub const CMD_DEBUG_CLEAR_BP: u8 = 0x74;
pub const CMD_DEBUG_STATE: u8 = 0x75;
pub const CMD_DEBUG_LOCALS: u8 = 0x76;
pub const CMD_REBOOT: u8 = 0x7F;

// Status codes
pub const ST_OK: u8 = 0x00;
pub const ST_ERR: u8 = 0x01;

/// CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF).
pub fn crc16(data: &[u8]) -> u16 {
    let mut crc = 0xFFFFu16;
    for &b in data {
        crc ^= (b as u16) << 8;
        for _ in 0..8 {
            crc = if crc & 0x8000 != 0 { (crc << 1) ^ 0x1021 } else { crc << 1 };
        }
    }
    crc
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Frame {
    pub code: u8,
    pub payload: Vec<u8>,
}

impl Frame {
    pub fn new(code: u8, payload: impl Into<Vec<u8>>) -> Self {
        Self { code, payload: payload.into() }
    }

    pub fn ok(payload: impl Into<Vec<u8>>) -> Self {
        Self::new(ST_OK, payload)
    }

    pub fn err(message: impl AsRef<str>) -> Self {
        Self::new(ST_ERR, message.as_ref().as_bytes().to_vec())
    }

    pub fn encode(&self) -> Vec<u8> {
        let mut body = vec![self.code];
        body.extend_from_slice(&(self.payload.len() as u32).to_le_bytes());
        body.extend_from_slice(&self.payload);
        let crc = crc16(&body);
        let mut out = MAGIC.to_vec();
        out.extend_from_slice(&body);
        out.extend_from_slice(&crc.to_le_bytes());
        out
    }

    /// Try to decode one frame; returns (frame, bytes_consumed).
    pub fn decode(buf: &[u8]) -> Result<Option<(Frame, usize)>, String> {
        if buf.len() < 9 {
            return Ok(None);
        }
        if buf[0..2] != MAGIC {
            return Err("bad frame magic".into());
        }
        let code = buf[2];
        let len = u32::from_le_bytes(buf[3..7].try_into().unwrap()) as usize;
        if len > 64 * 1024 * 1024 {
            return Err("frame too large".into());
        }
        let total = 2 + 1 + 4 + len + 2;
        if buf.len() < total {
            return Ok(None);
        }
        let payload = buf[7..7 + len].to_vec();
        let crc_got = u16::from_le_bytes([buf[7 + len], buf[8 + len]]);
        let crc_want = crc16(&buf[2..7 + len]);
        if crc_got != crc_want {
            return Err(format!("crc mismatch: got {crc_got:04x}, want {crc_want:04x}"));
        }
        Ok(Some((Frame { code, payload }, total)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_roundtrip() {
        let f = Frame::new(CMD_FLASH_APP, b"payload-bytes".to_vec());
        let bytes = f.encode();
        let (decoded, used) = Frame::decode(&bytes).unwrap().unwrap();
        assert_eq!(used, bytes.len());
        assert_eq!(decoded, f);
    }

    #[test]
    fn partial_frame_returns_none() {
        let bytes = Frame::new(CMD_PING, vec![1, 2, 3]).encode();
        assert!(Frame::decode(&bytes[..5]).unwrap().is_none());
    }

    #[test]
    fn corrupt_crc_rejected() {
        let mut bytes = Frame::new(CMD_PING, vec![1, 2, 3]).encode();
        let n = bytes.len();
        bytes[n - 1] ^= 0xFF;
        assert!(Frame::decode(&bytes).is_err());
    }

    #[test]
    fn crc16_known_value() {
        // CRC-16/CCITT-FALSE("123456789") = 0x29B1
        assert_eq!(crc16(b"123456789"), 0x29B1);
    }
}
