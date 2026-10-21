//! OTA A/B for the RP2040: the flash layout, the boot-state log and the
//! update image, shared by the bootloader (`firmware-rp2040/boot`) and the
//! firmware so both read the same state the same way.
//!
//! ## Why there is a bootloader at all
//!
//! The RP2040 executes in place from external flash and has no notion of
//! slots: the ROM runs whatever sits after the 256-byte second stage. So an
//! A/B scheme needs a small stage of its own at the start of flash that reads
//! which slot to run, points `VTOR` at it and jumps. Each slot holds an image
//! linked for that slot's address — the firmware is built twice — which is why
//! an update image carries both builds and the device keeps the one for the
//! slot it is not running from.
//!
//! ## The state log
//!
//! Choosing a slot must survive power loss at any instant, and NOR flash only
//! clears bits. So state is never rewritten in place: it is an append-only log
//! of 16-byte records in one of two sectors, replayed on every boot. When a
//! sector fills, its summary is written to the other sector *records first,
//! header last* — a sector without a valid header is ignored, so a copy
//! interrupted half-way leaves the old sector in charge — and only then is
//! the old one erased.
//!
//! A new image is a *trial* until the firmware confirms it (`rustnet ota
//! confirm`). The bootloader records an attempt before each trial boot, so an
//! image that hangs or faults before it can do anything still uses one up;
//! after [`MAX_ATTEMPTS`] it boots the confirmed slot again, which notices the
//! abandoned trial and logs the rollback.

use rustnet_hal::extmem::ExtMemory;
use rustnet_hal::HalResult;

/// Flash is mapped here for execute-in-place.
pub const XIP_BASE: u32 = 0x1000_0000;

/// The bootloader: second stage at 0, then the slot picker, up to 16 KB.
pub const BOOT_LEN: u32 = 0x4000;
/// Slot A and slot B, as offsets into the device. 496 KB each — the image is
/// about 340 KB.
pub const SLOT_A: u32 = 0x0000_4000;
pub const SLOT_B: u32 = 0x0008_0000;
pub const SLOT_LEN: u32 = 0x0007_C000;
/// Two 4 KB sectors of boot state, just below the 1 MB storage window.
pub const STATE_OFFSET: u32 = 0x000F_E000;
pub const STATE_LEN: u32 = 2 * SECTOR;
const SECTOR: u32 = 4096;

/// Boots a trial image gets before the confirmed one is booted instead.
pub const MAX_ATTEMPTS: u32 = 3;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Slot {
    A,
    B,
}

impl Slot {
    pub fn other(self) -> Slot {
        match self {
            Slot::A => Slot::B,
            Slot::B => Slot::A,
        }
    }

    /// Offset of the slot from the start of flash.
    pub fn offset(self) -> u32 {
        match self {
            Slot::A => SLOT_A,
            Slot::B => SLOT_B,
        }
    }

    pub fn name(self) -> &'static str {
        match self {
            Slot::A => "A",
            Slot::B => "B",
        }
    }

    /// The slot an image linked or running at `addr` belongs to.
    pub fn containing(addr: u32) -> Option<Slot> {
        let off = addr.checked_sub(XIP_BASE)?;
        [Slot::A, Slot::B].into_iter().find(|s| (s.offset()..s.offset() + SLOT_LEN).contains(&off))
    }

    fn code(self) -> u32 {
        match self {
            Slot::A => 0,
            Slot::B => 1,
        }
    }

    fn from_code(c: u32) -> Option<Slot> {
        match c {
            0 => Some(Slot::A),
            1 => Some(Slot::B),
            _ => None,
        }
    }
}

/// Is `words` (the first two words of a slot) a plausible vector table for an
/// image in `slot`: a stack pointer in SRAM and a Thumb reset vector inside
/// the slot? A slot that was never written reads as all ones and fails this.
pub fn vector_table_ok(slot: Slot, initial_sp: u32, reset: u32) -> bool {
    let sp_ok = (0x2000_0000..=0x2004_2000).contains(&initial_sp) && initial_sp % 4 == 0;
    let reset_ok = reset & 1 == 1 && Slot::containing(reset & !1) == Some(slot);
    sp_ok && reset_ok
}

// ---- records ----------------------------------------------------------

const RECORD: usize = 16;
const RECORD_MAGIC: u32 = 0x544F_4E52; // "RNOT"
const SECTOR_MAGIC: u32 = 0x534F_4E52; // "RNOS"

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Record {
    /// `slot` holds the known-good image.
    Confirmed(Slot),
    /// `slot` holds a new, verified image to try.
    Trial(Slot),
    /// The bootloader is about to boot the trial in `slot`.
    Attempt(Slot),
    /// The trial in `slot` was given up (rolled back).
    Abandon(Slot),
}

impl Record {
    fn encode(self) -> [u8; RECORD] {
        let (kind, slot) = match self {
            Record::Confirmed(s) => (1u32, s),
            Record::Trial(s) => (2, s),
            Record::Attempt(s) => (3, s),
            Record::Abandon(s) => (4, s),
        };
        let mut out = [0u8; RECORD];
        out[0..4].copy_from_slice(&RECORD_MAGIC.to_le_bytes());
        out[4..8].copy_from_slice(&kind.to_le_bytes());
        out[8..12].copy_from_slice(&slot.code().to_le_bytes());
        out[12..16].copy_from_slice(&(!(kind ^ slot.code().rotate_left(16))).to_le_bytes());
        out
    }

    /// `None` for anything that is not a whole, intact record — including a
    /// write that power loss cut short.
    fn decode(b: &[u8]) -> Option<Record> {
        let w = |i: usize| u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]]);
        if w(0) != RECORD_MAGIC || w(12) != !(w(4) ^ w(8).rotate_left(16)) {
            return None;
        }
        let slot = Slot::from_code(w(8))?;
        Some(match w(4) {
            1 => Record::Confirmed(slot),
            2 => Record::Trial(slot),
            3 => Record::Attempt(slot),
            4 => Record::Abandon(slot),
            _ => return None,
        })
    }
}

/// What the log says, replayed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BootState {
    pub confirmed: Slot,
    pub trial: Option<Slot>,
    pub attempts: u32,
}

impl Default for BootState {
    /// A board that was just flashed over USB: slot A, nothing pending.
    fn default() -> Self {
        Self { confirmed: Slot::A, trial: None, attempts: 0 }
    }
}

impl BootState {
    pub fn apply(&mut self, r: Record) {
        match r {
            Record::Confirmed(s) => *self = BootState { confirmed: s, trial: None, attempts: 0 },
            Record::Trial(s) => {
                self.trial = Some(s);
                self.attempts = 0;
            }
            Record::Attempt(s) if self.trial == Some(s) => self.attempts += 1,
            Record::Abandon(s) if self.trial == Some(s) => {
                self.trial = None;
                self.attempts = 0;
            }
            _ => {}
        }
    }

    /// The slot to boot, before any attempt is recorded for it.
    pub fn boot_slot(&self) -> Slot {
        match self.trial {
            Some(t) if self.attempts < MAX_ATTEMPTS => t,
            _ => self.confirmed,
        }
    }

    /// The log that rebuilds this state from nothing.
    fn summary(&self, out: &mut [Option<Record>; 2 + MAX_ATTEMPTS as usize]) {
        *out = [None; 2 + MAX_ATTEMPTS as usize];
        out[0] = Some(Record::Confirmed(self.confirmed));
        if let Some(t) = self.trial {
            out[1] = Some(Record::Trial(t));
            for i in 0..self.attempts.min(MAX_ATTEMPTS) as usize {
                out[2 + i] = Some(Record::Attempt(t));
            }
        }
    }
}

/// The state log over the two state sectors. `mem` is a window onto exactly
/// [`STATE_LEN`] bytes of flash at [`STATE_OFFSET`].
pub struct StateLog {
    pub state: BootState,
    /// The sector in charge, its sequence number and the next free record
    /// position in it; `None` when neither sector holds a valid header.
    active: Option<(u32, u32, u32)>,
}

impl StateLog {
    pub fn load(mem: &mut dyn ExtMemory) -> HalResult<StateLog> {
        let mut best: Option<(u32, u32)> = None;
        for sector in 0..2u32 {
            let mut h = [0u8; RECORD];
            mem.read(sector * SECTOR, &mut h)?;
            let w = |i: usize| u32::from_le_bytes([h[i], h[i + 1], h[i + 2], h[i + 3]]);
            if w(0) == SECTOR_MAGIC && w(12) == !w(4) && best.is_none_or(|(_, seq)| w(4) > seq) {
                best = Some((sector, w(4)));
            }
        }
        let mut log = StateLog { state: BootState::default(), active: None };
        if let Some((sector, seq)) = best {
            let mut cursor = RECORD as u32;
            let mut r = [0u8; RECORD];
            while cursor < SECTOR {
                mem.read(sector * SECTOR + cursor, &mut r)?;
                if r.iter().all(|&b| b == 0xFF) {
                    break;
                }
                // A torn record is skipped, not treated as the end: what
                // follows it was written later and is intact.
                if let Some(rec) = Record::decode(&r) {
                    log.state.apply(rec);
                }
                cursor += RECORD as u32;
            }
            log.active = Some((sector, seq, cursor));
        }
        Ok(log)
    }

    /// Append `r` and apply it, compacting into the other sector when this
    /// one is full.
    pub fn append(&mut self, mem: &mut dyn ExtMemory, r: Record) -> HalResult<()> {
        match self.active {
            Some((sector, seq, cursor)) if cursor + RECORD as u32 <= SECTOR => {
                mem.write(sector * SECTOR + cursor, &r.encode())?;
                self.active = Some((sector, seq, cursor + RECORD as u32));
                self.state.apply(r);
                Ok(())
            }
            _ => {
                self.state.apply(r);
                self.rewrite(mem)
            }
        }
    }

    /// Write the current state as a fresh sector, then retire the old one.
    fn rewrite(&mut self, mem: &mut dyn ExtMemory) -> HalResult<()> {
        let (target, seq) = match self.active {
            Some((sector, seq, _)) => (1 - sector, seq.wrapping_add(1)),
            None => (0, 1),
        };
        mem.erase(target * SECTOR, SECTOR)?;
        let mut summary = [None; 2 + MAX_ATTEMPTS as usize];
        self.state.summary(&mut summary);
        let mut cursor = RECORD as u32;
        for rec in summary.into_iter().flatten() {
            mem.write(target * SECTOR + cursor, &rec.encode())?;
            cursor += RECORD as u32;
        }
        // The header last: until it lands, the old sector is still in charge.
        let mut h = [0xFFu8; RECORD];
        h[0..4].copy_from_slice(&SECTOR_MAGIC.to_le_bytes());
        h[4..8].copy_from_slice(&seq.to_le_bytes());
        h[12..16].copy_from_slice(&(!seq).to_le_bytes());
        mem.write(target * SECTOR, &h)?;
        if let Some((old, _, _)) = self.active {
            mem.erase(old * SECTOR, SECTOR)?;
        }
        self.active = Some((target, seq, cursor));
        Ok(())
    }
}

// ---- the update image -------------------------------------------------

/// `RNP2`: an update payload carrying the firmware linked for both slots.
pub const PAIR_MAGIC: [u8; 4] = *b"RNP2";
const PAIR_HEADER: usize = 16;

/// Splits a streamed pair image, keeping the half linked for `target`.
///
/// Layout: magic, `len_a: u32`, `len_b: u32`, reserved `u32`, then the slot-A
/// image and the slot-B image, each starting with its vector table.
pub struct PairParser {
    target: Slot,
    header: [u8; PAIR_HEADER],
    seen: usize,
    len_a: usize,
    len_b: usize,
}

impl PairParser {
    pub fn new(target: Slot) -> Self {
        Self { target, header: [0; PAIR_HEADER], seen: 0, len_a: 0, len_b: 0 }
    }

    /// Feed the next payload bytes; `sink(offset, bytes)` receives the parts
    /// that belong at `offset` in the target slot.
    pub fn feed<E>(
        &mut self,
        mut chunk: &[u8],
        mut sink: impl FnMut(u32, &[u8]) -> Result<(), E>,
        bad: impl Fn(&'static str) -> E,
    ) -> Result<(), E> {
        if self.seen < PAIR_HEADER {
            let take = chunk.len().min(PAIR_HEADER - self.seen);
            self.header[self.seen..self.seen + take].copy_from_slice(&chunk[..take]);
            self.seen += take;
            chunk = &chunk[take..];
            if self.seen < PAIR_HEADER {
                return Ok(());
            }
            let h = &self.header;
            if h[0..4] != PAIR_MAGIC {
                return Err(bad("not an RP2040 update image (expected RNP2 — build it with tools/make_ota.py)"));
            }
            self.len_a = u32::from_le_bytes([h[4], h[5], h[6], h[7]]) as usize;
            self.len_b = u32::from_le_bytes([h[8], h[9], h[10], h[11]]) as usize;
            if self.len_a > SLOT_LEN as usize || self.len_b > SLOT_LEN as usize {
                return Err(bad("an image in the update is larger than a slot"));
            }
        }
        // Position of `chunk[0]` within the two images, after the header.
        let at = self.seen - PAIR_HEADER;
        let (start, len) = match self.target {
            Slot::A => (0, self.len_a),
            Slot::B => (self.len_a, self.len_b),
        };
        if at + chunk.len() > self.len_a + self.len_b {
            return Err(bad("update image is longer than its header says"));
        }
        let lo = at.max(start);
        let hi = (at + chunk.len()).min(start + len);
        if lo < hi {
            sink((lo - start) as u32, &chunk[lo - at..hi - at])?;
        }
        self.seen += chunk.len();
        Ok(())
    }

    /// The length of the target's image once every byte has arrived.
    pub fn finish(&self) -> Result<u32, &'static str> {
        if self.seen < PAIR_HEADER || self.seen - PAIR_HEADER != self.len_a + self.len_b {
            return Err("update image ended early");
        }
        let len = match self.target {
            Slot::A => self.len_a,
            Slot::B => self.len_b,
        };
        if len < 8 {
            return Err("the update carries no image for this slot");
        }
        Ok(len as u32)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use alloc::vec;
    use alloc::vec::Vec;
    use rustnet_hal::extmem::ExtMemKind;
    use rustnet_hal::HalError;

    /// NOR flash in RAM: writes only clear bits, erases set whole sectors.
    struct Nor(Vec<u8>, usize);

    impl ExtMemory for Nor {
        fn kind(&self) -> ExtMemKind {
            ExtMemKind::QspiFlash
        }
        fn size(&self) -> u32 {
            self.0.len() as u32
        }
        fn read(&mut self, a: u32, buf: &mut [u8]) -> HalResult<()> {
            buf.copy_from_slice(&self.0[a as usize..a as usize + buf.len()]);
            Ok(())
        }
        fn write(&mut self, a: u32, d: &[u8]) -> HalResult<()> {
            if self.1 == 0 {
                return Err(HalError::Bus("power cut"));
            }
            self.1 -= 1;
            for (i, b) in d.iter().enumerate() {
                self.0[a as usize + i] &= b;
            }
            Ok(())
        }
        fn erase(&mut self, a: u32, len: u32) -> HalResult<()> {
            assert!(a % SECTOR == 0 && len % SECTOR == 0);
            self.0[a as usize..(a + len) as usize].fill(0xFF);
            Ok(())
        }
        fn sector_size(&self) -> u32 {
            SECTOR
        }
    }

    fn blank() -> Nor {
        Nor(vec![0xFF; STATE_LEN as usize], usize::MAX)
    }

    #[test]
    fn layout_fits_below_the_storage_window() {
        assert_eq!(SLOT_A, BOOT_LEN);
        assert!(SLOT_A + SLOT_LEN <= SLOT_B);
        assert!(SLOT_B + SLOT_LEN <= STATE_OFFSET);
        assert_eq!(STATE_OFFSET + STATE_LEN, 1024 * 1024);
        // VTOR needs 256-byte alignment for 48 vectors.
        assert_eq!(SLOT_A % 256, 0);
        assert_eq!(SLOT_B % 256, 0);
    }

    #[test]
    fn blank_flash_boots_slot_a() {
        let log = StateLog::load(&mut blank()).unwrap();
        assert_eq!(log.state, BootState::default());
        assert_eq!(log.state.boot_slot(), Slot::A);
    }

    #[test]
    fn trial_gets_three_boots_then_rolls_back() {
        let mut nor = blank();
        let mut log = StateLog::load(&mut nor).unwrap();
        log.append(&mut nor, Record::Trial(Slot::B)).unwrap();
        for _ in 0..MAX_ATTEMPTS {
            let mut log = StateLog::load(&mut nor).unwrap();
            assert_eq!(log.state.boot_slot(), Slot::B);
            log.append(&mut nor, Record::Attempt(Slot::B)).unwrap();
        }
        let log = StateLog::load(&mut nor).unwrap();
        assert_eq!(log.state.boot_slot(), Slot::A);
        assert_eq!(log.state.trial, Some(Slot::B));
    }

    #[test]
    fn confirming_makes_the_trial_the_default() {
        let mut nor = blank();
        let mut log = StateLog::load(&mut nor).unwrap();
        log.append(&mut nor, Record::Trial(Slot::B)).unwrap();
        log.append(&mut nor, Record::Attempt(Slot::B)).unwrap();
        log.append(&mut nor, Record::Confirmed(Slot::B)).unwrap();
        let log = StateLog::load(&mut nor).unwrap();
        assert_eq!(log.state, BootState { confirmed: Slot::B, trial: None, attempts: 0 });
    }

    #[test]
    fn compaction_preserves_state_across_many_updates() {
        let mut nor = blank();
        let mut log = StateLog::load(&mut nor).unwrap();
        let mut slot = Slot::A;
        for _ in 0..400 {
            slot = slot.other();
            log.append(&mut nor, Record::Trial(slot)).unwrap();
            log.append(&mut nor, Record::Attempt(slot)).unwrap();
            log.append(&mut nor, Record::Confirmed(slot)).unwrap();
        }
        log.append(&mut nor, Record::Trial(slot.other())).unwrap();
        log.append(&mut nor, Record::Attempt(slot.other())).unwrap();
        let reloaded = StateLog::load(&mut nor).unwrap();
        assert_eq!(reloaded.state, log.state);
        assert_eq!(reloaded.state, BootState { confirmed: slot, trial: Some(slot.other()), attempts: 1 });
    }

    #[test]
    fn power_cut_during_compaction_keeps_the_old_sector() {
        // Fill sector 0 to the brim with a confirmed B.
        let mut nor = blank();
        let mut log = StateLog::load(&mut nor).unwrap();
        log.append(&mut nor, Record::Confirmed(Slot::B)).unwrap();
        let per_sector = (SECTOR as usize / RECORD) - 1;
        for _ in 1..per_sector {
            log.append(&mut nor, Record::Abandon(Slot::A)).unwrap();
        }
        // The next append compacts; cut power after the summary record but
        // before the header that would put the new sector in charge.
        nor.1 = 1;
        assert!(log.append(&mut nor, Record::Trial(Slot::A)).is_err());
        let after = StateLog::load(&mut nor).unwrap();
        assert_eq!(after.state, BootState { confirmed: Slot::B, trial: None, attempts: 0 });
    }

    #[test]
    fn torn_record_is_skipped() {
        let mut nor = blank();
        let mut log = StateLog::load(&mut nor).unwrap();
        log.append(&mut nor, Record::Trial(Slot::B)).unwrap();
        // The first append wrote a fresh sector: header, Confirmed(A), then
        // the trial. Corrupt the trial: clear a bit in its check word.
        nor.0[RECORD * 3 - 1] &= 0x7F;
        log.append(&mut nor, Record::Attempt(Slot::B)).unwrap();
        let after = StateLog::load(&mut nor).unwrap();
        // Without the trial the attempt means nothing.
        assert_eq!(after.state, BootState::default());
    }

    #[test]
    fn vector_table_check() {
        let reset = XIP_BASE + SLOT_B + 0x1c1;
        assert!(vector_table_ok(Slot::B, 0x2004_2000, reset));
        assert!(!vector_table_ok(Slot::A, 0x2004_2000, reset));
        assert!(!vector_table_ok(Slot::B, 0xFFFF_FFFF, 0xFFFF_FFFF));
        assert!(!vector_table_ok(Slot::B, 0x2004_2000, reset & !1));
    }

    fn pair(a: &[u8], b: &[u8]) -> Vec<u8> {
        let mut v = PAIR_MAGIC.to_vec();
        v.extend((a.len() as u32).to_le_bytes());
        v.extend((b.len() as u32).to_le_bytes());
        v.extend([0; 4]);
        v.extend(a);
        v.extend(b);
        v
    }

    fn split(image: &[u8], target: Slot, chunk: usize) -> Result<Vec<u8>, &'static str> {
        let mut p = PairParser::new(target);
        let mut out = Vec::new();
        for c in image.chunks(chunk) {
            p.feed(
                c,
                |off, bytes| {
                    assert_eq!(off as usize, out.len());
                    out.extend_from_slice(bytes);
                    Ok(())
                },
                |e| e,
            )?;
        }
        assert_eq!(p.finish()? as usize, out.len());
        Ok(out)
    }

    #[test]
    fn pair_parser_keeps_the_target_half_at_any_chunking() {
        let a: Vec<u8> = (0..1000u32).map(|i| i as u8).collect();
        let b: Vec<u8> = (0..777u32).map(|i| (i * 7) as u8).collect();
        let img = pair(&a, &b);
        for chunk in [1, 3, 16, 17, 4096] {
            assert_eq!(split(&img, Slot::A, chunk).unwrap(), a);
            assert_eq!(split(&img, Slot::B, chunk).unwrap(), b);
        }
    }

    #[test]
    fn pair_parser_rejects_bad_images() {
        assert!(split(b"ELF\x7f............", Slot::A, 64).is_err());
        let mut img = pair(&[1; 64], &[2; 64]);
        img.truncate(img.len() - 1);
        assert!(split(&img, Slot::B, 64).is_err());
        let mut img = pair(&[1; 64], &[2; 64]);
        img.push(0);
        assert!(split(&img, Slot::B, 64).is_err());
        assert!(split(&pair(&[1; 64], &[]), Slot::B, 64).is_err());
    }
}
