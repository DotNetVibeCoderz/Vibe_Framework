//! System.Collections.Generic intrinsics: List, Dictionary, Queue, Stack,
//! KeyValuePair, Tuple and the enumerator cursors that make `foreach` work.

#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};
use super::{push, ref_arg, value_cmp};
use crate::host::RuntimeHost;
use crate::interp::Interpreter;
use crate::value::{ElemType, HeapObject, Value};

fn list_of<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    v: Value,
) -> Result<u32, String> {
    let r = ref_arg(interp, v)?;
    match interp.heap.get(r)? {
        HeapObject::ListObj(_) => Ok(r),
        other => Err(format!("expected List, got {other:?}")),
    }
}

fn with_list<H: RuntimeHost, T>(
    interp: &mut Interpreter<'_, H>,
    v: Value,
    f: impl FnOnce(&mut Vec<Value>) -> T,
) -> Result<T, String> {
    let r = list_of(interp, v)?;
    match interp.heap.get_mut(r)? {
        HeapObject::ListObj(data) => Ok(f(data)),
        _ => unreachable!(),
    }
}

pub fn list_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
    is_newobj: bool,
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    if is_newobj && op == ".ctor" {
        let r = interp.heap.alloc(HeapObject::ListObj(Vec::new()));
        push(interp, Value::Ref(r));
        return Ok(());
    }
    match op {
        "Add" => {
            let v = interp.deref(args[1])?;
            with_list(interp, args[0], |d| d.push(v))?;
        }
        "get_Count" => {
            let n = with_list(interp, args[0], |d| d.len())?;
            push(interp, Value::I32(n as i32));
        }
        "get_Item" => {
            let i = interp.deref(args[1])?.as_i32()? as usize;
            let v = with_list(interp, args[0], |d| d.get(i).copied())?
                .ok_or("ArgumentOutOfRangeException")?;
            push(interp, v);
        }
        "set_Item" => {
            let i = interp.deref(args[1])?.as_i32()? as usize;
            let v = interp.deref(args[2])?;
            let ok = with_list(interp, args[0], |d| {
                if i < d.len() {
                    d[i] = v;
                    true
                } else {
                    false
                }
            })?;
            if !ok {
                return Err("ArgumentOutOfRangeException".into());
            }
        }
        "Insert" => {
            let i = interp.deref(args[1])?.as_i32()? as usize;
            let v = interp.deref(args[2])?;
            with_list(interp, args[0], |d| {
                let i = i.min(d.len());
                d.insert(i, v);
            })?;
        }
        "RemoveAt" => {
            let i = interp.deref(args[1])?.as_i32()? as usize;
            let ok = with_list(interp, args[0], |d| {
                if i < d.len() {
                    d.remove(i);
                    true
                } else {
                    false
                }
            })?;
            if !ok {
                return Err("ArgumentOutOfRangeException".into());
            }
        }
        "Remove" => {
            let v = interp.deref(args[1])?;
            let r = list_of(interp, args[0])?;
            let data = match interp.heap.get(r)? {
                HeapObject::ListObj(d) => d.clone(),
                _ => unreachable!(),
            };
            let idx = data.iter().position(|x| interp.value_eq(*x, v));
            if let Some(i) = idx {
                with_list(interp, args[0], |d| d.remove(i))?;
            }
            push(interp, Value::I32(idx.is_some() as i32));
        }
        "Clear" => {
            with_list(interp, args[0], |d| d.clear())?;
        }
        "Contains" | "IndexOf" => {
            let v = interp.deref(args[1])?;
            let r = list_of(interp, args[0])?;
            let data = match interp.heap.get(r)? {
                HeapObject::ListObj(d) => d.clone(),
                _ => unreachable!(),
            };
            let idx = data.iter().position(|x| interp.value_eq(*x, v));
            if op == "Contains" {
                push(interp, Value::I32(idx.is_some() as i32));
            } else {
                push(interp, Value::I32(idx.map(|i| i as i32).unwrap_or(-1)));
            }
        }
        "ToArray" => {
            let data = with_list(interp, args[0], |d| d.clone())?;
            let r = interp.heap.alloc(HeapObject::Array { elem: ElemType::Ref, data });
            push(interp, Value::Ref(r));
        }
        "AddRange" => {
            let src = super::linq::coerce_sequence(interp, args[1])?;
            with_list(interp, args[0], |d| d.extend(src))?;
        }
        "Sort" => {
            let r = list_of(interp, args[0])?;
            let mut data = match interp.heap.get(r)? {
                HeapObject::ListObj(d) => d.clone(),
                _ => unreachable!(),
            };
            data.sort_by(|a, b| value_cmp(interp, *a, *b));
            if let HeapObject::ListObj(d) = interp.heap.get_mut(r)? {
                *d = data;
            }
        }
        "Reverse" => {
            with_list(interp, args[0], |d| d.reverse())?;
        }
        "GetEnumerator" => return get_enumerator(interp, args),
        other => return Err(format!("unsupported List intrinsic: {other}")),
    }
    Ok(())
}

pub fn dict_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
    is_newobj: bool,
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    if is_newobj && op == ".ctor" {
        let r = interp.heap.alloc(HeapObject::MapObj(Vec::new()));
        push(interp, Value::Ref(r));
        return Ok(());
    }
    let this = ref_arg(interp, args[0])?;
    let pairs = match interp.heap.get(this)? {
        HeapObject::MapObj(p) => p.clone(),
        other => return Err(format!("expected Dictionary, got {other:?}")),
    };
    match op {
        "Add" | "set_Item" => {
            let k = interp.deref(args[1])?;
            let v = interp.deref(args[2])?;
            let existing = pairs.iter().position(|(pk, _)| interp.value_eq(*pk, k));
            if let Some(i) = existing {
                if op == "Add" {
                    return Err("ArgumentException: key already exists".into());
                }
                if let HeapObject::MapObj(p) = interp.heap.get_mut(this)? {
                    p[i].1 = v;
                }
            } else if let HeapObject::MapObj(p) = interp.heap.get_mut(this)? {
                p.push((k, v));
            }
        }
        "get_Item" => {
            let k = interp.deref(args[1])?;
            let found = pairs.iter().find(|(pk, _)| interp.value_eq(*pk, k));
            match found {
                Some((_, v)) => push(interp, *v),
                None => return Err("KeyNotFoundException".into()),
            }
        }
        "ContainsKey" => {
            let k = interp.deref(args[1])?;
            let found = pairs.iter().any(|(pk, _)| interp.value_eq(*pk, k));
            push(interp, Value::I32(found as i32));
        }
        "Remove" => {
            let k = interp.deref(args[1])?;
            let idx = pairs.iter().position(|(pk, _)| interp.value_eq(*pk, k));
            if let Some(i) = idx {
                if let HeapObject::MapObj(p) = interp.heap.get_mut(this)? {
                    p.remove(i);
                }
            }
            push(interp, Value::I32(idx.is_some() as i32));
        }
        "get_Count" => push(interp, Value::I32(pairs.len() as i32)),
        "Clear" => {
            if let HeapObject::MapObj(p) = interp.heap.get_mut(this)? {
                p.clear();
            }
        }
        "get_Keys" => {
            let data: Vec<Value> = pairs.iter().map(|(k, _)| *k).collect();
            let r = interp.heap.alloc(HeapObject::ListObj(data));
            push(interp, Value::Ref(r));
        }
        "get_Values" => {
            let data: Vec<Value> = pairs.iter().map(|(_, v)| *v).collect();
            let r = interp.heap.alloc(HeapObject::ListObj(data));
            push(interp, Value::Ref(r));
        }
        "GetEnumerator" => return get_enumerator(interp, args),
        other => return Err(format!("unsupported Dictionary intrinsic: {other}")),
    }
    Ok(())
}

pub fn queue_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
    is_newobj: bool,
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    if is_newobj && op == ".ctor" {
        let r = interp.heap.alloc(HeapObject::ListObj(Vec::new()));
        push(interp, Value::Ref(r));
        return Ok(());
    }
    match op {
        "Enqueue" => {
            let v = interp.deref(args[1])?;
            with_list(interp, args[0], |d| d.push(v))?;
        }
        "Dequeue" => {
            let v = with_list(interp, args[0], |d| {
                if d.is_empty() { None } else { Some(d.remove(0)) }
            })?
            .ok_or("InvalidOperationException: queue empty")?;
            push(interp, v);
        }
        "Peek" => {
            let v = with_list(interp, args[0], |d| d.first().copied())?
                .ok_or("InvalidOperationException: queue empty")?;
            push(interp, v);
        }
        "get_Count" => {
            let n = with_list(interp, args[0], |d| d.len())?;
            push(interp, Value::I32(n as i32));
        }
        "Clear" => {
            with_list(interp, args[0], |d| d.clear())?;
        }
        "GetEnumerator" => return get_enumerator(interp, args),
        other => return Err(format!("unsupported Queue intrinsic: {other}")),
    }
    Ok(())
}

pub fn stack_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
    is_newobj: bool,
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    if is_newobj && op == ".ctor" {
        let r = interp.heap.alloc(HeapObject::ListObj(Vec::new()));
        push(interp, Value::Ref(r));
        return Ok(());
    }
    match op {
        "Push" => {
            let v = interp.deref(args[1])?;
            with_list(interp, args[0], |d| d.push(v))?;
        }
        "Pop" => {
            let v = with_list(interp, args[0], |d| d.pop())?
                .ok_or("InvalidOperationException: stack empty")?;
            push(interp, v);
        }
        "Peek" => {
            let v = with_list(interp, args[0], |d| d.last().copied())?
                .ok_or("InvalidOperationException: stack empty")?;
            push(interp, v);
        }
        "get_Count" => {
            let n = with_list(interp, args[0], |d| d.len())?;
            push(interp, Value::I32(n as i32));
        }
        "Clear" => {
            with_list(interp, args[0], |d| d.clear())?;
        }
        "GetEnumerator" => return get_enumerator(interp, args),
        other => return Err(format!("unsupported Stack intrinsic: {other}")),
    }
    Ok(())
}

pub fn tuple_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    name: &str,
    args: &[Value],
    is_newobj: bool,
) -> Result<(), String> {
    let op = name.split("::").nth(1).unwrap_or("").split('(').next().unwrap_or("");
    if (is_newobj && op == ".ctor") || op == "Create" {
        let mut items = Vec::with_capacity(args.len());
        for a in args {
            items.push(interp.deref(*a)?);
        }
        let r = interp.heap.alloc(HeapObject::ListObj(items));
        push(interp, Value::Ref(r));
        return Ok(());
    }
    if let Some(n) = op.strip_prefix("get_Item") {
        let idx: usize = n.parse::<usize>().map_err(|_| "bad tuple accessor")? - 1;
        let v = with_list(interp, args[0], |d| d.get(idx).copied())?
            .ok_or("tuple item out of range")?;
        push(interp, v);
        return Ok(());
    }
    Err(format!("unsupported Tuple intrinsic: {op}"))
}

pub fn kvp_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    name: &str,
    args: &[Value],
) -> Result<(), String> {
    let idx = if name.ends_with("::get_Key()") { 0 } else { 1 };
    let v = with_list(interp, args[0], |d| d.get(idx).copied())?
        .ok_or("corrupt KeyValuePair")?;
    push(interp, v);
    Ok(())
}

// ---- enumerators (foreach) ----

pub fn get_enumerator<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    args: &[Value],
) -> Result<(), String> {
    let target = ref_arg(interp, args[0])?;
    match interp.heap.get(target)? {
        HeapObject::Array { .. } | HeapObject::ListObj(_) | HeapObject::MapObj(_)
        | HeapObject::Str(_) => {
            let r = interp.heap.alloc(HeapObject::Cursor { target, pos: 0 });
            push(interp, Value::Ref(r));
            Ok(())
        }
        HeapObject::Cursor { .. } => {
            // GetEnumerator on an enumerator (IEnumerable indirection).
            push(interp, Value::Ref(target));
            Ok(())
        }
        other => Err(format!("GetEnumerator on {other:?}")),
    }
}

fn cursor_len<H: RuntimeHost>(interp: &Interpreter<'_, H>, target: u32) -> usize {
    match interp.heap.get(target) {
        Ok(HeapObject::Array { data, .. }) => data.len(),
        Ok(HeapObject::ListObj(data)) => data.len(),
        Ok(HeapObject::MapObj(pairs)) => pairs.len(),
        Ok(HeapObject::Str(s)) => s.chars().count(),
        _ => 0,
    }
}

pub fn cursor_move_next<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    args: &[Value],
) -> Result<(), String> {
    let r = ref_arg(interp, args[0])?;
    let (target, pos) = match interp.heap.get(r)? {
        HeapObject::Cursor { target, pos } => (*target, *pos),
        other => return Err(format!("MoveNext on {other:?}")),
    };
    let len = cursor_len(interp, target);
    let has = (pos as usize) < len;
    if has {
        if let HeapObject::Cursor { pos, .. } = interp.heap.get_mut(r)? {
            *pos += 1;
        }
    }
    push(interp, Value::I32(has as i32));
    Ok(())
}

pub fn cursor_current<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    args: &[Value],
) -> Result<(), String> {
    let r = ref_arg(interp, args[0])?;
    let (target, pos) = match interp.heap.get(r)? {
        HeapObject::Cursor { target, pos } => (*target, *pos),
        other => return Err(format!("get_Current on {other:?}")),
    };
    let idx = pos.saturating_sub(1) as usize;
    let v = match interp.heap.get(target)? {
        HeapObject::Array { data, .. } => *data.get(idx).ok_or("enumerator out of range")?,
        HeapObject::ListObj(data) => *data.get(idx).ok_or("enumerator out of range")?,
        HeapObject::MapObj(pairs) => {
            let (k, v) = *pairs.get(idx).ok_or("enumerator out of range")?;
            Value::Ref(interp.heap.alloc(HeapObject::ListObj(vec![k, v])))
        }
        HeapObject::Str(s) => {
            let c = s.chars().nth(idx).ok_or("enumerator out of range")?;
            Value::I32(c as i32)
        }
        other => return Err(format!("enumerator over {other:?}")),
    };
    push(interp, v);
    Ok(())
}
