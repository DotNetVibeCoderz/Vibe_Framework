//! List every host call an .rnx makes, with the exact canonical name the
//! interpreter passes to `RuntimeHost::invoke` (dev utility).
//!
//! Usage: cargo run -p rustnet-core --example host_calls -- app.rnx [fuel]
//!
//! Firmware dispatches on these strings by exact match, so a name that differs
//! by one character fails on the device while a loosely-matching host harness
//! sails through. This prints what a firmware must actually answer.

use std::collections::BTreeMap;

use rustnet_core::{HostValue, Interpreter, Module, RuntimeHost};

#[derive(Default)]
struct TracingHost {
    /// Canonical name -> (call count, the HostValue variants actually passed).
    calls: BTreeMap<String, (u32, Vec<&'static str>)>,
    clock: u64,
}

/// The variant a firmware's argument accessor will really see. Worth printing:
/// a C# `bool` arrives as `I32`, so an accessor that insists on `Bool` rejects
/// every call that takes one.
fn variant(v: &HostValue) -> &'static str {
    match v {
        HostValue::I32(_) => "I32",
        HostValue::I64(_) => "I64",
        HostValue::F64(_) => "F64",
        HostValue::Bool(_) => "Bool",
        HostValue::Str(_) => "Str",
        HostValue::Bytes(_) => "Bytes",
        HostValue::Void => "Void",
        HostValue::Null => "Null",
    }
}

impl RuntimeHost for TracingHost {
    fn console_write(&mut self, _text: &str) {}
    fn now_ms(&mut self) -> u64 {
        self.clock
    }
    fn sleep_ms(&mut self, ms: u64) {
        self.clock += ms;
    }
    fn invoke(&mut self, name: &str, args: Vec<HostValue>) -> Result<HostValue, String> {
        let entry = self.calls.entry(name.to_string()).or_default();
        entry.0 += 1;
        if entry.1.is_empty() {
            entry.1 = args.iter().map(variant).collect();
        }
        // Answer everything permissively; the point is the shape, not the value.
        Ok(if name.ends_with("()") { HostValue::I32(0) } else { HostValue::Void })
    }
}

fn main() {
    let path = std::env::args().nth(1).expect("usage: host_calls <file.rnx> [fuel]");
    let fuel: u64 = std::env::args().nth(2).and_then(|s| s.parse().ok()).unwrap_or(5_000_000);

    let bytes = std::fs::read(&path).expect("cannot read file");
    let module = Module::from_bytes(&bytes).expect("invalid RNX");

    let mut interp = Interpreter::new(&module, TracingHost::default());
    let exit = interp.run(fuel);

    println!("exit after {fuel} instructions: {exit:?}");
    println!();
    println!("host calls the firmware must answer by exact match:");
    if interp.host.calls.is_empty() {
        println!("  (none reached — raise the fuel budget?)");
    }
    for (name, (count, arg_types)) in &interp.host.calls {
        println!("  {count:>7}x  {name:?}  args: {arg_types:?}");
    }
}
