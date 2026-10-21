//! PIO — the RP2040's programmable I/O, hand-assembled.
//!
//! Each PIO block has four state machines sharing 32 words of instruction
//! memory. A state machine runs one 16-bit instruction per (divided) clock,
//! exactly, which is what makes it the right place for microsecond signal
//! timing: no interrupt latency, no bus contention, no cache.
//!
//! There is no `pioasm` in this build, so the programs are assembled by the
//! `const fn`s in [`asm`] and checked in the tests against the words
//! `pioasm` emits for the same source. Every program here is loaded at offset
//! 0 and the jump targets are absolute; a program is reloaded before each use,
//! which costs a dozen register writes and removes any question of what is in
//! instruction memory.
//!
//! ## Instruction format (datasheet 3.4)
//!
//! ```text
//!  15..13  12..8            7..0
//!  opcode  delay/side-set   operands
//! ```
//!
//! Nothing here uses side-set, so bits 12..8 are pure delay.

use crate::{reg, resets};

/// The PIO instruction encoder. One function per opcode; arguments are
/// enums so an operand cannot land in the wrong field.
pub mod asm {
    #[derive(Clone, Copy)]
    pub enum JmpCond {
        Always = 0,
        /// `!x`: jump if X is zero.
        XZero = 1,
        /// `x--`: jump if X is non-zero, decrementing it either way.
        XDec = 2,
        YZero = 3,
        YDec = 4,
        XNeY = 5,
        /// `pin`: jump if the `EXECCTRL.JMP_PIN` input is high.
        Pin = 6,
        /// `!osre`: jump if the output shift register is not empty.
        NotOsre = 7,
    }

    #[derive(Clone, Copy)]
    pub enum InSrc {
        Pins = 0,
        X = 1,
        Y = 2,
        Null = 3,
        Isr = 6,
        Osr = 7,
    }

    #[derive(Clone, Copy)]
    pub enum OutDst {
        Pins = 0,
        X = 1,
        Y = 2,
        Null = 3,
        PinDirs = 4,
        Pc = 5,
        Isr = 6,
        Exec = 7,
    }

    #[derive(Clone, Copy)]
    pub enum MovDst {
        Pins = 0,
        X = 1,
        Y = 2,
        Exec = 4,
        Pc = 5,
        Isr = 6,
        Osr = 7,
    }

    #[derive(Clone, Copy)]
    pub enum MovOp {
        None = 0,
        Invert = 1,
        Reverse = 2,
    }

    #[derive(Clone, Copy)]
    pub enum MovSrc {
        Pins = 0,
        X = 1,
        Y = 2,
        Null = 3,
        Status = 5,
        Isr = 6,
        Osr = 7,
    }

    #[derive(Clone, Copy)]
    pub enum SetDst {
        Pins = 0,
        X = 1,
        Y = 2,
        PinDirs = 4,
    }

    /// `jmp cond, addr` — opcode 000.
    pub const fn jmp(cond: JmpCond, addr: u8) -> u16 {
        ((cond as u16) << 5) | (addr as u16 & 0x1F)
    }

    /// `in src, bits` — opcode 010. A count of 32 is encoded as 0.
    pub const fn in_(src: InSrc, bits: u8) -> u16 {
        0x4000 | ((src as u16) << 5) | (bits as u16 & 0x1F)
    }

    /// `out dst, bits` — opcode 011. A count of 32 is encoded as 0.
    pub const fn out(dst: OutDst, bits: u8) -> u16 {
        0x6000 | ((dst as u16) << 5) | (bits as u16 & 0x1F)
    }

    /// `push [iffull] [block|noblock]` — opcode 100, bit 7 clear.
    pub const fn push(if_full: bool, block: bool) -> u16 {
        0x8000 | ((if_full as u16) << 6) | ((block as u16) << 5)
    }

    /// `pull [ifempty] [block|noblock]` — opcode 100, bit 7 set.
    pub const fn pull(if_empty: bool, block: bool) -> u16 {
        0x8080 | ((if_empty as u16) << 6) | ((block as u16) << 5)
    }

    /// `mov dst, op src` — opcode 101.
    pub const fn mov(dst: MovDst, op: MovOp, src: MovSrc) -> u16 {
        0xA000 | ((dst as u16) << 5) | ((op as u16) << 3) | (src as u16)
    }

    /// `set dst, value` — opcode 111; value is 5 bits.
    pub const fn set(dst: SetDst, value: u8) -> u16 {
        0xE000 | ((dst as u16) << 5) | (value as u16 & 0x1F)
    }

    /// `instr [cycles]`: extra idle cycles after the instruction (0..=31 with
    /// no side-set).
    pub const fn delay(instr: u16, cycles: u8) -> u16 {
        instr | ((cycles as u16 & 0x1F) << 8)
    }
}

use asm::*;

/// A program, its wrap range, and nothing else: all three here start at 0.
pub struct Program {
    pub code: &'static [u16],
    pub wrap_bottom: u8,
    pub wrap_top: u8,
}

/// Drive a timed edge train from the TX FIFO.
///
/// Each FIFO word is `count << 1 | level`: the level goes to the pin, then the
/// state machine waits `count + 1` cycles. From one `out pins` to the next is
/// `count + 4` cycles (out, out, `count + 1` jumps, pull), so a segment of
/// `d` cycles is written as `count = d - 4`.
///
/// ```text
/// 0: pull block        ; OSR <- next segment (stalls when the train is done)
/// 1: out pins, 1       ; the level, from bit 0
/// 2: out x, 31         ; the count, from bits 31..1
/// 3: jmp x--, 3        ; spin count+1 cycles; then wrap to 0
/// ```
pub const GENERATOR: Program = Program {
    code: &[
        pull(false, true),
        out(OutDst::Pins, 1),
        out(OutDst::X, 31),
        jmp(JmpCond::XDec, 3),
    ],
    wrap_bottom: 0,
    wrap_top: 3,
};

/// Fixed cycles per generator segment beyond its count.
pub const GENERATOR_OVERHEAD: u32 = 4;

/// Measure how long the `JMP_PIN` input stays at each level.
///
/// Two counting loops, one per level, each two cycles per count. At the end of
/// a level the state machine pushes the count (as `!x`, since X counts down
/// from all-ones) and starts counting the other level. Enter at
/// [`CAPTURE_LOW_ENTRY`] if the pin is low now, [`CAPTURE_HIGH_ENTRY`] if
/// high.
///
/// ```text
/// 0: mov x, ~null      ; low level: X = 0xFFFF_FFFF
/// 1: jmp pin, 3        ; pin went high -> report
/// 2: jmp x--, 1        ; still low: count (2 cycles per count)
/// 3: in x, 32          ; autopush the low count
/// 4: mov x, ~null      ; high level: X = 0xFFFF_FFFF
/// 5: jmp pin, 7        ; still high -> count
/// 6: jmp 8             ; went low -> report
/// 7: jmp x--, 5        ; count (2 cycles per count)
/// 8: in x, 32          ; autopush the high count; wrap to 0
/// ```
///
/// Edge to edge, a high level of `c` counts lasts `2c + 3` cycles and a low
/// level `2c + 4` (see [`capture_width_cycles`]); the 2-cycle loop is the
/// resolution, 0.4 µs at the 5 MHz this port runs it at.
pub const CAPTURE: Program = Program {
    code: &[
        mov(MovDst::X, MovOp::Invert, MovSrc::Null),
        jmp(JmpCond::Pin, 3),
        jmp(JmpCond::XDec, 1),
        in_(InSrc::X, 32),
        mov(MovDst::X, MovOp::Invert, MovSrc::Null),
        jmp(JmpCond::Pin, 7),
        jmp(JmpCond::Always, 8),
        jmp(JmpCond::XDec, 5),
        in_(InSrc::X, 32),
    ],
    wrap_bottom: 0,
    wrap_top: 8,
};

pub const CAPTURE_LOW_ENTRY: u8 = 0;
pub const CAPTURE_HIGH_ENTRY: u8 = 4;

/// The state machine clock for signal work: 5 MHz, five cycles a microsecond.
/// Exactly `clk_sys / 25` at 125 MHz, so no fractional-divider jitter.
pub const SM_HZ: u32 = 5_000_000;
pub const CYCLES_PER_US: u32 = SM_HZ / 1_000_000;

/// The `SMx_CLKDIV` word for [`SM_HZ`]: `INT` at 31:16, `FRAC` (256ths) at
/// 15:8. `None` when `clk_sys` is below the target or the divider would
/// overflow.
pub fn clkdiv(sys_hz: u32) -> Option<u32> {
    let div256 = (sys_hz as u64 * 256 + SM_HZ as u64 / 2) / SM_HZ as u64;
    let int = div256 >> 8;
    if int == 0 || int > 0xFFFF {
        return None;
    }
    Some(((int as u32) << 16) | (((div256 & 0xFF) as u32) << 8))
}

/// The generator FIFO word for one segment of `us` microseconds at `level`.
/// `None` for a segment too short to produce (under 1 µs) or too long for
/// the 31-bit count.
pub fn generator_word(level: bool, us: u32) -> Option<u32> {
    let cycles = us as u64 * CYCLES_PER_US as u64;
    if cycles < GENERATOR_OVERHEAD as u64 {
        return None;
    }
    let count = cycles - GENERATOR_OVERHEAD as u64;
    if count >= 1 << 31 {
        return None;
    }
    Some(((count as u32) << 1) | level as u32)
}

/// The word that ends a train: settle at `level` and stall on the next pull.
pub const fn generator_final_word(level: bool) -> u32 {
    level as u32
}

/// Cycles a captured level lasted, edge to edge, from the pushed value.
pub fn capture_width_cycles(raw: u32, high: bool) -> u64 {
    let count = !raw as u64;
    2 * count + if high { 3 } else { 4 }
}

/// The same, in microseconds, rounded to the nearest.
pub fn capture_width_us(raw: u32, high: bool) -> u32 {
    let cycles = capture_width_cycles(raw, high);
    ((cycles + CYCLES_PER_US as u64 / 2) / CYCLES_PER_US as u64).min(u32::MAX as u64) as u32
}

// --- registers ------------------------------------------------------------

const CTRL: usize = 0x000;
const FSTAT: usize = 0x004;
const FDEBUG: usize = 0x008;
const TXF0: usize = 0x010;
const RXF0: usize = 0x020;
const INSTR_MEM0: usize = 0x048;
const SM0_CLKDIV: usize = 0x0C8;
const SM0_EXECCTRL: usize = 0x0CC;
const SM0_SHIFTCTRL: usize = 0x0D0;
const SM0_INSTR: usize = 0x0D8;
const SM0_PINCTRL: usize = 0x0DC;
const SM_STRIDE: usize = 0x18;

/// `SHIFTCTRL` fields.
pub const SHIFT_FJOIN_RX: u32 = 1 << 31;
pub const SHIFT_FJOIN_TX: u32 = 1 << 30;
pub const SHIFT_OUT_RIGHT: u32 = 1 << 19;
pub const SHIFT_IN_RIGHT: u32 = 1 << 18;
pub const SHIFT_AUTOPUSH: u32 = 1 << 16;

/// `EXECCTRL` for a program: `JMP_PIN` at 28:24, `WRAP_TOP` 16:12,
/// `WRAP_BOTTOM` 11:7.
pub const fn execctrl(jmp_pin: u32, program: &Program) -> u32 {
    ((jmp_pin & 0x1F) << 24) | ((program.wrap_top as u32 & 0x1F) << 12)
        | ((program.wrap_bottom as u32 & 0x1F) << 7)
}

/// `PINCTRL` with `pin` as the single OUT, SET and IN pin: `SET_COUNT` 28:26,
/// `OUT_COUNT` 25:20, `IN_BASE` 19:15, `SET_BASE` 9:5, `OUT_BASE` 4:0.
pub const fn pinctrl_single(pin: u32) -> u32 {
    let pin = pin & 0x1F;
    (1 << 26) | (1 << 20) | (pin << 15) | (pin << 5) | pin
}

/// One state machine of one PIO block.
#[derive(Clone, Copy)]
pub struct StateMachine {
    pub base: usize,
    pub reset: u32,
    pub sm: u32,
}

pub const PIO0_SM0: StateMachine = StateMachine { base: crate::base::PIO0, reset: resets::PIO0, sm: 0 };

impl StateMachine {
    fn sm_reg(&self, sm0_offset: usize) -> usize {
        self.base + sm0_offset + self.sm as usize * SM_STRIDE
    }

    pub fn release(&self) -> bool {
        resets::is_released(self.reset) || resets::unreset(self.reset)
    }

    pub fn set_enabled(&self, on: bool) {
        let bit = 1 << self.sm;
        if on {
            reg::set_bits(self.base + CTRL, bit);
        } else {
            reg::clear_bits(self.base + CTRL, bit);
        }
    }

    /// Load `program` at offset 0 and configure the machine for it. The
    /// machine must be disabled.
    pub fn load(&self, program: &Program, clkdiv: u32, execctrl: u32, shiftctrl: u32, pinctrl: u32) {
        for (i, word) in program.code.iter().enumerate() {
            reg::write(self.base + INSTR_MEM0 + 4 * i, *word as u32);
        }
        reg::write(self.sm_reg(SM0_CLKDIV), clkdiv);
        reg::write(self.sm_reg(SM0_EXECCTRL), execctrl);
        reg::write(self.sm_reg(SM0_SHIFTCTRL), shiftctrl);
        reg::write(self.sm_reg(SM0_PINCTRL), pinctrl);
        self.clear_fifos();
        // Restart the machine's internal state and its clock divider phase:
        // both are self-clearing strobes in CTRL (bits 7:4 and 11:8).
        reg::set_bits(self.base + CTRL, (1 << (4 + self.sm)) | (1 << (8 + self.sm)));
        // Clear this machine's sticky debug flags (write-one-to-clear).
        reg::write(self.base + FDEBUG, self.fdebug_bits());
    }

    /// Flipping `FJOIN_RX` flushes both FIFOs; flipping it back restores the
    /// configuration. The SDK's `pio_sm_clear_fifos`, for the same reason:
    /// there is no other way to empty a TX FIFO.
    fn clear_fifos(&self) {
        let shift = self.sm_reg(SM0_SHIFTCTRL);
        reg::toggle_bits(shift, SHIFT_FJOIN_RX);
        reg::toggle_bits(shift, SHIFT_FJOIN_RX);
    }

    fn fdebug_bits(&self) -> u32 {
        // RXSTALL 3:0, RXUNDER 11:8, TXOVER 19:16, TXSTALL 27:24.
        (0x0101_0101) << self.sm
    }

    /// Run one instruction now, whether or not the machine is enabled.
    pub fn exec(&self, instr: u16) {
        reg::write(self.sm_reg(SM0_INSTR), instr as u32);
    }

    pub fn tx_full(&self) -> bool {
        reg::read(self.base + FSTAT) & (1 << (16 + self.sm)) != 0
    }

    pub fn tx_empty(&self) -> bool {
        reg::read(self.base + FSTAT) & (1 << (24 + self.sm)) != 0
    }

    pub fn rx_empty(&self) -> bool {
        reg::read(self.base + FSTAT) & (1 << (8 + self.sm)) != 0
    }

    /// The machine has stalled on a `pull` with nothing to pull.
    pub fn tx_stalled(&self) -> bool {
        reg::read(self.base + FDEBUG) & (1 << (24 + self.sm)) != 0
    }

    pub fn push(&self, word: u32) {
        reg::write(self.base + TXF0 + 4 * self.sm as usize, word);
    }

    pub fn pop(&self) -> u32 {
        reg::read(self.base + RXF0 + 4 * self.sm as usize)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The encoder against `pioasm`'s output for the same source lines.
    #[test]
    fn instructions_encode_as_pioasm_does() {
        assert_eq!(pull(false, true), 0x80A0); //            pull block
        assert_eq!(pull(false, false), 0x8080); //           pull noblock
        assert_eq!(push(false, true), 0x8020); //            push block
        assert_eq!(push(true, false), 0x8040); //            push iffull noblock
        assert_eq!(out(OutDst::Pins, 1), 0x6001); //         out pins, 1
        assert_eq!(out(OutDst::X, 31), 0x603F); //           out x, 31
        assert_eq!(out(OutDst::X, 32), 0x6020); //           out x, 32
        assert_eq!(jmp(JmpCond::XDec, 3), 0x0043); //        jmp x--, 3
        assert_eq!(jmp(JmpCond::Pin, 7), 0x00C7); //         jmp pin, 7
        assert_eq!(jmp(JmpCond::Always, 8), 0x0008); //      jmp 8
        assert_eq!(jmp(JmpCond::NotOsre, 0), 0x00E0); //     jmp !osre, 0
        assert_eq!(in_(InSrc::X, 32), 0x4020); //            in x, 32
        assert_eq!(in_(InSrc::Pins, 1), 0x4001); //          in pins, 1
        assert_eq!(mov(MovDst::X, MovOp::Invert, MovSrc::Null), 0xA02B); // mov x, ~null
        assert_eq!(mov(MovDst::Isr, MovOp::None, MovSrc::X), 0xA0C1); //    mov isr, x
        assert_eq!(set(SetDst::PinDirs, 1), 0xE081); //      set pindirs, 1
        assert_eq!(set(SetDst::PinDirs, 0), 0xE080); //      set pindirs, 0
        assert_eq!(set(SetDst::Pins, 1), 0xE001); //         set pins, 1
        assert_eq!(set(SetDst::X, 31), 0xE03F); //           set x, 31
        assert_eq!(delay(set(SetDst::Pins, 1), 31), 0xFF01); // set pins, 1 [31]
    }

    #[test]
    fn the_programs_assemble_to_the_expected_words() {
        assert_eq!(GENERATOR.code, &[0x80A0, 0x6001, 0x603F, 0x0043]);
        assert_eq!(
            CAPTURE.code,
            &[0xA02B, 0x00C3, 0x0041, 0x4020, 0xA02B, 0x00C7, 0x0008, 0x0045, 0x4020]
        );
        // Every jump lands inside its program.
        for p in [&GENERATOR, &CAPTURE] {
            assert!(p.code.len() <= 32);
            assert_eq!(p.wrap_top as usize, p.code.len() - 1);
            for w in p.code {
                if w >> 13 == 0 {
                    assert!(((w & 0x1F) as usize) < p.code.len(), "jump out of range: {w:#06x}");
                }
            }
        }
    }

    /// 125 MHz / 25 = 5 MHz, integer, no fractional jitter.
    #[test]
    fn the_divider_gives_five_cycles_a_microsecond() {
        assert_eq!(clkdiv(125_000_000), Some(25 << 16));
        // 133 MHz: 26.6 — the fraction is in 256ths at bits 15:8.
        assert_eq!(clkdiv(133_000_000), Some((26 << 16) | (154 << 8)));
        assert_eq!(clkdiv(1_000_000), None);
        assert_eq!(CYCLES_PER_US, 5);
    }

    #[test]
    fn generator_words_pack_level_and_count() {
        // 1 µs is 5 cycles: count 1.
        assert_eq!(generator_word(true, 1), Some((1 << 1) | 1));
        // 100 µs low: 500 cycles, count 496.
        assert_eq!(generator_word(false, 100), Some(496 << 1));
        assert_eq!(generator_word(true, 0), None);
        // The 31-bit count holds about 429 seconds.
        assert!(generator_word(true, 429_000_000).is_some());
        assert_eq!(generator_word(true, u32::MAX), None);
        assert_eq!(generator_final_word(true), 1);
    }

    /// The segment length the program really produces, from its own cycle
    /// count: out, out, count+1 jumps, pull.
    #[test]
    fn a_generated_segment_lasts_exactly_its_duration() {
        for us in [1u32, 2, 10, 560, 1_000, 65_535] {
            let word = generator_word(false, us).unwrap();
            let count = word >> 1;
            let cycles = 1 + 1 + (count + 1) + 1;
            assert_eq!(cycles, us * CYCLES_PER_US, "{us} µs");
        }
    }

    #[test]
    fn captured_counts_convert_to_microseconds() {
        // No counts at all: the shortest high level is 3 cycles, 0.6 µs -> 1.
        assert_eq!(capture_width_cycles(!0, true), 3);
        assert_eq!(capture_width_cycles(!0, false), 4);
        // 1 ms high is 5000 cycles: (5000 - 3) / 2 = 2498 counts, within one
        // loop of the truth.
        assert_eq!(capture_width_us(!2498, true), 1000);
        assert_eq!(capture_width_us(!2498, false), 1000);
        // 560 µs low (an NEC IR bit): 2800 cycles = 2 * 1398 + 4.
        assert_eq!(capture_width_us(!1398, false), 560);
    }

    #[test]
    fn control_words_pack_their_fields() {
        assert_eq!(execctrl(14, &CAPTURE), (14 << 24) | (8 << 12));
        assert_eq!(execctrl(0, &GENERATOR), 3 << 12);
        assert_eq!(pinctrl_single(25), (1 << 26) | (1 << 20) | (25 << 15) | (25 << 5) | 25);
    }

    #[test]
    fn state_machine_registers() {
        assert_eq!(PIO0_SM0.base + INSTR_MEM0, 0x5020_0048);
        assert_eq!(PIO0_SM0.sm_reg(SM0_CLKDIV), 0x5020_00C8);
        assert_eq!(PIO0_SM0.sm_reg(SM0_PINCTRL), 0x5020_00DC);
        let sm1 = StateMachine { sm: 1, ..PIO0_SM0 };
        assert_eq!(sm1.sm_reg(SM0_CLKDIV), 0x5020_00E0);
        assert_eq!(sm1.fdebug_bits(), 0x0202_0202);
        assert_eq!(PIO0_SM0.reset, 1 << 10);
    }
}
