//! RustNet's RP2040 slot picker.
//!
//! The ROM runs the second stage, the second stage runs this, and this runs
//! the firmware: it replays the OTA boot-state log, records an attempt before
//! booting a trial image, and jumps into slot A or B. The rules live in
//! `rustnet_hal_rp2040::ota`, where they are host-tested; this file is only
//! the flash access and the jump.
//!
//! It does as little as it can. It never touches the clocks, so the firmware
//! starts from the same state the ROM would have left it, and if neither slot
//! holds a plausible image it drops into the ROM's USB bootloader rather than
//! hanging — a Pico in that state is one UF2 copy from working again.

#![no_std]
#![no_main]

use core::alloc::{GlobalAlloc, Layout};

use cortex_m_rt::entry;
use rp2040_hal::rom_data;
use rustnet_hal::extmem::{ExtMemKind, ExtMemory};
use rustnet_hal::{HalError, HalResult};
use rustnet_hal_rp2040::ota::{vector_table_ok, Record, Slot, StateLog, STATE_LEN, STATE_OFFSET, XIP_BASE};

/// The second stage, at flash offset 0 where the ROM looks for it.
#[link_section = ".boot2"]
#[used]
pub static BOOT2: [u8; 256] = rp2040_boot2::BOOT_LOADER_W25Q080;

/// The HAL crates link `alloc`, so an allocator must exist; nothing here
/// allocates, and one that always fails says so if that ever changes.
struct NoAlloc;

unsafe impl GlobalAlloc for NoAlloc {
    unsafe fn alloc(&self, _: Layout) -> *mut u8 {
        core::ptr::null_mut()
    }
    unsafe fn dealloc(&self, _: *mut u8, _: Layout) {}
}

#[global_allocator]
static ALLOCATOR: NoAlloc = NoAlloc;

const PAGE: usize = 256;
const SECTOR: u32 = 4096;

/// The two boot-state sectors, through the boot ROM's flash routines — the
/// same discipline as the firmware's `storage.rs`: anything that runs while
/// execute-in-place is off must live in RAM.
struct StateFlash;

#[inline(never)]
#[link_section = ".data.ram_func"]
unsafe fn with_xip_disabled(body: impl FnOnce()) {
    cortex_m::interrupt::free(|_| {
        rom_data::connect_internal_flash();
        rom_data::flash_exit_xip();
        body();
        rom_data::flash_flush_cache();
        rom_data::flash_enter_cmd_xip();
    });
}

impl StateFlash {
    fn check(offset: u32, len: usize) -> HalResult<u32> {
        if offset as usize + len > STATE_LEN as usize {
            return Err(HalError::InvalidArgument("past the boot-state sectors"));
        }
        Ok(STATE_OFFSET + offset)
    }
}

impl ExtMemory for StateFlash {
    fn kind(&self) -> ExtMemKind {
        ExtMemKind::QspiFlash
    }

    fn size(&self) -> u32 {
        STATE_LEN
    }

    fn sector_size(&self) -> u32 {
        SECTOR
    }

    fn read(&mut self, offset: u32, buf: &mut [u8]) -> HalResult<()> {
        let addr = Self::check(offset, buf.len())?;
        // SAFETY: inside the mapped flash, checked above.
        unsafe { core::ptr::copy_nonoverlapping((XIP_BASE + addr) as *const u8, buf.as_mut_ptr(), buf.len()) };
        Ok(())
    }

    /// Records are 16 bytes and never straddle a page, so one page program
    /// per write, with the rest of the page reprogrammed to what it holds.
    fn write(&mut self, offset: u32, data: &[u8]) -> HalResult<()> {
        let addr = Self::check(offset, data.len())?;
        let page_start = addr & !(PAGE as u32 - 1);
        let within = (addr - page_start) as usize;
        if within + data.len() > PAGE {
            return Err(HalError::InvalidArgument("a boot-state write crosses a page"));
        }
        let mut page = [0xFFu8; PAGE];
        // SAFETY: the page is inside the mapped flash.
        unsafe { core::ptr::copy_nonoverlapping((XIP_BASE + page_start) as *const u8, page.as_mut_ptr(), PAGE) };
        page[within..within + data.len()].copy_from_slice(data);
        // SAFETY: the closure touches only the stack and the ROM routine.
        unsafe { with_xip_disabled(|| rom_data::flash_range_program(page_start, page.as_ptr(), PAGE)) };
        Ok(())
    }

    fn erase(&mut self, offset: u32, len: u32) -> HalResult<()> {
        let addr = Self::check(offset, len as usize)?;
        // SAFETY: as above.
        unsafe { with_xip_disabled(|| rom_data::flash_range_erase(addr, len as usize, SECTOR, 0x20)) };
        Ok(())
    }
}

fn slot_ok(slot: Slot) -> bool {
    let base = (XIP_BASE + slot.offset()) as *const u32;
    // SAFETY: the slot is mapped flash.
    let (sp, reset) = unsafe { (base.read_volatile(), base.add(1).read_volatile()) };
    vector_table_ok(slot, sp, reset)
}

/// Point the vector table at `slot` and enter its reset handler with its stack.
fn jump(slot: Slot) -> ! {
    let base = XIP_BASE + slot.offset();
    // SAFETY: the slot holds a plausible vector table (checked by the
    // caller); nothing of this program is needed after the jump.
    unsafe {
        (*cortex_m::peripheral::SCB::PTR).vtor.write(base);
        cortex_m::asm::bootload(base as *const u32)
    }
}

#[entry]
fn main() -> ! {
    #[cfg(feature = "force-slot-a")]
    jump(Slot::A);
    #[allow(unreachable_code)]
    let mut flash = StateFlash;
    let mut slot = Slot::A;
    if let Ok(mut log) = StateLog::load(&mut flash) {
        slot = log.state.boot_slot();
        // Count the attempt *before* the jump: an image that faults before it
        // can say anything still uses one up.
        if log.state.trial == Some(slot) && log.append(&mut flash, Record::Attempt(slot)).is_err() {
            slot = log.state.confirmed;
        }
    }
    for candidate in [slot, slot.other()] {
        if slot_ok(candidate) {
            jump(candidate);
        }
    }
    rom_data::reset_to_usb_boot(0, 0);
    loop {
        cortex_m::asm::wfi();
    }
}

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! {
    rom_data::reset_to_usb_boot(0, 0);
    loop {}
}
