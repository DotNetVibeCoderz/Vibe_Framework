
#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};
/// Reference into the GC heap.
pub type HeapRef = u32;

/// A slot on the CLR evaluation stack / in locals / in object fields.
/// Small integer types are widened to I32 as the CLI spec requires.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Value {
    I32(i32),
    I64(i64),
    F64(f64),
    Ref(HeapRef),
    Null,
    /// Managed pointer produced by ldloca/ldarga/ldflda/ldelema.
    Addr(Addr),
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Addr {
    Local(u16),
    Arg(u16),
    Field(HeapRef, u16),
    Elem(HeapRef, u32),
    StaticSlot(u32),
}

impl Value {
    pub fn as_i32(&self) -> Result<i32, String> {
        match self {
            Value::I32(v) => Ok(*v),
            Value::I64(v) => Ok(*v as i32),
            Value::F64(v) => Ok(*v as i32),
            other => Err(format!("expected int32, got {other:?}")),
        }
    }

    pub fn as_i64(&self) -> Result<i64, String> {
        match self {
            Value::I32(v) => Ok(*v as i64),
            Value::I64(v) => Ok(*v),
            Value::F64(v) => Ok(*v as i64),
            other => Err(format!("expected int64, got {other:?}")),
        }
    }

    pub fn as_f64(&self) -> Result<f64, String> {
        match self {
            Value::I32(v) => Ok(*v as f64),
            Value::I64(v) => Ok(*v as f64),
            Value::F64(v) => Ok(*v),
            other => Err(format!("expected float, got {other:?}")),
        }
    }

    pub fn is_truthy(&self) -> bool {
        match self {
            Value::I32(v) => *v != 0,
            Value::I64(v) => *v != 0,
            Value::F64(v) => *v != 0.0,
            Value::Ref(_) => true,
            Value::Addr(_) => true,
            Value::Null => false,
        }
    }
}

/// Element type codes used by newarr/box tokens in RNX.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ElemType {
    I8 = 0,
    U8 = 1,
    I16 = 2,
    U16 = 3,
    I32 = 4,
    U32 = 5,
    I64 = 6,
    U64 = 7,
    F32 = 8,
    F64 = 9,
    Bool = 10,
    Char = 11,
    Ref = 12,
}

impl ElemType {
    pub fn from_code(code: u32) -> Result<Self, String> {
        Ok(match code {
            0 => ElemType::I8,
            1 => ElemType::U8,
            2 => ElemType::I16,
            3 => ElemType::U16,
            4 => ElemType::I32,
            5 => ElemType::U32,
            6 => ElemType::I64,
            7 => ElemType::U64,
            8 => ElemType::F32,
            9 => ElemType::F64,
            10 => ElemType::Bool,
            11 => ElemType::Char,
            12 => ElemType::Ref,
            _ => return Err(format!("bad element type code {code}")),
        })
    }

    pub fn default_value(&self) -> Value {
        match self {
            ElemType::I64 | ElemType::U64 => Value::I64(0),
            ElemType::F32 | ElemType::F64 => Value::F64(0.0),
            ElemType::Ref => Value::Null,
            _ => Value::I32(0),
        }
    }
}

/// Objects living on the GC heap.
#[derive(Debug, Clone)]
pub enum HeapObject {
    Str(String),
    Array { elem: ElemType, data: Vec<Value> },
    Object { type_idx: u16, fields: Vec<Value> },
    Boxed(Value),
    /// Bound function: method index + captured `this` (Null for static).
    Delegate { method: u32, target: Value },
    /// System.Collections.Generic.List / Queue / Stack backing store.
    ListObj(Vec<Value>),
    /// Dictionary as an association list (small-N embedded workloads).
    MapObj(Vec<(Value, Value)>),
    /// Enumerator over an Array/ListObj/MapObj (foreach support).
    Cursor { target: HeapRef, pos: u32 },
    /// Green thread handle: delegate to run + scheduler slot once started.
    ThreadObj { delegate: HeapRef, thread_idx: Option<u32> },
    /// Async task: state 0 = pending, 1 = completed, 2 = faulted.
    /// `value` is the result (or the exception when faulted);
    /// `continuations` are async state machines to resume on completion.
    TaskObj { state: u8, value: Value, continuations: Vec<HeapRef> },
    /// `System.Type` reflection handle: the RNX type index (None for BCL
    /// types without an RNX entry) plus the full type name.
    TypeObj { type_idx: Option<u16>, name: String },
    /// `System.Reflection.MethodInfo` handle: index into the module methods.
    MethodInfoObj { method_idx: u32 },
    /// `System.Reflection.FieldInfo` handle: the field's simple name plus its
    /// resolved slot (instance-layout slot, or global static slot).
    FieldInfoObj { slot: u32, is_static: bool, name: String },
    /// `System.Reflection.PropertyInfo` handle: the property name plus its
    /// `get_`/`set_` accessor method indices (paired by name).
    PropertyInfoObj { getter: Option<u32>, setter: Option<u32>, name: String },
}

pub const TASK_PENDING: u8 = 0;
pub const TASK_DONE: u8 = 1;
pub const TASK_FAULTED: u8 = 2;
