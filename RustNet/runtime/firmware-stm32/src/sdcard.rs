//! microSD over SPI, in the card's SPI mode.
//!
//! Enough of the spec to be a block device: bring the card out of its native
//! SD mode, learn whether it addresses by byte or by block, and read and write
//! 512-byte sectors. A filesystem goes on top of this, not inside it.
//!
//! # Why the clock changes
//!
//! A card must be initialised at 400 kHz or slower — that is the rate its
//! internal state machine is guaranteed to answer at before it knows what it
//! is talking to. Once it has answered, the bus can go as fast as the wiring
//! allows. Missing this is the classic reason a card enumerates on one board
//! and not another.
//!
//! # Borrowing
//!
//! Chip select is a GPIO and the data lines are SPI, and `Board` hands out
//! each of those as its own `&mut`. So the two cannot be held at once, and
//! every step re-borrows rather than keeping a handle.

use alloc::format;
use alloc::string::String;
use alloc::vec;
use alloc::vec::Vec;

use rustnet_hal::gpio::{Level, PinMode};
use rustnet_hal::spi::SpiMode;
use rustnet_hal::Board as _;

/// SPI3 on this board, index 2 in the HAL's numbering.
const SPI_BUS: u8 = 2;

pub const BLOCK_LEN: usize = 512;

/// Slow enough for any card to answer while it is still working out what the
/// host is; the spec's ceiling for identification is 400 kHz.
const INIT_HZ: u32 = 200_000;
/// Deliberately modest. The card answered every identification command at
/// 200 kHz, so anything that fails only after the speed goes up is a signal
/// integrity problem, not a protocol one — and this is the knob for it.
const DATA_HZ: u32 = 1_000_000;

const CMD0_GO_IDLE: u8 = 0;
const CMD8_SEND_IF_COND: u8 = 8;
const CMD9_SEND_CSD: u8 = 9;
const CMD13_SEND_STATUS: u8 = 13;
const CMD16_SET_BLOCKLEN: u8 = 16;
const CMD17_READ_SINGLE: u8 = 17;
const CMD24_WRITE_SINGLE: u8 = 24;
const CMD55_APP: u8 = 55;
const CMD58_READ_OCR: u8 = 58;
const ACMD41_INIT: u8 = 41;

/// Start of a data block, and of a single-block write.
const TOKEN_START: u8 = 0xFE;

/// How a card addresses. High-capacity cards count blocks; the older ones
/// count bytes, and handing them a block number reads the wrong place
/// silently rather than failing.
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Addressing {
    Byte,
    Block,
}

pub struct SdCard {
    cs: u32,
    addressing: Addressing,
    /// What each identification step actually answered. Two rounds of guessing
    /// at this cost more than recording it does: a card that reports success
    /// and then refuses to read is claiming something one of these bytes
    /// contradicts.
    pub trace: String,
}

/// Capacity in megabytes, decoded from a CSD.
///
/// Worth having because it is the cheapest proof that a card is answering
/// truthfully rather than returning noise that happens to parse.
pub fn capacity_mb(csd: &[u8; 16]) -> Option<u32> {
    // CSD_STRUCTURE lives in the top two bits: 1 means version 2, where
    // C_SIZE counts 512 KB units. Version 1 uses a different layout.
    if csd[0] >> 6 != 1 {
        return None;
    }
    let c_size = (((csd[7] & 0x3F) as u32) << 16) | ((csd[8] as u32) << 8) | csd[9] as u32;
    Some((c_size + 1) / 2)
}

impl SdCard {
    /// Bring a card up.
    ///
    /// `cs` and `ctrl` are HAL pin indices (`port * 16 + index`). `ctrl` is
    /// the slot's own enable line where a board has one — the Netduino does,
    /// and leaving it floating is enough for the card never to answer, which
    /// looks exactly like a card that is not there.
    pub fn init(
        board: &mut dyn rustnet_hal::Board,
        cs: u32,
        ctrl: Option<(u32, Level)>,
    ) -> Result<Self, String> {
        if let Some((pin, level)) = ctrl {
            board
                .gpio(pin)
                .and_then(|p| p.set_mode(PinMode::Output))
                .map_err(|e| format!("card enable: {e}"))?;
            board
                .gpio(pin)
                .and_then(|p| p.write(level))
                .map_err(|e| format!("card enable: {e}"))?;
            // Give the slot a moment to come up before talking to it.
            board.delay().delay_ms(10);
        }

        board
            .gpio(cs)
            .and_then(|p| p.set_mode(PinMode::Output))
            .map_err(|e| format!("chip select: {e}"))?;

        let mut card = SdCard { cs, addressing: Addressing::Byte, trace: String::new() };
        card.deselect(board);

        board
            .spi(SPI_BUS)
            .and_then(|s| s.configure(INIT_HZ, SpiMode::Mode0))
            .map_err(|e| format!("spi: {e}"))?;

        // At least 74 clocks with the line idle, chip select released: this is
        // how a card is told to enter SPI mode at all.
        card.xfer(board, &[0xFF; 10], &mut [0u8; 10])?;

        card.select(board);
        let result = card.bring_up(board);
        card.deselect(board);
        result?;

        board
            .spi(SPI_BUS)
            .and_then(|s| s.configure(DATA_HZ, SpiMode::Mode0))
            .map_err(|e| format!("spi: {e}"))?;
        Ok(card)
    }

    fn bring_up(&mut self, board: &mut dyn rustnet_hal::Board) -> Result<(), String> {
        // CRC matters only until the card is in SPI mode, so these two are
        // the only commands carrying a real one.
        let r0 = self.command(board, CMD0_GO_IDLE, 0, 0x95)?;
        self.trace.push_str(&format!("cmd0={r0:#04x}"));
        if r0 != 0x01 {
            return Err(format!("no card responded to CMD0 (got {r0:#04x})"));
        }

        let mut echo = [0u8; 4];
        let r1 = self.command(board, CMD8_SEND_IF_COND, 0x0000_01AA, 0x87)?;
        self.trace.push_str(&format!(" cmd8={r1:#04x}"));
        let v2 = if r1 & 0x04 == 0 {
            self.xfer_in(board, &mut echo)?;
            self.trace.push_str(&format!(" echo={echo:02x?}"));
            if echo[2] != 0x01 || echo[3] != 0xAA {
                return Err(String::from("CMD8 echo mismatch: unusable voltage range"));
            }
            true
        } else {
            // Illegal command: a version 1 card, which never had CMD8.
            false
        };

        // ACMD41 is how a card is told to start its power-up, and it answers
        // busy until that finishes. Cards routinely take tens of milliseconds.
        let host_capacity_support = if v2 { 0x4000_0000 } else { 0 };
        let mut ready = false;
        let mut rounds = 0u32;
        let mut last = 0xFFu8;
        for _ in 0..1000 {
            rounds += 1;
            let r55 = self.command(board, CMD55_APP, 0, 0x01)?;
            last = self.command(board, ACMD41_INIT, host_capacity_support, 0x01)?;
            if last == 0 {
                self.trace.push_str(&format!(" cmd55={r55:#04x} acmd41=ok/{rounds}"));
                ready = true;
                break;
            }
            self.delay_ms(board, 1);
        }
        if !ready {
            return Err(format!("ACMD41 never cleared after {rounds} tries (last {last:#04x})"));
        }

        self.addressing = Addressing::Byte;
        if v2 {
            let mut ocr = [0u8; 4];
            let r58 = self.command(board, CMD58_READ_OCR, 0, 0x01)?;
            if r58 == 0 {
                self.xfer_in(board, &mut ocr)?;
                self.trace.push_str(&format!(" ocr={ocr:02x?}"));
                // CCS, bit 30 of the OCR: set means block addressing.
                if ocr[0] & 0x40 != 0 {
                    self.addressing = Addressing::Block;
                }
            } else {
                self.trace.push_str(&format!(" cmd58={r58:#04x}"));
            }
        }
        if self.addressing == Addressing::Byte {
            self.command(board, CMD16_SET_BLOCKLEN, BLOCK_LEN as u32, 0x01)?;
        }
        Ok(())
    }

    pub fn addressing(&self) -> Addressing {
        self.addressing
    }

    /// Read the card's CSD register.
    ///
    /// This is the discriminator when a card identifies but will not read: the
    /// CSD lives in the controller, not the flash array, yet it arrives through
    /// the same data-token path a block does. CSD good and blocks bad points at
    /// the storage; both bad points at this driver.
    pub fn read_csd(
        &mut self,
        board: &mut dyn rustnet_hal::Board,
        out: &mut [u8; 16],
    ) -> Result<(), String> {
        self.select(board);
        let result = (|| {
            self.xfer_in(board, &mut [0u8; 1])?;
            let r1 = self.command(board, CMD9_SEND_CSD, 0, 0x01)?;
            if r1 != 0 {
                return Err(format!("CMD9 rejected with R1 {r1:#04x}"));
            }
            self.await_token(board)?;
            self.xfer_in(board, out)?;
            self.xfer_in(board, &mut [0u8; 2])?;
            Ok(())
        })();
        self.deselect(board);
        result
    }

    /// Ask the card why, in its own words.
    ///
    /// R2 is R1 plus a second byte naming the objection: locked card, address
    /// or parameter error, a failed erase. When a read is refused and the
    /// controller is demonstrably healthy, this is the byte that says which.
    pub fn status(&mut self, board: &mut dyn rustnet_hal::Board) -> Result<[u8; 2], String> {
        self.select(board);
        let result = (|| {
            self.xfer_in(board, &mut [0u8; 1])?;
            let r1 = self.command(board, CMD13_SEND_STATUS, 0, 0x01)?;
            let mut second = [0u8; 1];
            self.xfer_in(board, &mut second)?;
            Ok([r1, second[0]])
        })();
        self.deselect(board);
        result
    }

    /// Send a command and hand back exactly what came off the wire, with no
    /// interpretation at all.
    ///
    /// Point this at a command that works and one that does not, and the
    /// difference between the two byte streams is the defect. Everything else
    /// in this driver decides what a byte *means* before reporting it, which
    /// is precisely what hid this.
    pub fn probe(
        &mut self,
        board: &mut dyn rustnet_hal::Board,
        cmd: u8,
        arg: u32,
    ) -> Result<String, String> {
        self.select(board);
        let result = (|| {
            let mut lead = [0u8; 1];
            self.xfer_in(board, &mut lead)?;
            let frame = [
                0x40 | cmd,
                (arg >> 24) as u8,
                (arg >> 16) as u8,
                (arg >> 8) as u8,
                arg as u8,
                0x01,
            ];
            let mut echo = [0u8; 6];
            self.xfer(board, &frame, &mut echo)?;
            let mut resp = [0u8; 16];
            self.xfer_in(board, &mut resp)?;
            Ok(format!("lead={:02x} echo={echo:02x?} resp={resp:02x?}", lead[0]))
        })();
        self.deselect(board);
        result
    }

    /// Re-clock the bus, for testing whether a failure is electrical.
    ///
    /// Register reads (OCR, CSD) touch only the controller and draw little
    /// current; a block read wakes the flash array and draws far more. When the
    /// first kind works and the second does not, with the card reporting no
    /// logical error, supply or signal integrity is the suspect — and clock
    /// rate is the one knob that separates those from a protocol mistake.
    pub fn set_clock(&self, board: &mut dyn rustnet_hal::Board, hz: u32) -> Result<(), String> {
        board
            .spi(SPI_BUS)
            .and_then(|s| s.configure(hz, SpiMode::Mode0))
            .map_err(|e| format!("spi: {e}"))
    }

    /// Read one 512-byte block.
    pub fn read_block(
        &mut self,
        board: &mut dyn rustnet_hal::Board,
        block: u32,
        out: &mut [u8],
    ) -> Result<(), String> {
        if out.len() != BLOCK_LEN {
            return Err(String::from("read needs a 512-byte buffer"));
        }
        self.select(board);
        let result = (|| {
            // A byte of idle after asserting chip select: the card needs the
            // clocks to notice it has been addressed at all.
            self.xfer_in(board, &mut [0u8; 1])?;
            let r1 = self.command(board, CMD17_READ_SINGLE, self.address(block), 0x01)?;
            if r1 != 0 {
                return Err(format!("CMD17 rejected with R1 {r1:#04x}"));
            }
            self.await_token(board)?;
            self.xfer_in(board, out)?;
            // Two CRC bytes follow, unchecked: the SPI layer below has its own
            // integrity story and the card refuses a bad block outright.
            self.xfer_in(board, &mut [0u8; 2])?;
            Ok(())
        })();
        self.deselect(board);
        result
    }

    /// Write one 512-byte block.
    pub fn write_block(
        &mut self,
        board: &mut dyn rustnet_hal::Board,
        block: u32,
        data: &[u8],
    ) -> Result<(), String> {
        if data.len() != BLOCK_LEN {
            return Err(String::from("write needs a 512-byte buffer"));
        }
        self.select(board);
        let result = (|| {
            if self.command(board, CMD24_WRITE_SINGLE, self.address(block), 0x01)? != 0 {
                return Err(String::from("CMD24 rejected"));
            }
            self.xfer(board, &[0xFF, TOKEN_START], &mut [0u8; 2])?;
            self.xfer(board, data, &mut vec![0u8; BLOCK_LEN])?;
            self.xfer(board, &[0xFF, 0xFF], &mut [0u8; 2])?; // dummy CRC

            let mut response = [0u8; 1];
            self.xfer_in(board, &mut response)?;
            if response[0] & 0x1F != 0x05 {
                return Err(format!("card refused the block: {:#04x}", response[0]));
            }
            // The card holds the line low while it programs. This is the one
            // place a card can take hundreds of milliseconds.
            for _ in 0..100_000 {
                let mut busy = [0u8; 1];
                self.xfer_in(board, &mut busy)?;
                if busy[0] != 0x00 {
                    return Ok(());
                }
            }
            Err(String::from("card stayed busy after a write"))
        })();
        self.deselect(board);
        result
    }

    fn address(&self, block: u32) -> u32 {
        match self.addressing {
            Addressing::Block => block,
            Addressing::Byte => block.wrapping_mul(BLOCK_LEN as u32),
        }
    }

    /// Send a command and return its R1 response.
    fn command(
        &mut self,
        board: &mut dyn rustnet_hal::Board,
        cmd: u8,
        arg: u32,
        crc: u8,
    ) -> Result<u8, String> {
        let frame = [
            0x40 | cmd,
            (arg >> 24) as u8,
            (arg >> 16) as u8,
            (arg >> 8) as u8,
            arg as u8,
            crc,
        ];
        self.xfer(board, &frame, &mut [0u8; 6])?;

        // The card answers within eight bytes, flagged by a clear top bit.
        for _ in 0..16 {
            let mut byte = [0u8; 1];
            self.xfer_in(board, &mut byte)?;
            if byte[0] & 0x80 == 0 {
                return Ok(byte[0]);
            }
        }
        Err(format!("no response to CMD{cmd}"))
    }

    /// Wait for the start-of-data token, reporting what actually came back if
    /// it never arrives. A bare "error token" tells you nothing; the first few
    /// bytes tell you whether the card is objecting or the bus is noisy.
    fn await_token(&mut self, board: &mut dyn rustnet_hal::Board) -> Result<(), String> {
        let mut seen: Vec<u8> = Vec::new();
        for _ in 0..50_000 {
            let mut byte = [0u8; 1];
            self.xfer_in(board, &mut byte)?;
            match byte[0] {
                TOKEN_START => return Ok(()),
                0xFF => continue,
                other => {
                    // Not necessarily fatal: residue from the previous command
                    // can trail into this window. Keep looking for a while, and
                    // keep what was seen for the message.
                    if seen.len() < 8 {
                        seen.push(other);
                    }
                    if seen.len() >= 8 {
                        break;
                    }
                }
            }
        }
        Err(format!("no data token; saw {seen:02x?}"))
    }

    fn xfer(
        &self,
        board: &mut dyn rustnet_hal::Board,
        tx: &[u8],
        rx: &mut [u8],
    ) -> Result<(), String> {
        board.spi(SPI_BUS).and_then(|s| s.transfer(tx, rx)).map_err(|e| format!("spi: {e}"))
    }

    /// Clock in bytes while holding the line idle.
    fn xfer_in(&self, board: &mut dyn rustnet_hal::Board, rx: &mut [u8]) -> Result<(), String> {
        let tx = vec![0xFFu8; rx.len()];
        self.xfer(board, &tx, rx)
    }

    fn select(&self, board: &mut dyn rustnet_hal::Board) {
        let _ = board.gpio(self.cs).and_then(|p| p.write(Level::Low));
        // Do not start a command on a bus that is still carrying the previous
        // one. This was the defect behind every failed read: the card was
        // still streaming the tail of the last data block, so the command
        // frame went out on top of it and the response window read residue.
        let _ = self.drain(board);
    }

    fn deselect(&self, board: &mut dyn rustnet_hal::Board) {
        // Let the card finish whatever it is still sending *before* dropping
        // chip select; afterwards it has no way to tell us it is done.
        let _ = self.drain(board);
        let _ = board.gpio(self.cs).and_then(|p| p.write(Level::High));
        // Eight clocks with the line released, so the card lets go of it.
        let _ = self.xfer(board, &[0xFF], &mut [0u8; 1]);
    }

    /// Clock until the card stops driving the bus, i.e. until it reads idle.
    ///
    /// A single throwaway byte is not enough: a data block that was only
    /// partly consumed leaves several behind, and they surface as the *next*
    /// command's response.
    fn drain(&self, board: &mut dyn rustnet_hal::Board) -> Result<(), String> {
        for _ in 0..1024 {
            let mut byte = [0u8; 1];
            self.xfer_in(board, &mut byte)?;
            if byte[0] == 0xFF {
                return Ok(());
            }
        }
        Err(String::from("card would not release the bus"))
    }

    fn delay_ms(&self, board: &mut dyn rustnet_hal::Board, ms: u64) {
        board.delay().delay_ms(ms);
    }
}
