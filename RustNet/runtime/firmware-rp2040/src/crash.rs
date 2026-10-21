//! What a crash leaves behind, for a board whose only channel is USB.
//!
//! A hard fault or a panic stops the core, and with it the USB service: the
//! port stays listed on the host but nothing answers, and a stored app with
//! autostart does the same again on every power-up. So instead the handlers
//! note *where* in the watchdog's scratch registers — they survive a soft
//! reset, not a power cycle — and reset. The next boot reports the note and
//! starts in **safe mode**: the app is not started, RNDP answers, and the tools
//! can replace the app or read the log.
//!
//! Scratch 0–3 are ours; the boot ROM keeps 4–7 for its own reboot protocol.
//! A crash count in scratch 3 stops a firmware that faults during its own
//! start-up from reboot-looping: from the third consecutive crash the handler
//! stays put instead. The count is cleared once RNDP answers a request.


const WATCHDOG: usize = 0x4005_8000;
const SCRATCH0: *mut u32 = (WATCHDOG + 0x0C) as *mut u32;
const SCRATCH1: *mut u32 = (WATCHDOG + 0x10) as *mut u32;
const SCRATCH2: *mut u32 = (WATCHDOG + 0x14) as *mut u32;
const SCRATCH3: *mut u32 = (WATCHDOG + 0x18) as *mut u32;

const HARD_FAULT: u32 = 0xFA17_0001;
const PANIC: u32 = 0xFA17_0002;
/// Consecutive crashes after which the handler stops resetting.
const MAX_CRASHES: u32 = 3;

fn read(r: *mut u32) -> u32 {
    // SAFETY: a watchdog scratch register, always mapped.
    unsafe { r.read_volatile() }
}

fn write(r: *mut u32, v: u32) {
    // SAFETY: as above.
    unsafe { r.write_volatile(v) }
}

/// Record a crash; `true` if the caller should reset into safe mode.
pub fn record(kind_panic: bool, a: u32, b: u32) -> bool {
    let count = read(SCRATCH3);
    let count = if count > 0xFF { 1 } else { count + 1 };
    write(SCRATCH3, count);
    write(SCRATCH0, if kind_panic { PANIC } else { HARD_FAULT });
    write(SCRATCH1, a);
    write(SCRATCH2, b);
    count < MAX_CRASHES
}

/// At boot: report a recorded crash and clear it. `true` means start in safe
/// mode (do not autostart the app).
pub fn take_report(out: &mut impl core::fmt::Write) -> bool {
    let kind = read(SCRATCH0);
    let (a, b) = (read(SCRATCH1), read(SCRATCH2));
    write(SCRATCH0, 0);
    match kind {
        HARD_FAULT => {
            let _ = writeln!(
                out,
                "[crash] the last run hard-faulted at pc={a:#010x} (lr={b:#010x}); safe mode: app not started"
            );
            true
        }
        PANIC => {
            let _ = writeln!(out, "[crash] the last run panicked at line {a} (file tag {b:#010x}); safe mode: app not started");
            true
        }
        _ => false,
    }
}

/// The board answered the tools: whatever crashed before is behind us.
pub fn healthy() {
    write(SCRATCH3, 0);
}

/// A 32-bit tag for a source file name, so a panic location fits a register.
pub fn file_tag(file: &str) -> u32 {
    file.bytes().fold(0x811C_9DC5u32, |h, c| (h ^ c as u32).wrapping_mul(0x0100_0193))
}

#[cortex_m_rt::exception]
unsafe fn HardFault(ef: &cortex_m_rt::ExceptionFrame) -> ! {
    if record(false, ef.pc(), ef.lr()) {
        cortex_m::peripheral::SCB::sys_reset();
    }
    loop {
        cortex_m::asm::wfi();
    }
}
