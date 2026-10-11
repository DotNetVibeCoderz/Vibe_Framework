//! The bindings `Rustzor.Core` declares as `InternalCall`, and the browser
//! imports behind them.
//!
//! The managed side is deliberately thin here. There is **one** general call,
//! `Js.Invoke(op, json)`, and every `Rustzor.Browser.*` library is C# built on
//! it, so adding a browser API is a C# change plus a line of JavaScript, never
//! a runtime rebuild. The few dedicated bindings below exist because they run
//! often enough that the JSON round trip would show.

use rustclr_bcl::support::{arg_i32, arg_string_or_empty};
use rustclr_core::{ExecResult, Interpreter, Value};

/// The functions `rustzor.js` provides under the `rustzor` import module.
pub mod imports {
    #[cfg(target_arch = "wasm32")]
    #[link(wasm_import_module = "rustzor")]
    unsafe extern "C" {
        fn host_write(stream: i32, ptr: *const u8, len: usize);
        fn host_now() -> f64;
        fn host_wall() -> f64;
        /// Minutes local time is ahead of UTC (`-getTimezoneOffset()`).
        fn host_tz_offset() -> i32;
        /// Runs `op` with a JSON argument. Returns the byte length of the
        /// pending result, or -1 for `null`; [`host_take`] copies it out.
        fn host_call(op: *const u8, op_len: usize, arg: *const u8, arg_len: usize) -> i32;
        fn host_take(dst: *mut u8);
        /// Calls function `index` of the AOT module. `args` holds `argc`
        /// 64-bit slots; `shape` says how to read each one and the result.
        fn host_aot_call(index: i32, args: *const i64, argc: i32) -> i64;
        /// Copies `len` bytes between this module's memory and the AOT
        /// module's: into it when `to_aot` is non-zero, out of it otherwise.
        fn host_aot_copy(to_aot: i32, here: *mut u8, there: i32, len: i32);
    }

    #[cfg(target_arch = "wasm32")]
    pub fn write(stream: i32, text: &str) {
        unsafe { host_write(stream, text.as_ptr(), text.len()) }
    }

    #[cfg(target_arch = "wasm32")]
    pub fn now() -> f64 {
        unsafe { host_now() }
    }

    #[cfg(target_arch = "wasm32")]
    pub fn wall_clock() -> f64 {
        unsafe { host_wall() }
    }

    #[cfg(target_arch = "wasm32")]
    pub fn tz_offset() -> i32 {
        unsafe { host_tz_offset() }
    }

    #[cfg(target_arch = "wasm32")]
    pub fn call(op: &str, arg: &str) -> Option<String> {
        let len = unsafe { host_call(op.as_ptr(), op.len(), arg.as_ptr(), arg.len()) };
        if len < 0 {
            return None;
        }
        let mut buf = vec![0u8; len as usize];
        if len > 0 {
            unsafe { host_take(buf.as_mut_ptr()) };
        }
        Some(String::from_utf8(buf).unwrap_or_default())
    }

    #[cfg(target_arch = "wasm32")]
    pub fn aot_call(index: i32, args: &[i64]) -> i64 {
        unsafe { host_aot_call(index, args.as_ptr(), args.len() as i32) }
    }

    #[cfg(target_arch = "wasm32")]
    pub fn aot_copy_in(here: &[u8], there: i32) {
        unsafe { host_aot_copy(1, here.as_ptr() as *mut u8, there, here.len() as i32) }
    }

    #[cfg(target_arch = "wasm32")]
    pub fn aot_copy_out(here: &mut [u8], there: i32) {
        unsafe { host_aot_copy(0, here.as_mut_ptr(), there, here.len() as i32) }
    }

    // Off wasm there is no browser. These let the crate build and its unit
    // tests run on the host.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn write(stream: i32, text: &str) {
        if stream == 2 { eprintln!("{text}") } else { println!("{text}") }
    }
    #[cfg(not(target_arch = "wasm32"))]
    pub fn now() -> f64 {
        0.0
    }
    #[cfg(not(target_arch = "wasm32"))]
    pub fn wall_clock() -> f64 {
        0.0
    }
    #[cfg(not(target_arch = "wasm32"))]
    pub fn tz_offset() -> i32 {
        0
    }
    #[cfg(not(target_arch = "wasm32"))]
    pub fn call(_op: &str, _arg: &str) -> Option<String> {
        None
    }
    #[cfg(not(target_arch = "wasm32"))]
    pub fn aot_call(_index: i32, _args: &[i64]) -> i64 {
        unreachable!("no AOT module outside a browser")
    }
    #[cfg(not(target_arch = "wasm32"))]
    pub fn aot_copy_in(_here: &[u8], _there: i32) {}
    #[cfg(not(target_arch = "wasm32"))]
    pub fn aot_copy_out(_here: &mut [u8], _there: i32) {}
}

fn string_result(interp: &mut Interpreter, s: Option<String>) -> Option<Value> {
    Some(match s {
        Some(s) => Value::Obj(interp.alloc_string(&s)),
        None => Value::Null,
    })
}

pub fn install(interp: &mut Interpreter) {
    // The general bridge. `op` names a function in rustzor.js's table, such as
    // `dom.patch` or `storage.get`; `json` is its argument.
    interp.register_native("Rustzor.Interop.Js::Invoke(string,string)", |i, a| {
        let op = arg_string_or_empty(i, a, 0)?;
        let json = arg_string_or_empty(i, a, 1)?;
        let reply = imports::call(&op, &json);
        Ok(string_result(i, reply))
    });
    interp.register_native("Rustzor.Interop.Js::InvokeVoid(string,string)", |i, a| {
        let op = arg_string_or_empty(i, a, 0)?;
        let json = arg_string_or_empty(i, a, 1)?;
        let _ = imports::call(&op, &json);
        Ok(None)
    });
    interp.register_native("Rustzor.Interop.Js::Now()", |_, _| Ok(Some(Value::F(imports::now()))));
    interp.register_native("Rustzor.Interop.Js::Log(int,string)", |i, a| {
        let level = arg_i32(i, a, 0)?;
        let text = arg_string_or_empty(i, a, 1)?;
        imports::write(10 + level, &text);
        Ok(None)
    });
    interp.register_native("Rustzor.Interop.Js::IsBrowserNative()", |_, _| {
        Ok(Some(Value::I32(i32::from(cfg!(target_arch = "wasm32")))))
    });

    // `Task.Delay` without threads is a busy wait in RustBCL, and a busy wait
    // in a browser is a frozen tab. Rustzor replaces it with a pending task
    // that a `setTimeout` completes, the same shape .NET has.
    interp.register_native("System.Threading.Tasks.Task::Delay(int)", delay);
}

/// `Task.Delay(ms)` becomes `Rustzor.Interop.Scheduler.Delay(ms)`, which is C#.
///
/// Implemented by calling back into managed code rather than building a task
/// here, because the task machinery belongs to RustBCL and the timer registry
/// belongs to Rustzor.Core; neither should reach into the other's fields.
fn delay(interp: &mut Interpreter, args: &[Value]) -> ExecResult<Option<Value>> {
    let ms = arg_i32(interp, args, 0)?;
    let registry = &interp.loader.registry;
    let target = registry.find_type_by_name("Rustzor.Interop.Scheduler").and_then(|ty| {
        registry.ty(ty).methods.iter().copied().find(|m| {
            let info = registry.method(*m);
            info.name == "Delay" && info.signature.params.len() == 1
        })
    });
    match target {
        Some(method) => interp.invoke(method, vec![Value::I32(ms)]),
        None => Err(rustclr_core::ExecutionError::exception(
            rustclr_core::ClrExceptionKind::NotSupported,
            "Task.Delay in the browser needs Rustzor.Core to be loaded",
        )),
    }
}
