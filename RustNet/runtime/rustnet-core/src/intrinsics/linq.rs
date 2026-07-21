//! Eager LINQ-to-objects over arrays / List / Dictionary. Lazy pipelines
//! evaluate step by step, each producing an array — the right trade-off
//! for small embedded collections.

#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};
use super::{push, ref_arg, value_cmp};
use crate::host::RuntimeHost;
use crate::interp::Interpreter;
use crate::value::{ElemType, HeapObject, Value};

/// Extract the elements of any enumerable value.
pub fn coerce_sequence<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    v: Value,
) -> Result<Vec<Value>, String> {
    let r = ref_arg(interp, v)?;
    match interp.heap.get(r)? {
        HeapObject::Array { data, .. } => Ok(data.clone()),
        HeapObject::ListObj(data) => Ok(data.clone()),
        HeapObject::MapObj(pairs) => {
            let pairs = pairs.clone();
            Ok(pairs
                .into_iter()
                .map(|(k, val)| {
                    Value::Ref(interp.heap.alloc(HeapObject::ListObj(vec![k, val])))
                })
                .collect())
        }
        HeapObject::Str(s) => Ok(s.chars().map(|c| Value::I32(c as i32)).collect()),
        other => Err(format!("value is not enumerable: {other:?}")),
    }
}

fn delegate_of<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    v: Value,
) -> Result<u32, String> {
    let r = ref_arg(interp, v)?;
    match interp.heap.get(r)? {
        HeapObject::Delegate { .. } => Ok(r),
        other => Err(format!("expected lambda/delegate, got {other:?}")),
    }
}

fn call1<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    delegate: u32,
    arg: Value,
) -> Result<Value, String> {
    interp
        .invoke_delegate(delegate, vec![arg])?
        .ok_or_else(|| "lambda returned void".into())
}

fn out_array<H: RuntimeHost>(interp: &mut Interpreter<'_, H>, data: Vec<Value>) -> Value {
    Value::Ref(interp.heap.alloc(HeapObject::Array { elem: ElemType::Ref, data }))
}

fn numeric_sum<H: RuntimeHost>(
    interp: &Interpreter<'_, H>,
    items: &[Value],
) -> Result<Value, String> {
    let _ = interp;
    let all_int = items.iter().all(|v| matches!(v, Value::I32(_) | Value::I64(_)));
    if all_int {
        let mut sum: i64 = 0;
        for v in items {
            sum = sum.wrapping_add(v.as_i64()?);
        }
        if items.iter().all(|v| matches!(v, Value::I32(_))) {
            Ok(Value::I32(sum as i32))
        } else {
            Ok(Value::I64(sum))
        }
    } else {
        let mut sum = 0.0;
        for v in items {
            sum += v.as_f64()?;
        }
        Ok(Value::F64(sum))
    }
}

pub fn linq_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    match op {
        "Range" => {
            let start = interp.deref(args[0])?.as_i32()?;
            let count = interp.deref(args[1])?.as_i32()?.max(0);
            let data = (start..start + count).map(Value::I32).collect();
            let out = out_array(interp, data);
            push(interp, out);
            return Ok(());
        }
        _ => {}
    }
    let src = coerce_sequence(interp, args[0])?;
    match op {
        "Where" => {
            let d = delegate_of(interp, args[1])?;
            let mut out = Vec::new();
            for item in src {
                if call1(interp, d, item)?.is_truthy() {
                    out.push(item);
                }
            }
            let arr = out_array(interp, out);
            push(interp, arr);
        }
        "Select" => {
            let d = delegate_of(interp, args[1])?;
            let mut out = Vec::with_capacity(src.len());
            for item in src {
                out.push(call1(interp, d, item)?);
            }
            let arr = out_array(interp, out);
            push(interp, arr);
        }
        "Count" => {
            if args.len() == 1 {
                push(interp, Value::I32(src.len() as i32));
            } else {
                let d = delegate_of(interp, args[1])?;
                let mut n = 0;
                for item in src {
                    if call1(interp, d, item)?.is_truthy() {
                        n += 1;
                    }
                }
                push(interp, Value::I32(n));
            }
        }
        "Sum" => {
            let items = if args.len() > 1 {
                let d = delegate_of(interp, args[1])?;
                let mut mapped = Vec::with_capacity(src.len());
                for item in src {
                    mapped.push(call1(interp, d, item)?);
                }
                mapped
            } else {
                src
            };
            let v = numeric_sum(interp, &items)?;
            push(interp, v);
        }
        "Average" => {
            if src.is_empty() {
                return Err("InvalidOperationException: sequence is empty".into());
            }
            let mut sum = 0.0;
            for v in &src {
                sum += v.as_f64()?;
            }
            push(interp, Value::F64(sum / src.len() as f64));
        }
        "Min" | "Max" => {
            if src.is_empty() {
                return Err("InvalidOperationException: sequence is empty".into());
            }
            let mut best = src[0];
            for &v in &src[1..] {
                let ord = value_cmp(interp, v, best);
                let better = if op == "Min" {
                    ord == core::cmp::Ordering::Less
                } else {
                    ord == core::cmp::Ordering::Greater
                };
                if better {
                    best = v;
                }
            }
            push(interp, best);
        }
        "First" | "FirstOrDefault" | "Last" | "LastOrDefault" => {
            let filtered = if args.len() > 1 {
                let d = delegate_of(interp, args[1])?;
                let mut out = Vec::new();
                for item in src {
                    if call1(interp, d, item)?.is_truthy() {
                        out.push(item);
                    }
                }
                out
            } else {
                src
            };
            let picked = if op.starts_with("First") {
                filtered.first().copied()
            } else {
                filtered.last().copied()
            };
            match picked {
                Some(v) => push(interp, v),
                None if op.ends_with("OrDefault") => push(interp, Value::I32(0)),
                None => return Err("InvalidOperationException: sequence is empty".into()),
            }
        }
        "Any" => {
            if args.len() == 1 {
                push(interp, Value::I32(!src.is_empty() as i32));
            } else {
                let d = delegate_of(interp, args[1])?;
                let mut any = false;
                for item in src {
                    if call1(interp, d, item)?.is_truthy() {
                        any = true;
                        break;
                    }
                }
                push(interp, Value::I32(any as i32));
            }
        }
        "All" => {
            let d = delegate_of(interp, args[1])?;
            let mut all = true;
            for item in src {
                if !call1(interp, d, item)?.is_truthy() {
                    all = false;
                    break;
                }
            }
            push(interp, Value::I32(all as i32));
        }
        "Contains" => {
            let needle = interp.deref(args[1])?;
            let found = src.iter().any(|v| interp.value_eq(*v, needle));
            push(interp, Value::I32(found as i32));
        }
        "Take" => {
            let n = interp.deref(args[1])?.as_i32()?.max(0) as usize;
            let data = src.into_iter().take(n).collect();
            let arr = out_array(interp, data);
            push(interp, arr);
        }
        "Skip" => {
            let n = interp.deref(args[1])?.as_i32()?.max(0) as usize;
            let data = src.into_iter().skip(n).collect();
            let arr = out_array(interp, data);
            push(interp, arr);
        }
        "Reverse" => {
            let mut data = src;
            data.reverse();
            let arr = out_array(interp, data);
            push(interp, arr);
        }
        "Distinct" => {
            let mut data: Vec<Value> = Vec::new();
            for v in src {
                if !data.iter().any(|x| interp.value_eq(*x, v)) {
                    data.push(v);
                }
            }
            let arr = out_array(interp, data);
            push(interp, arr);
        }
        "OrderBy" | "OrderByDescending" => {
            let d = delegate_of(interp, args[1])?;
            let mut keyed = Vec::with_capacity(src.len());
            for item in src {
                let key = call1(interp, d, item)?;
                keyed.push((key, item));
            }
            keyed.sort_by(|(ka, _), (kb, _)| value_cmp(interp, *ka, *kb));
            if op == "OrderByDescending" {
                keyed.reverse();
            }
            let data = keyed.into_iter().map(|(_, v)| v).collect();
            let arr = out_array(interp, data);
            push(interp, arr);
        }
        "ToArray" => {
            let arr = out_array(interp, src);
            push(interp, arr);
        }
        "ToList" => {
            let r = interp.heap.alloc(HeapObject::ListObj(src));
            push(interp, Value::Ref(r));
        }
        "ElementAt" => {
            let i = interp.deref(args[1])?.as_i32()? as usize;
            let v = src.get(i).copied().ok_or("ArgumentOutOfRangeException")?;
            push(interp, v);
        }
        other => Err(format!("unsupported LINQ operator: {other}"))?,
    }
    Ok(())
}
