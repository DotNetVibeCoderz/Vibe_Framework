//! The QSPI flash above the image, as somewhere to keep things.
//!
//! The RP2040 has no internal flash of its own: it executes in place out of an
//! external QSPI part, 2 MB on a Pico. The image occupies the bottom of it and
//! everything above is free, so that is where applications, the provisioning
//! key and any data files go.
//!
//! ## Why this cannot be an ordinary driver
//!
//! **Programming flash means stopping execute-in-place**, and the program is
//! executing from that flash. Every write here therefore runs through the
//! boot ROM's own routines, which are in mask ROM rather than in the flash
//! being written, and interrupts are masked for the duration. Code that
//! erases a sector from a function living in flash erases the function it is
//! running.
//!
//! **The cache has to be flushed afterwards.** XIP caches what it read, and
//! after a write the cached copy is the old one — a read-back that skips this
//! returns what was there before and looks like a write that silently failed.
//!
//! ## The window
//!
//! The image is a little under 256 KB and grows; the window starts at 1 MB to
//! leave room for it to double without moving, which matters because moving it
//! invalidates everything already stored there. A Pico's 2 MB leaves 1 MB for
//! storage, which is four times what the K210 port gives its filesystem.

use rustnet_hal::extmem::{ExtMemKind, ExtMemory};
use rustnet_hal::{HalError, HalResult};

use rp2040_hal::rom_data;

/// Flash is mapped here for reading; the ROM routines take offsets from the
/// start of the device instead.
const XIP_BASE: usize = 0x1000_0000;

/// Where the storage window begins, as an offset into the device.
pub const STORAGE_OFFSET: u32 = 1024 * 1024;
/// How much of it there is. A Pico carries 2 MB.
pub const STORAGE_LEN: u32 = 1024 * 1024;

/// The QSPI part's erase and program granularity. Both are properties of NOR
/// flash rather than of this chip: a sector is the smallest thing that can be
/// erased, a page the largest that can be programmed at once.
const SECTOR_SIZE: u32 = 4096;
const PAGE_SIZE: usize = 256;
/// The 20h sector-erase opcode every part in this family answers.
const SECTOR_ERASE_CMD: u8 = 0x20;

pub struct QspiFlash {
    base: u32,
    len: u32,
}

impl QspiFlash {
    pub const fn new(base: u32, len: u32) -> Self {
        Self { base, len }
    }

    fn check(&self, offset: u32, len: usize) -> HalResult<u32> {
        let end = offset
            .checked_add(len as u32)
            .ok_or(HalError::InvalidArgument("storage offset overflows"))?;
        if end > self.len {
            return Err(HalError::InvalidArgument("past the end of the storage window"));
        }
        Ok(self.base + offset)
    }
}

/// Run `body` with execute-in-place disabled and interrupts masked.
///
/// # Safety
/// `body` must touch nothing in flash — no calls into flash-resident code, no
/// string literals, no statics. It is running with the memory it was fetched
/// from disconnected.
#[inline(never)]
#[link_section = ".data.ram_func"]
unsafe fn with_xip_disabled(body: impl FnOnce()) {
    cortex_m::interrupt::free(|_| {
        rom_data::connect_internal_flash();
        rom_data::flash_exit_xip();
        body();
        // Without this the cache still holds what was there before, and a
        // read-back returns the old contents — a write that appears to have
        // silently done nothing.
        rom_data::flash_flush_cache();
        rom_data::flash_enter_cmd_xip();
    });
}

impl ExtMemory for QspiFlash {
    fn kind(&self) -> ExtMemKind {
        ExtMemKind::QspiFlash
    }

    fn size(&self) -> u32 {
        self.len
    }

    fn sector_size(&self) -> u32 {
        SECTOR_SIZE
    }

    fn read(&mut self, offset: u32, buf: &mut [u8]) -> HalResult<()> {
        let addr = self.check(offset, buf.len())?;
        // Reads go through the memory map rather than the ROM: XIP is exactly
        // the hardware that makes flash readable as memory.
        let src = (XIP_BASE + addr as usize) as *const u8;
        // SAFETY: `check` kept the range inside the window, which is inside
        // the device's mapped space.
        unsafe { core::ptr::copy_nonoverlapping(src, buf.as_mut_ptr(), buf.len()) };
        Ok(())
    }

    /// Program `data` at `offset`, at any alignment.
    ///
    /// NOR flash can be programmed a byte at a time, but the ROM routine
    /// cannot: `flash_range_program` insists on a 256-byte address and a
    /// whole number of pages. So a write that starts or ends mid-page is
    /// widened to the pages containing it, and the bytes outside the request
    /// are carried over from what is already there.
    ///
    /// Rewriting a byte with the value it already holds is harmless —
    /// programming only clears bits, and clearing a bit that is already clear
    /// does nothing. That is also why this cannot *move* data: it can only
    /// clear bits, so the bytes outside the request must be reprogrammed
    /// identically, never changed.
    ///
    /// The caller still owes an erase. This refuses to demand page alignment
    /// but it cannot invent erased space: writing into bits that are already
    /// clear silently ANDs into them, and the damage surfaces later.
    fn write(&mut self, offset: u32, data: &[u8]) -> HalResult<()> {
        let addr = self.check(offset, data.len())?;
        let mut page = [0xFFu8; PAGE_SIZE];
        let mut written = 0;
        while written < data.len() {
            let target = addr + written as u32;
            let page_start = target & !(PAGE_SIZE as u32 - 1);
            let within = (target - page_start) as usize;
            let take = (data.len() - written).min(PAGE_SIZE - within);

            // Read the page back through the memory map, then overlay. The
            // untouched bytes get reprogrammed to the values they already
            // hold, which is a no-op in the silicon.
            let src = (XIP_BASE + page_start as usize) as *const u8;
            // SAFETY: `page_start` is inside the window `check` validated,
            // rounded down, so the whole page is mapped.
            unsafe { core::ptr::copy_nonoverlapping(src, page.as_mut_ptr(), PAGE_SIZE) };
            page[within..within + take].copy_from_slice(&data[written..written + take]);

            // SAFETY: the closure touches only its captured integers, the
            // stack buffer and the ROM routine, so nothing it needs lives in
            // the flash being programmed.
            unsafe {
                with_xip_disabled(|| {
                    rom_data::flash_range_program(page_start, page.as_ptr(), PAGE_SIZE);
                })
            };
            written += take;
        }
        Ok(())
    }

    fn erase(&mut self, offset: u32, len: u32) -> HalResult<()> {
        let addr = self.check(offset, len as usize)?;
        if addr % SECTOR_SIZE != 0 || len % SECTOR_SIZE != 0 {
            return Err(HalError::InvalidArgument(
                "flash erases must cover whole 4 KB sectors",
            ));
        }
        let count = len as usize;
        // SAFETY: as above — the closure captures integers only.
        unsafe {
            with_xip_disabled(|| {
                rom_data::flash_range_erase(addr, count, SECTOR_SIZE, SECTOR_ERASE_CMD);
            })
        };
        Ok(())
    }
}

/// Names the firmware keeps for itself.
///
/// They live under `/.sys/` so a `data push` cannot collide with them by
/// accident, and so `apps list` and an application's own files stay visibly
/// separate from the things that make the device work.
pub mod sys {
    /// The RNX of the flashed application, already signature-checked.
    pub const APP: &str = "/.sys/app.rnx";
    /// What `rustnet flash --name` called it.
    pub const APP_NAME: &str = "/.sys/app.name";
    /// The provisioning key, as DER. Write-once in spirit: see
    /// `docs/security.md` — a device whose key is replaced accepts anything
    /// its new owner signs.
    pub const PUB_KEY: &str = "/.sys/signing.pub";
    /// Present, with the app's name, when it should run on power-up.
    pub const AUTOSTART: &str = "/.sys/autostart";
}
