//! What a crash leaves behind, so the board stays reachable.
//!
//! A panic or a hard fault used to blink a code on the LED forever. On the
//! Netduino, whose only link is USB, that is a dead port — and the stored app
//! runs again on every power-up, so a crashing app could only be removed over
//! DFU. Now the handlers note *where* in a few words of RAM that the startup
//! code does not clear (`.uninit`, which a soft reset leaves alone), and reset.
//! The next boot reports the note and starts in **safe mode**: the app is not
//! run, RNDP answers, and the tools can replace it.
//!
//! A crash count stops a firmware that faults during its own start-up from
//! reboot-looping: from the third consecutive crash the handler blinks instead.
//! The count is cleared once RNDP answers a request. At power-up the RAM holds
//! noise, which the magic word and its complement filter out.

use core::mem::MaybeUninit;

#[link_section = ".uninit.CRASH"]
static mut NOTE: MaybeUninit<[u32; 5]> = MaybeUninit::uninit();

/// For an allocation failure: the size asked for, and the heap at the time.
#[link_section = ".uninit.CRASH_DETAIL"]
static mut DETAIL: MaybeUninit<[u32; 3]> = MaybeUninit::uninit();

const MAGIC: u32 = 0xC4A5_4E07;
const HARD_FAULT: u32 = 1;
const PANIC: u32 = 2;
const MAX_CRASHES: u32 = 3;

fn read() -> [u32; 5] {
    // SAFETY: plain words; any bit pattern is a valid [u32; 5].
    unsafe { core::ptr::addr_of!(NOTE).cast::<[u32; 5]>().read_volatile() }
}

fn write(v: [u32; 5]) {
    // SAFETY: single-threaded writes of plain words.
    unsafe { core::ptr::addr_of_mut!(NOTE).cast::<[u32; 5]>().write_volatile(v) }
}

/// The note's crash count, or 0 when the RAM holds no valid note.
fn count() -> u32 {
    let n = read();
    // The low byte only: the rest of the word is the panic's file tag.
    if n[0] == MAGIC && n[4] == !MAGIC {
        n[3] & 0xFF
    } else {
        0
    }
}

/// Record a crash; `true` if the caller should reset into safe mode.
pub fn record(panic: bool, a: u32, b: u32) -> bool {
    // Words: magic, kind, a (pc or line), count | b << 8 (file tag), !magic.
    let crashes = count() + 1;
    write([MAGIC, if panic { PANIC } else { HARD_FAULT }, a, crashes | (b << 8), !MAGIC]);
    crashes < MAX_CRASHES
}

/// Record what an allocation failure asked for: `requested` bytes, with
/// `used`/`free` the heap at the time. Zero `requested` = not an allocation.
pub fn record_alloc(requested: u32, used: u32, free: u32) {
    // SAFETY: plain words, single-threaded.
    unsafe { core::ptr::addr_of_mut!(DETAIL).cast::<[u32; 3]>().write_volatile([requested, used, free]) }
}

/// The size in "memory allocation of N bytes failed", without allocating.
pub fn requested_size(info: &core::panic::PanicInfo) -> u32 {
    struct Digits {
        value: u32,
        seen: bool,
        done: bool,
    }
    impl core::fmt::Write for Digits {
        fn write_str(&mut self, s: &str) -> core::fmt::Result {
            for c in s.chars() {
                if self.done {
                    break;
                }
                match c.to_digit(10) {
                    Some(d) => {
                        self.value = self.value.saturating_mul(10).saturating_add(d);
                        self.seen = true;
                    }
                    None if self.seen => self.done = true,
                    None => {}
                }
            }
            Ok(())
        }
    }
    let mut d = Digits { value: 0, seen: false, done: false };
    let _ = core::fmt::write(&mut d, format_args!("{}", info.message()));
    d.value
}

/// At boot: report a recorded crash. `true` means start in safe mode.
pub fn take_report(out: &mut impl core::fmt::Write) -> bool {
    let n = read();
    if n[0] != MAGIC || n[4] != !MAGIC {
        return false;
    }
    // Keep the count (word 3, low byte) for the loop guard; drop the rest.
    write([MAGIC, 0, 0, n[3] & 0xFF, !MAGIC]);
    match n[1] {
        HARD_FAULT => {
            let _ = writeln!(out, "[crash] the last run hard-faulted at pc={:#010x}; safe mode: app not started", n[2]);
            true
        }
        PANIC => {
            let _ = writeln!(
                out,
                "[crash] the last run panicked at line {} (file tag {:#06x}); safe mode: app not started",
                n[2],
                n[3] >> 8
            );
            // SAFETY: plain words.
            let d = unsafe { core::ptr::addr_of!(DETAIL).cast::<[u32; 3]>().read_volatile() };
            if d[0] != 0 && d[0] < 0x0100_0000 {
                let _ = writeln!(out, "[crash] it asked for {} bytes with {} in use and {} free", d[0], d[1], d[2]);
            }
            true
        }
        _ => false,
    }
}

/// The board answered the tools: whatever crashed before is behind us.
pub fn healthy() {
    if count() != 0 {
        write([0; 5]);
    }
}

/// A 24-bit tag for a source file name, so a panic location fits the note.
pub fn file_tag(file: &str) -> u32 {
    file.bytes().fold(0x811C_9DC5u32, |h, c| (h ^ c as u32).wrapping_mul(0x0100_0193)) & 0xFF_FFFF
}
