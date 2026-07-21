//! Text & encoding intrinsics: String, StringBuilder, interpolated string
//! handler, Char, Convert (Base64), BitConverter, UTF-8 Encoding, Regex.

#[allow(unused_imports)]
use alloc::{boxed::Box, format, string::{String, ToString}, vec, vec::Vec};
use super::{alloc_byte_array, alloc_str_array, bytes_arg, push, ref_arg, str_arg};
use crate::host::RuntimeHost;
use crate::interp::Interpreter;
use crate::regex::Regex;
use crate::value::{Addr, ElemType, HeapObject, Value};

pub fn string_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    match op {
        "Concat" => {
            let mut out = String::new();
            // `string.Concat` with 5+ parts lowers to `Concat(ReadOnlySpan<T>)`,
            // which arrives as a single inline-array reference — concatenate its
            // elements. (Also covers `Concat(IEnumerable)` given an array.)
            if args.len() == 1 {
                if let Value::Ref(r) = interp.deref(args[0])? {
                    if let Ok(HeapObject::Array { data, .. }) = interp.heap.get(r) {
                        let data = data.clone();
                        for v in data {
                            out.push_str(&interp.display_value(v)?);
                        }
                        let rr = interp.heap.alloc_str(out);
                        push(interp, Value::Ref(rr));
                        return Ok(());
                    }
                }
            }
            for a in args {
                let v = interp.deref(*a)?;
                out.push_str(&interp.display_value(v)?);
            }
            let r = interp.heap.alloc_str(out);
            push(interp, Value::Ref(r));
        }
        "Join" => {
            let sep = str_arg(interp, args[0])?;
            let items = super::linq::coerce_sequence(interp, args[1])?;
            let mut parts = Vec::with_capacity(items.len());
            for v in items {
                parts.push(interp.display_value(v)?);
            }
            let r = interp.heap.alloc_str(parts.join(&sep));
            push(interp, Value::Ref(r));
        }
        "get_Length" => {
            let s = str_arg(interp, args[0])?;
            push(interp, Value::I32(s.chars().count() as i32));
        }
        "get_Chars" => {
            let s = str_arg(interp, args[0])?;
            let i = interp.deref(args[1])?.as_i32()?;
            let c = s.chars().nth(i as usize).ok_or("IndexOutOfRangeException")?;
            push(interp, Value::I32(c as i32));
        }
        "Substring" => {
            let s = str_arg(interp, args[0])?;
            let start = interp.deref(args[1])?.as_i32()? as usize;
            let chars: Vec<char> = s.chars().collect();
            let end = if args.len() > 2 {
                start + interp.deref(args[2])?.as_i32()? as usize
            } else {
                chars.len()
            };
            if start > chars.len() || end > chars.len() {
                return Err("ArgumentOutOfRangeException".into());
            }
            let sub: String = chars[start..end].iter().collect();
            let r = interp.heap.alloc_str(sub);
            push(interp, Value::Ref(r));
        }
        "IndexOf" => {
            let s = str_arg(interp, args[0])?;
            let needle = match interp.deref(args[1])? {
                Value::I32(c) => char::from_u32(c as u32).map(String::from).unwrap_or_default(),
                v => str_arg(interp, v)?,
            };
            let idx = match s.find(&needle) {
                Some(byte_idx) => s[..byte_idx].chars().count() as i32,
                None => -1,
            };
            push(interp, Value::I32(idx));
        }
        "Contains" => {
            let s = str_arg(interp, args[0])?;
            let n = str_arg(interp, args[1])?;
            push(interp, Value::I32(s.contains(&n) as i32));
        }
        "StartsWith" => {
            let s = str_arg(interp, args[0])?;
            let n = str_arg(interp, args[1])?;
            push(interp, Value::I32(s.starts_with(&n) as i32));
        }
        "EndsWith" => {
            let s = str_arg(interp, args[0])?;
            let n = str_arg(interp, args[1])?;
            push(interp, Value::I32(s.ends_with(&n) as i32));
        }
        "Replace" => {
            let s = str_arg(interp, args[0])?;
            let from = str_arg(interp, args[1])?;
            let to = str_arg(interp, args[2])?;
            let r = interp.heap.alloc_str(s.replace(&from, &to));
            push(interp, Value::Ref(r));
        }
        "ToUpper" => {
            let s = str_arg(interp, args[0])?;
            let r = interp.heap.alloc_str(s.to_uppercase());
            push(interp, Value::Ref(r));
        }
        "ToLower" => {
            let s = str_arg(interp, args[0])?;
            let r = interp.heap.alloc_str(s.to_lowercase());
            push(interp, Value::Ref(r));
        }
        "Trim" | "TrimStart" | "TrimEnd" => {
            let s = str_arg(interp, args[0])?;
            // Optional char[] / char argument selects the trim set.
            let trimmed = if args.len() > 1 {
                let chars = char_set_arg(interp, args[1])?;
                let pat: &[char] = &chars;
                match op {
                    "TrimStart" => s.trim_start_matches(pat),
                    "TrimEnd" => s.trim_end_matches(pat),
                    _ => s.trim_matches(pat),
                }
            } else {
                match op {
                    "TrimStart" => s.trim_start(),
                    "TrimEnd" => s.trim_end(),
                    _ => s.trim(),
                }
            };
            let r = interp.heap.alloc_str(trimmed.to_string());
            push(interp, Value::Ref(r));
        }
        "PadLeft" | "PadRight" => {
            let s = str_arg(interp, args[0])?;
            let width = interp.deref(args[1])?.as_i32()? as usize;
            let fill = if args.len() > 2 {
                char::from_u32(interp.deref(args[2])?.as_i32()? as u32).unwrap_or(' ')
            } else {
                ' '
            };
            let len = s.chars().count();
            let mut out = String::new();
            if op == "PadLeft" {
                for _ in len..width {
                    out.push(fill);
                }
                out.push_str(&s);
            } else {
                out.push_str(&s);
                for _ in len..width {
                    out.push(fill);
                }
            }
            let r = interp.heap.alloc_str(out);
            push(interp, Value::Ref(r));
        }
        "Split" => {
            let s = str_arg(interp, args[0])?;
            let sep = match interp.deref(args[1])? {
                Value::I32(c) => char::from_u32(c as u32).unwrap_or(',').to_string(),
                v => str_arg(interp, v)?,
            };
            let parts: Vec<String> = if sep.is_empty() {
                vec![s]
            } else {
                s.split(&sep).map(|p| p.to_string()).collect()
            };
            let arr = alloc_str_array(interp, parts);
            push(interp, arr);
        }
        "ToCharArray" => {
            let s = str_arg(interp, args[0])?;
            let data = s.chars().map(|c| Value::I32(c as i32)).collect();
            let r = interp.heap.alloc(HeapObject::Array { elem: ElemType::Char, data });
            push(interp, Value::Ref(r));
        }
        "op_Equality" | "Equals" => {
            let a = str_arg(interp, args[0])?;
            let b = str_arg(interp, args[1])?;
            push(interp, Value::I32((a == b) as i32));
        }
        "op_Inequality" => {
            let a = str_arg(interp, args[0])?;
            let b = str_arg(interp, args[1])?;
            push(interp, Value::I32((a != b) as i32));
        }
        "CompareTo" | "Compare" => {
            let a = str_arg(interp, args[0])?;
            let b = str_arg(interp, args[1])?;
            let ord = match a.cmp(&b) {
                core::cmp::Ordering::Less => -1,
                core::cmp::Ordering::Equal => 0,
                core::cmp::Ordering::Greater => 1,
            };
            push(interp, Value::I32(ord));
        }
        "IsNullOrEmpty" | "IsNullOrWhiteSpace" => {
            let v = interp.deref(args[0])?;
            let empty = match v {
                Value::Null => true,
                Value::Ref(r) => {
                    let s = interp.heap.str_value(r)?;
                    if op == "IsNullOrEmpty" { s.is_empty() } else { s.trim().is_empty() }
                }
                _ => false,
            };
            push(interp, Value::I32(empty as i32));
        }
        other => return Err(format!("unsupported String intrinsic: {other}")),
    }
    Ok(())
}

pub fn stringbuilder_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
    is_newobj: bool,
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    if is_newobj && op == ".ctor" {
        let initial = if !args.is_empty() {
            match interp.deref(args[0])? {
                Value::Ref(_) => str_arg(interp, args[0])?,
                _ => String::new(), // capacity ctor
            }
        } else {
            String::new()
        };
        let r = interp.heap.alloc_str(initial);
        push(interp, Value::Ref(r));
        return Ok(());
    }
    let this = ref_arg(interp, args[0])?;
    match op {
        "Append" | "AppendLine" => {
            // char args arrive as I32; the signature tells them apart from
            // numeric appends (Append(char) must append the glyph, not digits).
            let mut text = if args.len() > 1 {
                let v = interp.deref(args[1])?;
                if rest.starts_with("Append(char)") {
                    char::from_u32(v.as_i32()? as u32).unwrap_or('\u{FFFD}').to_string()
                } else {
                    interp.display_value(v)?
                }
            } else {
                String::new()
            };
            if op == "AppendLine" {
                text.push('\n');
            }
            match interp.heap.get_mut(this)? {
                HeapObject::Str(s) => s.push_str(&text),
                other => return Err(format!("StringBuilder expected, got {other:?}")),
            }
            push(interp, Value::Ref(this));
        }
        "get_Length" => {
            let n = interp.heap.str_value(this)?.chars().count();
            push(interp, Value::I32(n as i32));
        }
        "Clear" => {
            if let HeapObject::Str(s) = interp.heap.get_mut(this)? {
                s.clear();
            }
            push(interp, Value::Ref(this));
        }
        "ToString" => {
            let s = interp.heap.str_value(this)?.to_string();
            let r = interp.heap.alloc_str(s);
            push(interp, Value::Ref(r));
        }
        other => return Err(format!("unsupported StringBuilder intrinsic: {other}")),
    }
    Ok(())
}

/// C# string interpolation: `$"x={x}"` lowers to this handler struct.
pub fn interp_handler<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
    is_newobj: bool,
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    match op {
        ".ctor" => {
            let r = interp.heap.alloc_str(String::new());
            if is_newobj {
                // Await-spill lowering constructs the handler by value.
                push(interp, Value::Ref(r));
                return Ok(());
            }
            // Normal lowering: `this` is a byref to the handler local.
            let Value::Addr(addr) = args[0] else {
                return Err("interpolation handler ctor without byref this".into());
            };
            interp.store_addr(addr, Value::Ref(r))?;
            Ok(())
        }
        "AppendLiteral" | "AppendFormatted" => {
            let this = ref_arg(interp, args[0])?;
            let v = interp.deref(args[1])?;
            let mut text = interp.display_value(v)?;
            // AppendFormatted with alignment/format args: ignore extras.
            if op == "AppendFormatted" && args.len() > 2 {
                if let Ok(fmt) = str_arg(interp, args[2]) {
                    if !fmt.is_empty() {
                        text = format_value(v, &fmt, text);
                    }
                }
            }
            match interp.heap.get_mut(this)? {
                HeapObject::Str(s) => s.push_str(&text),
                other => return Err(format!("handler expected string buffer, got {other:?}")),
            }
            Ok(())
        }
        "ToStringAndClear" => {
            let this = ref_arg(interp, args[0])?;
            let s = interp.heap.str_value(this)?.to_string();
            let r = interp.heap.alloc_str(s);
            push(interp, Value::Ref(r));
            Ok(())
        }
        other => Err(format!("unsupported interpolation handler op: {other}")),
    }
}

fn format_value(v: Value, fmt: &str, fallback: String) -> String {
    let (spec, digits) = fmt.split_at(1);
    let digits: usize = digits.parse().unwrap_or(2);
    match (spec, v) {
        ("F" | "f", Value::F64(x)) => format!("{x:.digits$}"),
        ("F" | "f", Value::I32(x)) => format!("{:.digits$}", x as f64),
        ("X" | "x", Value::I32(x)) => {
            if spec == "X" { format!("{x:X}") } else { format!("{x:x}") }
        }
        ("D" | "d", Value::I32(x)) => format!("{x:0digits$}"),
        _ => fallback,
    }
}

pub fn char_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    let c = char::from_u32(interp.deref(args[0])?.as_i32()? as u32).unwrap_or('\0');
    match op {
        "IsDigit" => push(interp, Value::I32(c.is_ascii_digit() as i32)),
        "IsLetter" => push(interp, Value::I32(c.is_alphabetic() as i32)),
        "IsLetterOrDigit" => push(interp, Value::I32(c.is_alphanumeric() as i32)),
        "IsWhiteSpace" => push(interp, Value::I32(c.is_whitespace() as i32)),
        "IsUpper" => push(interp, Value::I32(c.is_uppercase() as i32)),
        "IsLower" => push(interp, Value::I32(c.is_lowercase() as i32)),
        "ToUpper" => push(interp, Value::I32(c.to_ascii_uppercase() as i32)),
        "ToLower" => push(interp, Value::I32(c.to_ascii_lowercase() as i32)),
        other => return Err(format!("unsupported Char intrinsic: {other}")),
    }
    Ok(())
}

// ---- Convert (Base64 + numeric conversions) ----

const B64: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

fn base64_encode(data: &[u8]) -> String {
    let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
    for chunk in data.chunks(3) {
        let b = [chunk[0], *chunk.get(1).unwrap_or(&0), *chunk.get(2).unwrap_or(&0)];
        let n = ((b[0] as u32) << 16) | ((b[1] as u32) << 8) | b[2] as u32;
        out.push(B64[(n >> 18) as usize & 63] as char);
        out.push(B64[(n >> 12) as usize & 63] as char);
        out.push(if chunk.len() > 1 { B64[(n >> 6) as usize & 63] as char } else { '=' });
        out.push(if chunk.len() > 2 { B64[n as usize & 63] as char } else { '=' });
    }
    out
}

fn base64_decode(text: &str) -> Result<Vec<u8>, String> {
    let mut out = Vec::new();
    let mut acc = 0u32;
    let mut bits = 0u32;
    for c in text.chars() {
        if c == '=' || c.is_whitespace() {
            continue;
        }
        let v = B64
            .iter()
            .position(|&b| b as char == c)
            .ok_or("FormatException: invalid base64")? as u32;
        acc = (acc << 6) | v;
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            out.push((acc >> bits) as u8);
        }
    }
    Ok(out)
}

pub fn convert_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    match op {
        "ToBase64String" => {
            let bytes = bytes_arg(interp, args[0])?;
            let r = interp.heap.alloc_str(base64_encode(&bytes));
            push(interp, Value::Ref(r));
        }
        "FromBase64String" => {
            let s = str_arg(interp, args[0])?;
            let bytes = base64_decode(&s)?;
            let arr = alloc_byte_array(interp, &bytes);
            push(interp, arr);
        }
        "ToInt32" => {
            let v = interp.deref(args[0])?;
            let n = match v {
                Value::Ref(_) => {
                    let s = str_arg(interp, args[0])?;
                    s.trim().parse::<i32>().map_err(|_| format!("FormatException: '{s}'"))?
                }
                other => crate::fmath::round(other.as_f64()?) as i32,
            };
            push(interp, Value::I32(n));
        }
        "ToDouble" => {
            let v = interp.deref(args[0])?;
            let n = match v {
                Value::Ref(_) => {
                    let s = str_arg(interp, args[0])?;
                    s.trim().parse::<f64>().map_err(|_| format!("FormatException: '{s}'"))?
                }
                other => other.as_f64()?,
            };
            push(interp, Value::F64(n));
        }
        "ToInt64" => {
            let v = interp.deref(args[0])?;
            push(interp, Value::I64(v.as_i64()?));
        }
        "ToString" => {
            let v = interp.deref(args[0])?;
            let s = interp.display_value(v)?;
            let r = interp.heap.alloc_str(s);
            push(interp, Value::Ref(r));
        }
        other => return Err(format!("unsupported Convert intrinsic: {other}")),
    }
    Ok(())
}

pub fn bitconverter_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    match op {
        "GetBytes" => {
            let v = interp.deref(args[0])?;
            let bytes: Vec<u8> = if rest.contains("(i8") || rest.contains("(u8)") {
                v.as_i64()?.to_le_bytes().to_vec()
            } else if rest.contains("(r8") {
                v.as_f64()?.to_le_bytes().to_vec()
            } else if rest.contains("(r4") {
                (v.as_f64()? as f32).to_le_bytes().to_vec()
            } else if rest.contains("(i2") || rest.contains("(u2") || rest.contains("(char") {
                (v.as_i32()? as i16).to_le_bytes().to_vec()
            } else if rest.contains("(bool") {
                vec![v.is_truthy() as u8]
            } else {
                v.as_i32()?.to_le_bytes().to_vec()
            };
            let arr = alloc_byte_array(interp, &bytes);
            push(interp, arr);
        }
        "DoubleToInt64Bits" => {
            let v = interp.deref(args[0])?.as_f64()?;
            push(interp, Value::I64(v.to_bits() as i64));
        }
        "Int64BitsToDouble" => {
            let v = interp.deref(args[0])?.as_i64()?;
            push(interp, Value::F64(f64::from_bits(v as u64)));
        }
        "ToInt32" | "ToUInt32" | "ToInt16" | "ToUInt16" | "ToInt64" | "ToUInt64" | "ToDouble"
        | "ToSingle" | "ToBoolean" => {
            let bytes = bytes_arg(interp, args[0])?;
            let off = if args.len() > 1 { interp.deref(args[1])?.as_i32()? as usize } else { 0 };
            let need = match op {
                "ToBoolean" => 1,
                "ToInt16" | "ToUInt16" => 2,
                "ToInt64" | "ToUInt64" | "ToDouble" => 8,
                "ToSingle" => 4,
                _ => 4,
            };
            if off + need > bytes.len() {
                return Err("ArgumentOutOfRangeException".into());
            }
            let slice = &bytes[off..off + need];
            let out = match op {
                "ToBoolean" => Value::I32((slice[0] != 0) as i32),
                "ToInt16" => Value::I32(i16::from_le_bytes(slice.try_into().unwrap()) as i32),
                "ToUInt16" => Value::I32(u16::from_le_bytes(slice.try_into().unwrap()) as i32),
                "ToInt64" | "ToUInt64" => {
                    Value::I64(i64::from_le_bytes(slice.try_into().unwrap()))
                }
                "ToDouble" => Value::F64(f64::from_le_bytes(slice.try_into().unwrap())),
                "ToSingle" => Value::F64(f32::from_le_bytes(slice.try_into().unwrap()) as f64),
                "ToUInt32" => Value::I32(u32::from_le_bytes(slice.try_into().unwrap()) as i32),
                _ => Value::I32(i32::from_le_bytes(slice.try_into().unwrap())),
            };
            push(interp, out);
        }
        "ToString" => {
            let bytes = bytes_arg(interp, args[0])?;
            let hex: Vec<String> = bytes.iter().map(|b| format!("{b:02X}")).collect();
            let r = interp.heap.alloc_str(hex.join("-"));
            push(interp, Value::Ref(r));
        }
        other => return Err(format!("unsupported BitConverter intrinsic: {other}")),
    }
    Ok(())
}

pub fn encoding_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    match op {
        "get_UTF8" | "get_ASCII" => {
            let r = interp.heap.alloc(HeapObject::Boxed(Value::I32(65001)));
            push(interp, Value::Ref(r));
        }
        "GetBytes" => {
            // instance: args[0] = encoding, args[1] = string
            let s = str_arg(interp, args[1])?;
            let arr = alloc_byte_array(interp, s.as_bytes());
            push(interp, arr);
        }
        "GetString" => {
            let bytes = bytes_arg(interp, args[1])?;
            let s = String::from_utf8_lossy(&bytes).to_string();
            let r = interp.heap.alloc_str(s);
            push(interp, Value::Ref(r));
        }
        other => return Err(format!("unsupported Encoding intrinsic: {other}")),
    }
    Ok(())
}

pub fn regex_intrinsic<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    rest: &str,
    args: &[Value],
    is_newobj: bool,
) -> Result<(), String> {
    let op = rest.split('(').next().unwrap_or(rest);
    if is_newobj && op == ".ctor" {
        // Regex instance = its pattern string.
        let pattern = str_arg(interp, args[0])?;
        Regex::new(&pattern)?; // validate eagerly
        let r = interp.heap.alloc_str(pattern);
        push(interp, Value::Ref(r));
        return Ok(());
    }
    // Static forms take (input, pattern, ...); instance forms take
    // (this=pattern, input, ...).
    let (input, pattern, extra) = if rest.contains("(string,string") {
        (str_arg(interp, args[0])?, str_arg(interp, args[1])?, 2)
    } else {
        (str_arg(interp, args[1])?, str_arg(interp, args[0])?, 2)
    };
    let re = Regex::new(&pattern)?;
    match op {
        "IsMatch" => push(interp, Value::I32(re.is_match(&input) as i32)),
        "Replace" => {
            let rep = str_arg(interp, args[extra])?;
            let r = interp.heap.alloc_str(re.replace_all(&input, &rep));
            push(interp, Value::Ref(r));
        }
        "Split" => {
            let parts = re.split(&input);
            let arr = alloc_str_array(interp, parts);
            push(interp, arr);
        }
        "Match" => {
            // Returns the matched substring ("" when no match) — a
            // pragmatic subset of Match for embedded use.
            let matched = re
                .find(&input, 0)
                .map(|(s, e)| input.chars().skip(s).take(e - s).collect::<String>())
                .unwrap_or_default();
            let r = interp.heap.alloc_str(matched);
            push(interp, Value::Ref(r));
        }
        other => return Err(format!("unsupported Regex intrinsic: {other}")),
    }
    Ok(())
}

// keep Addr import used
#[allow(dead_code)]
fn _t(_a: Addr) {}

/// A trim character set from either a `char[]` (array ref) or a single
/// `char` (i4). Empty means "whitespace" to the caller.
fn char_set_arg<H: RuntimeHost>(
    interp: &mut Interpreter<'_, H>,
    v: Value,
) -> Result<Vec<char>, String> {
    match interp.deref(v)? {
        Value::Ref(r) => match interp.heap.get(r)? {
            HeapObject::Array { data, .. } => Ok(data
                .iter()
                .filter_map(|x| x.as_i32().ok().and_then(|c| char::from_u32(c as u32)))
                .collect()),
            HeapObject::Str(s) => Ok(s.chars().collect()),
            _ => Ok(Vec::new()),
        },
        other => Ok(char::from_u32(other.as_i32()? as u32).into_iter().collect()),
    }
}
