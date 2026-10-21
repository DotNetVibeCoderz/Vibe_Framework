//! OTA A/B on the Pico: the RNDP side of `rustnet ota push / confirm /
//! rollback`.
//!
//! An update is a signed RNSB container of kind `Firmware` whose payload is an
//! `RNP2` pair — this firmware linked for slot A and for slot B
//! (`tools/make_ota.py`). It streams in over RNDP and is never held whole: the
//! signature is checked over the stream ([`StreamVerifier`]) while the half
//! linked for the slot this image is *not* running from goes straight to
//! flash, erased a sector ahead of the data. Only once every byte has arrived
//! and the signature holds is the slot recorded as a trial; the slot picker
//! (`boot/`) then gives it [`MAX_ATTEMPTS`] boots to be confirmed before it
//! goes back to the image that is running now. The layout and the state log
//! are in `rustnet_hal_rp2040::ota`.

use alloc::format;
use alloc::string::String;
use alloc::vec::Vec;

use rustnet_hal::extmem::ExtMemory;
use rustnet_hal_rp2040::ota::{
    vector_table_ok, PairParser, Record, Slot, StateLog, MAX_ATTEMPTS, SLOT_LEN, STATE_LEN, STATE_OFFSET,
};
use rustnet_secureboot::{ChipFamily, ImageKind, StreamVerifier};

use crate::storage::QspiFlash;

const SECTOR: u32 = 4096;

/// The slot this image is running from: the slot picker points `VTOR` at it.
pub fn running_slot() -> Slot {
    // SAFETY: a read of a core register.
    let vtor = unsafe { (*cortex_m::peripheral::SCB::PTR).vtor.read() };
    Slot::containing(vtor).unwrap_or(Slot::A)
}

fn state_flash() -> QspiFlash {
    QspiFlash::new(STATE_OFFSET, STATE_LEN)
}

fn load() -> Result<StateLog, String> {
    StateLog::load(&mut state_flash()).map_err(|e| format!("boot state: {e}"))
}

fn append(log: &mut StateLog, r: Record) -> Result<(), String> {
    log.append(&mut state_flash(), r).map_err(|e| format!("boot state: {e}"))
}

fn slot_ok(slot: Slot) -> bool {
    let mut head = [0u8; 8];
    if QspiFlash::new(slot.offset(), SLOT_LEN).read(0, &mut head).is_err() {
        return false;
    }
    let w = |i: usize| u32::from_le_bytes([head[i], head[i + 1], head[i + 2], head[i + 3]]);
    vector_table_ok(slot, w(0), w(4))
}

/// An update being received.
pub struct Session {
    verifier: StreamVerifier,
    key: Vec<u8>,
    target: Slot,
    parser: PairParser,
    flash: QspiFlash,
    /// Everything below this offset in the target slot has been erased.
    erased_to: u32,
}

impl Session {
    pub fn begin(key: Vec<u8>) -> Session {
        let target = running_slot().other();
        Session {
            verifier: StreamVerifier::new(ChipFamily::Rp2040),
            key,
            target,
            parser: PairParser::new(target),
            flash: QspiFlash::new(target.offset(), SLOT_LEN),
            erased_to: 0,
        }
    }

    pub fn write(&mut self, chunk: &[u8]) -> Result<(), String> {
        let payload = self.verifier.feed(chunk).map_err(|e| format!("ota image rejected: {e}"))?;
        if self.verifier.kind().is_some_and(|k| k != ImageKind::Firmware) {
            return Err(String::from("ota image rejected: not a firmware image"));
        }
        let flash = &mut self.flash;
        let erased_to = &mut self.erased_to;
        self.parser.feed(
            payload,
            |offset, bytes| {
                let end = offset + bytes.len() as u32;
                // A sector at a time, just ahead of the data: erasing the whole
                // slot up front masks interrupts for seconds and the USB link
                // with them.
                while *erased_to < end {
                    flash.erase(*erased_to, SECTOR).map_err(|e| format!("erasing slot: {e}"))?;
                    *erased_to += SECTOR;
                }
                flash.write(offset, bytes).map_err(|e| format!("writing slot: {e}"))?;
                // Read it back: a program into bits that were not erased
                // ANDs silently, and the damage would only show at boot.
                let mut check = [0u8; 256];
                for (i, part) in bytes.chunks(check.len()).enumerate() {
                    let back = &mut check[..part.len()];
                    flash.read(offset + (i * 256) as u32, back).map_err(|e| format!("reading slot: {e}"))?;
                    if back != part {
                        return Err(format!("slot {:#x} did not read back as written", offset + (i * 256) as u32));
                    }
                }
                Ok(())
            },
            |e| String::from(e),
        )
    }

    /// Check the signature and arm the slot as a trial. The caller reboots.
    pub fn end(self) -> Result<Slot, String> {
        self.parser.finish().map_err(String::from)?;
        let (kind, _) = self.verifier.finish(&self.key).map_err(|e| format!("ota image rejected: {e}"))?;
        if kind != ImageKind::Firmware {
            return Err(String::from("ota image rejected: not a firmware image"));
        }
        if !slot_ok(self.target) {
            return Err(format!(
                "the image for slot {} has no valid vector table — was it linked for that slot?",
                self.target.name()
            ));
        }
        let mut log = load()?;
        append(&mut log, Record::Trial(self.target))?;
        Ok(self.target)
    }
}

/// Keep the running image: it stops being a trial.
pub fn confirm() -> Result<Slot, String> {
    let running = running_slot();
    let mut log = load()?;
    if log.state.trial == Some(running) {
        append(&mut log, Record::Confirmed(running))?;
    }
    Ok(log.state.confirmed)
}

/// Go back to the other image. Returns the slot to boot; the caller reboots.
pub fn rollback() -> Result<Slot, String> {
    let running = running_slot();
    let mut log = load()?;
    if log.state.trial == Some(running) {
        append(&mut log, Record::Abandon(running))?;
        return Ok(log.state.confirmed);
    }
    let other = running.other();
    if !slot_ok(other) {
        return Err(format!("slot {} holds no image to roll back to", other.name()));
    }
    append(&mut log, Record::Confirmed(other))?;
    Ok(other)
}

/// At start-up: say which slot is running, and settle a trial the slot picker
/// gave up on.
pub fn boot_report(out: &mut impl core::fmt::Write) {
    let running = running_slot();
    #[cfg(feature = "ota-diag")]
    {
        let mut w = [0u8; 16];
        for slot in [Slot::A, Slot::B] {
            let _ = QspiFlash::new(slot.offset(), SLOT_LEN).read(0, &mut w[..8]);
            let word = |i: usize| u32::from_le_bytes([w[i], w[i + 1], w[i + 2], w[i + 3]]);
            let _ = writeln!(
                out,
                "[ota-diag] slot {} sp={:#010x} reset={:#010x} ok={}",
                slot.name(),
                word(0),
                word(4),
                slot_ok(slot)
            );
        }
        for sector in 0..2u32 {
            let _ = state_flash().read(sector * SECTOR, &mut w);
            let _ = writeln!(out, "[ota-diag] state sector {sector} header {:02x?}", &w);
        }
    }
    let mut log = match load() {
        Ok(log) => log,
        Err(e) => {
            let _ = writeln!(out, "[ota] {e}");
            return;
        }
    };
    match log.state.trial {
        Some(t) if t == running => {
            let _ = writeln!(
                out,
                "[ota] slot {} is a trial update (boot {} of {MAX_ATTEMPTS}); `rustnet ota confirm` keeps it",
                running.name(),
                log.state.attempts
            );
        }
        Some(t) => {
            let _ = append(&mut log, Record::Abandon(t));
            let _ = writeln!(
                out,
                "[ota] the update in slot {} was not confirmed within {MAX_ATTEMPTS} boots; rolled back to slot {}",
                t.name(),
                running.name()
            );
        }
        None => {
            let _ = writeln!(out, "[ota] running slot {}", running.name());
        }
    }
}
