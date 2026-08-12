//! Entering the ROM's DFU bootloader on command, so reflashing needs no hands.
//!
//! The Meadow has no BOOT0 button and no reset button worth the name: putting
//! it into DFU means holding its boot pin while power cycles, which is fine
//! once and intolerable as a development loop. This port has two firmware
//! images that have to be swapped constantly — the normal one and the ESP32
//! bridge — so every ESP32 experiment costs two manual DFU entries.
//!
//! `rustnet firmware flash --board meadow-f7 --device serial:COMn` now asks
//! the running board into DFU over RNDP first, and this is the device half.
//!
//! **Arm, reset, then jump — not jump directly.** Jumping to the system
//! bootloader from a running application hands it a chip whose clocks, USB
//! core, caches and interrupts are all configured for *this* firmware, and it
//! does not put them back. That is the same shape of bug that made
//! `dfu-util --leave` leave HSE unable to start (see the crate docs): a jump
//! is not a reset. So a request only records a magic word in a RAM location
//! the startup code does not clear, and asks for a system reset. The check
//! below then runs at the top of `main` — after a genuine reset, with every
//! peripheral at its reset default and caches and interrupts off — which is
//! exactly the state the ROM expects to be entered in.

use core::mem::MaybeUninit;

/// Arbitrary, but must not be a value RAM plausibly holds by accident: an
/// unlucky match sends the board to DFU on an ordinary reboot.
const MAGIC: u32 = 0xB007_DF11;

/// Where the F76x/F77x ROM bootloader lives. AN2606, table for STM32F76xxx.
const SYSTEM_MEMORY: u32 = 0x1FF0_0000;

/// Not in `.bss`: `cortex-m-rt` zeroes that before `main`, which would erase
/// the request in the microseconds between the reset and reading it. `.uninit`
/// is left alone by the startup code, and SRAM contents survive a system
/// reset — the two facts this whole mechanism rests on.
#[link_section = ".uninit.DFU_REQUEST"]
static mut DFU_REQUEST: MaybeUninit<u32> = MaybeUninit::uninit();

/// Record that the next boot should land in the ROM bootloader.
///
/// The caller is expected to reset immediately afterwards; nothing else acts
/// on this.
pub fn arm() {
    // SAFETY: single-threaded firmware, and the only other access is the read
    // in `check_and_jump` which runs before anything else exists.
    unsafe {
        core::ptr::addr_of_mut!(DFU_REQUEST).write_volatile(MaybeUninit::new(MAGIC));
    }
}

/// Jump to the ROM bootloader if the previous boot asked for it.
///
/// Call this first in `main`, before clocks, GPIO or anything else — the whole
/// point is to hand the ROM a chip in its reset state.
pub fn check_and_jump() {
    // SAFETY: as above; nothing else has run yet.
    let requested = unsafe { core::ptr::addr_of!(DFU_REQUEST).read_volatile() };
    if unsafe { requested.assume_init() } != MAGIC {
        return;
    }

    // Clear it before jumping, not after. There is no "after" — and a magic
    // left in place would send the board to DFU on every reset from here on,
    // including the one the ROM performs when flashing finishes, which would
    // leave it permanently unable to run the image just written to it.
    unsafe {
        core::ptr::addr_of_mut!(DFU_REQUEST).write_volatile(MaybeUninit::new(0));
    }

    unsafe { jump() }
}

/// Registers touched here, all outside any peripheral this port drives.
const RCC_APB2ENR: usize = 0x4002_3844;
const RCC_APB2ENR_SYSCFGEN: u32 = 1 << 14;
const SYSCFG_MEMRMP: usize = 0x4001_3800;
const SCB_VTOR: usize = 0xE000_ED08;
const SYST_CSR: usize = 0xE000_E010;

unsafe fn jump() -> ! {
    // The ROM reads its own vector table through address 0, so system flash
    // has to be what lives there. Everything else below is belt and braces
    // after a fresh reset, but this part is load-bearing.
    let apb2 = (RCC_APB2ENR as *mut u32).read_volatile();
    (RCC_APB2ENR as *mut u32).write_volatile(apb2 | RCC_APB2ENR_SYSCFGEN);
    (SYSCFG_MEMRMP as *mut u32).write_volatile(0x1);

    (SYST_CSR as *mut u32).write_volatile(0);
    (SCB_VTOR as *mut u32).write_volatile(SYSTEM_MEMORY);

    // The ROM's vector table: entry 0 is the stack pointer it wants, entry 1
    // its reset vector.
    let stack = (SYSTEM_MEMORY as *const u32).read_volatile();
    let entry = ((SYSTEM_MEMORY + 4) as *const u32).read_volatile();

    // `bootstrap` rather than writing MSP and then calling through the reset
    // vector: between those two steps the stack pointer belongs to the ROM
    // while the code still belongs to us, and anything the compiler decides to
    // spill in that window is written to the ROM's stack. It does both in one
    // asm block for exactly that reason.
    cortex_m::asm::bootstrap(stack as *const u32, entry as *const u32)
}
