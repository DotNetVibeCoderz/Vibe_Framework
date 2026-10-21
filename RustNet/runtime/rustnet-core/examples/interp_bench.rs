//! Interpreter throughput micro-benchmark: a counting loop of plain IL,
//! no host calls. Usage: cargo run --release -p rustnet-core --example interp_bench
use rustnet_core::{opcodes as op, Builder, Interpreter, RunExit, TestHost};

fn main() {
    let n: i32 = 5_000_000;
    let mut b = Builder::new();
    // i = 0; s = 0; while (i < n) { s = s + (i & 7); i = i + 1; }
    let mut code = vec![0x16, 0x0A, 0x16, 0x0B]; // i=0 (loc0), s=0 (loc1)
    code.extend([op::BR_S, 10]);
    let body = code.len();
    code.extend([0x07, 0x06, 0x1D, 0x5F, 0x58, 0x0B]); // s = s + (i & 7)
    code.extend([0x06, 0x17, 0x58, 0x0A]); // i = i + 1
    code.extend([0x06, 0x20]);
    code.extend(n.to_le_bytes()); // ldc.i4 n
    let here = code.len() + 2;
    code.extend([op::BLT_S, (body as isize - here as isize) as i8 as u8]);
    code.push(op::RET);
    let main = b.add_method("Bench::Main()", None, rustnet_core::MFLAG_STATIC, 0, 2, code);
    b.set_entry(main);
    let module = b.build();
    let mut interp = Interpreter::new(&module, TestHost::default());
    let t = std::time::Instant::now();
    let exit = interp.run_to_completion();
    let s = t.elapsed().as_secs_f64();
    assert_eq!(exit, RunExit::Completed);
    println!("{} instructions in {:.3} s = {:.1} M instr/s ({:.1} ns/instr)",
        interp.instructions, s, interp.instructions as f64 / s / 1e6, s * 1e9 / interp.instructions as f64);
}
