//! The module's 32 MB QSPI NOR, as somewhere to keep things.
//!
//! The Meadow carries an **S25FL256L** (schematic sheet 4, `U501`) on the
//! STM32's QUADSPI controller. That is 32 MB of storage on a board whose
//! internal flash is 2 MB and mostly spoken for, so it is where applications,
//! the provisioning key and any data files belong.
//!
//! ## Pins
//!
//! From sheet 2, by matching each net to its BGA ball and the ball to its pin:
//!
//! | Net | Ball | Pin | Function |
//! |---|---|---|---|
//! | `FLASH_CLK` | M6 | PB2 | `QUADSPI_CLK` |
//! | `FLASH_CS_L` | R12 | PB10 | `QUADSPI_BK1_NCS` |
//! | `QUAD_SPI_IO0` | N14 | PD11 | `QUADSPI_BK1_IO0` |
//! | `QUAD_SPI_IO1` | N13 | PD12 | `QUADSPI_BK1_IO1` |
//! | `QUAD_SPI_IO2` | A2 | PE2 | `QUADSPI_BK1_IO2` |
//! | `QUAD_SPI_IO3` | M15 | PD13 | `QUADSPI_BK1_IO3` |
//!
//! All six on alternate function 9. Three of them fell out of the net-to-pin
//! match directly; the other three matched the wrong column, and going via the
//! ball numbers settled them. The result is the F7's textbook BK1 mapping,
//! which is the cross-check: a derivation that lands exactly on the
//! manufacturer's canonical set is unlikely to have landed there by accident.
//!
//! ## One line, not four
//!
//! The controller can drive all four data lines, and this drives one. Quad
//! mode needs the flash's own status registers configured first, and a
//! half-configured quad transfer fails in ways that are hard to tell from
//! wiring faults. Single-line SPI over the same controller is the same
//! silicon, the same pins and far fewer ways to be wrong — and at 27 MHz it
//! already moves a megabyte in well under a second, which is not this port's
//! bottleneck. Quad is a later optimisation with a working baseline to
//! compare against.
//!
//! ## Four-byte addressing
//!
//! 32 MB is past the 16 MB a three-byte address can reach, so the four-byte
//! opcodes are used throughout (`13h` read, `12h` page program, `21h` sector
//! erase). The alternative — the `B7h` enter-4-byte-mode latch — leaves the
//! chip in a state that survives a reset and confuses anything that assumes
//! otherwise, including the next firmware to run.

use rustnet_hal::extmem::{ExtMemKind, ExtMemory};
use rustnet_hal::{HalError, HalResult};

use crate::{rd, wr};

const QUADSPI: usize = 0xA000_1000;
const CR: usize = QUADSPI + 0x00;
const DCR: usize = QUADSPI + 0x04;
const SR: usize = QUADSPI + 0x08;
const FCR: usize = QUADSPI + 0x0C;
const DLR: usize = QUADSPI + 0x10;
const CCR: usize = QUADSPI + 0x14;
const AR: usize = QUADSPI + 0x18;
const DR: usize = QUADSPI + 0x20;

const SR_TCF: u32 = 1 << 1;
const SR_FTF: u32 = 1 << 2;
const SR_BUSY: u32 = 1 << 5;

const RCC_BASE: usize = 0x4002_3800;
const RCC_AHB1ENR: usize = RCC_BASE + 0x30;
const RCC_AHB3ENR: usize = RCC_BASE + 0x38;
const AHB3_QSPIEN: u32 = 1 << 1;

/// S25FL256L commands, all four-byte-address variants where addressed.
const CMD_READ_ID: u8 = 0x9F;
const CMD_READ_STATUS: u8 = 0x05;
const CMD_WRITE_ENABLE: u8 = 0x06;
const CMD_READ_4B: u8 = 0x13;
const CMD_PAGE_PROGRAM_4B: u8 = 0x12;
const CMD_SECTOR_ERASE_4B: u8 = 0x21;

/// Status register 1, bit 0: a program or erase is still running.
const STATUS_WIP: u8 = 1 << 0;

/// The part: 32 MB, 4 KB erase sectors, 256-byte program pages.
pub const CAPACITY: u32 = 32 * 1024 * 1024;
pub const SECTOR_SIZE: u32 = 4096;
const PAGE_SIZE: usize = 256;

/// `FSIZE` is log2 of the capacity, less one.
const FSIZE: u32 = 24;

/// The JEDEC identity this port expects: Cypress/Infineon S25FL256L.
pub const EXPECTED_ID: [u8; 3] = [0x01, 0x60, 0x19];

pub struct Qspi;

impl Qspi {
    /// Bring the controller and its six pins up.
    pub fn new() -> Self {
        Self::setup_pins();
        wr(RCC_AHB3ENR, rd(RCC_AHB3ENR) | AHB3_QSPIEN);

        // Disabled while configured; most of CR is ignored while EN is set.
        wr(CR, 0);
        // FSIZE for a 32 MB device, and a chip-select high time of 4 cycles —
        // NOR parts need the select to lift properly between commands and the
        // reset default of one cycle is marginal at this clock.
        wr(DCR, (FSIZE << 16) | (3 << 8));
        // Prescaler 7 divides the 216 MHz AHB clock to 27 MHz. Conservative
        // on purpose: the part will do far more, and a bring-up that is fast
        // and unreliable is worse than one that is slow and correct.
        wr(CR, (7 << 24) | (3 << 8) | 1); // PRESCALER | FTHRES=4 | EN

        Qspi
    }

    fn setup_pins() {
        // Ports B, D and E.
        wr(RCC_AHB1ENR, rd(RCC_AHB1ENR) | (1 << 1) | (1 << 3) | (1 << 4));

        const GPIOB: usize = 0x4002_0400;
        const GPIOD: usize = 0x4002_0C00;
        const GPIOE: usize = 0x4002_1000;

        // (port base, pin, AF) — all of QUADSPI is on AF9 for this family.
        for (base, pin) in [
            (GPIOB, 2u32),  // CLK
            (GPIOB, 10),    // BK1_NCS
            (GPIOD, 11),    // BK1_IO0
            (GPIOD, 12),    // BK1_IO1
            (GPIOE, 2),     // BK1_IO2
            (GPIOD, 13),    // BK1_IO3
        ] {
            let sh = pin * 2;
            let moder = rd(base);
            wr(base, (moder & !(0b11 << sh)) | (0b10 << sh)); // alternate function

            // The bus clocks at tens of megahertz; a pin left at the reset
            // slew rate rounds the edges enough to lose bits at the far end.
            let ospeedr = rd(base + 0x08);
            wr(base + 0x08, ospeedr | (0b11 << sh));

            let (afr, shift) = if pin < 8 { (base + 0x20, pin * 4) } else { (base + 0x24, (pin - 8) * 4) };
            let v = rd(afr);
            wr(afr, (v & !(0xF << shift)) | (9 << shift));
        }
    }

    fn wait_idle(&self) -> HalResult<()> {
        for _ in 0..2_000_000u32 {
            if rd(SR) & SR_BUSY == 0 {
                return Ok(());
            }
        }
        Err(HalError::Bus("qspi controller stayed busy"))
    }

    /// Run a command with no address and no data.
    fn command(&mut self, instr: u8) -> HalResult<()> {
        self.wait_idle()?;
        // FMODE=indirect write, IMODE=1 line, no address, no data.
        wr(CCR, (0b01 << 8) | instr as u32);
        self.finish()
    }

    /// Run a command that reads `buf.len()` bytes, optionally from `addr`.
    fn read_into(&mut self, instr: u8, addr: Option<u32>, buf: &mut [u8]) -> HalResult<()> {
        self.wait_idle()?;
        wr(DLR, buf.len() as u32 - 1);
        let admode = if addr.is_some() { 0b01 << 10 } else { 0 };
        let adsize = if addr.is_some() { 0b11 << 12 } else { 0 }; // 32-bit
        wr(
            CCR,
            (0b01 << 26)          // FMODE = indirect read
                | (0b01 << 24)    // DMODE = 1 line
                | adsize
                | admode
                | (0b01 << 8)     // IMODE = 1 line
                | instr as u32,
        );
        // Writing AR is what starts an addressed read; without an address the
        // CCR write already started it.
        if let Some(a) = addr {
            wr(AR, a);
        }

        for slot in buf.iter_mut() {
            let mut spins = 0u32;
            loop {
                let sr = rd(SR);
                if sr & (SR_FTF | SR_TCF) != 0 {
                    break;
                }
                spins += 1;
                if spins > 2_000_000 {
                    return Err(HalError::Bus("qspi read stalled"));
                }
            }
            // SAFETY: the data register is byte-addressable, and a byte read
            // pops exactly one byte from the FIFO — a word read would take
            // four and lose three.
            *slot = unsafe { core::ptr::read_volatile(DR as *const u8) };
        }
        self.finish()
    }

    /// Run a command that writes `data` at `addr`.
    fn write_from(&mut self, instr: u8, addr: u32, data: &[u8]) -> HalResult<()> {
        self.wait_idle()?;
        wr(DLR, data.len() as u32 - 1);
        wr(
            CCR,
            (0b00 << 26)          // FMODE = indirect write
                | (0b01 << 24)    // DMODE = 1 line
                | (0b11 << 12)    // ADSIZE = 32-bit
                | (0b01 << 10)    // ADMODE = 1 line
                | (0b01 << 8)     // IMODE = 1 line
                | instr as u32,
        );
        wr(AR, addr);

        for &byte in data {
            let mut spins = 0u32;
            while rd(SR) & SR_FTF == 0 {
                spins += 1;
                if spins > 2_000_000 {
                    return Err(HalError::Bus("qspi write stalled"));
                }
            }
            // SAFETY: as above — one byte per push.
            unsafe { core::ptr::write_volatile(DR as *mut u8, byte) };
        }
        self.finish()
    }

    /// Wait for the transfer to complete and clear its flag.
    fn finish(&mut self) -> HalResult<()> {
        for _ in 0..2_000_000u32 {
            if rd(SR) & SR_TCF != 0 {
                wr(FCR, SR_TCF);
                return Ok(());
            }
        }
        Err(HalError::Bus("qspi transfer never completed"))
    }

    /// The part's JEDEC identity: manufacturer, type, capacity.
    ///
    /// Read at boot and reported, so a board whose flash is absent, wired
    /// wrongly or a different part says so plainly instead of corrupting
    /// storage quietly.
    pub fn read_id(&mut self) -> HalResult<[u8; 3]> {
        let mut id = [0u8; 3];
        self.read_into(CMD_READ_ID, None, &mut id)?;
        Ok(id)
    }

    fn wait_write_done(&mut self) -> HalResult<()> {
        // A 4 KB sector erase on this part is typically tens of milliseconds
        // and specified far longer; polling is the only honest way to know.
        for _ in 0..200_000u32 {
            let mut status = [0u8; 1];
            self.read_into(CMD_READ_STATUS, None, &mut status)?;
            if status[0] & STATUS_WIP == 0 {
                return Ok(());
            }
        }
        Err(HalError::Bus("flash stayed busy after a write"))
    }

    fn write_enable(&mut self) -> HalResult<()> {
        self.command(CMD_WRITE_ENABLE)
    }
}

impl ExtMemory for Qspi {
    fn kind(&self) -> ExtMemKind {
        ExtMemKind::QspiFlash
    }

    fn size(&self) -> u32 {
        CAPACITY
    }

    fn sector_size(&self) -> u32 {
        SECTOR_SIZE
    }

    fn read(&mut self, addr: u32, buf: &mut [u8]) -> HalResult<()> {
        if buf.is_empty() {
            return Ok(());
        }
        if addr.saturating_add(buf.len() as u32) > CAPACITY {
            return Err(HalError::InvalidArgument("read past the end of flash"));
        }
        self.read_into(CMD_READ_4B, Some(addr), buf)
    }

    /// Program `data` at `addr`, at any alignment.
    ///
    /// NOR flash programs a byte at a time, but only within one 256-byte page
    /// per command — a program that would cross a page boundary wraps to the
    /// start of the same page instead of continuing, which corrupts silently.
    /// So the write is split on page boundaries here rather than demanding
    /// aligned callers, exactly as the RP2040 port had to learn.
    fn write(&mut self, addr: u32, data: &[u8]) -> HalResult<()> {
        if data.is_empty() {
            return Ok(());
        }
        if addr.saturating_add(data.len() as u32) > CAPACITY {
            return Err(HalError::InvalidArgument("write past the end of flash"));
        }
        let mut written = 0usize;
        while written < data.len() {
            let target = addr + written as u32;
            let room = PAGE_SIZE - (target as usize % PAGE_SIZE);
            let take = room.min(data.len() - written);
            self.write_enable()?;
            self.write_from(CMD_PAGE_PROGRAM_4B, target, &data[written..written + take])?;
            self.wait_write_done()?;
            written += take;
        }
        Ok(())
    }

    fn erase(&mut self, addr: u32, len: u32) -> HalResult<()> {
        if addr % SECTOR_SIZE != 0 || len % SECTOR_SIZE != 0 {
            return Err(HalError::InvalidArgument(
                "flash erases must cover whole 4 KB sectors",
            ));
        }
        if addr.saturating_add(len) > CAPACITY {
            return Err(HalError::InvalidArgument("erase past the end of flash"));
        }
        let mut at = addr;
        while at < addr + len {
            self.write_enable()?;
            self.wait_idle()?;
            // Erase takes an address and no data.
            wr(
                CCR,
                (0b11 << 12)      // ADSIZE = 32-bit
                    | (0b01 << 10) // ADMODE = 1 line
                    | (0b01 << 8)  // IMODE = 1 line
                    | CMD_SECTOR_ERASE_4B as u32,
            );
            wr(AR, at);
            self.finish()?;
            self.wait_write_done()?;
            at += SECTOR_SIZE;
        }
        Ok(())
    }
}

/// Names the firmware keeps for itself.
///
/// Under `/.sys/` so a `data push` cannot collide with them by accident, and
/// so an application's own files stay visibly separate from the things that
/// make the device work.
pub mod sys {
    /// The RNX of the flashed application, already signature-checked.
    pub const APP: &str = "/.sys/app.rnx";
    /// What `rustnet flash --name` called it.
    pub const APP_NAME: &str = "/.sys/app.name";
    /// The provisioning key, as DER.
    pub const PUB_KEY: &str = "/.sys/signing.pub";
    /// Present, holding the app's name, when it should run on power-up.
    pub const AUTOSTART: &str = "/.sys/autostart";
    /// The network to join at boot, as `ssid\npsk`.
    ///
    /// Credentials live on the device, never in an application image: a
    /// flashed `.rnx` is a file that gets copied, mailed and committed, and
    /// anything baked into it travels with it. `rustnet wifi --ssid ... --psk
    /// ...` writes this, and an application asks `Wifi.GetSsid()` what it
    /// ended up on.
    pub const WIFI: &str = "/.sys/wifi";
}
