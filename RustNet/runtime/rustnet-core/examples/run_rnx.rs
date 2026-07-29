//! Run an .rnx module on the host with a TestHost (dev utility).
//! Usage: cargo run -p rustnet-core --example run_rnx -- app.rnx

use rustnet_core::{HostValue, Interpreter, Module, RunExit, RuntimeHost};
use std::collections::HashMap;

/// Permissive host: HAL calls succeed with plausible values, files live in
/// a HashMap, everything is echoed for inspection.
#[derive(Default)]
struct DevHost {
    console: String,
    files: HashMap<String, Vec<u8>>,
    clock: u64,
    /// App-defined `[InternalCall]` hooks — a firmware answers these, so they
    /// are answered benignly here and reported at exit rather than aborting
    /// the run.
    unknown: Vec<String>,
}

impl RuntimeHost for DevHost {
    fn console_write(&mut self, text: &str) {
        // Stream rather than buffer: an embedded-style app often never
        // returns, and output that only appears at exit never appears at all.
        print!("{text}");
        let _ = std::io::Write::flush(&mut std::io::stdout());
        self.console.push_str(text);
    }
    fn now_ms(&mut self) -> u64 {
        self.clock += 1;
        self.clock
    }
    fn sleep_ms(&mut self, ms: u64) {
        self.clock += ms;
    }
    fn invoke(&mut self, name: &str, args: Vec<HostValue>) -> Result<HostValue, String> {
        let str_arg = |i: usize| match args.get(i) {
            Some(HostValue::Str(s)) => s.clone(),
            _ => String::new(),
        };
        match name {
            n if n.starts_with("RustNet.Hal.Adc::ReadRaw") => Ok(HostValue::I32(2048)),
            n if n.starts_with("RustNet.Hal.Adc::ReadMillivolts") => Ok(HostValue::I32(1650)),
            n if n.starts_with("RustNet.Hal.Gpio::Read") => Ok(HostValue::Bool(false)),
            n if n.starts_with("RustNet.Hal.I2c::Read") => Ok(HostValue::Bytes(vec![0; 8])),
            n if n.starts_with("RustNet.Hal.") => Ok(HostValue::Void),
            "RustNet.IO.FileSystem::WriteAllText(string,string)" => {
                self.files.insert(str_arg(0), str_arg(1).into_bytes());
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::AppendText(string,string)" => {
                self.files.entry(str_arg(0)).or_default().extend(str_arg(1).into_bytes());
                Ok(HostValue::Void)
            }
            "RustNet.IO.FileSystem::ReadAllText(string)" => {
                let path = str_arg(0);
                match self.files.get(&path) {
                    Some(d) => Ok(HostValue::Str(String::from_utf8_lossy(d).to_string())),
                    None => Err(format!("not found: {path}")),
                }
            }
            "RustNet.IO.FileSystem::Exists(string)" => {
                Ok(HostValue::Bool(self.files.contains_key(&str_arg(0))))
            }
            n if n.starts_with("RustNet.IO.FileSystem::") => Ok(HostValue::Void),
            n if n.starts_with("RustNet.Graphics.Display::") => Ok(HostValue::Void),
            n if n.starts_with("RustNet.Diagnostics.Log::") => {
                self.console.push_str(&format!("[log] {}\n", str_arg(0)));
                Ok(HostValue::Void)
            }
            n if n.starts_with("RustNet.Net.Wifi::") => Ok(HostValue::Bool(true)),
            n if n.starts_with("RustNet.Sys.") => Ok(HostValue::I32(4100)),
            other => {
                if !self.unknown.iter().any(|n| n == other) {
                    self.unknown.push(other.to_string());
                }
                Ok(if other.ends_with("()") { HostValue::I32(0) } else { HostValue::Void })
            }
        }
    }
}

fn main() {
    let path = std::env::args().nth(1).expect("usage: run_rnx <file.rnx>");
    let bytes = std::fs::read(&path).expect("cannot read file");
    let module = Module::from_bytes(&bytes).expect("invalid RNX");
    eprintln!(
        "loaded {}: {} methods, {} types, {} strings",
        path,
        module.methods.len(),
        module.types.len(),
        module.strings.len()
    );
    let mut interp = Interpreter::new(&module, DevHost::default());
    let exit = interp.run_to_completion();
    for name in &interp.host.unknown {
        eprintln!("[note] answered benignly (a firmware must implement it): {name}");
    }
    match exit {
        RunExit::Completed => eprintln!("[exit: completed, {} instructions]", interp.instructions),
        other => {
            eprintln!("[exit: {other:?}]");
            std::process::exit(1);
        }
    }
}
