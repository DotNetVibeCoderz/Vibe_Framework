//! Measure the peak heap an .rnx needs, to size a bare-metal target's
//! allocator before flashing it (dev utility).
//!
//! Usage: cargo run -p rustnet-core --example heap_probe -- app.rnx [fuel]
//!
//! Reports the high-water mark separately for loading the module, building the
//! interpreter, and running it — so an image that will not fit on a device
//! says so here, where the answer costs seconds instead of a flash cycle.

use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicUsize, Ordering};

use rustnet_core::{HostValue, Interpreter, Module, RuntimeHost};

static LIVE: AtomicUsize = AtomicUsize::new(0);
static PEAK: AtomicUsize = AtomicUsize::new(0);

struct Counting;

unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, l: Layout) -> *mut u8 {
        let p = unsafe { System.alloc(l) };
        if !p.is_null() {
            let now = LIVE.fetch_add(l.size(), Ordering::Relaxed) + l.size();
            PEAK.fetch_max(now, Ordering::Relaxed);
        }
        p
    }
    unsafe fn dealloc(&self, p: *mut u8, l: Layout) {
        LIVE.fetch_sub(l.size(), Ordering::Relaxed);
        unsafe { System.dealloc(p, l) }
    }
}

#[global_allocator]
static ALLOC: Counting = Counting;

fn live_kb() -> f64 {
    LIVE.load(Ordering::Relaxed) as f64 / 1024.0
}
fn peak_kb() -> f64 {
    PEAK.load(Ordering::Relaxed) as f64 / 1024.0
}

/// Minimal host: enough to keep an embedded-style app running, nothing more.
#[derive(Default)]
struct ProbeHost {
    clock: u64,
}

impl RuntimeHost for ProbeHost {
    fn console_write(&mut self, _text: &str) {}
    fn now_ms(&mut self) -> u64 {
        self.clock
    }
    fn sleep_ms(&mut self, ms: u64) {
        // Advance rather than really sleeping, so a blink loop makes progress.
        self.clock += ms;
    }
    fn invoke(&mut self, name: &str, _args: Vec<HostValue>) -> Result<HostValue, String> {
        match name {
            n if n.ends_with("()") && n.contains("UserLed") => Ok(HostValue::I32(10)),
            n if n.starts_with("RustNet.Hal.Gpio::Read") => Ok(HostValue::Bool(false)),
            n if n.starts_with("RustNet.Hal.") => Ok(HostValue::Void),
            other => Err(format!("unsupported internal call: {other}")),
        }
    }
}

fn main() {
    let path = std::env::args().nth(1).expect("usage: heap_probe <file.rnx> [fuel]");
    let fuel: u64 = std::env::args().nth(2).and_then(|s| s.parse().ok()).unwrap_or(20_000_000);

    let bytes = std::fs::read(&path).expect("cannot read file");
    println!("module file           {:>10.1} KB", bytes.len() as f64 / 1024.0);

    let module = Module::from_bytes(&bytes).expect("invalid RNX");
    println!(
        "after parse           live {:>7.1} KB   peak {:>7.1} KB   ({} methods, {} types, {} strings)",
        live_kb(),
        peak_kb(),
        module.methods.len(),
        module.types.len(),
        module.strings.len()
    );

    let mut interp = Interpreter::new(&module, ProbeHost::default());
    println!("after Interpreter::new  live {:>7.1} KB   peak {:>7.1} KB", live_kb(), peak_kb());

    let exit = interp.run(fuel);
    println!(
        "after {fuel} instructions  live {:>7.1} KB   peak {:>7.1} KB   [{exit:?}]",
        live_kb(),
        peak_kb()
    );

    println!();
    println!(
        "PEAK {:.1} KB  — a device heap must exceed this, plus allocator overhead and fragmentation",
        peak_kb()
    );
}
