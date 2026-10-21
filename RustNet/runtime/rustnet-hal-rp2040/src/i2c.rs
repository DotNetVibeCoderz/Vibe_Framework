//! I²C — the RP2040's two DesignWare `DW_apb_i2c` controllers, as masters.
//!
//! | HAL bus | Controller | Pico pins |
//! |---|---|---|
//! | 0 | I2C0 at `0x4004_4000` | SDA GP4, SCL GP5 |
//! | 1 | I2C1 at `0x4004_8000` | SDA GP6, SCL GP7 |
//!
//! The same IP the K210 port drives, with the RP2040's choices on top, which
//! are the vendor SDK's and are followed here deliberately:
//!
//! - **Always "fast" speed mode, with the counts computed for the rate.** The
//!   controller's standard and fast modes differ only in which pair of count
//!   registers it uses, so programming the fast pair for 100 kHz *is* standard
//!   mode on the wire. The split is 40% high / 60% low, which meets both
//!   modes' minimum low time.
//! - **`TX_EMPTY_CTRL` on**, so `TX_EMPTY` means the shift register has gone
//!   out on the wire, not merely that the FIFO popped it. That is what makes a
//!   per-byte abort check meaningful: by the time it is read, the byte's
//!   acknowledge has been sampled.
//! - **Each byte carries its own `STOP` / `RESTART`.** The last byte of a
//!   transfer carries `STOP`; the first read after a write carries `RESTART`,
//!   which is what [`I2cBus::write_read`] uses for a register read.
//!
//! **An unanswered address is a latched status, not an error return.**
//! `TX_ABRT_SOURCE` records why the controller gave up, and the hardware sends
//! STOP and flushes both FIFOs by itself. Missing the check turns an absent
//! device into a read that "succeeds" with zeros.
//!
//! **The address lives in a register (`TAR`) that can only change while the
//! controller is disabled**, so every transfer disables, retargets, enables.
//!
//! Pull-ups: the pads' internal ones (~50 kΩ) are switched on so a bare bus
//! idles high, but they are too weak for anything above short-wire 100 kHz.
//! Real boards want 4.7 kΩ to 3V3 on SDA and SCL; every breakout has them.

use rustnet_hal::i2c::I2cBus;
use rustnet_hal::{HalError, HalResult};

use crate::{reg, resets};

const IC_CON: usize = 0x00;
const IC_TAR: usize = 0x04;
const IC_DATA_CMD: usize = 0x10;
const IC_FS_SCL_HCNT: usize = 0x1C;
const IC_FS_SCL_LCNT: usize = 0x20;
const IC_INTR_MASK: usize = 0x30;
const IC_RAW_INTR_STAT: usize = 0x34;
const IC_RX_TL: usize = 0x38;
const IC_TX_TL: usize = 0x3C;
const IC_CLR_INTR: usize = 0x40;
const IC_CLR_TX_ABRT: usize = 0x54;
const IC_CLR_STOP_DET: usize = 0x60;
const IC_ENABLE: usize = 0x6C;
const IC_TXFLR: usize = 0x74;
const IC_RXFLR: usize = 0x78;
const IC_SDA_HOLD: usize = 0x7C;
const IC_TX_ABRT_SOURCE: usize = 0x80;
const IC_FS_SPKLEN: usize = 0xA0;

/// `IC_CON`.
const CON_MASTER_MODE: u32 = 1 << 0;
const CON_SPEED_FAST: u32 = 2 << 1;
const CON_RESTART_EN: u32 = 1 << 5;
const CON_SLAVE_DISABLE: u32 = 1 << 6;
const CON_TX_EMPTY_CTRL: u32 = 1 << 8;

/// `IC_DATA_CMD`: the byte, plus what to do around it.
const CMD_READ: u32 = 1 << 8;
const CMD_STOP: u32 = 1 << 9;
const CMD_RESTART: u32 = 1 << 10;

/// `IC_RAW_INTR_STAT`.
const INTR_TX_EMPTY: u32 = 1 << 4;
const INTR_TX_ABRT: u32 = 1 << 6;
const INTR_STOP_DET: u32 = 1 << 9;

/// `IC_TX_ABRT_SOURCE` causes worth naming.
const ABRT_7B_ADDR_NOACK: u32 = 1 << 0;
const ABRT_TXDATA_NOACK: u32 = 1 << 3;
const ABRT_ARB_LOST: u32 = 1 << 12;

/// Both FIFOs are 16 entries deep on this chip.
const FIFO_DEPTH: u32 = 16;

/// Iterations to allow the bus to move. A byte at 100 kHz is ~90 µs, about
/// 11 000 system cycles; this allows for generous clock stretching and still
/// ends in well under a second on a dead bus.
const SPIN_LIMIT: u32 = 2_000_000;

/// The rates this driver accepts: standard mode up to fast-mode plus.
pub const MIN_HZ: u32 = 10_000;
pub const MAX_HZ: u32 = 1_000_000;

#[derive(Clone, Copy)]
pub struct I2cDef {
    pub base: usize,
    pub reset: u32,
}

pub const I2C0: I2cDef = I2cDef { base: crate::base::I2C0, reset: resets::I2C0 };
pub const I2C1: I2cDef = I2cDef { base: crate::base::I2C1, reset: resets::I2C1 };

/// The SCL counts and timing extras for one rate.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SclTiming {
    pub hcnt: u32,
    pub lcnt: u32,
    /// Spike suppression, in `clk_sys` cycles.
    pub spklen: u32,
    /// SDA hold after SCL falls: 300 ns (120 ns above 1 MHz), in cycles.
    pub sda_hold: u32,
}

impl SclTiming {
    /// The rate the counts really give.
    pub fn actual_hz(&self, sys_hz: u32) -> u32 {
        sys_hz / (self.hcnt + self.lcnt)
    }
}

/// The SDK's `i2c_set_baudrate`, reproduced: a period rounded to the nearest
/// whole cycle, split 40/60 high/low, refused where the controller's limits
/// (counts at least 8, at most 16 bits; hold shorter than the low phase) say
/// the result would not be I²C.
pub fn scl_timing(sys_hz: u32, baud: u32) -> Option<SclTiming> {
    if baud == 0 || sys_hz == 0 {
        return None;
    }
    let period = (sys_hz + baud / 2) / baud;
    let lcnt = period * 3 / 5;
    let hcnt = period - lcnt;
    if hcnt < 8 || lcnt < 8 || hcnt > 0xFFFF || lcnt > 0xFFFF {
        return None;
    }
    let sda_hold = if baud < 1_000_000 {
        (sys_hz as u64 * 3 / 10_000_000) as u32 + 1
    } else {
        (sys_hz as u64 * 3 / 25_000_000) as u32 + 1
    };
    if sda_hold + 2 > lcnt {
        return None;
    }
    let spklen = if lcnt < 16 { 1 } else { lcnt / 16 };
    Some(SclTiming { hcnt, lcnt, spklen, sda_hold })
}

/// Turn a latched abort cause into an error a caller can act on.
pub fn abort_error(source: u32) -> HalError {
    if source & ABRT_7B_ADDR_NOACK != 0 {
        HalError::Bus("I2C address not acknowledged (no device?)")
    } else if source & ABRT_TXDATA_NOACK != 0 {
        HalError::Bus("I2C data byte not acknowledged")
    } else if source & ABRT_ARB_LOST != 0 {
        HalError::Bus("I2C arbitration lost")
    } else {
        HalError::Bus("I2C transfer aborted")
    }
}

pub struct Rp2040I2c {
    def: I2cDef,
    sys_hz: u32,
    baud: u32,
    ready: bool,
}

impl Rp2040I2c {
    pub const fn new(def: I2cDef, sys_hz: u32) -> Self {
        Self { def, sys_hz, baud: 100_000, ready: false }
    }

    #[inline]
    fn r(&self, offset: usize) -> u32 {
        reg::read(self.def.base + offset)
    }

    #[inline]
    fn w(&self, offset: usize, value: u32) {
        reg::write(self.def.base + offset, value);
    }

    /// Reset the block and program it for `self.baud`. Also the recovery path
    /// after a timeout: a controller wedged mid-transfer is cheaper to reset
    /// than to reason about.
    fn init(&mut self) -> HalResult<()> {
        let t = scl_timing(self.sys_hz, self.baud)
            .ok_or(HalError::InvalidArgument("I2C rate not reachable from clk_sys"))?;
        resets::hold(self.def.reset);
        if !resets::unreset(self.def.reset) {
            return Err(HalError::Timeout);
        }
        self.w(IC_ENABLE, 0);
        self.w(
            IC_CON,
            CON_SPEED_FAST | CON_MASTER_MODE | CON_SLAVE_DISABLE | CON_RESTART_EN | CON_TX_EMPTY_CTRL,
        );
        self.w(IC_TX_TL, 0);
        self.w(IC_RX_TL, 0);
        self.w(IC_INTR_MASK, 0);
        self.w(IC_FS_SCL_HCNT, t.hcnt);
        self.w(IC_FS_SCL_LCNT, t.lcnt);
        self.w(IC_FS_SPKLEN, t.spklen);
        // SDA_TX_HOLD is the low half; the receive hold above it stays 0.
        self.w(IC_SDA_HOLD, t.sda_hold & 0xFFFF);
        self.ready = true;
        Ok(())
    }

    fn begin(&mut self, addr: u8) -> HalResult<()> {
        if addr > 0x7F {
            return Err(HalError::InvalidArgument("I2C addresses are 7-bit (0..=0x7F)"));
        }
        if !self.ready {
            self.init()?;
        }
        self.w(IC_ENABLE, 0);
        self.w(IC_TAR, addr as u32);
        self.w(IC_ENABLE, 1);
        // Stale status from the last transfer would read as this one's.
        let _ = self.r(IC_CLR_INTR);
        Ok(())
    }

    /// A transfer that timed out leaves the controller in an unknown state.
    /// Disable it and mark it for a full reset before the next one.
    fn fail(&mut self, e: HalError) -> HalError {
        self.w(IC_ENABLE, 0);
        self.ready = false;
        e
    }

    fn wait_stop(&mut self) -> HalResult<()> {
        if !reg::wait_until(SPIN_LIMIT, || self.r(IC_RAW_INTR_STAT) & INTR_STOP_DET != 0) {
            return Err(self.fail(HalError::Timeout));
        }
        let _ = self.r(IC_CLR_STOP_DET);
        Ok(())
    }

    /// The abort, if one is latched: read the cause, clear it, let the STOP the
    /// hardware sends by itself finish, and report.
    fn take_abort(&mut self) -> HalResult<()> {
        let source = self.r(IC_TX_ABRT_SOURCE);
        if source == 0 && self.r(IC_RAW_INTR_STAT) & INTR_TX_ABRT == 0 {
            return Ok(());
        }
        // Reading CLR_TX_ABRT clears the flag *and* the cause register.
        let _ = self.r(IC_CLR_TX_ABRT);
        self.wait_stop()?;
        self.w(IC_ENABLE, 0);
        Err(abort_error(source))
    }

    fn write_bytes(&mut self, data: &[u8], stop: bool) -> HalResult<()> {
        for (i, byte) in data.iter().enumerate() {
            let last = i + 1 == data.len();
            self.w(IC_DATA_CMD, *byte as u32 | if last && stop { CMD_STOP } else { 0 });
            // With TX_EMPTY_CTRL this is "the byte left the shift register",
            // so its acknowledge has been sampled by now.
            if !reg::wait_until(SPIN_LIMIT, || self.r(IC_RAW_INTR_STAT) & INTR_TX_EMPTY != 0) {
                return Err(self.fail(HalError::Timeout));
            }
            self.take_abort()?;
        }
        if stop {
            self.wait_stop()?;
        }
        Ok(())
    }

    fn read_bytes(&mut self, buf: &mut [u8], restart: bool) -> HalResult<()> {
        let len = buf.len();
        for (i, slot) in buf.iter_mut().enumerate() {
            let first = i == 0;
            let last = i + 1 == len;
            if !reg::wait_until(SPIN_LIMIT, || self.r(IC_TXFLR) < FIFO_DEPTH) {
                return Err(self.fail(HalError::Timeout));
            }
            // Each byte received has to be asked for with a read command.
            self.w(
                IC_DATA_CMD,
                CMD_READ
                    | if first && restart { CMD_RESTART } else { 0 }
                    | if last { CMD_STOP } else { 0 },
            );
            let mut spins = 0u32;
            while self.r(IC_RXFLR) == 0 {
                self.take_abort()?;
                spins += 1;
                if spins == SPIN_LIMIT {
                    return Err(self.fail(HalError::Timeout));
                }
                core::hint::spin_loop();
            }
            *slot = (self.r(IC_DATA_CMD) & 0xFF) as u8;
        }
        self.wait_stop()
    }
}

impl I2cBus for Rp2040I2c {
    fn set_frequency(&mut self, hz: u32) -> HalResult<()> {
        if !(MIN_HZ..=MAX_HZ).contains(&hz) || scl_timing(self.sys_hz, hz).is_none() {
            return Err(HalError::InvalidArgument("I2C rate must be 10 kHz ..= 1 MHz"));
        }
        self.baud = hz;
        self.init()
    }

    /// An empty write is turned into a one-byte read.
    ///
    /// This controller cannot address a device without moving a data byte —
    /// there is no "address only" command — and a one-byte *write* would
    /// change state on most devices (it sets the register pointer). Reading
    /// one byte is what the SDK's bus-scan example does for the same reason,
    /// so `probe` stays harmless.
    fn write(&mut self, addr: u8, data: &[u8]) -> HalResult<()> {
        if data.is_empty() {
            let mut sink = [0u8; 1];
            return self.read(addr, &mut sink);
        }
        self.begin(addr)?;
        self.write_bytes(data, true)
    }

    fn read(&mut self, addr: u8, buf: &mut [u8]) -> HalResult<()> {
        if buf.is_empty() {
            return Ok(());
        }
        self.begin(addr)?;
        self.read_bytes(buf, false)
    }

    /// Write, then RESTART into the read with no STOP between — the shape a
    /// register read needs, and the one devices that drop their register
    /// pointer on STOP insist on.
    fn write_read(&mut self, addr: u8, data: &[u8], buf: &mut [u8]) -> HalResult<()> {
        if data.is_empty() {
            return self.read(addr, buf);
        }
        if buf.is_empty() {
            return self.write(addr, data);
        }
        self.begin(addr)?;
        self.write_bytes(data, false)?;
        self.read_bytes(buf, true)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn controllers_and_reset_bits() {
        assert_eq!(I2C0.base, 0x4004_4000);
        assert_eq!(I2C1.base, 0x4004_8000);
        assert_eq!(I2C0.reset, 1 << 3);
        assert_eq!(I2C1.reset, 1 << 4);
    }

    /// The SDK's numbers for 100 kHz on a 125 MHz clk_sys: period 1250, so
    /// LCNT 750 and HCNT 500, spike length 46, SDA hold 38 cycles (304 ns).
    #[test]
    fn standard_mode_matches_the_vendor_sdk() {
        let t = scl_timing(125_000_000, 100_000).expect("reachable");
        assert_eq!(t, SclTiming { hcnt: 500, lcnt: 750, spklen: 46, sda_hold: 38 });
        assert_eq!(t.actual_hz(125_000_000), 100_000);
    }

    /// 400 kHz: period 313 (rounded from 312.5), LCNT 187, HCNT 126.
    #[test]
    fn fast_mode_matches_the_vendor_sdk() {
        let t = scl_timing(125_000_000, 400_000).expect("reachable");
        assert_eq!(t, SclTiming { hcnt: 126, lcnt: 187, spklen: 11, sda_hold: 38 });
        assert_eq!(t.actual_hz(125_000_000), 399_361);
    }

    /// Fast-mode plus uses the shorter 120 ns hold.
    #[test]
    fn fast_mode_plus_uses_the_short_hold() {
        let t = scl_timing(125_000_000, 1_000_000).expect("reachable");
        assert_eq!((t.hcnt, t.lcnt), (50, 75));
        assert_eq!(t.sda_hold, 16);
    }

    /// The low phase is at least 60% of the period, which is what keeps the
    /// fast-mode minimum (1.3 µs low at 400 kHz) satisfied.
    #[test]
    fn the_low_phase_meets_the_spec_minimum() {
        let t = scl_timing(125_000_000, 400_000).unwrap();
        let low_ns = t.lcnt as u64 * 1_000_000_000 / 125_000_000;
        assert!(low_ns >= 1_300, "{low_ns} ns");
    }

    #[test]
    fn unreachable_rates_are_refused() {
        assert_eq!(scl_timing(125_000_000, 0), None);
        // Counts below 8: far beyond any I²C mode.
        assert_eq!(scl_timing(125_000_000, 10_000_000), None);
        // 16-bit counts overflow below ~1.2 kHz at 125 MHz.
        assert_eq!(scl_timing(125_000_000, 1_000), None);
    }

    #[test]
    fn abort_causes_are_named() {
        assert_eq!(
            abort_error(ABRT_7B_ADDR_NOACK),
            HalError::Bus("I2C address not acknowledged (no device?)")
        );
        assert_eq!(abort_error(ABRT_TXDATA_NOACK), HalError::Bus("I2C data byte not acknowledged"));
        assert_eq!(abort_error(ABRT_ARB_LOST), HalError::Bus("I2C arbitration lost"));
        assert_eq!(abort_error(1 << 7), HalError::Bus("I2C transfer aborted"));
    }

    #[test]
    fn data_cmd_bits() {
        assert_eq!(CMD_READ, 0x100);
        assert_eq!(CMD_STOP, 0x200);
        assert_eq!(CMD_RESTART, 0x400);
        assert_eq!(
            CON_SPEED_FAST | CON_MASTER_MODE | CON_SLAVE_DISABLE | CON_RESTART_EN | CON_TX_EMPTY_CTRL,
            0x165
        );
    }
}
