//! Persistence in a reserved sector of the MCU's own flash, as a filesystem.
//!
//! The sector is handed to `rustnet-flashfs`: named blobs in an append-only
//! log, newest wins — the same store the K210, RP2040 and Meadow F7 ports use.
//! What the firmware itself keeps there (the provisioned key, the uploaded
//! application and its name) are ordinary files under [`sys`], and the
//! application's own `RustNet.IO.FileSystem` calls get everything else.
//!
//! flashfs already carries the NOR discipline this needs: the whole span a
//! record will occupy is checked blank before it is written, every record is
//! read back after, and a compaction erases only the used span rounded to
//! sectors. Here that rounding always lands on the one 128 KB sector the
//! window is.
//!
//! ## The one thing this chip makes hard: RAM
//!
//! A compaction has to hold every live file in RAM between reading it and
//! erasing, and the whole store is a single sector, so there is nowhere else
//! to put them. On the F401RE that is a 64 KB heap the running application is
//! already using. So every write here is planned first ([`rustnet_flashfs::plan_write`])
//! and refused with a message naming the numbers if the heap cannot cover it,
//! and the application's own files are held to a budget ([`crate::board::FILES_BUDGET`])
//! small enough that replacing the application — the write that matters most —
//! can always compact. flashfs is the backstop: it claims compaction RAM
//! fallibly and before erasing, so running short there is an error, not a
//! halted board or an erased sector.
//!
//! ## The old record log
//!
//! Firmware before this kept three fixed records in a different log ("RNS1").
//! [`migrate_legacy`] carries those across once at boot, so a provisioned
//! board stays provisioned through the upgrade.

use alloc::format;
use alloc::string::String;
use alloc::vec::Vec;

use rustnet_flashfs as flashfs;
use rustnet_hal::extmem::ExtMemory;

/// Names the firmware keeps for itself — the same spellings as the RP2040 and
/// Meadow ports. Under `/.sys/` so an application's files cannot collide with
/// them, and an application is refused access to that directory outright: a
/// writable `signing.pub` would make signature checking decorative.
pub mod sys {
    pub const DIR: &str = "/.sys";
    /// The RNX of the flashed application, already signature-checked.
    pub const APP: &str = "/.sys/app.rnx";
    /// What `rustnet flash --name` called it.
    pub const APP_NAME: &str = "/.sys/app.name";
    /// The provisioning key, as DER.
    pub const PUB_KEY: &str = "/.sys/signing.pub";
}

/// Heap left untouched by any write, for the bookkeeping around it (the scan's
/// name list, format strings, the allocator's own fragmentation).
const HEAP_MARGIN: usize = 2 * 1024;

/// The region this board sets aside, as the HAL wants it described.
pub fn region() -> rustnet_hal_stm32::InternalFlash {
    rustnet_hal_stm32::InternalFlash::new(
        crate::STORAGE_BASE,
        crate::STORAGE_LEN,
        crate::STORAGE_SECTOR,
        crate::STORAGE_LEN,
    )
}

fn fs(board: &mut dyn rustnet_hal::Board) -> Result<&mut dyn ExtMemory, String> {
    board.extmem(0).map_err(|e| format!("no storage: {e}"))
}

/// The file at `path`, if there is one and it can be read.
pub fn load(board: &mut dyn rustnet_hal::Board, path: &str) -> Option<Vec<u8>> {
    flashfs::read(fs(board).ok()?, path).ok()
}

/// Write a whole file, after checking the heap can carry it.
pub fn store(board: &mut dyn rustnet_hal::Board, path: &str, data: &[u8]) -> Result<(), String> {
    let region = fs(board)?;
    let plan = flashfs::plan_write(region, path, data.len())?;
    check_ram(&plan)?;
    flashfs::write(region, path, data)
}

/// Bytes of the window in use, for `info`.
pub fn used(board: &mut dyn rustnet_hal::Board) -> u32 {
    fs(board).map(|r| flashfs::used(r)).unwrap_or(0)
}

fn check_ram(plan: &flashfs::WritePlan) -> Result<(), String> {
    let needed = plan.ram_needed() as usize + HEAP_MARGIN;
    let free = crate::heap_free();
    if needed <= free {
        return Ok(());
    }
    Err(if plan.compacts {
        format!(
            "not enough RAM to write: storage is full and must be compacted, which holds \
             the other {} live bytes in RAM while its single 128 KB flash sector is erased, \
             plus {} for the new record; {free} bytes of heap are free. Nothing was changed; \
             delete files to make room",
            plan.carried, plan.record
        )
    } else {
        format!(
            "not enough RAM to write {} bytes to storage; {free} bytes of heap are free",
            plan.record
        )
    })
}

// ---------------------------------------------------------------------------
// The application's files
// ---------------------------------------------------------------------------

/// Normalise an application's path and keep it out of the firmware's own.
pub fn app_path(path: &str) -> Result<String, String> {
    let path = flashfs::normalise(path)?;
    if path == sys::DIR || path.starts_with("/.sys/") {
        return Err(format!("{path}: reserved for the firmware"));
    }
    Ok(path)
}

/// Write (or with `append`, extend) one of the application's files.
///
/// Held to [`crate::board::FILES_BUDGET`]: what matters is not the sector's
/// 128 KB but what replacing the application has to carry through RAM, which
/// is every live file except the application itself.
pub fn app_write(
    board: &mut dyn rustnet_hal::Board,
    path: &str,
    data: &[u8],
    append: bool,
) -> Result<(), String> {
    let path = app_path(path)?;

    let joined;
    let data = if append {
        let mut existing = flashfs::read(fs(board)?, &path).unwrap_or_default();
        existing
            .try_reserve_exact(data.len())
            .map_err(|_| format!("out of memory appending {} bytes to {path}", data.len()))?;
        existing.extend_from_slice(data);
        joined = existing;
        &joined[..]
    } else {
        data
    };

    let region = fs(board)?;
    let plan = flashfs::plan_write(region, &path, data.len())?;
    let app = flashfs::plan_write(region, sys::APP, 0)?.replaced;
    let besides_app = (plan.carried + plan.record).saturating_sub(app);
    let budget = crate::board::FILES_BUDGET;
    if besides_app > budget {
        return Err(format!(
            "storage budget exceeded: files would total {besides_app} bytes and {} allows \
             {budget}. Replacing the application compacts the single 128 KB flash sector, \
             holding every other file in RAM meanwhile, so this cap is what keeps \
             `rustnet flash` working",
            crate::board::NAME
        ));
    }
    check_ram(&plan)?;
    flashfs::write(region, &path, data)
}

pub fn app_read(board: &mut dyn rustnet_hal::Board, path: &str) -> Result<Vec<u8>, String> {
    flashfs::read(fs(board)?, &app_path(path)?)
}

pub fn app_exists(board: &mut dyn rustnet_hal::Board, path: &str) -> Result<bool, String> {
    flashfs::exists(fs(board)?, &app_path(path)?)
}

/// Deleting writes a tombstone, so it can need a compaction like any write.
pub fn app_delete(board: &mut dyn rustnet_hal::Board, path: &str) -> Result<(), String> {
    let path = app_path(path)?;
    let region = fs(board)?;
    if !flashfs::exists(region, &path)? {
        return Ok(());
    }
    check_ram(&flashfs::plan_write(region, &path, 0)?)?;
    flashfs::delete(region, &path)
}

pub fn app_create_directory(board: &mut dyn rustnet_hal::Board, path: &str) -> Result<(), String> {
    let path = app_path(path)?;
    let region = fs(board)?;
    check_ram(&flashfs::plan_write(region, &path, 0)?)?;
    flashfs::create_directory(region, &path)
}

/// Immediate children of `path`, without the firmware's own directory.
pub fn app_list(board: &mut dyn rustnet_hal::Board, path: &str) -> Result<Vec<String>, String> {
    let path = app_path(path)?;
    let mut names = flashfs::list(fs(board)?, &path)?;
    if path == "/" {
        names.retain(|n| n != ".sys");
    }
    Ok(names)
}

// ---------------------------------------------------------------------------
// One-time migration from the old record log
// ---------------------------------------------------------------------------

/// "RNS1" little-endian: the magic of the fixed-record log earlier firmware
/// kept in this sector. Its records were `magic | kind | len | data pad4`.
const LEGACY_MAGIC: u32 = 0x3153_4E52;
const LEGACY_HEADER: u32 = 12;

/// What became of the old log, for the boot banner.
pub enum Migration {
    /// No old log: nothing to do, the usual case.
    None,
    /// Carried across: how many records.
    Done(usize),
    /// The old log could not be carried; the message says why. Failing before
    /// the erase leaves it in place (and the first write will erase it);
    /// failing after it loses whatever had not been written yet.
    Failed(String),
}

/// Carry the newest key, application and name out of an old-format log into
/// flashfs files.
///
/// Safe in the way that matters: everything is read into RAM and checked
/// before the sector is touched, so a reset before the erase leaves the old
/// log to be migrated on the next boot. A reset between the erase and the
/// last write loses what was not yet written — the key goes first, as the
/// thing costliest to lose. Runs at boot, before the application allocates
/// anything, which is when the heap is emptiest.
pub fn migrate_legacy(board: &mut dyn rustnet_hal::Board) -> Migration {
    match try_migrate(board) {
        Ok(None) => Migration::None,
        Ok(Some(n)) => Migration::Done(n),
        Err(e) => Migration::Failed(e),
    }
}

fn try_migrate(board: &mut dyn rustnet_hal::Board) -> Result<Option<usize>, String> {
    let region = fs(board)?;
    let mut word = [0u8; 4];
    region.read(0, &mut word).map_err(|e| format!("{e}"))?;
    if u32::from_le_bytes(word) != LEGACY_MAGIC {
        return Ok(None);
    }

    // Old kinds 1, 2, 3 = key, app, name; written in that order below.
    let targets = [(1u32, sys::PUB_KEY), (2, sys::APP), (3, sys::APP_NAME)];
    let mut newest: [Option<(u32, u32)>; 3] = [None; 3];
    let capacity = region.size();
    let mut at = 0u32;
    while at + LEGACY_HEADER <= capacity {
        let mut header = [0u8; LEGACY_HEADER as usize];
        region.read(at, &mut header).map_err(|e| format!("{e}"))?;
        let w = |i: usize| u32::from_le_bytes([header[i], header[i + 1], header[i + 2], header[i + 3]]);
        if w(0) != LEGACY_MAGIC {
            break;
        }
        let (kind, len) = (w(4), w(8));
        let next = at + LEGACY_HEADER + ((len + 3) & !3);
        if len > capacity || next > capacity {
            break;
        }
        if let Some(slot) = targets.iter().position(|(k, _)| *k == kind) {
            newest[slot] = Some((at + LEGACY_HEADER, len));
        }
        at = next;
    }

    let mut carried: Vec<(&str, Vec<u8>)> = Vec::new();
    for ((_, path), found) in targets.iter().zip(newest) {
        let Some((offset, len)) = found else { continue };
        let mut data = Vec::new();
        data.try_reserve_exact(len as usize)
            .map_err(|_| format!("old storage holds {len} bytes for {path}, more than free RAM"))?;
        data.resize(len as usize, 0);
        region.read(offset, &mut data).map_err(|e| format!("{e}"))?;
        carried.push((path, data));
    }

    // An erased sector is what flashfs starts from; it would erase this
    // itself on the first write, but only after the data was gone from view.
    let size = region.size();
    region.erase(0, size).map_err(|e| format!("{e}"))?;
    for (path, data) in &carried {
        flashfs::write(region, path, data)?;
    }
    Ok(Some(carried.len()))
}
