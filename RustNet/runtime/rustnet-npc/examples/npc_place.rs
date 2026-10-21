//! Place an RNPC module at a given address exactly as firmware would, and
//! write the loaded bytes plus its export table — for running a module under
//! an emulator before it ever reaches a board.
//! Usage: cargo run -p rustnet-npc --example npc_place -- module.rnpc 0x20001000 out.bin [v6m|v7em]
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let blob = std::fs::read(&a[1]).expect("read module");
    let base = u32::from_str_radix(a[2].trim_start_matches("0x"), 16).expect("hex base");
    let arch = if a.get(4).map(|s| s.as_str()) == Some("v7em") {
        rustnet_npc::Arch::ThumbV7EM
    } else {
        rustnet_npc::Arch::ThumbV6M
    };
    let m = rustnet_npc::Module::parse(&blob, arch).expect("parse");
    let mut ram = vec![0u8; m.load_size()];
    m.load_into(&mut ram, base).expect("load");
    std::fs::write(&a[3], &ram).expect("write");
    for (name, value) in &m.exports {
        println!("{name} {value}");
    }
}
