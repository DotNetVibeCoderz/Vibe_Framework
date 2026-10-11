//! RustNetRuntime in the browser.
//!
//! This crate is the whole of Rustzor's native side. It takes the RustCLR
//! interpreter and RustBCL — the same crates that run C# on a desktop and on a
//! microcontroller — and gives them a browser to talk to.
//!
//! # The ABI is plain `extern "C"`
//!
//! No `wasm-bindgen`: the runtime crates are dependency-free and this one stays
//! that way. Everything crosses as integers, and strings cross as UTF-8 bytes
//! in this module's linear memory. `js/rustzor.js` is the other half.
//!
//! * **Exports** (`rz_*`) are how JavaScript drives the runtime: load an
//!   assembly, run `Main`, invoke a static method by name.
//! * **Imports** (module `rustzor`) are how the runtime reaches the browser:
//!   console output, clocks, and one generic call, `host_call(op, json)`, that
//!   every `Rustzor.Browser.*` library goes through.
//!
//! # One call at a time
//!
//! There is one interpreter and it is not re-entrant from outside. A DOM event
//! that fires *while* C# is running — `focus()` raising `focus`, say — would
//! need a second entry. `rustzor.js` queues such events and replays them when
//! the outer call returns, so this side can treat every export as exclusive.

#![allow(clippy::missing_safety_doc)]

mod aot;
mod bridge;
mod host;

use std::cell::RefCell;

use rustclr_core::{ExecutionError, Interpreter, MethodId, Value};
use rustclr_metadata::Image;

pub use host::BrowserHost;

/// Everything the runtime keeps between calls from JavaScript.
struct State {
    interp: Interpreter,
    /// Methods already found by `Type::Name`, so a hot event handler is
    /// looked up once rather than on every click.
    method_cache: std::collections::HashMap<String, MethodId>,
}

thread_local! {
    static STATE: RefCell<Option<State>> = const { RefCell::new(None) };
    /// The last result or error, read back by JavaScript through
    /// `rz_result_ptr` / `rz_result_len`.
    static RESULT: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };
}

fn set_result(bytes: &[u8]) {
    RESULT.with(|r| {
        let mut r = r.borrow_mut();
        r.clear();
        r.extend_from_slice(bytes);
    });
}

fn with_state<R>(f: impl FnOnce(&mut State) -> R) -> Option<R> {
    STATE.with(|s| s.borrow_mut().as_mut().map(f))
}

/// Status codes every export returns. Negative is failure; the message is in
/// the result buffer.
const OK: i32 = 0;
const OK_NULL: i32 = 1;
const ERR_NOT_INITIALISED: i32 = -1;
const ERR_RUNTIME: i32 = -2;
const ERR_ARGUMENT: i32 = -3;

fn fail(code: i32, message: &str) -> i32 {
    set_result(message.as_bytes());
    code
}

fn describe(interp: &Interpreter, e: &ExecutionError) -> String {
    let mut text = e.to_string();
    // A thrown exception carries the frames it was thrown from; by the time
    // the error reaches here the interpreter's own stack has unwound.
    if let ExecutionError::Exception { object, .. } = e {
        let frames = interp.heap.with::<rustclr_core::ClrException, _>(*object, |x| x.stack_trace.clone());
        if let Some(frames) = frames.filter(|f| !f.is_empty()) {
            text.push('\n');
            text.push_str(&frames.join("\n"));
            return text;
        }
    }
    let trace = interp.stack_trace();
    if !trace.is_empty() {
        text.push_str("\n   at ");
        text.push_str(&trace.join("\n   at "));
    }
    text
}

/// # Safety
/// `ptr..ptr+len` must lie in this module's memory, which is the only memory
/// JavaScript can hand us.
unsafe fn bytes<'a>(ptr: *const u8, len: usize) -> &'a [u8] {
    if len == 0 {
        return &[];
    }
    unsafe { core::slice::from_raw_parts(ptr, len) }
}

unsafe fn text<'a>(ptr: *const u8, len: usize) -> Option<&'a str> {
    core::str::from_utf8(unsafe { bytes(ptr, len) }).ok()
}

// ── memory ───────────────────────────────────────────────────────────────────

/// Allocates `len` bytes for JavaScript to write into.
#[unsafe(no_mangle)]
pub extern "C" fn rz_alloc(len: usize) -> *mut u8 {
    let mut buf = Vec::<u8>::with_capacity(len.max(1));
    let ptr = buf.as_mut_ptr();
    core::mem::forget(buf);
    ptr
}

/// Frees what [`rz_alloc`] returned.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rz_free(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        drop(unsafe { Vec::from_raw_parts(ptr, 0, len.max(1)) });
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn rz_result_ptr() -> *const u8 {
    RESULT.with(|r| r.borrow().as_ptr())
}

#[unsafe(no_mangle)]
pub extern "C" fn rz_result_len() -> usize {
    RESULT.with(|r| r.borrow().len())
}

// ── lifecycle ────────────────────────────────────────────────────────────────

/// Creates the interpreter and installs RustBCL plus the browser bindings.
///
/// `minimal` installs RustBCL's console-only subset: a page that needs no
/// collections, LINQ or reflection starts faster with it.
#[unsafe(no_mangle)]
pub extern "C" fn rz_init(minimal: i32) -> i32 {
    let mut interp = Interpreter::with_host(Box::new(BrowserHost::new()));
    if minimal != 0 {
        rustclr_bcl::install_minimal(&mut interp);
    } else {
        rustclr_bcl::install(&mut interp);
    }
    bridge::install(&mut interp);
    let count = interp.native_count();
    STATE.with(|s| {
        *s.borrow_mut() = Some(State {
            interp,
            method_cache: Default::default(),
        })
    });
    count as i32
}

/// Loads one assembly. Libraries must come before the assemblies that
/// reference them: a `TypeRef` only binds to a type already registered.
///
/// Returns the assembly id, or a negative status.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rz_load(ptr: *const u8, len: usize) -> i32 {
    let data = unsafe { bytes(ptr, len) }.to_vec();
    with_state(|state| {
        let image = match Image::from_bytes(data) {
            Ok(image) => image,
            Err(e) => return fail(ERR_ARGUMENT, &format!("not a .NET assembly: {e}")),
        };
        match state.interp.loader.load_image(image) {
            Ok(id) => {
                set_result(state.interp.loader.assembly(id).name.as_bytes());
                id.0 as i32
            }
            Err(e) => fail(ERR_RUNTIME, &format!("load failed: {e}")),
        }
    })
    .unwrap_or_else(|| fail(ERR_NOT_INITIALISED, "rz_init has not been called"))
}

/// Runs an assembly's entry point. Returns its exit code.
#[unsafe(no_mangle)]
pub extern "C" fn rz_run_main(assembly: i32) -> i32 {
    with_state(|state| {
        let Some(id) = state
            .interp
            .loader
            .assemblies()
            .iter()
            .find(|a| a.id.0 as i32 == assembly)
            .map(|a| a.id)
        else {
            return fail(ERR_ARGUMENT, "no such assembly");
        };
        match state.interp.run_entry_point(id) {
            Ok(code) => {
                set_result(b"");
                code
            }
            Err(e) => {
                let message = describe(&state.interp, &e);
                fail(ERR_RUNTIME, &message)
            }
        }
    })
    .unwrap_or_else(|| fail(ERR_NOT_INITIALISED, "rz_init has not been called"))
}

/// Finds a static method by `Namespace.Type` and name, taking `argc`
/// parameters.
fn find_static(state: &mut State, type_name: &str, method: &str, argc: usize) -> Option<MethodId> {
    let key = format!("{type_name}::{method}/{argc}");
    if let Some(id) = state.method_cache.get(&key) {
        return Some(*id);
    }
    let registry = &state.interp.loader.registry;
    let ty = registry.find_type_by_name(type_name)?;
    let id = registry.ty(ty).methods.iter().copied().find(|m| {
        let info = registry.method(*m);
        info.name == method && info.is_static() && info.signature.params.len() == argc
    })?;
    state.method_cache.insert(key, id);
    Some(id)
}

/// Invokes a static method that takes zero or one `string` and returns
/// `void` or `string`.
///
/// The request is `Type\u{1}Method` or `Type\u{1}Method\u{1}argument`. That one
/// shape is all JavaScript ever needs: every callback the browser makes into
/// C# goes through `Rustzor.Interop.Callbacks.Dispatch(string)`, which decodes
/// its own argument.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rz_invoke(ptr: *const u8, len: usize) -> i32 {
    let Some(request) = (unsafe { text(ptr, len) }) else {
        return fail(ERR_ARGUMENT, "request is not UTF-8");
    };
    let mut parts = request.splitn(3, '\u{1}');
    let (Some(type_name), Some(method)) = (parts.next(), parts.next()) else {
        return fail(ERR_ARGUMENT, "expected Type\\u0001Method[\\u0001argument]");
    };
    let argument = parts.next();
    let (type_name, method, argument) = (type_name.to_owned(), method.to_owned(), argument.map(str::to_owned));

    with_state(|state| {
        let argc = usize::from(argument.is_some());
        let Some(id) = find_static(state, &type_name, &method, argc) else {
            return fail(ERR_ARGUMENT, &format!("no static method {type_name}::{method} taking {argc} argument(s)"));
        };
        let args = match &argument {
            Some(a) => vec![Value::Obj(state.interp.alloc_string(a))],
            None => Vec::new(),
        };
        match state.interp.invoke(id, args) {
            Ok(Some(value)) => match state.interp.value_as_string(&value) {
                Some(s) => {
                    set_result(s.as_bytes());
                    OK
                }
                None if value.as_handle().is_some_and(|h| h.is_null()) => OK_NULL,
                None => {
                    let shown = format!("{value:?}");
                    set_result(shown.as_bytes());
                    OK
                }
            },
            Ok(None) => OK_NULL,
            Err(e) => {
                let message = describe(&state.interp, &e);
                fail(ERR_RUNTIME, &message)
            }
        }
    })
    .unwrap_or_else(|| fail(ERR_NOT_INITIALISED, "rz_init has not been called"))
}

/// Forces a full collection. The page calls this when idle.
#[unsafe(no_mangle)]
pub extern "C" fn rz_collect() {
    with_state(|state| state.interp.force_collect());
}

/// Runtime counters as JSON, for the dev overlay and `rustzor bench`.
#[unsafe(no_mangle)]
pub extern "C" fn rz_stats() -> i32 {
    with_state(|state| {
        let heap = state.interp.heap.stats();
        let stats = &state.interp.stats;
        let (aot_methods, aot_calls) = aot::counters();
        let json = format!(
            concat!(
                "{{\"instructions\":{},\"calls\":{},\"allocations\":{},",
                "\"liveBytes\":{},\"peakLiveBytes\":{},\"collections\":{},",
                "\"types\":{},\"methods\":{},\"natives\":{},",
                "\"aotMethods\":{},\"aotCalls\":{}}}"
            ),
            stats.instructions_executed,
            stats.calls,
            heap.total_allocations,
            heap.total_bytes_allocated.saturating_sub(heap.total_bytes_freed),
            heap.peak_live_bytes,
            heap.collections,
            state.interp.loader.registry.type_count(),
            state.interp.loader.registry.method_count(),
            state.interp.native_count(),
            aot_methods,
            aot_calls,
        );
        set_result(json.as_bytes());
        OK
    })
    .unwrap_or_else(|| fail(ERR_NOT_INITIALISED, "rz_init has not been called"))
}

/// Installs an AOT manifest: which methods the compiled module provides.
///
/// The manifest is lines of `Assembly|0x06000012|index|shape`; see
/// [`aot::install`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rz_aot_install(ptr: *const u8, len: usize) -> i32 {
    let Some(manifest) = (unsafe { text(ptr, len) }) else {
        return fail(ERR_ARGUMENT, "manifest is not UTF-8");
    };
    let manifest = manifest.to_owned();
    with_state(|state| match aot::install(&mut state.interp, &manifest) {
        Ok(count) => count as i32,
        Err(e) => fail(ERR_ARGUMENT, &e),
    })
    .unwrap_or_else(|| fail(ERR_NOT_INITIALISED, "rz_init has not been called"))
}
