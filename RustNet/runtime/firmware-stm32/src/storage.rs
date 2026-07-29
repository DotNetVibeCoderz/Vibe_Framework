//! Persistence in a reserved sector of the MCU's own flash.
//!
//! Enough to survive a reset — a provisioned key, an uploaded application,
//! its name — and no more. This is not a filesystem: there are no paths, no
//! directories, and a handful of fixed record kinds.
//!
//! The layout is a log. Records are appended, and the newest of each kind
//! wins; updating means writing a new one rather than rewriting the old.
//! That suits NOR flash, where bits only go 1 → 0 and the only way back is
//! erasing a whole 128 KB sector. Compaction — read the live set, erase,
//! write it back — happens only when the sector fills, so a development
//! cycle of repeated `rustnet flash` costs one erase per several uploads
//! rather than one per upload.
//!
//! ```text
//! magic u32 | kind u32 | len u32 | data[len] | padding to a 4-byte boundary
//! ```
//!
//! Erased flash reads as `0xFFFFFFFF`, so a header whose magic does not
//! match is the end of the log.

use alloc::format;
use alloc::string::String;
use alloc::vec;
use alloc::vec::Vec;

use rustnet_hal::Board as _;

/// "RNS1" little-endian. Any other value ends the scan.
const MAGIC: u32 = 0x3153_4E52;
const HEADER: u32 = 12;

pub const KIND_PUB_KEY: u32 = 1;
pub const KIND_APP: u32 = 2;
pub const KIND_APP_NAME: u32 = 3;

/// Kinds a compaction preserves, newest value of each.
const KINDS: [u32; 3] = [KIND_PUB_KEY, KIND_APP, KIND_APP_NAME];

fn align4(n: u32) -> u32 {
    (n + 3) & !3
}

/// Where the next record would go, if the log is intact.
struct Scan {
    /// Offset just past the last valid record.
    end: u32,
    /// Newest (offset, len) per kind, in `KINDS` order.
    latest: [Option<(u32, u32)>; KINDS.len()],
}

fn scan(board: &mut dyn rustnet_hal::Board) -> Result<Scan, String> {
    let region = board.extmem(0).map_err(|e| format!("{e}"))?;
    let capacity = region.size();

    let mut found: [Option<(u32, u32)>; KINDS.len()] = [None; KINDS.len()];
    let mut at = 0u32;

    while at + HEADER <= capacity {
        let mut header = [0u8; HEADER as usize];
        region.read(at, &mut header).map_err(|e| format!("{e}"))?;

        let magic = u32::from_le_bytes([header[0], header[1], header[2], header[3]]);
        if magic != MAGIC {
            break;
        }
        let kind = u32::from_le_bytes([header[4], header[5], header[6], header[7]]);
        let len = u32::from_le_bytes([header[8], header[9], header[10], header[11]]);

        let next = at + HEADER + align4(len);
        if len > capacity || next > capacity {
            // A truncated tail — a reset mid-write — ends the log here rather
            // than being trusted.
            break;
        }
        if let Some(slot) = KINDS.iter().position(|k| *k == kind) {
            found[slot] = Some((at + HEADER, len));
        }
        at = next;
    }

    Ok(Scan { end: at, latest: found })
}

/// The newest value stored under `kind`, if any.
pub fn load(board: &mut dyn rustnet_hal::Board, kind: u32) -> Option<Vec<u8>> {
    let slot = KINDS.iter().position(|k| *k == kind)?;
    let found = scan(board).ok()?.latest[slot]?;
    let (offset, len) = found;

    let region = board.extmem(0).ok()?;
    let mut data = vec![0u8; len as usize];
    region.read(offset, &mut data).ok()?;
    Some(data)
}

/// Is the next append site actually erased?
///
/// Appending only works into erased flash. NOR programming clears bits but
/// cannot set them, and the F4 raises no error for writing over used
/// space — it simply ANDs, so the record lands unreadable and the failure
/// is silent. A region inherited from whatever ran on the board before us
/// looks exactly like this.
fn tail_is_blank(board: &mut dyn rustnet_hal::Board, at: u32) -> Result<bool, String> {
    let region = board.extmem(0).map_err(|e| format!("{e}"))?;
    if at + 4 > region.size() {
        return Ok(false);
    }
    let mut probe = [0u8; 4];
    region.read(at, &mut probe).map_err(|e| format!("{e}"))?;
    Ok(probe == [0xFF; 4])
}

/// Append a record, compacting first if it will not fit.
pub fn store(board: &mut dyn rustnet_hal::Board, kind: u32, data: &[u8]) -> Result<(), String> {
    let needed = HEADER + align4(data.len() as u32);

    let mut scanned = scan(board)?;
    let capacity = board.extmem(0).map_err(|e| format!("{e}"))?.size();

    if scanned.end + needed > capacity || !tail_is_blank(board, scanned.end)? {
        compact(board, kind)?;
        scanned = scan(board)?;
        if scanned.end + needed > capacity {
            return Err(String::from("storage full even after compaction"));
        }
    }

    write_record(board, scanned.end, kind, data)
}

fn write_record(
    board: &mut dyn rustnet_hal::Board,
    at: u32,
    kind: u32,
    data: &[u8],
) -> Result<(), String> {
    // One buffer so the whole record goes down as a single aligned write;
    // the padding must be written too, or the next scan reads flash that was
    // never programmed.
    let mut record = Vec::with_capacity((HEADER + align4(data.len() as u32)) as usize);
    record.extend_from_slice(&MAGIC.to_le_bytes());
    record.extend_from_slice(&kind.to_le_bytes());
    record.extend_from_slice(&(data.len() as u32).to_le_bytes());
    record.extend_from_slice(data);
    while record.len() % 4 != 0 {
        record.push(0xFF);
    }

    let region = board.extmem(0).map_err(|e| format!("{e}"))?;
    region.write(at, &record).map_err(|e| format!("{e}"))
}

/// Erase the sector and write back the newest of each kind, with `replacing`
/// dropped because the caller is about to supersede it.
fn compact(board: &mut dyn rustnet_hal::Board, replacing: u32) -> Result<(), String> {
    let mut live: Vec<(u32, Vec<u8>)> = Vec::new();
    for kind in KINDS {
        if kind == replacing {
            continue;
        }
        if let Some(value) = load(board, kind) {
            live.push((kind, value));
        }
    }

    {
        let region = board.extmem(0).map_err(|e| format!("{e}"))?;
        let size = region.size();
        // Roughly a second, during which the core and every interrupt handler
        // are stalled: the flash controller blocks all access to flash, and
        // that is where the code lives.
        region.erase(0, size).map_err(|e| format!("{e}"))?;
    }

    let mut at = 0u32;
    for (kind, value) in live {
        write_record(board, at, kind, &value)?;
        at += HEADER + align4(value.len() as u32);
    }
    Ok(())
}

/// Throw everything away — used by `rustnet apps erase`.
pub fn wipe(board: &mut dyn rustnet_hal::Board) -> Result<(), String> {
    let region = board.extmem(0).map_err(|e| format!("{e}"))?;
    let size = region.size();
    region.erase(0, size).map_err(|e| format!("{e}"))
}

/// How much of the region is in use, for reporting.
pub fn used(board: &mut dyn rustnet_hal::Board) -> u32 {
    scan(board).map(|s| s.end).unwrap_or(0)
}

/// The region this board sets aside, as the HAL wants it described.
pub fn region() -> rustnet_hal_stm32::InternalFlash {
    rustnet_hal_stm32::InternalFlash::new(
        crate::STORAGE_BASE,
        crate::STORAGE_LEN,
        crate::STORAGE_SECTOR,
        crate::STORAGE_LEN,
    )
}
