//! Signal control — timed edge trains and pulse capture, on PIO.
//!
//! `RustNet.Hal.Signal` asks for microsecond timing on any pin: drive a train
//! of edges (an IR remote code, a stepper burst), record the widths of the
//! pulses arriving on a pin, or fire a trigger and time the echo (a PING)))-
//! style ultrasonic ranger). On a Cortex-M0+ at 125 MHz a software loop can
//! nearly do it, until a USB poll or a flash stall lands in the middle. A PIO
//! state machine cannot be interrupted, so every edge is placed to the cycle.
//!
//! One state machine, PIO0 SM0, does all three, with the program reloaded for
//! each call ([`crate::pio::GENERATOR`], [`crate::pio::CAPTURE`]). It runs at
//! 5 MHz — five cycles a microsecond, an exact `/25` from 125 MHz — so a
//! generated edge is exact to the microsecond and a captured width is good to
//! the capture loop's 0.4 µs.
//!
//! The CPU's part is only to keep the FIFOs moving, and that has slack: the TX
//! FIFO is joined to eight words deep, every segment is at least a
//! microsecond, and nothing on this port takes interrupts. If the CPU ever did
//! fall behind, the generator would stall *holding the current level* — a
//! stretched segment, detected and reported as an error rather than returned
//! as success.
//!
//! **Every call blocks**, as the trait specifies, and nothing else on the
//! board runs meanwhile — including the USB service. A capture with a
//! multi-second timeout is a multi-second gap in answering the host; keep
//! timeouts to what the sensor needs (an ultrasonic echo is under 40 ms).

use alloc::vec::Vec;

use rustnet_hal::signal::SignalControl;
use rustnet_hal::{HalError, HalResult};

use crate::gpio::{self, func, PIN_COUNT};
use crate::pio::{self, asm, StateMachine, PIO0_SM0};
use crate::timer;

/// Most widths one capture returns: 16 KB of heap, on a 128 KB heap.
pub const MAX_CAPTURE: usize = 4096;

/// Slack added to every deadline for the CPU's own setup, in µs.
const DEADLINE_SLACK_US: u64 = 1_000;

pub struct Rp2040Signal {
    pin: u32,
    sys_hz: u32,
    sm: StateMachine,
}

/// Spin until `done` or the deadline. Guarded by an iteration ceiling too, so
/// a timer that is not running cannot make this a hang.
fn wait_until_us(deadline: u64, mut done: impl FnMut() -> bool) -> bool {
    let mut guard = 0u32;
    loop {
        if done() {
            return true;
        }
        if timer::now_us() >= deadline {
            return false;
        }
        guard += 1;
        if guard == u32::MAX {
            return false;
        }
        core::hint::spin_loop();
    }
}

/// Widths an echo capture needs, `(starting low, starting high)`. Starting at
/// the idle level, the first full level is the echo; starting at the echo
/// level, the first is the gap after a partial echo and the second the next
/// whole one.
pub const fn echo_want(pulse_high: bool) -> (usize, usize) {
    if pulse_high {
        (1, 2)
    } else {
        (2, 1)
    }
}

/// Which captured width is the echo.
pub const fn echo_index(start_high: bool, pulse_high: bool) -> usize {
    if start_high == pulse_high {
        1
    } else {
        0
    }
}

impl Rp2040Signal {
    pub const fn new(pin: u32, sys_hz: u32) -> Self {
        Self { pin, sys_hz, sm: PIO0_SM0 }
    }

    pub fn set_pin(&mut self, pin: u32) {
        self.pin = pin;
    }

    fn prepare(&self, program: &pio::Program, shiftctrl: u32) -> HalResult<()> {
        if self.pin >= PIN_COUNT {
            return Err(HalError::InvalidArgument("RP2040 has GPIO0..=29"));
        }
        let div = pio::clkdiv(self.sys_hz)
            .ok_or(HalError::InvalidArgument("clk_sys too slow for 1 µs signal timing"))?;
        if !self.sm.release() {
            return Err(HalError::Timeout);
        }
        self.sm.set_enabled(false);
        self.sm.load(
            program,
            div,
            pio::execctrl(self.pin, program),
            shiftctrl,
            pio::pinctrl_single(self.pin),
        );
        Ok(())
    }

    /// Capture, returning the level the pin was at when it started and the
    /// widths of the complete levels after the first edge.
    ///
    /// How many widths are wanted can depend on that starting level, so it is
    /// given per level: `(if it starts low, if it starts high)`. Asking for
    /// more than needed is not free — the capture only ends early on the
    /// count, otherwise it waits out a whole edge-free `timeout_us`.
    fn capture_levels(&mut self, want: (usize, usize), timeout_us: u32) -> HalResult<(bool, Vec<u32>)> {
        let mut widths = Vec::new();
        widths
            .try_reserve_exact(want.0.max(want.1).min(MAX_CAPTURE))
            .map_err(|_| HalError::InvalidArgument("not enough RAM for that many edges"))?;

        self.prepare(
            &pio::CAPTURE,
            pio::SHIFT_FJOIN_RX | pio::SHIFT_AUTOPUSH | pio::SHIFT_IN_RIGHT | pio::SHIFT_OUT_RIGHT,
        )?;
        // Release the line: the pin is PIO's, with its output disabled, so
        // nothing on this side drives it while it is measured. The pad's pulls
        // are left as the application set them.
        self.sm.exec(asm::set(asm::SetDst::PinDirs, 0));
        gpio::set_function(self.pin, func::PIO0);

        let start_high = gpio::level(self.pin);
        let max = if start_high { want.1 } else { want.0 }.min(MAX_CAPTURE);
        // If the pin changes between this read and the enable, the first count
        // is just short — it is discarded either way.
        let entry = if start_high { pio::CAPTURE_HIGH_ENTRY } else { pio::CAPTURE_LOW_ENTRY };
        self.sm.exec(asm::jmp(asm::JmpCond::Always, entry));
        self.sm.set_enabled(true);

        let window = timeout_us as u64;
        // The first value is the partial level before the first edge: proof
        // that an edge came, nothing more.
        let first = wait_until_us(timer::now_us() + window, || !self.sm.rx_empty());
        if first {
            let _ = self.sm.pop();
            let mut high = !start_high;
            while widths.len() < max {
                // An edge-free stretch longer than the timeout ends the
                // capture, the way an idle line ends an RMT frame on ESP32.
                if !wait_until_us(timer::now_us() + window, || !self.sm.rx_empty()) {
                    break;
                }
                widths.push(pio::capture_width_us(self.sm.pop(), high));
                high = !high;
            }
        }
        self.sm.set_enabled(false);
        Ok((start_high, widths))
    }
}

impl SignalControl for Rp2040Signal {
    fn generate(&mut self, initial_high: bool, timings_us: &[u32]) -> HalResult<()> {
        // Every word worked out before anything is touched, so a bad timing
        // is refused with the pin unchanged.
        let mut words = Vec::new();
        words
            .try_reserve_exact(timings_us.len() + 1)
            .map_err(|_| HalError::InvalidArgument("not enough RAM for that many edges"))?;
        let mut level = initial_high;
        let mut total_us = 0u64;
        for &us in timings_us {
            words.push(pio::generator_word(level, us).ok_or(HalError::InvalidArgument(
                "signal timings must be 1 µs ..= 429 s",
            ))?);
            total_us += us as u64;
            level = !level;
        }
        // The trait's contract: the pin toggles after every duration, so it
        // ends on the opposite of the last segment's level and stays there.
        words.push(pio::generator_final_word(level));

        self.prepare(&pio::GENERATOR, pio::SHIFT_FJOIN_TX | pio::SHIFT_OUT_RIGHT | pio::SHIFT_IN_RIGHT)?;
        // Level first, then direction, then hand the pin over — so the pin
        // goes straight to its initial level with no glitch through the other.
        self.sm.exec(asm::set(asm::SetDst::Pins, initial_high as u8));
        self.sm.exec(asm::set(asm::SetDst::PinDirs, 1));
        self.sm.exec(asm::jmp(asm::JmpCond::Always, 0));
        gpio::set_function(self.pin, func::PIO0);

        let mut next = 0;
        while next < words.len() && !self.sm.tx_full() {
            self.sm.push(words[next]);
            next += 1;
        }
        let deadline = timer::now_us() + total_us + DEADLINE_SLACK_US;
        self.sm.set_enabled(true);

        while next < words.len() {
            if !wait_until_us(deadline, || !self.sm.tx_full()) {
                self.sm.set_enabled(false);
                return Err(HalError::Timeout);
            }
            // A stall before the last word means the machine ran dry and held
            // a level longer than asked. Say so rather than return success.
            if self.sm.tx_stalled() {
                self.sm.set_enabled(false);
                return Err(HalError::Bus("signal generator underrun"));
            }
            self.sm.push(words[next]);
            next += 1;
        }
        // Done when the last word has been pulled and the machine has stalled
        // asking for another.
        let finished = wait_until_us(deadline, || self.sm.tx_empty() && self.sm.tx_stalled());
        // Disabling leaves the PIO's output latch, and so the pin, where it is.
        self.sm.set_enabled(false);
        if finished {
            Ok(())
        } else {
            Err(HalError::Timeout)
        }
    }

    fn capture(&mut self, max_edges: usize, timeout_us: u32) -> HalResult<Vec<u32>> {
        if max_edges == 0 {
            return Ok(Vec::new());
        }
        self.capture_levels((max_edges, max_edges), timeout_us).map(|(_, widths)| widths)
    }

    /// Drive the trigger, release the line, and time the first complete pulse
    /// at the trigger's polarity.
    ///
    /// The line is pulled toward its idle level while released, so a sensor
    /// that has not started its echo yet does not read as noise. If the line
    /// is already at the echo level when the capture starts (the echo began
    /// during the hand-over), that partial pulse is skipped and the next full
    /// one is timed.
    fn pulse_feedback(&mut self, pulse_high: bool, pulse_us: u32, timeout_us: u32) -> HalResult<u32> {
        self.generate(pulse_high, &[pulse_us.max(1)])?;
        gpio::set_pulls(self.pin, !pulse_high, pulse_high);
        let (start_high, widths) = self.capture_levels(echo_want(pulse_high), timeout_us)?;
        Ok(widths.get(echo_index(start_high, pulse_high)).copied().unwrap_or(0))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Width `i` of a capture that started at `start_high` is at level
    /// `!start_high` for even `i` — the first full level is after the first
    /// edge. The chosen echo must be at the trigger's level and inside the
    /// count asked for, for every polarity and starting level.
    #[test]
    fn the_echo_is_the_first_whole_pulse_at_the_trigger_level() {
        for pulse_high in [false, true] {
            for start_high in [false, true] {
                let i = echo_index(start_high, pulse_high);
                let level = if i % 2 == 0 { !start_high } else { start_high };
                assert_eq!(level, pulse_high, "pulse_high={pulse_high} start_high={start_high}");
                let want = echo_want(pulse_high);
                let wanted = if start_high { want.1 } else { want.0 };
                assert_eq!(i + 1, wanted, "asks for exactly the widths it uses");
            }
        }
    }
}
