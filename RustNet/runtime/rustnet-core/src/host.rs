//! The runtime's window to the outside world. The firmware implements
//! `RuntimeHost` on top of the HAL + services (fs, net, gfx); unit tests
//! use mocks. Internal-call methods that the interpreter does not handle
//! natively (Console/Math/String) are routed to `invoke` by canonical
//! name, e.g. `RustNet.Hal.Gpio::Write(i4,bool)`.

#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};

#[derive(Debug, Clone, PartialEq)]
pub enum HostValue {
    I32(i32),
    I64(i64),
    F64(f64),
    Bool(bool),
    Str(String),
    Bytes(Vec<u8>),
    Void,
    Null,
}

pub trait RuntimeHost {
    /// Console output (Console.Write/WriteLine already formatted).
    fn console_write(&mut self, text: &str);

    /// Monotonic milliseconds since boot.
    fn now_ms(&mut self) -> u64;

    /// Cooperative sleep. On firmware this yields to the scheduler.
    fn sleep_ms(&mut self, ms: u64);

    /// Named internal call fallback. Return Err to surface a managed error.
    fn invoke(&mut self, name: &str, args: Vec<HostValue>) -> Result<HostValue, String>;
}

/// Host for tests and `rustnet-run`: captures console output, routes
/// invoke to a table of closures.
#[derive(Default)]
pub struct TestHost {
    pub console: String,
    pub clock_ms: u64,
    pub slept_ms: u64,
    pub calls: Vec<(String, Vec<HostValue>)>,
}

impl RuntimeHost for TestHost {
    fn console_write(&mut self, text: &str) {
        self.console.push_str(text);
    }

    fn now_ms(&mut self) -> u64 {
        self.clock_ms
    }

    fn sleep_ms(&mut self, ms: u64) {
        self.slept_ms += ms;
        self.clock_ms += ms;
    }

    fn invoke(&mut self, name: &str, args: Vec<HostValue>) -> Result<HostValue, String> {
        self.calls.push((name.to_string(), args.clone()));
        // A tiny built-in device model so runtime tests can exercise HAL calls.
        match name {
            "RustNet.Hal.Gpio::Write(i4,bool)" => Ok(HostValue::Void),
            "RustNet.Hal.Gpio::Read(i4)" => Ok(HostValue::Bool(true)),
            "RustNet.Hal.Adc::ReadRaw(i4)" => Ok(HostValue::I32(2048)),
            _ => Err(format!("unknown internal call: {name}")),
        }
    }
}
