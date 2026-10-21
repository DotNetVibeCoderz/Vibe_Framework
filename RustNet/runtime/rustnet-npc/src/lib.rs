//! NPC (Native Procedure Caller) on microcontrollers.
//!
//! The host/virtual device loads native modules as dynamic libraries. A
//! microcontroller has no dynamic loader, so its modules travel in the
//! **RNPC** format instead: a block of machine code and data linked at
//! address 0, plus the list of 32-bit words that hold absolute addresses.
//! Loading is a copy into RAM and one addition per listed word — everything
//! else (calls, branches, PC-relative loads) is position-independent by
//! construction, because the block moves as a whole.
//!
//! The host tools produce RNPC from a `no_std` Rust staticlib (`rustnet native
//! build --target thumbv6m-none-eabi`): `rust-lld` links it at 0 keeping its
//! relocations (`--emit-relocs`), and the converter records every absolute
//! one: a 32-bit word (`R_ARM_ABS32`, literal pools and pointer tables), or —
//! on Cortex-M4/M7, where the precompiled `core` builds addresses with them —
//! a `movw`/`movt` instruction pair (`R_ARM_THM_MOVW_ABS_NC` + `MOVT_ABS`).
//!
//! ```text
//! "RNPC" | u16 version | u16 arch | u32 image_len | u32 bss_len | u32 align
//!        | u32 n_exports | u32 n_relocs
//!        | n_exports × (u16 name_len | name | u32 value)
//!        | n_relocs  × u32 relocation word
//!        | image bytes
//!
//! A relocation word is the offset of an absolute 32-bit word, or — bit 31
//! set — the offset of a `movw` immediately followed (as the next relocation
//! word) by the offset of its `movt`.
//! ```
//!
//! Every procedure has the NPC ABI:
//! `extern "C" fn(args: *const u8, args_len: u32, out: *mut u8, out_cap: u32) -> i32`.
//! On Thumb, a function's `value` carries the Thumb bit and is called as is.

#![no_std]

extern crate alloc;

use alloc::string::String;
use alloc::vec::Vec;

pub mod split;

pub const MAGIC: &[u8; 4] = b"RNPC";
pub const VERSION: u16 = 1;

/// The instruction set a module was built for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u16)]
pub enum Arch {
    /// Cortex-M0/M0+ (RP2040).
    ThumbV6M = 1,
    /// Cortex-M4/M7 (STM32F4, Meadow F7).
    ThumbV7EM = 2,
}

impl Arch {
    pub fn from_u16(v: u16) -> Option<Self> {
        match v {
            1 => Some(Arch::ThumbV6M),
            2 => Some(Arch::ThumbV7EM),
            _ => None,
        }
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum NpcError {
    NotAModule,
    UnsupportedVersion(u16),
    WrongArch { module: u16, device: Arch },
    Truncated,
    /// A relocation points outside the image, or not at a 4-byte boundary.
    BadRelocation(u32),
    /// The destination buffer is too small or misaligned.
    BadDestination,
    NoSuchProcedure,
}

impl core::fmt::Display for NpcError {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            NpcError::NotAModule => write!(f, "not an RNPC native module"),
            NpcError::UnsupportedVersion(v) => write!(f, "unsupported RNPC version {v}"),
            NpcError::WrongArch { module, device } => {
                write!(f, "module built for architecture {module}, device is {device:?}")
            }
            NpcError::Truncated => write!(f, "native module truncated"),
            NpcError::BadRelocation(o) => write!(f, "bad relocation at offset {o}"),
            NpcError::BadDestination => write!(f, "load buffer too small or misaligned"),
            NpcError::NoSuchProcedure => write!(f, "no such procedure"),
        }
    }
}

/// A parsed module, not yet placed in memory.
#[derive(Debug)]
pub struct Module<'a> {
    pub arch: Arch,
    pub image: &'a [u8],
    pub bss_len: u32,
    pub align: u32,
    pub exports: Vec<(String, u32)>,
    pub relocs: Vec<Reloc>,
}

/// One place in the image that holds an absolute address.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Reloc {
    /// A 32-bit little-endian word.
    Word(u32),
    /// A Thumb-2 `movw` (low half) and `movt` (high half) loading one address.
    MovwMovt { movw: u32, movt: u32 },
}

/// Bit 31 of a relocation word: a movw/movt pair follows.
pub const RELOC_PAIR: u32 = 0x8000_0000;

/// The 16-bit immediate of a Thumb-2 MOVW/MOVT (encoding T3/T1) at `at`.
fn thumb_imm16(b: &[u8], at: usize) -> u16 {
    let hw1 = u16::from_le_bytes([b[at], b[at + 1]]);
    let hw2 = u16::from_le_bytes([b[at + 2], b[at + 3]]);
    let imm4 = hw1 & 0xF;
    let i = (hw1 >> 10) & 1;
    let imm3 = (hw2 >> 12) & 7;
    let imm8 = hw2 & 0xFF;
    (imm4 << 12) | (i << 11) | (imm3 << 8) | imm8
}

fn set_thumb_imm16(b: &mut [u8], at: usize, v: u16) {
    let mut hw1 = u16::from_le_bytes([b[at], b[at + 1]]);
    let mut hw2 = u16::from_le_bytes([b[at + 2], b[at + 3]]);
    hw1 = (hw1 & !0x040F) | ((v >> 12) & 0xF) | (((v >> 11) & 1) << 10);
    hw2 = (hw2 & !0x70FF) | (((v >> 8) & 7) << 12) | (v & 0xFF);
    b[at..at + 2].copy_from_slice(&hw1.to_le_bytes());
    b[at + 2..at + 4].copy_from_slice(&hw2.to_le_bytes());
}

/// Is this a Thumb-2 MOVW (`movt == false`) or MOVT instruction?
fn is_movw_movt(b: &[u8], at: usize, movt: bool) -> bool {
    let hw1 = u16::from_le_bytes([b[at], b[at + 1]]);
    let hw2 = u16::from_le_bytes([b[at + 2], b[at + 3]]);
    let want = if movt { 0xF2C0 } else { 0xF240 };
    (hw1 & 0xFBF0) == want && (hw2 & 0x8000) == 0
}

struct Reader<'a> {
    b: &'a [u8],
    at: usize,
}

impl<'a> Reader<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8], NpcError> {
        let s = self.b.get(self.at..self.at + n).ok_or(NpcError::Truncated)?;
        self.at += n;
        Ok(s)
    }
    fn u16(&mut self) -> Result<u16, NpcError> {
        let s = self.take(2)?;
        Ok(u16::from_le_bytes([s[0], s[1]]))
    }
    fn u32(&mut self) -> Result<u32, NpcError> {
        let s = self.take(4)?;
        Ok(u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
    }
}

impl<'a> Module<'a> {
    /// Parse and validate an RNPC blob for a device of architecture `device`.
    pub fn parse(blob: &'a [u8], device: Arch) -> Result<Self, NpcError> {
        let mut r = Reader { b: blob, at: 0 };
        if r.take(4)? != MAGIC {
            return Err(NpcError::NotAModule);
        }
        let version = r.u16()?;
        if version != VERSION {
            return Err(NpcError::UnsupportedVersion(version));
        }
        let arch_raw = r.u16()?;
        let arch = Arch::from_u16(arch_raw).ok_or(NpcError::WrongArch { module: arch_raw, device })?;
        // Thumb-2 code does not run on an M0+; M0+ code does run on an M7.
        if arch != device && !(arch == Arch::ThumbV6M && device == Arch::ThumbV7EM) {
            return Err(NpcError::WrongArch { module: arch_raw, device });
        }
        let image_len = r.u32()? as usize;
        let bss_len = r.u32()?;
        let align = r.u32()?.max(4);
        if !align.is_power_of_two() || align > 4096 {
            return Err(NpcError::NotAModule);
        }
        let n_exports = r.u32()? as usize;
        let n_relocs = r.u32()? as usize;
        if n_exports > 4096 || n_relocs > (blob.len() / 4) {
            return Err(NpcError::Truncated);
        }
        let mut exports = Vec::with_capacity(n_exports);
        for _ in 0..n_exports {
            let len = r.u16()? as usize;
            let name = core::str::from_utf8(r.take(len)?).map_err(|_| NpcError::NotAModule)?;
            let value = r.u32()?;
            exports.push((String::from(name), value));
        }
        let mut words = Vec::with_capacity(n_relocs);
        for _ in 0..n_relocs {
            words.push(r.u32()?);
        }
        let image = r.take(image_len)?;
        let mut relocs = Vec::with_capacity(n_relocs);
        let mut k = 0;
        while k < words.len() {
            let w = words[k];
            if w & RELOC_PAIR == 0 {
                if w % 4 != 0 || w as usize + 4 > image_len {
                    return Err(NpcError::BadRelocation(w));
                }
                relocs.push(Reloc::Word(w));
                k += 1;
            } else {
                let movw = w & !RELOC_PAIR;
                let movt = *words.get(k + 1).ok_or(NpcError::BadRelocation(w))?;
                for (at, is_t) in [(movw, false), (movt, true)] {
                    if at % 2 != 0 || at as usize + 4 > image_len || !is_movw_movt(image, at as usize, is_t) {
                        return Err(NpcError::BadRelocation(at));
                    }
                }
                relocs.push(Reloc::MovwMovt { movw, movt });
                k += 2;
            }
        }
        if r.at != blob.len() {
            return Err(NpcError::NotAModule); // trailing bytes
        }
        for (_, v) in &exports {
            if (*v & !1) as usize >= image_len {
                return Err(NpcError::NoSuchProcedure);
            }
        }
        Ok(Self { arch, image, bss_len, align, exports, relocs })
    }

    /// Bytes of RAM the loaded module occupies.
    pub fn load_size(&self) -> usize {
        self.image.len() + self.bss_len as usize
    }

    /// Place the module in `dest`, which will live at address `base` when it
    /// runs (`dest.as_ptr() as u32` on the device; anything in tests). The
    /// image is copied, `.bss` zeroed and every absolute word rebased.
    pub fn load_into(&self, dest: &mut [u8], base: u32) -> Result<(), NpcError> {
        if dest.len() < self.load_size() || base % self.align != 0 {
            return Err(NpcError::BadDestination);
        }
        dest[..self.image.len()].copy_from_slice(self.image);
        for b in &mut dest[self.image.len()..self.load_size()] {
            *b = 0;
        }
        for r in &self.relocs {
            match *r {
                Reloc::Word(off) => {
                    let o = off as usize;
                    let w = u32::from_le_bytes([dest[o], dest[o + 1], dest[o + 2], dest[o + 3]]);
                    dest[o..o + 4].copy_from_slice(&w.wrapping_add(base).to_le_bytes());
                }
                Reloc::MovwMovt { movw, movt } => {
                    let (lo, hi) = (movw as usize, movt as usize);
                    let v = ((thumb_imm16(dest, hi) as u32) << 16 | thumb_imm16(dest, lo) as u32).wrapping_add(base);
                    set_thumb_imm16(dest, lo, v as u16);
                    set_thumb_imm16(dest, hi, (v >> 16) as u16);
                }
            }
        }
        Ok(())
    }

    /// Offset of a procedure within the loaded module (Thumb bit included).
    pub fn export(&self, name: &str) -> Result<u32, NpcError> {
        self.exports
            .iter()
            .find(|(n, _)| n == name)
            .map(|(_, v)| *v)
            .ok_or(NpcError::NoSuchProcedure)
    }
}

/// Serialise a module (used by tests here and mirrored by the C# converter).
pub fn build(arch: Arch, image: &[u8], bss_len: u32, align: u32, exports: &[(&str, u32)], relocs: &[u32]) -> Vec<u8> {
    // `relocs` are raw relocation words (pairs already flagged with RELOC_PAIR).
    let mut out = Vec::new();
    out.extend_from_slice(MAGIC);
    out.extend_from_slice(&VERSION.to_le_bytes());
    out.extend_from_slice(&(arch as u16).to_le_bytes());
    out.extend_from_slice(&(image.len() as u32).to_le_bytes());
    out.extend_from_slice(&bss_len.to_le_bytes());
    out.extend_from_slice(&align.to_le_bytes());
    out.extend_from_slice(&(exports.len() as u32).to_le_bytes());
    out.extend_from_slice(&(relocs.len() as u32).to_le_bytes());
    for (name, value) in exports {
        out.extend_from_slice(&(name.len() as u16).to_le_bytes());
        out.extend_from_slice(name.as_bytes());
        out.extend_from_slice(&value.to_le_bytes());
    }
    for r in relocs {
        out.extend_from_slice(&r.to_le_bytes());
    }
    out.extend_from_slice(image);
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use alloc::vec;

    fn sample() -> Vec<u8> {
        // 16-byte image: a word at 8 holding the address of offset 0x4 (an
        // absolute pointer, so relocated), and a word at 12 that is not.
        let mut image = vec![0u8; 16];
        image[8..12].copy_from_slice(&0x4u32.to_le_bytes());
        image[12..16].copy_from_slice(&0xDEADu32.to_le_bytes());
        build(Arch::ThumbV6M, &image, 8, 4, &[("rustnet_sum_u32", 0x1), ("other", 0x5)], &[8])
    }

    #[test]
    fn load_rebases_absolute_words_only() {
        let blob = sample();
        let m = Module::parse(&blob, Arch::ThumbV6M).unwrap();
        assert_eq!(m.load_size(), 24);
        let mut ram = vec![0xAAu8; 24];
        m.load_into(&mut ram, 0x2000_1000).unwrap();
        assert_eq!(u32::from_le_bytes(ram[8..12].try_into().unwrap()), 0x2000_1004);
        assert_eq!(u32::from_le_bytes(ram[12..16].try_into().unwrap()), 0xDEAD);
        assert!(ram[16..].iter().all(|b| *b == 0), "bss zeroed");
        assert_eq!(m.export("rustnet_sum_u32").unwrap(), 1);
        assert_eq!(m.export("missing"), Err(NpcError::NoSuchProcedure));
    }

    /// Encode a Thumb-2 MOVW (or MOVT) r0, #imm16.
    fn movw(imm: u16, movt: bool) -> [u8; 4] {
        let hw1: u16 = if movt { 0xF2C0 } else { 0xF240 } | ((imm >> 12) & 0xF) | (((imm >> 11) & 1) << 10);
        let hw2: u16 = (((imm >> 8) & 7) << 12) | (imm & 0xFF);
        let mut b = [0u8; 4];
        b[..2].copy_from_slice(&hw1.to_le_bytes());
        b[2..].copy_from_slice(&hw2.to_le_bytes());
        b
    }

    #[test]
    fn movw_movt_pairs_are_rebased_across_the_halfword_carry() {
        // movw r0,#0xFFF0 ; movt r0,#0x0001 — the address 0x0001_FFF0 — then a
        // base whose low half carries into the high one.
        let mut image = vec![0u8; 12];
        image[2..6].copy_from_slice(&movw(0xFFF0, false));
        image[6..10].copy_from_slice(&movw(0x0001, true));
        let blob = build(Arch::ThumbV7EM, &image, 0, 4, &[("f", 3)], &[2 | RELOC_PAIR, 6]);
        let m = Module::parse(&blob, Arch::ThumbV7EM).unwrap();
        assert_eq!(m.relocs, vec![Reloc::MovwMovt { movw: 2, movt: 6 }]);
        let mut ram = vec![0u8; 12];
        m.load_into(&mut ram, 0x2000_0020).unwrap();
        let v = (thumb_imm16(&ram, 6) as u32) << 16 | thumb_imm16(&ram, 2) as u32;
        assert_eq!(v, 0x2002_0010);
        assert!(is_movw_movt(&ram, 2, false) && is_movw_movt(&ram, 6, true), "still the same instructions");
        // A pair that does not point at movw/movt is refused.
        let wrong = build(Arch::ThumbV7EM, &image, 0, 4, &[], &[6 | RELOC_PAIR, 2]);
        assert!(matches!(Module::parse(&wrong, Arch::ThumbV7EM), Err(NpcError::BadRelocation(_))));
    }

    #[test]
    fn rejects_bad_modules() {
        let blob = sample();
        assert!(matches!(Module::parse(&blob, Arch::ThumbV7EM), Ok(_)), "M0+ code runs on an M7");
        let v7 = build(Arch::ThumbV7EM, &[0; 8], 0, 4, &[], &[]);
        assert!(matches!(Module::parse(&v7, Arch::ThumbV6M), Err(NpcError::WrongArch { .. })));
        assert_eq!(Module::parse(&blob[..blob.len() - 1], Arch::ThumbV6M).unwrap_err(), NpcError::Truncated);
        let mut extra = blob.clone();
        extra.push(0);
        assert_eq!(Module::parse(&extra, Arch::ThumbV6M).unwrap_err(), NpcError::NotAModule);
        let bad_reloc = build(Arch::ThumbV6M, &[0; 8], 0, 4, &[], &[6]);
        assert_eq!(Module::parse(&bad_reloc, Arch::ThumbV6M).unwrap_err(), NpcError::BadRelocation(6));
        let past_end = build(Arch::ThumbV6M, &[0; 8], 0, 4, &[], &[8]);
        assert_eq!(Module::parse(&past_end, Arch::ThumbV6M).unwrap_err(), NpcError::BadRelocation(8));
        let m = Module::parse(&blob, Arch::ThumbV6M).unwrap();
        assert_eq!(m.load_into(&mut [0u8; 8], 0x2000_0000), Err(NpcError::BadDestination));
        assert_eq!(m.load_into(&mut [0u8; 24], 0x2000_0002), Err(NpcError::BadDestination));
    }
}
