use std::env;
use std::fs;
use std::path::PathBuf;

fn main() {
    // cortex-m-rt's link.x pulls MEMORY from a memory.x on the link search
    // path. Which one depends on the board feature — the parts differ in both
    // flash and RAM size, and getting this wrong fails silently at runtime
    // rather than at link time.
    let netduino = env::var_os("CARGO_FEATURE_BOARD_NETDUINO3_WIFI").is_some();
    let nucleo = env::var_os("CARGO_FEATURE_BOARD_NUCLEO_F401RE").is_some();

    let source = match (nucleo, netduino) {
        (true, false) => "memory-f401re.x",
        (false, true) => "memory-f427vi.x",
        (true, true) => panic!("select exactly one board feature, not both"),
        (false, false) => panic!("select a board feature (board-nucleo-f401re or board-netduino3-wifi)"),
    };

    let out = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    fs::write(out.join("memory.x"), fs::read(source).unwrap()).unwrap();

    println!("cargo:rustc-link-search={}", out.display());
    println!("cargo:rerun-if-changed=memory-f401re.x");
    println!("cargo:rerun-if-changed=memory-f427vi.x");
}
