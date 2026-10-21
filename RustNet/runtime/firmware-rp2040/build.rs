//! Write the linker's `memory.x` for the slot this build is for.
//!
//! The firmware no longer starts at the beginning of flash: the RustNet slot
//! picker (`boot/`) sits there behind the ROM's second stage and jumps into
//! slot A or slot B (`rustnet_hal_rp2040::ota` holds the layout). An image is
//! linked for one slot's address, so an OTA update carries two builds — the
//! default one for slot A and `--features slot-b` for slot B — and the device
//! keeps the one for the slot it is not running from (`tools/make_ota.py`).

use std::env;
use std::fs;
use std::path::PathBuf;

// Must match rustnet-hal-rp2040/src/ota.rs (SLOT_A, SLOT_B, SLOT_LEN).
const SLOT_A: u32 = 0x1000_4000;
const SLOT_B: u32 = 0x1008_0000;
const SLOT_LEN: u32 = 0x0007_C000;

fn main() {
    let origin = if env::var_os("CARGO_FEATURE_SLOT_B").is_some() { SLOT_B } else { SLOT_A };
    let memory = format!(
        "MEMORY {{\n    FLASH : ORIGIN = {origin:#010x}, LENGTH = {SLOT_LEN:#x}\n    RAM   : ORIGIN = 0x20000000, LENGTH = 256K\n}}\n"
    );
    let out = PathBuf::from(env::var("OUT_DIR").unwrap());
    fs::write(out.join("memory.x"), memory).unwrap();
    println!("cargo:rustc-link-search={}", out.display());
    println!("cargo:rerun-if-changed=build.rs");
}
