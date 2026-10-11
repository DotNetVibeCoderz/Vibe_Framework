//! The AOT tier: methods compiled ahead of time to a second WebAssembly
//! module, offered to the interpreter through RustCLR's [`NativeTier`] seam.
//!
//! `Rustzor.Compiler` translates the methods it can (numeric code: integer and
//! floating arithmetic, locals, branches, calls between compiled methods,
//! primitive arrays) into `app.aot.wasm`, and writes a manifest naming each
//! one by assembly and `MethodDef` token. Everything it declines, it says so,
//! and the interpreter runs it exactly as before. That is the same contract
//! the desktop JIT keeps: declining is the normal answer.
//!
//! # The boundary
//!
//! The AOT module has its own linear memory. A call copies its arguments into
//! that memory as 64-bit slots at [`ARGS_BASE`], primitive arrays into a bump
//! region from [`ARRAY_BASE`], and calls the method's trampoline, which reads
//! its parameters from the slots. Arrays are copied back afterwards, because
//! the compiled code may have written them. A fault, such as an index out of
//! range or an integer division by zero, is written to address 0 and becomes
//! the managed exception the interpreter would have thrown.
//!
//! Compiled code never sees a heap handle. It cannot allocate, cannot call the
//! interpreter, and cannot reach an object field, so the collector never has
//! to know it exists.

use std::cell::Cell;
use std::collections::HashMap;

use rustclr_core::gc::Handle;
use rustclr_core::gc::safepoint::Mutators;
use rustclr_core::gc::shared::SharedHeap;
use rustclr_core::{
    ArrayStorage, ClrArray, ClrExceptionKind, ExecResult, ExecutionError, Interpreter, Loader,
    MethodId, NativeTier, Value,
};

use crate::bridge::imports;

const FAULT_ADDR: i32 = 0;
const ARGS_BASE: i32 = 16;
const ARRAY_BASE: i32 = 1024;
const MAX_ARGS: usize = 64;

thread_local! {
    static METHODS: Cell<usize> = const { Cell::new(0) };
    static CALLS: Cell<u64> = const { Cell::new(0) };
}

/// Methods installed and calls made, for `rz_stats`.
pub fn counters() -> (usize, u64) {
    (METHODS.with(Cell::get), CALLS.with(Cell::get))
}

/// How a parameter or the result crosses the boundary.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Kind {
    Void,
    I32,
    I64,
    F32,
    F64,
    ArrI32,
    ArrI64,
    ArrF32,
    ArrF64,
    ArrU8,
}

impl Kind {
    fn parse(c: char) -> Option<Self> {
        Some(match c {
            'v' => Kind::Void,
            'i' => Kind::I32,
            'l' => Kind::I64,
            'f' => Kind::F32,
            'd' => Kind::F64,
            'I' => Kind::ArrI32,
            'L' => Kind::ArrI64,
            'F' => Kind::ArrF32,
            'D' => Kind::ArrF64,
            'Y' => Kind::ArrU8,
            _ => return None,
        })
    }

    fn is_array(self) -> bool {
        matches!(self, Kind::ArrI32 | Kind::ArrI64 | Kind::ArrF32 | Kind::ArrF64 | Kind::ArrU8)
    }
}

#[derive(Debug, Clone)]
struct Entry {
    index: i32,
    params: Vec<Kind>,
    ret: Kind,
}

struct WasmTier {
    entries: HashMap<MethodId, Entry>,
}

/// Reads the manifest and installs the tier.
///
/// One line per compiled method: `Assembly|0x06000012|index|params|ret`, where
/// `params` is one shape letter per parameter (see [`Kind::parse`]). A line
/// naming an assembly or token that is not loaded is skipped, not fatal: an
/// app may be served with a stale AOT module, and the price of that should be
/// speed, never correctness.
pub fn install(interp: &mut Interpreter, manifest: &str) -> Result<usize, String> {
    let mut entries = HashMap::new();
    for (n, line) in manifest.lines().enumerate() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        let fields: Vec<&str> = line.split('|').collect();
        if fields.len() != 5 {
            return Err(format!("manifest line {}: expected 5 fields", n + 1));
        }
        let token = u32::from_str_radix(fields[1].trim_start_matches("0x"), 16)
            .map_err(|_| format!("manifest line {}: bad token", n + 1))?;
        let index: i32 = fields[2].parse().map_err(|_| format!("manifest line {}: bad index", n + 1))?;
        let params: Option<Vec<Kind>> = fields[3].chars().map(Kind::parse).collect();
        let ret = fields[4].chars().next().and_then(Kind::parse);
        let (Some(params), Some(ret)) = (params, ret) else {
            return Err(format!("manifest line {}: bad shape", n + 1));
        };
        if params.len() > MAX_ARGS {
            continue;
        }
        let Some(assembly) = interp.loader.find_assembly(fields[0]) else { continue };
        let Some(method) = interp.loader.assembly(assembly).method_by_row.get(&(token & 0x00FF_FFFF)).copied()
        else {
            continue;
        };
        entries.insert(method, Entry { index, params, ret });
    }
    let count = entries.len();
    METHODS.with(|m| m.set(count));
    interp.native_tier = Some(Box::new(WasmTier { entries }));
    Ok(count)
}

fn fault(code: i32) -> ExecutionError {
    let (kind, message) = match code {
        1 => (ClrExceptionKind::IndexOutOfRange, "Index was outside the bounds of the array."),
        2 => (ClrExceptionKind::DivideByZero, "Attempted to divide by zero."),
        3 => (ClrExceptionKind::Overflow, "Arithmetic operation resulted in an overflow."),
        4 => (ClrExceptionKind::NullReference, "Object reference not set to an instance of an object."),
        _ => (ClrExceptionKind::InvalidOperation, "AOT code faulted."),
    };
    ExecutionError::exception(kind, message)
}

/// Copies a primitive array's elements as little-endian bytes.
fn array_bytes(storage: &ArrayStorage) -> Option<Vec<u8>> {
    Some(match storage {
        ArrayStorage::I32(v) => v.iter().flat_map(|x| x.to_le_bytes()).collect(),
        ArrayStorage::I64(v) => v.iter().flat_map(|x| x.to_le_bytes()).collect(),
        ArrayStorage::F32(v) => v.iter().flat_map(|x| x.to_le_bytes()).collect(),
        ArrayStorage::F64(v) => v.iter().flat_map(|x| x.to_le_bytes()).collect(),
        ArrayStorage::U8(v) => v.clone(),
        _ => return None,
    })
}

fn write_back(storage: &mut ArrayStorage, bytes: &[u8]) {
    match storage {
        ArrayStorage::I32(v) => {
            for (d, c) in v.iter_mut().zip(bytes.chunks_exact(4)) {
                *d = i32::from_le_bytes(c.try_into().unwrap());
            }
        }
        ArrayStorage::I64(v) => {
            for (d, c) in v.iter_mut().zip(bytes.chunks_exact(8)) {
                *d = i64::from_le_bytes(c.try_into().unwrap());
            }
        }
        ArrayStorage::F32(v) => {
            for (d, c) in v.iter_mut().zip(bytes.chunks_exact(4)) {
                *d = f32::from_le_bytes(c.try_into().unwrap());
            }
        }
        ArrayStorage::F64(v) => {
            for (d, c) in v.iter_mut().zip(bytes.chunks_exact(8)) {
                *d = f64::from_le_bytes(c.try_into().unwrap());
            }
        }
        ArrayStorage::U8(v) => {
            let n = v.len().min(bytes.len());
            v[..n].copy_from_slice(&bytes[..n]);
        }
        _ => {}
    }
}

fn storage_matches(kind: Kind, storage: &ArrayStorage) -> bool {
    matches!(
        (kind, storage),
        (Kind::ArrI32, ArrayStorage::I32(_))
            | (Kind::ArrI64, ArrayStorage::I64(_))
            | (Kind::ArrF32, ArrayStorage::F32(_))
            | (Kind::ArrF64, ArrayStorage::F64(_))
            | (Kind::ArrU8, ArrayStorage::U8(_))
    )
}

impl NativeTier for WasmTier {
    fn name(&self) -> &'static str {
        "rustzor-aot-wasm"
    }

    fn try_execute(
        &mut self,
        _loader: &Loader,
        heap: &SharedHeap,
        method: MethodId,
        args: &[Value],
        _mutators: &Mutators,
        _interpreter_roots: &mut dyn FnMut() -> Vec<Handle>,
    ) -> Option<ExecResult<Option<Value>>> {
        let entry = self.entries.get(&method)?;
        if args.len() != entry.params.len() {
            return None;
        }

        let mut slots = [0i64; MAX_ARGS];
        // (handle, AOT address, byte length) for every array, to copy back.
        let mut arrays: Vec<(Handle, i32, usize)> = Vec::new();
        let mut next = ARRAY_BASE;

        for (i, (kind, value)) in entry.params.iter().zip(args).enumerate() {
            slots[i] = match (kind, value) {
                (Kind::I32, Value::I32(v)) => *v as i64,
                (Kind::I64, Value::I64(v) | Value::NativeInt(v)) => *v,
                (Kind::I64, Value::I32(v)) => *v as i64,
                (Kind::F32, Value::F(v)) => (*v as f32).to_bits() as i64,
                (Kind::F64, Value::F(v)) => v.to_bits() as i64,
                (k, Value::Obj(h)) if k.is_array() => {
                    if h.is_null() {
                        return Some(Err(fault(4)));
                    }
                    let copied = heap.with::<ClrArray, _>(*h, |a| {
                        if storage_matches(*k, &a.storage) { array_bytes(&a.storage) } else { None }
                    });
                    // An array whose storage is not the shape the compiler
                    // assumed is declined, not guessed at.
                    let bytes = copied.flatten()?;
                    let len = bytes.len() / match k {
                        Kind::ArrI32 | Kind::ArrF32 => 4,
                        Kind::ArrU8 => 1,
                        _ => 8,
                    };
                    let header = [(len as i32).to_le_bytes(), [0; 4]].concat();
                    imports::aot_copy_in(&header, next);
                    imports::aot_copy_in(&bytes, next + 8);
                    arrays.push((*h, next + 8, bytes.len()));
                    let at = next;
                    next += 8 + ((bytes.len() as i32 + 7) & !7);
                    at as i64
                }
                (_, Value::Null) if kind.is_array() => return Some(Err(fault(4))),
                _ => return None,
            };
        }

        let argc = entry.params.len();
        let raw: Vec<u8> = slots[..argc].iter().flat_map(|s| s.to_le_bytes()).collect();
        imports::aot_copy_in(&[0u8; 4], FAULT_ADDR);
        imports::aot_copy_in(&raw, ARGS_BASE);
        let result = imports::aot_call(entry.index, &slots[..argc]);
        CALLS.with(|c| c.set(c.get() + 1));

        let mut code = [0u8; 4];
        imports::aot_copy_out(&mut code, FAULT_ADDR);
        let code = i32::from_le_bytes(code);

        for (handle, at, len) in arrays {
            let mut bytes = vec![0u8; len];
            imports::aot_copy_out(&mut bytes, at);
            heap.with_mut::<ClrArray, _>(handle, |a| write_back(&mut a.storage, &bytes));
        }

        if code != 0 {
            return Some(Err(fault(code)));
        }
        Some(Ok(match entry.ret {
            Kind::Void => None,
            Kind::I32 => Some(Value::I32(result as i32)),
            Kind::I64 => Some(Value::I64(result)),
            Kind::F32 => Some(Value::F(f32::from_bits(result as u32) as f64)),
            Kind::F64 => Some(Value::F(f64::from_bits(result as u64))),
            _ => return Some(Err(fault(0))),
        }))
    }

    fn stats(&self) -> (usize, usize) {
        (self.entries.len(), 0)
    }
}
