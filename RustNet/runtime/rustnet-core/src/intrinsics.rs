//! Internal-call dispatch. Corlib surface (Console, String, Math, Random,
//! collections, LINQ, text encoding, threading, ...) executes natively
//! here; everything else (HAL, fs, net, gfx, db) is marshalled to
//! `RuntimeHost::invoke` by canonical name.

mod collections;
mod linq;
mod textenc;

#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};
use crate::host::{HostValue, RuntimeHost};
use crate::interp::Interpreter;
use crate::value::{Addr, ElemType, HeapObject, Value};

pub fn call_internal<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    method_idx: u32,
    is_newobj: bool,
) -> Result<(), String> {
    let m = &interp.module.methods[method_idx as usize];
    let name = interp.module.strings[m.name as usize].clone();
    let argc = if is_newobj { m.param_count as usize } else { m.arg_count() };
    let mut args = vec![Value::Null; argc];
    for i in (0..argc).rev() {
        args[i] = interp
            .frames
            .last_mut()
            .and_then(|f| f.stack.pop())
            .ok_or_else(|| format!("stack underflow in internal call {name}"))?;
    }

    // -- interpolated string handler (needs the raw byref `this`) -------
    if let Some(rest) =
        name.strip_prefix("System.Runtime.CompilerServices.DefaultInterpolatedStringHandler::")
    {
        return textenc::interp_handler(interp, rest, &args, is_newobj);
    }

    // -- inline-array span helpers -------------------------------------
    // Roslyn lowers params `ReadOnlySpan<T>` calls (e.g. `string.Concat` with
    // 5+ parts) to an `<>y__InlineArrayN<T>` buffer + these helpers. The buffer
    // is modelled as a heap array (allocated by `initobj`), so a "span" is just
    // that array reference and an element ref is an array-element byref.
    if let Some(rest) = name.strip_prefix("<PrivateImplementationDetails>::") {
        if rest.starts_with("InlineArrayElementRef") {
            let buf = interp.deref(args[0])?;
            let idx = interp.deref(args[1])?.as_i32()?;
            if let Value::Ref(r) = buf {
                push(interp, Value::Addr(Addr::Elem(r, idx as u32)));
                return Ok(());
            }
            return Err("InlineArrayElementRef: buffer not initialized".into());
        }
        if rest.starts_with("InlineArrayFirstElementRef") {
            let buf = interp.deref(args[0])?;
            if let Value::Ref(r) = buf {
                push(interp, Value::Addr(Addr::Elem(r, 0)));
                return Ok(());
            }
            return Err("InlineArrayFirstElementRef: buffer not initialized".into());
        }
        if rest.starts_with("InlineArrayAsReadOnlySpan") || rest.starts_with("InlineArrayAsSpan") {
            // The span is the underlying array itself.
            let buf = interp.deref(args[0])?;
            push(interp, buf);
            return Ok(());
        }
    }

    // -- Object -------------------------------------------------------
    // Parameterless base ctors of framework roots we don't model (Object,
    // Attribute) are no-ops — there is nothing to initialize or marshal.
    if name == "System.Object::.ctor()" || name == "System.Attribute::.ctor()" {
        return Ok(());
    }
    if name == "System.Object::Equals(object)" || name == "System.Object::ReferenceEquals(object,object)" {
        let a = interp.deref(args[argc - 2])?;
        let b = interp.deref(args[argc - 1])?;
        let eq = interp.value_eq(a, b);
        push(interp, Value::I32(eq as i32));
        return Ok(());
    }
    // Reflection types (Type/MethodInfo/MemberInfo) overload ==/!= — their
    // params erase to `object`, so this catches those without touching the
    // typed `string == string` operator.
    if name.ends_with("::op_Equality(object,object)") || name.ends_with("::op_Inequality(object,object)") {
        let a = interp.deref(args[argc - 2])?;
        let b = interp.deref(args[argc - 1])?;
        let eq = reflection_eq(interp, a, b);
        let inequality = name.ends_with("::op_Inequality(object,object)");
        push(interp, Value::I32((eq ^ inequality) as i32));
        return Ok(());
    }
    if name.ends_with("::GetHashCode()") {
        let v = interp.deref(args[0])?;
        let h = hash_value(interp, v);
        push(interp, Value::I32(h));
        return Ok(());
    }

    // -- reflection (System.Type) --------------------------------------
    if name == "System.Object::GetType()" {
        let this = interp.deref(args[0])?;
        let (type_idx, full) = type_identity(interp, this)?;
        let r = interp.heap.alloc(HeapObject::TypeObj { type_idx, name: full });
        push(interp, Value::Ref(r));
        return Ok(());
    }
    // `typeof(T)` lowers to `ldtoken T; call Type.GetTypeFromHandle(handle)`.
    // `ldtoken` already produced the `TypeObj`, so this is the identity.
    if name.starts_with("System.Type::GetTypeFromHandle(") {
        let v = interp.deref(args[0])?;
        push(interp, v);
        return Ok(());
    }
    // Members on a `System.Type` receiver. `Name` is inherited from
    // `System.Reflection.MemberInfo`, `FullName`/`Namespace`/`BaseType` are on
    // `System.Type` — so key off the receiver being a TypeObj, not the
    // declaring type, and match the bare method name.
    if !args.is_empty() {
        if let Ok(Value::Ref(r)) = interp.deref(args[0]) {
            if let Ok(HeapObject::TypeObj { type_idx, name: full }) = interp.heap.get(r) {
                let (type_idx, full) = (*type_idx, full.clone());
                let short = name.rsplit("::").next().unwrap_or("");
                match short {
                    "get_FullName()" | "ToString()" => {
                        let r = interp.heap.alloc_str(full);
                        push(interp, Value::Ref(r));
                        return Ok(());
                    }
                    "get_Name()" => {
                        let simple = full.rsplit('.').next().unwrap_or(&full).to_string();
                        let r = interp.heap.alloc_str(simple);
                        push(interp, Value::Ref(r));
                        return Ok(());
                    }
                    "get_Namespace()" => {
                        let ns = match full.rfind('.') {
                            Some(i) => full[..i].to_string(),
                            None => String::new(),
                        };
                        let r = interp.heap.alloc_str(ns);
                        push(interp, Value::Ref(r));
                        return Ok(());
                    }
                    "GetMethods()" => {
                        // Declared, non-constructor methods owned by this type.
                        let idxs: Vec<u32> = match type_idx {
                            Some(ti) => interp
                                .module
                                .methods
                                .iter()
                                .enumerate()
                                .filter(|(_, m)| {
                                    m.owner_type == ti
                                        && (m.flags & crate::rnx::MFLAG_CTOR) == 0
                                })
                                .map(|(i, _)| i as u32)
                                .collect(),
                            None => Vec::new(),
                        };
                        let data: Vec<Value> = idxs
                            .into_iter()
                            .map(|i| {
                                Value::Ref(
                                    interp
                                        .heap
                                        .alloc(HeapObject::MethodInfoObj { method_idx: i }),
                                )
                            })
                            .collect();
                        let arr = interp.heap.alloc(HeapObject::Array {
                            elem: crate::value::ElemType::Ref,
                            data,
                        });
                        push(interp, Value::Ref(arr));
                        return Ok(());
                    }
                    "GetMethod(string)" => {
                        let wanted = match interp.deref(*args.last().unwrap())? {
                            Value::Ref(sr) => match interp.heap.get(sr)? {
                                HeapObject::Str(s) => s.clone(),
                                _ => String::new(),
                            },
                            _ => String::new(),
                        };
                        let found = match type_idx {
                            Some(ti) => interp
                                .module
                                .methods
                                .iter()
                                .enumerate()
                                .position(|(i, m)| {
                                    m.owner_type == ti
                                        && (m.flags & crate::rnx::MFLAG_CTOR) == 0
                                        && method_simple_name(interp.module, i as u32) == wanted
                                })
                                .map(|i| i as u32),
                            None => None,
                        };
                        match found {
                            Some(i) => {
                                let r = interp
                                    .heap
                                    .alloc(HeapObject::MethodInfoObj { method_idx: i });
                                push(interp, Value::Ref(r));
                            }
                            None => push(interp, Value::Null),
                        }
                        return Ok(());
                    }
                    "GetFields()" => {
                        // Public fields, own + inherited (default binding).
                        let fields = match type_idx {
                            Some(ti) => collect_public_fields(interp.module, ti),
                            None => Vec::new(),
                        };
                        let mut data = Vec::with_capacity(fields.len());
                        for (name, slot, is_static) in fields {
                            data.push(Value::Ref(interp.heap.alloc(
                                HeapObject::FieldInfoObj { slot, is_static, name },
                            )));
                        }
                        let arr = interp.heap.alloc(HeapObject::Array {
                            elem: crate::value::ElemType::Ref,
                            data,
                        });
                        push(interp, Value::Ref(arr));
                        return Ok(());
                    }
                    "GetField(string)" => {
                        let wanted = match interp.deref(*args.last().unwrap())? {
                            Value::Ref(sr) => match interp.heap.get(sr)? {
                                HeapObject::Str(s) => s.clone(),
                                _ => String::new(),
                            },
                            _ => String::new(),
                        };
                        let found = match type_idx {
                            Some(ti) => collect_public_fields(interp.module, ti)
                                .into_iter()
                                .find(|(n, _, _)| *n == wanted),
                            None => None,
                        };
                        match found {
                            Some((name, slot, is_static)) => {
                                let r = interp.heap.alloc(HeapObject::FieldInfoObj {
                                    slot,
                                    is_static,
                                    name,
                                });
                                push(interp, Value::Ref(r));
                            }
                            None => push(interp, Value::Null),
                        }
                        return Ok(());
                    }
                    "GetProperties()" => {
                        let props = match type_idx {
                            Some(ti) => collect_properties(interp.module, ti),
                            None => Vec::new(),
                        };
                        let mut data = Vec::with_capacity(props.len());
                        for (name, getter, setter) in props {
                            data.push(Value::Ref(interp.heap.alloc(
                                HeapObject::PropertyInfoObj { getter, setter, name },
                            )));
                        }
                        let arr = interp.heap.alloc(HeapObject::Array {
                            elem: crate::value::ElemType::Ref,
                            data,
                        });
                        push(interp, Value::Ref(arr));
                        return Ok(());
                    }
                    "GetProperty(string)" => {
                        let wanted = match interp.deref(*args.last().unwrap())? {
                            Value::Ref(sr) => match interp.heap.get(sr)? {
                                HeapObject::Str(s) => s.clone(),
                                _ => String::new(),
                            },
                            _ => String::new(),
                        };
                        let found = match type_idx {
                            Some(ti) => collect_properties(interp.module, ti)
                                .into_iter()
                                .find(|(n, _, _)| *n == wanted),
                            None => None,
                        };
                        match found {
                            Some((name, getter, setter)) => {
                                let r = interp.heap.alloc(HeapObject::PropertyInfoObj {
                                    getter,
                                    setter,
                                    name,
                                });
                                push(interp, Value::Ref(r));
                            }
                            None => push(interp, Value::Null),
                        }
                        return Ok(());
                    }
                    "get_BaseType()" => {
                // Parent RNX type if known; otherwise everything but Object
                // derives from System.Object.
                let parent = type_idx
                    .and_then(|ti| interp.module.types.get(ti as usize))
                    .map(|td| td.parent)
                    .unwrap_or(crate::rnx::NO_TYPE);
                if parent != crate::rnx::NO_TYPE {
                    let pname = interp.module.type_name(parent as u16).to_string();
                    let r = interp
                        .heap
                        .alloc(HeapObject::TypeObj { type_idx: Some(parent as u16), name: pname });
                    push(interp, Value::Ref(r));
                } else if full != "System.Object" {
                    let r = interp.heap.alloc(HeapObject::TypeObj {
                        type_idx: None,
                        name: "System.Object".to_string(),
                    });
                    push(interp, Value::Ref(r));
                } else {
                    push(interp, Value::Null); // Object.BaseType is null
                }
                        return Ok(());
                    }
                    // `Type.GetCustomAttributes(bool)` / `(Type, bool)`:
                    // instantiate the user attributes applied to this type.
                    _ if short.starts_with("GetCustomAttributes(") => {
                        let descs: Vec<crate::rnx::AttrDesc> = match type_idx {
                            Some(ti) => interp
                                .module
                                .types
                                .get(ti as usize)
                                .map(|t| t.attrs.clone())
                                .unwrap_or_default(),
                            None => Vec::new(),
                        };
                        // Optional attribute-type filter (the 3-arg overload).
                        let filter: Option<String> = if args.len() >= 3 {
                            match interp.deref(args[1])? {
                                Value::Ref(fr) => match interp.heap.get(fr)? {
                                    HeapObject::TypeObj { name, .. } => Some(name.clone()),
                                    _ => None,
                                },
                                _ => None,
                            }
                        } else {
                            None
                        };
                        let mut data = Vec::new();
                        for d in descs {
                            let inst = build_attribute(interp, d)?;
                            if let Some(ref want) = filter {
                                if let Value::Ref(ir) = inst {
                                    if let Ok(HeapObject::Object { type_idx: ati, .. }) =
                                        interp.heap.get(ir)
                                    {
                                        if !attr_type_matches(interp.module, *ati, want) {
                                            continue;
                                        }
                                    }
                                }
                            }
                            data.push(inst);
                        }
                        let arr = interp.heap.alloc(HeapObject::Array {
                            elem: crate::value::ElemType::Ref,
                            data,
                        });
                        push(interp, Value::Ref(arr));
                        return Ok(());
                    }
                    _ => {}
                }
            }
        }
    }
    // Members on a `System.Reflection.MethodInfo` receiver.
    if !args.is_empty() {
        if let Ok(Value::Ref(r)) = interp.deref(args[0]) {
            if let Ok(HeapObject::MethodInfoObj { method_idx }) = interp.heap.get(r) {
                let method_idx = *method_idx;
                let short = name.rsplit("::").next().unwrap_or("");
                if short == "get_Name()" || short == "ToString()" {
                    let simple = method_simple_name(interp.module, method_idx);
                    let r = interp.heap.alloc_str(simple);
                    push(interp, Value::Ref(r));
                    return Ok(());
                }
                // `MethodInfo.Invoke(object target, object[] args)`.
                if short.starts_with("Invoke(") && args.len() >= 3 {
                    return reflect_invoke(interp, method_idx, args[1], args[2]);
                }
            }
        }
    }
    // Members on a `System.Reflection.FieldInfo` receiver.
    if !args.is_empty() {
        if let Ok(Value::Ref(r)) = interp.deref(args[0]) {
            if let Ok(HeapObject::FieldInfoObj { slot, is_static, name: fname }) =
                interp.heap.get(r)
            {
                let (slot, is_static, fname) = (*slot, *is_static, fname.clone());
                let short = name.rsplit("::").next().unwrap_or("");
                if short == "get_Name()" || short == "ToString()" {
                    let rr = interp.heap.alloc_str(fname);
                    push(interp, Value::Ref(rr));
                    return Ok(());
                }
                // `FieldInfo.GetValue(object obj)`.
                if short.starts_with("GetValue(") && args.len() >= 2 {
                    let v = if is_static {
                        *interp
                            .statics
                            .get(slot as usize)
                            .ok_or("FieldInfo.GetValue: static slot out of range")?
                    } else {
                        match interp.deref(args[1])? {
                            Value::Ref(or) => match interp.heap.get(or)? {
                                HeapObject::Object { fields, .. } => *fields
                                    .get(slot as usize)
                                    .ok_or("FieldInfo.GetValue: field slot out of range")?,
                                _ => return Err("FieldInfo.GetValue: not an object".into()),
                            },
                            Value::Null => return Err("NullReferenceException".into()),
                            other => return Err(format!("FieldInfo.GetValue on {other:?}")),
                        }
                    };
                    push(interp, v);
                    return Ok(());
                }
                // `FieldInfo.SetValue(object obj, object value)`.
                if short.starts_with("SetValue(") && args.len() >= 3 {
                    let mut val = interp.deref(args[2])?;
                    if let Value::Ref(vr) = val {
                        if let Ok(HeapObject::Boxed(inner)) = interp.heap.get(vr) {
                            val = *inner;
                        }
                    }
                    if is_static {
                        *interp
                            .statics
                            .get_mut(slot as usize)
                            .ok_or("FieldInfo.SetValue: static slot out of range")? = val;
                    } else {
                        match interp.deref(args[1])? {
                            Value::Ref(or) => match interp.heap.get_mut(or)? {
                                HeapObject::Object { fields, .. } => {
                                    *fields
                                        .get_mut(slot as usize)
                                        .ok_or("FieldInfo.SetValue: field slot out of range")? =
                                        val;
                                }
                                _ => return Err("FieldInfo.SetValue: not an object".into()),
                            },
                            Value::Null => return Err("NullReferenceException".into()),
                            other => return Err(format!("FieldInfo.SetValue on {other:?}")),
                        }
                    }
                    return Ok(());
                }
            }
        }
    }
    // Members on a `System.Reflection.PropertyInfo` receiver.
    if !args.is_empty() {
        if let Ok(Value::Ref(r)) = interp.deref(args[0]) {
            if let Ok(HeapObject::PropertyInfoObj { getter, setter, name: pname }) =
                interp.heap.get(r)
            {
                let (getter, setter, pname) = (*getter, *setter, pname.clone());
                let short = name.rsplit("::").next().unwrap_or("");
                if short == "get_Name()" || short == "ToString()" {
                    let rr = interp.heap.alloc_str(pname);
                    push(interp, Value::Ref(rr));
                    return Ok(());
                }
                // `PropertyInfo.GetValue(object obj)` -> invoke the getter.
                if short.starts_with("GetValue(") && args.len() >= 2 {
                    let obj = interp.deref(args[1])?;
                    return match getter {
                        Some(g) => dispatch_delegate(interp, g, obj, &[]),
                        None => Err(format!("property {pname} has no getter")),
                    };
                }
                // `PropertyInfo.SetValue(object obj, object value)` -> the setter.
                if short.starts_with("SetValue(") && args.len() >= 3 {
                    let obj = interp.deref(args[1])?;
                    let mut val = interp.deref(args[2])?;
                    if let Value::Ref(vr) = val {
                        if let Ok(HeapObject::Boxed(inner)) = interp.heap.get(vr) {
                            val = *inner;
                        }
                    }
                    return match setter {
                        Some(s) => dispatch_delegate(interp, s, obj, &[val]),
                        None => Err(format!("property {pname} has no setter")),
                    };
                }
            }
        }
    }

    // -- delegate invocation -------------------------------------------
    if name.contains("::Invoke(") && !args.is_empty() {
        let this = interp.deref(args[0])?;
        if let Value::Ref(r) = this {
            if let Ok(HeapObject::Delegate { method, target }) = interp.heap.get(r) {
                let (method, target) = (*method, *target);
                return dispatch_delegate(interp, method, target, &args[1..]);
            }
        }
    }

    // -- Console ------------------------------------------------------
    if let Some(rest) = name.strip_prefix("System.Console::") {
        let newline = rest.starts_with("WriteLine");
        let mut text = String::new();
        if rest.ends_with("(bool)") && args.len() == 1 {
            let v = interp.deref(args[0])?.as_i32()?;
            text.push_str(if v != 0 { "True" } else { "False" });
        } else {
            for a in &args {
                let v = interp.deref(*a)?;
                text.push_str(&interp.display_value(v)?);
            }
        }
        if newline {
            text.push('\n');
        }
        interp.host.console_write(&text);
        return Ok(());
    }

    // -- String / StringBuilder ----------------------------------------
    if let Some(rest) = name.strip_prefix("System.String::") {
        return textenc::string_intrinsic(interp, rest, &args);
    }
    if let Some(rest) = name.strip_prefix("System.Text.StringBuilder::") {
        return textenc::stringbuilder_intrinsic(interp, rest, &args, is_newobj);
    }

    // -- Math / Random / Char / numeric parse --------------------------
    if let Some(rest) = name.strip_prefix("System.Math::") {
        return math_intrinsic(interp, rest, &args);
    }
    if let Some(rest) = name.strip_prefix("System.Random::") {
        return random_intrinsic(interp, rest, &args, is_newobj);
    }
    if let Some(rest) = name.strip_prefix("System.Char::") {
        return textenc::char_intrinsic(interp, rest, &args);
    }
    if name == "System.Int32::Parse(string)" || name == "System.Int64::Parse(string)" {
        let s = str_arg(interp, args[0])?;
        let v: i64 = s.trim().parse().map_err(|_| format!("FormatException: '{s}'"))?;
        if name.contains("Int32") {
            push(interp, Value::I32(v as i32));
        } else {
            push(interp, Value::I64(v));
        }
        return Ok(());
    }
    if name == "System.Double::Parse(string)" || name == "System.Single::Parse(string)" {
        let s = str_arg(interp, args[0])?;
        let v: f64 = s.trim().parse().map_err(|_| format!("FormatException: '{s}'"))?;
        push(interp, Value::F64(v));
        return Ok(());
    }

    // -- Convert / BitConverter / Encoding / Regex ---------------------
    if let Some(rest) = name.strip_prefix("System.Convert::") {
        return textenc::convert_intrinsic(interp, rest, &args);
    }
    if let Some(rest) = name.strip_prefix("System.BitConverter::") {
        return textenc::bitconverter_intrinsic(interp, rest, &args);
    }
    if let Some(rest) = name.strip_prefix("System.Text.Encoding::") {
        return textenc::encoding_intrinsic(interp, rest, &args);
    }
    if let Some(rest) = name.strip_prefix("System.Text.RegularExpressions.Regex::") {
        return textenc::regex_intrinsic(interp, rest, &args, is_newobj);
    }

    // -- LINQ ----------------------------------------------------------
    if let Some(rest) = name.strip_prefix("System.Linq.Enumerable::") {
        return linq::linq_intrinsic(interp, rest, &args);
    }

    // -- Collections ---------------------------------------------------
    if name.starts_with("System.Collections.Generic.List`1::")
        || name.starts_with("System.Collections.Generic.List<")
    {
        let rest = name.split("::").nth(1).unwrap_or("");
        return collections::list_intrinsic(interp, rest, &args, is_newobj);
    }
    if name.starts_with("System.Collections.Generic.Dictionary`2::") {
        let rest = name.split("::").nth(1).unwrap_or("");
        return collections::dict_intrinsic(interp, rest, &args, is_newobj);
    }
    if name.starts_with("System.Collections.Generic.Queue`1::") {
        let rest = name.split("::").nth(1).unwrap_or("");
        return collections::queue_intrinsic(interp, rest, &args, is_newobj);
    }
    if name.starts_with("System.Collections.Generic.Stack`1::") {
        let rest = name.split("::").nth(1).unwrap_or("");
        return collections::stack_intrinsic(interp, rest, &args, is_newobj);
    }
    if name.contains("KeyValuePair") && (name.ends_with("::get_Key()") || name.ends_with("::get_Value()")) {
        return collections::kvp_intrinsic(interp, &name, &args);
    }

    // -- Tuple ---------------------------------------------------------
    if name.starts_with("System.Tuple") {
        return collections::tuple_intrinsic(interp, &name, &args, is_newobj);
    }

    // -- Threading -----------------------------------------------------
    if let Some(rest) = name.strip_prefix("System.Threading.Thread::") {
        return thread_intrinsic(interp, rest, &args, is_newobj);
    }
    if name.starts_with("System.Threading.Monitor::") {
        return Ok(()); // cooperative scheduler: critical sections are atomic
    }
    if let Some(rest) = name.strip_prefix("System.Threading.Interlocked::") {
        return interlocked_intrinsic(interp, rest, &args);
    }
    if name.starts_with("System.Threading.Tasks.Task") {
        let rest = name.split("::").nth(1).unwrap_or("");
        return task_intrinsic(interp, rest, &args);
    }
    if name.starts_with("System.Runtime.CompilerServices.AsyncTaskMethodBuilder") {
        let rest = name.split("::").nth(1).unwrap_or("");
        return async_builder_intrinsic(interp, rest, &args);
    }
    if name.starts_with("System.Runtime.CompilerServices.TaskAwaiter") {
        let generic = name.contains("TaskAwaiter`1");
        let rest = name.split("::").nth(1).unwrap_or("");
        return task_awaiter_intrinsic(interp, rest, &args, generic);
    }
    // Kick-off wrapper: YieldAwaitable from Task.Yield().
    if name.starts_with("System.Runtime.CompilerServices.YieldAwaitable") {
        let rest = name.split("::").nth(1).unwrap_or("");
        return yield_awaitable_intrinsic(interp, rest, &args);
    }

    // -- Exceptions ----------------------------------------------------
    if name.contains("Exception::") {
        if is_newobj && name.ends_with("::.ctor(string)") {
            let msg = args[0];
            let v = match interp.deref(msg)? {
                Value::Ref(r) => Value::Ref(r),
                _ => Value::Ref(interp.heap.alloc_str(String::new())),
            };
            push(interp, v);
            return Ok(());
        }
        if is_newobj && name.ends_with("::.ctor()") {
            let type_name = name.split("::").next().unwrap_or("Exception");
            let short = type_name.rsplit('.').next().unwrap_or("Exception").to_string();
            let r = interp.heap.alloc_str(short);
            push(interp, Value::Ref(r));
            return Ok(());
        }
        if name.ends_with("::get_Message()") {
            let this = interp.deref(args[0])?;
            push(interp, this);
            return Ok(());
        }
    }

    // -- Environment / GC ----------------------------------------------
    if name == "System.Environment::get_TickCount()" {
        let t = interp.host.now_ms() as i32;
        push(interp, Value::I32(t));
        return Ok(());
    }
    if name == "System.Environment::get_NewLine()" {
        let r = interp.heap.alloc_str("\n");
        push(interp, Value::Ref(r));
        return Ok(());
    }
    if name == "System.GC::Collect()" {
        interp.collect_garbage();
        return Ok(());
    }

    // -- enumerator catch-alls (foreach over List/Dictionary/arrays) ----
    if name.ends_with("::GetEnumerator()") {
        return collections::get_enumerator(interp, &args);
    }
    if name.ends_with("::MoveNext()") {
        return collections::cursor_move_next(interp, &args);
    }
    if name.ends_with("::get_Current()") {
        return collections::cursor_current(interp, &args);
    }
    if name.ends_with("::Dispose()") {
        return Ok(());
    }

    // -- value formatting ----------------------------------------------
    if name == "System.Boolean::ToString()" {
        let v = interp.deref(args[0])?.as_i32()?;
        let r = interp.heap.alloc_str(if v != 0 { "True" } else { "False" });
        push(interp, Value::Ref(r));
        return Ok(());
    }
    if name.ends_with("::ToString()") {
        let this = interp.deref(args[0])?;
        let s = interp.display_value(this)?;
        let r = interp.heap.alloc_str(s);
        push(interp, Value::Ref(r));
        return Ok(());
    }

    // -- RustNet corlib helpers ----------------------------------------
    match name.as_str() {
        "RustNet.Threading.Sleep::Ms(i4)" => {
            let ms = interp.deref(args[0])?.as_i32()?;
            interp.request_sleep(ms.max(0) as u64);
            return Ok(());
        }
        "RustNet.Sys.Uptime::Ms()" => {
            let now = interp.host.now_ms();
            push(interp, Value::I64(now as i64));
            return Ok(());
        }
        // Embedded resources travel in the RNX module (RNX v4).
        "RustNet.Resources.Resource::Exists(string)" => {
            let n = str_arg(interp, args[0])?;
            let found = interp.module.resource(&n).is_some();
            push(interp, Value::I32(found as i32));
            return Ok(());
        }
        "RustNet.Resources.Resource::GetBytes(string)" => {
            let n = str_arg(interp, args[0])?;
            let bytes = interp
                .module
                .resource(&n)
                .ok_or_else(|| format!("resource not found: {n}"))?
                .to_vec();
            let arr = alloc_byte_array(interp, &bytes);
            push(interp, arr);
            return Ok(());
        }
        "RustNet.Resources.Resource::GetString(string)" => {
            let n = str_arg(interp, args[0])?;
            let bytes = interp
                .module
                .resource(&n)
                .ok_or_else(|| format!("resource not found: {n}"))?;
            let s = String::from_utf8_lossy(bytes).to_string();
            let r = interp.heap.alloc_str(s);
            push(interp, Value::Ref(r));
            return Ok(());
        }
        _ => {}
    }

    // -- Everything else goes to the host ------------------------------
    let mut host_args = Vec::with_capacity(args.len());
    for a in &args {
        let v = interp.deref(*a)?;
        host_args.push(to_host_value(interp, v)?);
    }
    let result = interp.host.invoke(&name, host_args)?;
    if let Some(v) = from_host_value(interp, result) {
        push(interp, v);
    }
    Ok(())
}

/// Delegate call: managed target gets a real frame; internal target routes
/// straight back through the dispatcher.
fn dispatch_delegate<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    method: u32,
    target: Value,
    call_args: &[Value],
) -> Result<(), String> {
    let m = interp
        .module
        .methods
        .get(method as usize)
        .ok_or("bad delegate method index")?;
    if m.is_internal() {
        // Re-push args and dispatch.
        let f = interp.frames.last_mut().unwrap();
        if !m.is_static() {
            f.stack.push(target);
        }
        for a in call_args {
            f.stack.push(*a);
        }
        return call_internal(interp, method, false);
    }
    let mut args = Vec::with_capacity(call_args.len() + 1);
    if !m.is_static() {
        args.push(target);
    }
    args.extend_from_slice(call_args);
    interp.push_frame_public(method, args)
}

fn thread_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
    is_newobj: bool,
) -> Result<(), String> {
    match rest {
        ".ctor(object)" if is_newobj => {
            let delegate = match interp.deref(args[0])? {
                Value::Ref(r) => r,
                other => return Err(format!("Thread ctor expects delegate, got {other:?}")),
            };
            let r = interp.heap.alloc(HeapObject::ThreadObj { delegate, thread_idx: None });
            push(interp, Value::Ref(r));
            Ok(())
        }
        "Start()" => {
            let this = ref_arg(interp, args[0])?;
            let delegate = match interp.heap.get(this)? {
                HeapObject::ThreadObj { delegate, .. } => *delegate,
                other => return Err(format!("Thread.Start on {other:?}")),
            };
            let idx = interp.spawn_thread(delegate)?;
            if let HeapObject::ThreadObj { thread_idx, .. } = interp.heap.get_mut(this)? {
                *thread_idx = Some(idx);
            }
            Ok(())
        }
        "Join()" => {
            let this = ref_arg(interp, args[0])?;
            if let HeapObject::ThreadObj { thread_idx: Some(idx), .. } = interp.heap.get(this)? {
                let idx = *idx;
                interp.join_thread(idx);
            }
            Ok(())
        }
        "get_IsAlive()" => {
            let this = ref_arg(interp, args[0])?;
            let alive = match interp.heap.get(this)? {
                HeapObject::ThreadObj { thread_idx: Some(idx), .. } => {
                    !interp.thread_finished(*idx)
                }
                _ => false,
            };
            push(interp, Value::I32(alive as i32));
            Ok(())
        }
        "Sleep(i4)" => {
            let ms = interp.deref(args[0])?.as_i32()?;
            interp.request_sleep(ms.max(0) as u64);
            Ok(())
        }
        other => Err(format!("unsupported Thread intrinsic: {other}")),
    }
}

/// Retry the current call after the task settles: restore the popped
/// arguments, rewind to the call instruction and park the thread.
fn wait_and_retry<H: RuntimeHost>(interp: &mut Interpreter<'_, H>, task: u32, args: &[Value]) {
    let f = interp.frames.last_mut().expect("no frame in wait_and_retry");
    for a in args {
        f.stack.push(*a);
    }
    f.ip = f.instr_ip;
    interp.request_wait_task(task);
}

/// Resolve a value that should be (or hold) a task reference.
fn task_ref<H: RuntimeHost>(interp: &Interpreter<'_, H>, v: Value) -> Result<u32, String> {
    match v {
        Value::Ref(r) => match interp.heap.get(r)? {
            HeapObject::TaskObj { .. } => Ok(r),
            other => Err(format!("expected Task, got {other:?}")),
        },
        other => Err(format!("expected Task, got {other:?}")),
    }
}

/// Task state snapshot: (state, value).
fn task_state<H: RuntimeHost>(interp: &Interpreter<'_, H>, task: u32) -> (u8, Value) {
    match interp.heap.get(task) {
        Ok(HeapObject::TaskObj { state, value, .. }) => (*state, *value),
        _ => (crate::value::TASK_FAULTED, Value::Null),
    }
}

fn task_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
) -> Result<(), String> {
    use crate::value::{TASK_DONE, TASK_FAULTED, TASK_PENDING};
    match rest.split('(').next().unwrap_or(rest) {
        "Run" => {
            let delegate = match interp.deref(args[0])? {
                Value::Ref(r) => r,
                other => return Err(format!("Task.Run expects delegate, got {other:?}")),
            };
            let idx = interp.spawn_thread(delegate)?;
            let task = interp.heap.alloc(HeapObject::TaskObj {
                state: TASK_PENDING,
                value: Value::Null,
                continuations: Vec::new(),
            });
            interp.set_thread_completes(idx, task);
            push(interp, Value::Ref(task));
            Ok(())
        }
        "Delay" => {
            let ms = interp.deref(args[0])?.as_i32()?;
            let task = interp.spawn_delay_task(ms.max(0) as u64);
            push(interp, Value::Ref(task));
            Ok(())
        }
        "FromResult" => {
            let value = interp.deref(args[0])?;
            let task = interp.heap.alloc(HeapObject::TaskObj {
                state: TASK_DONE,
                value,
                continuations: Vec::new(),
            });
            push(interp, Value::Ref(task));
            Ok(())
        }
        "get_CompletedTask" | "Yield" => {
            let task = interp.heap.alloc(HeapObject::TaskObj {
                state: TASK_DONE,
                value: Value::Null,
                continuations: Vec::new(),
            });
            push(interp, Value::Ref(task));
            Ok(())
        }
        "GetAwaiter" => {
            // The awaiter IS the task reference.
            let this = interp.deref(args[0])?;
            let task = task_ref(interp, this)?;
            push(interp, Value::Ref(task));
            Ok(())
        }
        "get_IsCompleted" => {
            let this = interp.deref(args[0])?;
            let task = task_ref(interp, this)?;
            let (state, _) = task_state(interp, task);
            push(interp, Value::I32((state != TASK_PENDING) as i32));
            Ok(())
        }
        op @ ("Wait" | "get_Result") => {
            let this = interp.deref(args[0])?;
            // Legacy: Thread-backed handles still support Wait via join.
            if let Value::Ref(r) = this {
                if let Ok(HeapObject::ThreadObj { thread_idx: Some(idx), .. }) = interp.heap.get(r)
                {
                    let idx = *idx;
                    interp.join_thread(idx);
                    return Ok(());
                }
            }
            let task = task_ref(interp, this)?;
            match task_state(interp, task) {
                (TASK_PENDING, _) => {
                    wait_and_retry(interp, task, args);
                    Ok(())
                }
                (TASK_FAULTED, err) => {
                    let exc = interp.deref(err)?;
                    interp.raise(exc)
                }
                (_, value) => {
                    if op == "get_Result" {
                        push(interp, value);
                    }
                    Ok(())
                }
            }
        }
        other => Err(format!("unsupported Task intrinsic: {other}")),
    }
}

/// AsyncTaskMethodBuilder / AsyncTaskMethodBuilder`1: the builder value is
/// the task reference itself. Byref args (ldflda/ldloca) deref cleanly
/// because the state machine is a class in Debug lowering.
fn async_builder_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
) -> Result<(), String> {
    use crate::value::TASK_PENDING;
    let op = rest.split('(').next().unwrap_or(rest);
    match op {
        "Create" => {
            let task = interp.heap.alloc(HeapObject::TaskObj {
                state: TASK_PENDING,
                value: Value::Null,
                continuations: Vec::new(),
            });
            push(interp, Value::Ref(task));
            Ok(())
        }
        "Start" => {
            // args: [builder&, sm&] — run MoveNext synchronously to the
            // first await by pushing its frame.
            let sm = match interp.deref(args[1])? {
                Value::Ref(r) => r,
                other => {
                    return Err(format!(
                        "async Start expects a class state machine, got {other:?} \
                         (build with Debug configuration)"
                    ))
                }
            };
            let type_idx = match interp.heap.get(sm)? {
                HeapObject::Object { type_idx, .. } => *type_idx,
                other => return Err(format!("async Start on {other:?}")),
            };
            let method = interp
                .find_move_next(type_idx)
                .ok_or("async state machine has no MoveNext")?;
            interp.push_frame_public(method, vec![Value::Ref(sm)])
        }
        "get_Task" => {
            let v = interp.deref(args[0])?;
            let task = task_ref(interp, v)?;
            push(interp, Value::Ref(task));
            Ok(())
        }
        "SetResult" => {
            let v = interp.deref(args[0])?;
            let task = task_ref(interp, v)?;
            let value = if args.len() > 1 { interp.deref(args[1])? } else { Value::Null };
            interp.complete_task(task, value, false)
        }
        "SetException" => {
            let v = interp.deref(args[0])?;
            let task = task_ref(interp, v)?;
            let exc = interp.deref(args[1])?;
            interp.complete_task(task, exc, true)
        }
        "AwaitUnsafeOnCompleted" | "AwaitOnCompleted" => {
            // args: [builder&, awaiter&, sm&]
            let av = interp.deref(args[1])?;
            let awaited = task_ref(interp, av)?;
            let sm = match interp.deref(args[2])? {
                Value::Ref(r) => r,
                other => return Err(format!("await continuation on {other:?}")),
            };
            let pending = matches!(
                interp.heap.get(awaited)?,
                HeapObject::TaskObj { state: 0, .. }
            );
            if pending {
                if let HeapObject::TaskObj { continuations, .. } = interp.heap.get_mut(awaited)? {
                    continuations.push(sm);
                }
                Ok(())
            } else {
                interp.spawn_continuation(sm)
            }
        }
        "SetStateMachine" => Ok(()),
        other => Err(format!("unsupported AsyncTaskMethodBuilder intrinsic: {other}")),
    }
}

/// TaskAwaiter / TaskAwaiter`1: awaiter values are task references.
fn task_awaiter_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
    generic: bool,
) -> Result<(), String> {
    use crate::value::{TASK_FAULTED, TASK_PENDING};
    let op = rest.split('(').next().unwrap_or(rest);
    match op {
        "get_IsCompleted" => {
            let v = interp.deref(args[0])?;
            let task = task_ref(interp, v)?;
            let (state, _) = task_state(interp, task);
            push(interp, Value::I32((state != TASK_PENDING) as i32));
            Ok(())
        }
        "GetResult" => {
            let v = interp.deref(args[0])?;
            let task = task_ref(interp, v)?;
            match task_state(interp, task) {
                (TASK_FAULTED, err) => {
                    let exc = interp.deref(err)?;
                    interp.raise(exc)
                }
                (_, value) => {
                    if generic {
                        push(interp, value);
                    }
                    Ok(())
                }
            }
        }
        "UnsafeOnCompleted" | "OnCompleted" => {
            // Delegate-based continuation (rare without the builder).
            let v = interp.deref(args[0])?;
            let task = task_ref(interp, v)?;
            let cont = match interp.deref(args[1])? {
                Value::Ref(r) => r,
                other => return Err(format!("OnCompleted expects delegate, got {other:?}")),
            };
            let pending = matches!(interp.heap.get(task)?, HeapObject::TaskObj { state: 0, .. });
            if pending {
                // Continuations are state machines; wrap the delegate call
                // by spawning it as a thread once complete is not modeled —
                // run it now if already complete, else spawn on completion
                // is unsupported for raw delegates.
                Err("raw awaiter OnCompleted with pending task is not supported".into())
            } else {
                let idx = interp.spawn_thread(cont)?;
                let _ = idx;
                Ok(())
            }
        }
        other => Err(format!("unsupported TaskAwaiter intrinsic: {other}")),
    }
}

/// Task.Yield(): awaiting it reschedules the state machine immediately.
fn yield_awaitable_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    _args: &[Value],
) -> Result<(), String> {
    use crate::value::TASK_DONE;
    let op = rest.split('(').next().unwrap_or(rest);
    match op {
        // Yield's awaiter behaves like an already-completed task.
        "GetAwaiter" | "get_IsCompleted" | "GetResult" => {
            if op == "GetAwaiter" {
                let task = interp.heap.alloc(HeapObject::TaskObj {
                    state: TASK_DONE,
                    value: Value::Null,
                    continuations: Vec::new(),
                });
                push(interp, Value::Ref(task));
            } else if op == "get_IsCompleted" {
                push(interp, Value::I32(1));
            }
            Ok(())
        }
        other => Err(format!("unsupported YieldAwaitable intrinsic: {other}")),
    }
}

fn interlocked_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    let addr = match args[0] {
        Value::Addr(a) => a,
        other => return Err(format!("Interlocked needs a byref, got {other:?}")),
    };
    let current = interp.load_addr(addr)?.as_i64()?;
    let (new, ret) = match op {
        "Increment" => (current + 1, current + 1),
        "Decrement" => (current - 1, current - 1),
        "Add" => {
            let d = interp.deref(args[1])?.as_i64()?;
            (current + d, current + d)
        }
        "Exchange" => {
            let v = interp.deref(args[1])?.as_i64()?;
            (v, current)
        }
        other => return Err(format!("unsupported Interlocked intrinsic: {other}")),
    };
    let stored = match interp.load_addr(addr)? {
        Value::I64(_) => Value::I64(new),
        _ => Value::I32(new as i32),
    };
    interp.store_addr(addr, stored)?;
    push(interp, match stored {
        Value::I64(_) => Value::I64(ret),
        _ => Value::I32(ret as i32),
    });
    Ok(())
}

fn math_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    let int_args = rest.contains("(i4") || rest.contains("(i8");
    match op {
        "Abs" | "Max" | "Min" if int_args => {
            let a = interp.deref(args[0])?.as_i64()?;
            let out = match op {
                "Abs" => a.abs(),
                _ => {
                    let b = interp.deref(args[1])?.as_i64()?;
                    if op == "Max" { a.max(b) } else { a.min(b) }
                }
            };
            if rest.contains("(i8") {
                push(interp, Value::I64(out));
            } else {
                push(interp, Value::I32(out as i32));
            }
        }
        _ => {
            let a = interp.deref(args[0])?.as_f64()?;
            let out = match op {
                "Sqrt" => crate::fmath::sqrt(a),
                "Abs" => crate::fmath::fabs(a),
                "Sin" => crate::fmath::sin(a),
                "Cos" => crate::fmath::cos(a),
                "Tan" => crate::fmath::tan(a),
                "Atan" => crate::fmath::atan(a),
                "Log" => crate::fmath::ln(a),
                "Log10" => crate::fmath::log10(a),
                "Exp" => crate::fmath::exp(a),
                "Floor" => crate::fmath::floor(a),
                "Ceiling" => crate::fmath::ceil(a),
                "Round" => crate::fmath::round(a),
                "Atan2" | "Pow" | "Max" | "Min" => {
                    let b = interp.deref(args[1])?.as_f64()?;
                    match op {
                        "Atan2" => crate::fmath::atan2(a, b),
                        "Pow" => crate::fmath::pow(a, b),
                        "Max" => a.max(b),
                        _ => a.min(b),
                    }
                }
                other => return Err(format!("unsupported Math intrinsic: {other}")),
            };
            push(interp, Value::F64(out));
        }
    }
    Ok(())
}

fn random_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
    is_newobj: bool,
) -> Result<(), String> {
    if is_newobj {
        let seed = if !args.is_empty() {
            interp.deref(args[0])?.as_i64()? as u64
        } else {
            interp.host.now_ms() ^ 0x9E37_79B9_7F4A_7C15
        };
        let r = interp.heap.alloc(HeapObject::Boxed(Value::I64(seed.max(1) as i64)));
        push(interp, Value::Ref(r));
        return Ok(());
    }
    let this = interp.deref(args[0])?;
    let Value::Ref(state_ref) = this else {
        return Err("Random instance expected".into());
    };
    let mut state = match interp.heap.get(state_ref)? {
        HeapObject::Boxed(Value::I64(s)) => *s as u64,
        _ => return Err("corrupt Random state".into()),
    };
    state ^= state >> 12;
    state ^= state << 25;
    state ^= state >> 27;
    *interp.heap.get_mut(state_ref)? = HeapObject::Boxed(Value::I64(state as i64));
    let raw = (state.wrapping_mul(0x2545_F491_4F6C_DD1D) >> 33) as i32 & i32::MAX;
    if rest.starts_with("NextDouble") {
        push(interp, Value::F64(raw as f64 / i32::MAX as f64));
        return Ok(());
    }
    let value = if rest.starts_with("Next()") {
        raw
    } else if rest.starts_with("Next(i4)") {
        let max = interp.deref(args[1])?.as_i32()?;
        if max <= 0 { 0 } else { raw % max }
    } else if rest.starts_with("Next(i4,i4)") {
        let min = interp.deref(args[1])?.as_i32()?;
        let max = interp.deref(args[2])?.as_i32()?;
        if max <= min { min } else { min + raw % (max - min) }
    } else {
        return Err(format!("unsupported Random intrinsic: {rest}"));
    };
    push(interp, Value::I32(value));
    Ok(())
}

// ------------------------------------------------------------------
// shared helpers (used by the sibling intrinsic modules)
// ------------------------------------------------------------------

pub(crate) fn push<H: RuntimeHost>(interp: &mut Interpreter<'_, H>, v: Value) {
    interp.frames.last_mut().unwrap().stack.push(v);
}

pub(crate) fn str_arg<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    v: Value,
) -> Result<String, String> {
    let v = interp.deref(v)?;
    match v {
        Value::Ref(r) => match interp.heap.get(r)? {
            HeapObject::Str(s) => Ok(s.clone()),
            _ => interp.display_value(v),
        },
        Value::Null => Ok(String::new()),
        other => interp.display_value(other),
    }
}

pub(crate) fn ref_arg<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    v: Value,
) -> Result<u32, String> {
    match interp.deref(v)? {
        Value::Ref(r) => Ok(r),
        Value::Null => Err("NullReferenceException".into()),
        other => Err(format!("expected object reference, got {other:?}")),
    }
}

pub(crate) fn bytes_arg<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    v: Value,
) -> Result<Vec<u8>, String> {
    let r = ref_arg(interp, v)?;
    match interp.heap.get(r)? {
        HeapObject::Array { data, .. } => data
            .iter()
            .map(|v| v.as_i32().map(|x| x as u8))
            .collect::<Result<Vec<u8>, _>>(),
        other => Err(format!("expected byte[], got {other:?}")),
    }
}

pub(crate) fn alloc_byte_array<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    bytes: &[u8],
) -> Value {
    let data = bytes.iter().map(|b| Value::I32(*b as i32)).collect();
    Value::Ref(interp.heap.alloc(HeapObject::Array { elem: ElemType::U8, data }))
}

pub(crate) fn alloc_str_array<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    parts: Vec<String>,
) -> Value {
    let data = parts
        .into_iter()
        .map(|p| Value::Ref(interp.heap.alloc_str(p)))
        .collect();
    Value::Ref(interp.heap.alloc(HeapObject::Array { elem: ElemType::Ref, data }))
}

/// Total ordering used by Sort/OrderBy/Min/Max.
pub(crate) fn value_cmp<H: RuntimeHost>(
    interp: &Interpreter<'_, H>,
    a: Value,
    b: Value,
) -> core::cmp::Ordering {
    use core::cmp::Ordering;
    let str_of = |v: Value| -> Option<String> {
        if let Value::Ref(r) = v {
            if let Ok(HeapObject::Str(s)) = interp.heap.get(r) {
                return Some(s.clone());
            }
        }
        None
    };
    match (str_of(a), str_of(b)) {
        (Some(x), Some(y)) => x.cmp(&y),
        _ => {
            let x = a.as_f64().unwrap_or(0.0);
            let y = b.as_f64().unwrap_or(0.0);
            x.partial_cmp(&y).unwrap_or(Ordering::Equal)
        }
    }
}

/// Identity equality for reflection handles (used by `==`/`!=` on
/// Type/MethodInfo/MemberInfo): both null, same MethodInfo, or same Type name.
/// `MethodInfo.Invoke(object target, object[] args)`: unbox the boxed
/// argument array and call the method. A managed **void** method leaves the
/// pre-pushed `null` as the boxed return value; any other method's `ret`
/// supplies the `object` result the caller expects. Value-type results flow
/// back raw (the interpreter boxes on demand — `box`/`unbox.any` passthrough).
fn reflect_invoke<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    method_idx: u32,
    target_arg: Value,
    params_arg: Value,
) -> Result<(), String> {
    let target = interp.deref(target_arg)?;
    // Gather the parameter array (a null reference means "no arguments").
    let mut params: Vec<Value> = match interp.deref(params_arg)? {
        Value::Null => Vec::new(),
        Value::Ref(r) => match interp.heap.get(r)? {
            HeapObject::Array { data, .. } => data.clone(),
            _ => return Err("MethodInfo.Invoke: parameters must be object[]".into()),
        },
        other => return Err(format!("MethodInfo.Invoke: bad parameters {other:?}")),
    };
    // Value-type arguments arrive boxed (they were widened to `object`); the
    // callee expects raw values, so unwrap each box.
    for p in params.iter_mut() {
        if let Value::Ref(r) = *p {
            if let Ok(HeapObject::Boxed(inner)) = interp.heap.get(r) {
                *p = *inner;
            }
        }
    }
    let (is_internal, has_return) = interp
        .module
        .methods
        .get(method_idx as usize)
        .map(|m| (m.is_internal(), m.has_return()))
        .ok_or("MethodInfo.Invoke: bad method index")?;
    if !is_internal && !has_return {
        // Fill the caller's `object` result slot; the void `ret` adds nothing.
        push(interp, Value::Null);
    }
    dispatch_delegate(interp, method_idx, target, &params)
}

fn reflection_eq<H: RuntimeHost>(interp: &Interpreter<'_, H>, a: Value, b: Value) -> bool {
    match (a, b) {
        (Value::Null, Value::Null) => true,
        (Value::Null, _) | (_, Value::Null) => false,
        (Value::Ref(ra), Value::Ref(rb)) => {
            match (interp.heap.get(ra), interp.heap.get(rb)) {
                (
                    Ok(HeapObject::MethodInfoObj { method_idx: x }),
                    Ok(HeapObject::MethodInfoObj { method_idx: y }),
                ) => x == y,
                (Ok(HeapObject::TypeObj { name: x, .. }), Ok(HeapObject::TypeObj { name: y, .. })) => {
                    x == y
                }
                (
                    Ok(HeapObject::FieldInfoObj { slot: x, is_static: sx, .. }),
                    Ok(HeapObject::FieldInfoObj { slot: y, is_static: sy, .. }),
                ) => x == y && sx == sy,
                (
                    Ok(HeapObject::PropertyInfoObj { name: x, .. }),
                    Ok(HeapObject::PropertyInfoObj { name: y, .. }),
                ) => x == y,
                _ => ra == rb,
            }
        }
        _ => interp.value_eq(a, b),
    }
}

/// Public declared fields of `type_idx` and its ancestors (derived first),
/// as (simple name, slot, is_static). Mirrors `Type.GetFields()` default
/// binding: public instance + static, inherited included.
fn collect_public_fields(
    module: &crate::rnx::Module,
    type_idx: u16,
) -> alloc::vec::Vec<(String, u32, bool)> {
    let mut out = alloc::vec::Vec::new();
    let mut cur = Some(type_idx);
    while let Some(ti) = cur {
        let Some(td) = module.types.get(ti as usize) else {
            break;
        };
        for fd in &td.fields {
            if fd.is_public() {
                let name = module
                    .strings
                    .get(fd.name as usize)
                    .cloned()
                    .unwrap_or_default();
                out.push((name, fd.slot, fd.is_static()));
            }
        }
        cur = if td.parent != crate::rnx::NO_TYPE {
            Some(td.parent as u16)
        } else {
            None
        };
    }
    out
}

/// Properties of `type_idx` and its ancestors, discovered from `get_`/`set_`
/// accessor methods (paired by property name). Returns (name, getter, setter)
/// with the most-derived accessor kept. Mirrors `Type.GetProperties()`.
fn collect_properties(
    module: &crate::rnx::Module,
    type_idx: u16,
) -> alloc::vec::Vec<(String, Option<u32>, Option<u32>)> {
    let mut names: alloc::vec::Vec<String> = alloc::vec::Vec::new();
    let mut getters: alloc::vec::Vec<Option<u32>> = alloc::vec::Vec::new();
    let mut setters: alloc::vec::Vec<Option<u32>> = alloc::vec::Vec::new();
    let mut cur = Some(type_idx);
    while let Some(ti) = cur {
        let Some(td) = module.types.get(ti as usize) else {
            break;
        };
        for (i, m) in module.methods.iter().enumerate() {
            if m.owner_type != ti || (m.flags & crate::rnx::MFLAG_CTOR) != 0 {
                continue;
            }
            let simple = method_simple_name(module, i as u32);
            let (is_get, prop) = if let Some(p) = simple.strip_prefix("get_") {
                (true, p.to_string())
            } else if let Some(p) = simple.strip_prefix("set_") {
                (false, p.to_string())
            } else {
                continue;
            };
            let pos = match names.iter().position(|n| n == &prop) {
                Some(p) => p,
                None => {
                    names.push(prop);
                    getters.push(None);
                    setters.push(None);
                    names.len() - 1
                }
            };
            // Keep the most-derived accessor (types walked derived-first).
            if is_get {
                if getters[pos].is_none() {
                    getters[pos] = Some(i as u32);
                }
            } else if setters[pos].is_none() {
                setters[pos] = Some(i as u32);
            }
        }
        cur = if td.parent != crate::rnx::NO_TYPE {
            Some(td.parent as u16)
        } else {
            None
        };
    }
    let mut out = alloc::vec::Vec::with_capacity(names.len());
    for ((n, g), s) in names.into_iter().zip(getters).zip(setters) {
        out.push((n, g, s));
    }
    out
}

/// Materialize a constant custom-attribute argument as a runtime `Value`.
fn attr_arg_value<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    arg: &crate::rnx::AttrArg,
) -> Result<Value, String> {
    use crate::rnx::AttrArg;
    Ok(match arg {
        AttrArg::Null => Value::Null,
        AttrArg::I32(v) => Value::I32(*v),
        AttrArg::I64(v) => Value::I64(*v),
        AttrArg::F64(v) => Value::F64(*v),
        AttrArg::Bool(b) => Value::I32(*b as i32),
        AttrArg::Str(idx) => {
            let s = interp
                .module
                .strings
                .get(*idx as usize)
                .cloned()
                .unwrap_or_default();
            Value::Ref(interp.heap.alloc_str(s))
        }
    })
}

/// Construct a custom-attribute instance: allocate the object, run its ctor
/// with the decoded positional args, then apply named field/property args.
fn build_attribute<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    attr: crate::rnx::AttrDesc,
) -> Result<Value, String> {
    let ctor = attr.ctor;
    let owner = interp
        .module
        .methods
        .get(ctor as usize)
        .map(|m| m.owner_type)
        .ok_or("custom attribute: bad ctor index")?;
    let field_count = interp
        .module
        .types
        .get(owner as usize)
        .map(|t| t.field_count)
        .ok_or("custom attribute: bad type index")?;
    let mut fixed = Vec::with_capacity(attr.fixed.len());
    for a in &attr.fixed {
        fixed.push(attr_arg_value(interp, a)?);
    }
    let mut named = Vec::with_capacity(attr.named.len());
    for (kind, name_idx, a) in &attr.named {
        let name = interp
            .module
            .strings
            .get(*name_idx as usize)
            .cloned()
            .unwrap_or_default();
        let v = attr_arg_value(interp, a)?;
        named.push((*kind, name, v));
    }
    let obj = interp.heap.alloc(HeapObject::Object {
        type_idx: owner,
        fields: vec![Value::I32(0); field_count as usize],
    });
    let obj_ref = Value::Ref(obj);
    let mut cargs = Vec::with_capacity(fixed.len() + 1);
    cargs.push(obj_ref);
    cargs.extend(fixed);
    interp.invoke_managed(ctor, cargs)?;
    for (kind, name, v) in named {
        if kind == 0 {
            if let Some((_, slot, is_static)) = collect_public_fields(interp.module, owner)
                .into_iter()
                .find(|(n, _, _)| *n == name)
            {
                if is_static {
                    if let Some(s) = interp.statics.get_mut(slot as usize) {
                        *s = v;
                    }
                } else if let Ok(HeapObject::Object { fields, .. }) = interp.heap.get_mut(obj) {
                    if let Some(f) = fields.get_mut(slot as usize) {
                        *f = v;
                    }
                }
            }
        } else if let Some((_, _g, Some(setter))) = collect_properties(interp.module, owner)
            .into_iter()
            .find(|(n, _, _)| *n == name)
        {
            interp.invoke_managed(setter, vec![obj_ref, v])?;
        }
    }
    Ok(obj_ref)
}

/// Does attribute type `ti` equal `want` or derive from it?
fn attr_type_matches(module: &crate::rnx::Module, mut ti: u16, want: &str) -> bool {
    loop {
        if module.type_name(ti) == want {
            return true;
        }
        let Some(td) = module.types.get(ti as usize) else {
            return false;
        };
        if td.parent == crate::rnx::NO_TYPE {
            return false;
        }
        ti = td.parent as u16;
    }
}

/// A method's simple name (canonical `Ns.Type::Method(sig)` -> `Method`).
pub(crate) fn method_simple_name(module: &crate::rnx::Module, idx: u32) -> String {
    let Some(m) = module.methods.get(idx as usize) else {
        return String::new();
    };
    let canonical = &module.strings[m.name as usize];
    let after = canonical.rsplit("::").next().unwrap_or(canonical);
    after.split('(').next().unwrap_or(after).to_string()
}

/// Resolve a value's runtime type: (RNX type index if it has one, full type
/// name). Used by `object.GetType()`.
fn type_identity<H: RuntimeHost>(
    interp: &Interpreter<'_, H>,
    v: Value,
) -> Result<(Option<u16>, String), String> {
    Ok(match v {
        Value::I32(_) => (None, "System.Int32".to_string()),
        Value::I64(_) => (None, "System.Int64".to_string()),
        Value::F64(_) => (None, "System.Double".to_string()),
        Value::Null => return Err("NullReferenceException".into()),
        Value::Addr(_) => (None, "System.Object".to_string()),
        Value::Ref(r) => match interp.heap.get(r)? {
            HeapObject::Object { type_idx, .. } => {
                (Some(*type_idx), interp.module.type_name(*type_idx).to_string())
            }
            HeapObject::Str(_) => (None, "System.String".to_string()),
            HeapObject::Boxed(inner) => return type_identity(interp, *inner),
            HeapObject::Array { .. } => (None, "System.Array".to_string()),
            HeapObject::ListObj(_) => {
                (None, "System.Collections.Generic.List`1".to_string())
            }
            HeapObject::MapObj(_) => {
                (None, "System.Collections.Generic.Dictionary`2".to_string())
            }
            HeapObject::TypeObj { .. } => (None, "System.RuntimeType".to_string()),
            HeapObject::MethodInfoObj { .. } => {
                (None, "System.Reflection.RuntimeMethodInfo".to_string())
            }
            HeapObject::FieldInfoObj { .. } => {
                (None, "System.Reflection.RuntimeFieldInfo".to_string())
            }
            HeapObject::PropertyInfoObj { .. } => {
                (None, "System.Reflection.RuntimePropertyInfo".to_string())
            }
            HeapObject::Delegate { .. } => (None, "System.Delegate".to_string()),
            HeapObject::Cursor { .. } => (None, "System.Object".to_string()),
            HeapObject::TaskObj { .. } => {
                (None, "System.Threading.Tasks.Task".to_string())
            }
            HeapObject::ThreadObj { .. } => (None, "System.Threading.Thread".to_string()),
        },
    })
}

fn hash_value<H: RuntimeHost>(interp: &Interpreter<'_, H>, v: Value) -> i32 {
    match v {
        Value::I32(x) => x,
        Value::I64(x) => (x ^ (x >> 32)) as i32,
        Value::F64(x) => x.to_bits() as i32,
        Value::Ref(r) => match interp.heap.get(r) {
            Ok(HeapObject::Str(s)) => {
                let mut h: i32 = 5381;
                for b in s.bytes() {
                    h = h.wrapping_mul(33).wrapping_add(b as i32);
                }
                h
            }
            _ => r as i32,
        },
        _ => 0,
    }
}

fn to_host_value<H: RuntimeHost>(
    interp: &Interpreter<'_, H>,
    v: Value,
) -> Result<HostValue, String> {
    Ok(match v {
        Value::I32(x) => HostValue::I32(x),
        Value::I64(x) => HostValue::I64(x),
        Value::F64(x) => HostValue::F64(x),
        Value::Null => HostValue::Null,
        Value::Ref(r) => match interp.heap.get(r)? {
            HeapObject::Str(s) => HostValue::Str(s.clone()),
            HeapObject::Boxed(inner) => to_host_value(interp, *inner)?,
            HeapObject::Array { data, .. } => {
                let bytes = data
                    .iter()
                    .map(|v| v.as_i32().map(|x| x as u8))
                    .collect::<Result<Vec<u8>, _>>()?;
                HostValue::Bytes(bytes)
            }
            other => return Err(format!("cannot marshal {other:?} to host")),
        },
        Value::Addr(_) => return Err("cannot marshal managed pointer to host".into()),
    })
}

fn from_host_value<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    v: HostValue,
) -> Option<Value> {
    match v {
        HostValue::Void => None,
        HostValue::I32(x) => Some(Value::I32(x)),
        HostValue::I64(x) => Some(Value::I64(x)),
        HostValue::F64(x) => Some(Value::F64(x)),
        HostValue::Bool(b) => Some(Value::I32(b as i32)),
        HostValue::Null => Some(Value::Null),
        HostValue::Str(s) => {
            let r = interp.heap.alloc_str(s);
            Some(Value::Ref(r))
        }
        HostValue::Bytes(bytes) => Some(alloc_byte_array(interp, &bytes)),
    }
}
