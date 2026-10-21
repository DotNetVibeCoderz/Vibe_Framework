//! A sample NPC module for microcontrollers — the same four procedures as
//! `runtime/npc-sample`, but `no_std`, so `rustnet native build --target
//! thumbv6m-none-eabi` (RP2040) or `thumbv7em-none-eabihf` (Meadow F7) turns it
//! into an RNPC module the firmware loads into RAM.
//!
//! Every procedure has the one NPC shape:
//! `extern "C" fn(args, args_len, out, out_cap) -> i32`.

#![no_std]

use core::slice;

const E_OUT_TOO_SMALL: i32 = -1;
const E_BAD_ARGS: i32 = -2;

/// # Safety
/// `args`/`out` must point to buffers of the stated lengths (the runtime
/// guarantees this for every NPC call).
unsafe fn buffers<'a>(args: *const u8, len: u32, out: *mut u8, cap: u32) -> (&'a [u8], &'a mut [u8]) {
    let a = if len == 0 { &[][..] } else { slice::from_raw_parts(args, len as usize) };
    let o = if cap == 0 { &mut [][..] } else { slice::from_raw_parts_mut(out, cap as usize) };
    (a, o)
}

/// A table in .rodata reached through an absolute address — exercises the
/// relocations the loader applies.
static CRC_SEED: u32 = 0xFFFF_FFFF;

/// Sum of little-endian u32 values -> one little-endian u32.
#[no_mangle]
pub unsafe extern "C" fn rustnet_sum_u32(args: *const u8, len: u32, out: *mut u8, cap: u32) -> i32 {
    let (a, o) = buffers(args, len, out, cap);
    if a.len() % 4 != 0 {
        return E_BAD_ARGS;
    }
    if o.len() < 4 {
        return E_OUT_TOO_SMALL;
    }
    let mut sum = 0u32;
    for c in a.chunks_exact(4) {
        sum = sum.wrapping_add(u32::from_le_bytes([c[0], c[1], c[2], c[3]]));
    }
    o[..4].copy_from_slice(&sum.to_le_bytes());
    4
}

/// The argument bytes, reversed.
#[no_mangle]
pub unsafe extern "C" fn rustnet_reverse(args: *const u8, len: u32, out: *mut u8, cap: u32) -> i32 {
    let (a, o) = buffers(args, len, out, cap);
    if o.len() < a.len() {
        return E_OUT_TOO_SMALL;
    }
    for (i, b) in a.iter().rev().enumerate() {
        o[i] = *b;
    }
    a.len() as i32
}

/// CRC-32 (IEEE) of the argument bytes -> little-endian u32.
#[no_mangle]
pub unsafe extern "C" fn rustnet_crc32(args: *const u8, len: u32, out: *mut u8, cap: u32) -> i32 {
    let (a, o) = buffers(args, len, out, cap);
    if o.len() < 4 {
        return E_OUT_TOO_SMALL;
    }
    let mut crc = core::ptr::read_volatile(&CRC_SEED);
    for &b in a {
        crc ^= b as u32;
        for _ in 0..8 {
            crc = if crc & 1 != 0 { (crc >> 1) ^ 0xEDB8_8320 } else { crc >> 1 };
        }
    }
    o[..4].copy_from_slice(&(!crc).to_le_bytes());
    4
}

/// Always fails with code -7, to exercise error propagation.
#[no_mangle]
pub unsafe extern "C" fn rustnet_fail(_args: *const u8, _len: u32, _out: *mut u8, _cap: u32) -> i32 {
    -7
}

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! {
    loop {}
}
