//! RNPC version 2: modules whose code and data must live apart — the ESP32,
//! where instruction RAM is executable but word-addressable only, so constant
//! tables read a byte at a time must sit in data RAM.
//!
//! The module is linked with its code at 0 and its data at
//! [`DATA_LINK_BASE`]; the converter rewrites every absolute word to be
//! relative to the segment it points into and records, per word, which segment
//! holds the word and which segment its value points into. Loading adds the
//! right segment's address to each.
//!
//! ```text
//! "RNPC" | u16 2 | u16 arch | u32 text_len | u32 data_len | u32 bss_len
//!        | u32 n_exports | u32 n_relocs
//!        | n_exports × (u16 name_len | name | u32 text offset)
//!        | n_relocs  × u32 relocation word
//!        | text bytes | data bytes
//! ```
//!
//! A relocation word: bit 31 set = the word is in the data segment (else
//! text); bit 30 set = its value points into the data segment (else text);
//! bits 0..29 = the word's offset within its segment.

use alloc::string::String;
use alloc::vec::Vec;

use crate::{NpcError, MAGIC};

pub const VERSION_SPLIT: u16 = 2;

/// Where the host linker places the data segment, so a value can be told apart.
pub const DATA_LINK_BASE: u32 = 0x1000_0000;

pub const IN_DATA: u32 = 1 << 31;
pub const TO_DATA: u32 = 1 << 30;
const OFFSET_MASK: u32 = (1 << 30) - 1;

/// Instruction sets that use the split layout.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u16)]
pub enum SplitArch {
    /// ESP32 (Xtensa LX6, windowed ABI).
    XtensaEsp32 = 3,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SplitReloc {
    pub in_data: bool,
    pub to_data: bool,
    pub offset: u32,
}

#[derive(Debug)]
pub struct SplitModule<'a> {
    pub arch: SplitArch,
    pub text: &'a [u8],
    pub data: &'a [u8],
    pub bss_len: u32,
    pub exports: Vec<(String, u32)>,
    pub relocs: Vec<SplitReloc>,
}

impl<'a> SplitModule<'a> {
    pub fn parse(blob: &'a [u8], device: SplitArch) -> Result<Self, NpcError> {
        let mut at = 0usize;
        let mut take = |n: usize| -> Result<&'a [u8], NpcError> {
            let s = blob.get(at..at + n).ok_or(NpcError::Truncated)?;
            at += n;
            Ok(s)
        };
        if take(4)? != MAGIC {
            return Err(NpcError::NotAModule);
        }
        let u16_ = |b: &[u8]| u16::from_le_bytes([b[0], b[1]]);
        let u32_ = |b: &[u8]| u32::from_le_bytes([b[0], b[1], b[2], b[3]]);
        let version = u16_(take(2)?);
        if version != VERSION_SPLIT {
            return Err(NpcError::UnsupportedVersion(version));
        }
        let arch = u16_(take(2)?);
        if arch != device as u16 {
            return Err(NpcError::NotAModule);
        }
        let text_len = u32_(take(4)?) as usize;
        let data_len = u32_(take(4)?) as usize;
        let bss_len = u32_(take(4)?);
        let n_exports = u32_(take(4)?) as usize;
        let n_relocs = u32_(take(4)?) as usize;
        if n_exports > 4096 || n_relocs > blob.len() / 4 {
            return Err(NpcError::Truncated);
        }
        let mut exports = Vec::with_capacity(n_exports);
        for _ in 0..n_exports {
            let len = u16_(take(2)?) as usize;
            let name = core::str::from_utf8(take(len)?).map_err(|_| NpcError::NotAModule)?;
            let value = u32_(take(4)?);
            if value as usize >= text_len {
                return Err(NpcError::NoSuchProcedure);
            }
            exports.push((String::from(name), value));
        }
        let mut relocs = Vec::with_capacity(n_relocs);
        for _ in 0..n_relocs {
            let w = u32_(take(4)?);
            let r = SplitReloc { in_data: w & IN_DATA != 0, to_data: w & TO_DATA != 0, offset: w & OFFSET_MASK };
            let seg_len = if r.in_data { data_len } else { text_len };
            if r.offset % 4 != 0 || r.offset as usize + 4 > seg_len {
                return Err(NpcError::BadRelocation(w));
            }
            relocs.push(r);
        }
        let text = take(text_len)?;
        let data = take(data_len)?;
        if at != blob.len() {
            return Err(NpcError::NotAModule);
        }
        Ok(Self { arch: SplitArch::XtensaEsp32, text, data, bss_len, exports, relocs })
    }

    pub fn data_size(&self) -> usize {
        self.data.len() + self.bss_len as usize
    }

    /// Fill `text_out` (a staging copy — instruction RAM takes only word
    /// writes, so the caller copies it across a word at a time) and `data_out`
    /// (the data segment itself), relocated for the given addresses.
    pub fn load_into(&self, text_out: &mut [u8], text_base: u32, data_out: &mut [u8], data_base: u32) -> Result<(), NpcError> {
        if text_out.len() < self.text.len() || data_out.len() < self.data_size() || text_base % 4 != 0 || data_base % 4 != 0 {
            return Err(NpcError::BadDestination);
        }
        text_out[..self.text.len()].copy_from_slice(self.text);
        data_out[..self.data.len()].copy_from_slice(self.data);
        for b in &mut data_out[self.data.len()..self.data_size()] {
            *b = 0;
        }
        for r in &self.relocs {
            let seg: &mut [u8] = if r.in_data { data_out } else { text_out };
            let o = r.offset as usize;
            let w = u32::from_le_bytes([seg[o], seg[o + 1], seg[o + 2], seg[o + 3]]);
            let base = if r.to_data { data_base } else { text_base };
            seg[o..o + 4].copy_from_slice(&w.wrapping_add(base).to_le_bytes());
        }
        Ok(())
    }

    pub fn export(&self, name: &str) -> Result<u32, NpcError> {
        self.exports.iter().find(|(n, _)| n == name).map(|(_, v)| *v).ok_or(NpcError::NoSuchProcedure)
    }
}

/// Peek at a blob's format version without parsing it.
pub fn version_of(blob: &[u8]) -> Option<u16> {
    if blob.len() < 6 || &blob[..4] != MAGIC {
        return None;
    }
    Some(u16::from_le_bytes([blob[4], blob[5]]))
}

#[cfg(test)]
mod tests {
    use super::*;
    use alloc::vec;

    fn build(text: &[u8], data: &[u8], bss: u32, exports: &[(&str, u32)], relocs: &[u32]) -> Vec<u8> {
        let mut o = Vec::new();
        o.extend_from_slice(MAGIC);
        o.extend_from_slice(&VERSION_SPLIT.to_le_bytes());
        o.extend_from_slice(&(SplitArch::XtensaEsp32 as u16).to_le_bytes());
        for v in [text.len() as u32, data.len() as u32, bss, exports.len() as u32, relocs.len() as u32] {
            o.extend_from_slice(&v.to_le_bytes());
        }
        for (n, v) in exports {
            o.extend_from_slice(&(n.len() as u16).to_le_bytes());
            o.extend_from_slice(n.as_bytes());
            o.extend_from_slice(&v.to_le_bytes());
        }
        for r in relocs {
            o.extend_from_slice(&r.to_le_bytes());
        }
        o.extend_from_slice(text);
        o.extend_from_slice(data);
        o
    }

    #[test]
    fn words_are_rebased_by_the_segment_they_point_into() {
        // text: a literal at 4 pointing at data offset 8, a literal at 8
        // pointing at text offset 0x10. data: a pointer at 0 to text offset 0.
        let mut text = vec![0u8; 16];
        text[4..8].copy_from_slice(&8u32.to_le_bytes());
        text[8..12].copy_from_slice(&0x10u32.to_le_bytes());
        let data = 0u32.to_le_bytes();
        let blob = build(&text, &data, 4, &[("f", 0)], &[4 | TO_DATA, 8, IN_DATA]);
        assert_eq!(version_of(&blob), Some(2));
        let m = SplitModule::parse(&blob, SplitArch::XtensaEsp32).unwrap();
        let (mut t, mut d) = (vec![0u8; 16], vec![0xAAu8; 8]);
        m.load_into(&mut t, 0x4008_0000, &mut d, 0x3FFB_0000).unwrap();
        assert_eq!(u32::from_le_bytes(t[4..8].try_into().unwrap()), 0x3FFB_0008);
        assert_eq!(u32::from_le_bytes(t[8..12].try_into().unwrap()), 0x4008_0010);
        assert_eq!(u32::from_le_bytes(d[0..4].try_into().unwrap()), 0x4008_0000);
        assert_eq!(&d[4..8], &[0, 0, 0, 0], "bss zeroed");
    }

    #[test]
    fn rejects_bad_split_modules() {
        let blob = build(&[0; 8], &[0; 4], 0, &[], &[IN_DATA | 4]);
        assert!(matches!(SplitModule::parse(&blob, SplitArch::XtensaEsp32), Err(NpcError::BadRelocation(_))));
        let blob = build(&[0; 8], &[], 0, &[("f", 8)], &[]);
        assert_eq!(SplitModule::parse(&blob, SplitArch::XtensaEsp32).unwrap_err(), NpcError::NoSuchProcedure);
        let v1 = crate::build(crate::Arch::ThumbV6M, &[0; 4], 0, 4, &[], &[]);
        assert_eq!(SplitModule::parse(&v1, SplitArch::XtensaEsp32).unwrap_err(), NpcError::UnsupportedVersion(1));
    }
}
