//! What the part says it is.
//!
//! The Meadow F7's exact MCU is not in Wilderness Labs' published
//! documentation — their developer portal says only "STM32F7 ... up to
//! 216 MHz". This image is built for an **STM32F777**, and rather than assert
//! that and hope, it asks the silicon and reports the answer over RNDP.
//!
//! That matters because being wrong is quiet. A memory map sized for 512 KB of
//! RAM on a part that has 320 KB does not fail at link time or at boot; it
//! fails much later, when the allocator first reaches past the end of real
//! memory, in whatever code happened to be running. A line in `info` costs
//! nothing and turns that into a sentence.

use alloc::format;
use alloc::string::String;

/// Debug MCU identity register (ARM DDI + RM0410 §60.5.1).
const DBGMCU_IDCODE: usize = 0xE004_2000;

/// The flash size the part reports about itself, in KB, from system memory
/// (RM0410 §3.3.1). Sixteen bits, not thirty-two — reading it as a word picks
/// up whatever the adjacent halfword holds.
const FLASH_SIZE_KB: usize = 0x1FF0_F442;

/// The 96-bit unique device ID (RM0410 §41.1). Only the first word is used
/// here, as something to tell two otherwise identical boards apart in a log.
const UID_BASE: usize = 0x1FF0_F420;

/// The device ID this image was built for: STM32F76x/F77x.
const DEV_ID_F76X_F77X: u16 = 0x451;

pub struct ChipId {
    /// `DEV_ID`, the low twelve bits of IDCODE.
    pub dev_id: u16,
    /// `REV_ID`, the top sixteen — silicon revision, not part identity.
    pub rev_id: u16,
    pub flash_kb: u16,
    pub uid_word: u32,
}

/// Read the part's identity registers.
pub fn identify() -> ChipId {
    // SAFETY: fixed, always-mapped addresses on this family; all reads.
    unsafe {
        let idcode = core::ptr::read_volatile(DBGMCU_IDCODE as *const u32);
        ChipId {
            dev_id: (idcode & 0x0FFF) as u16,
            rev_id: (idcode >> 16) as u16,
            flash_kb: core::ptr::read_volatile(FLASH_SIZE_KB as *const u16),
            uid_word: core::ptr::read_volatile(UID_BASE as *const u32),
        }
    }
}

impl ChipId {
    /// Is this the family the image was built for?
    ///
    /// Deliberately the *family*, not the exact part: `DEV_ID` cannot tell an
    /// F767 from an F777 — they share 0x451, differing only in the crypto
    /// block. Anything that matters to this firmware (memory map, clock
    /// ceiling, peripheral set) is common to both.
    pub fn is_expected(&self) -> bool {
        self.dev_id == DEV_ID_F76X_F77X
    }

    /// The family name for a device ID, or `None` if it is not one this
    /// firmware knows about.
    pub fn family(&self) -> Option<&'static str> {
        match self.dev_id {
            0x449 => Some("STM32F74x/F75x"),
            0x451 => Some("STM32F76x/F77x"),
            0x452 => Some("STM32F72x/F73x"),
            _ => None,
        }
    }

    /// One line for the boot log and for `info`.
    pub fn describe(&self) -> String {
        let family = self.family().unwrap_or("unrecognised STM32F7");
        format!(
            "chip: {family} (dev {:#05x} rev {:#06x}), {} KB flash, uid {:08x}",
            self.dev_id, self.rev_id, self.flash_kb, self.uid_word
        )
    }
}
