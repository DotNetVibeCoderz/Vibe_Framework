use std::env;
use std::fs;
use std::path::PathBuf;

fn main() {
    // riscv-rt's link.x pulls MEMORY and the REGION_ALIAS definitions from a
    // memory.x on the link search path. There is one board so far, but the
    // selection goes through a feature from the start: the alternative is a
    // second board silently inheriting the first one's memory map, which fails
    // at runtime rather than at link time.
    let maix_go = env::var_os("CARGO_FEATURE_BOARD_MAIX_GO").is_some();

    let source = if maix_go {
        "memory-maixgo.x"
    } else {
        panic!("select a board feature (board-maix-go)")
    };

    let out = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    fs::write(out.join("memory.x"), fs::read(source).unwrap()).unwrap();

    println!("cargo:rustc-link-search={}", out.display());
    println!("cargo:rerun-if-changed=memory-maixgo.x");
}
