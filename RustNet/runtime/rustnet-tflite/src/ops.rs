//! Operator kernels. Every kernel computes in f32 on dequantised values and
//! returns the output tensor's values; the interpreter re-quantises them to
//! the output tensor's scale. Layouts follow TFLite: activations NHWC,
//! CONV_2D filters [out, kh, kw, in], DEPTHWISE_CONV_2D filters
//! [1, kh, kw, in*multiplier], FULLY_CONNECTED weights [out, in].

use alloc::format;
use alloc::string::String;
use alloc::vec;
use alloc::vec::Vec;

use crate::{opcode, Interpreter, Op, ACT_NONE, ACT_RELU, ACT_RELU6, ACT_RELU_N1_TO_1, ACT_TANH};

pub(crate) fn supported(code: i32) -> bool {
    matches!(
        code,
        opcode::ADD
            | opcode::AVERAGE_POOL_2D
            | opcode::CONCATENATION
            | opcode::CONV_2D
            | opcode::DEPTHWISE_CONV_2D
            | opcode::DEQUANTIZE
            | opcode::FLOOR
            | opcode::FULLY_CONNECTED
            | opcode::LOGISTIC
            | opcode::MAX_POOL_2D
            | opcode::MUL
            | opcode::RELU
            | opcode::RELU_N1_TO_1
            | opcode::RELU6
            | opcode::RESHAPE
            | opcode::SOFTMAX
            | opcode::TANH
            | opcode::PAD
            | opcode::MEAN
            | opcode::SUB
            | opcode::DIV
            | opcode::SQUEEZE
            | opcode::MAXIMUM
            | opcode::MINIMUM
            | opcode::EXPAND_DIMS
            | opcode::LEAKY_RELU
            | opcode::QUANTIZE
            | opcode::HARD_SWISH
            | opcode::SHAPE
            | opcode::STRIDED_SLICE
            | opcode::PACK
            | opcode::TRANSPOSE
            | opcode::CAST
            | opcode::ARG_MAX
    )
}

fn activate(v: f32, act: u8) -> f32 {
    match act {
        ACT_NONE => v,
        ACT_RELU => v.max(0.0),
        ACT_RELU_N1_TO_1 => v.clamp(-1.0, 1.0),
        ACT_RELU6 => v.clamp(0.0, 6.0),
        ACT_TANH => libm::tanhf(v),
        _ => v,
    }
}

fn input(op: &Op, i: usize) -> Result<usize, String> {
    match op.inputs.get(i) {
        Some(&t) if t >= 0 => Ok(t as usize),
        _ => Err(format!("missing input {i}")),
    }
}

/// An optional input (TFLite marks an omitted one with -1).
fn optional_input(op: &Op, i: usize) -> Option<usize> {
    match op.inputs.get(i) {
        Some(&t) if t >= 0 => Some(t as usize),
        _ => None,
    }
}

fn shape4(s: &[usize]) -> Result<[usize; 4], String> {
    match s.len() {
        4 => Ok([s[0], s[1], s[2], s[3]]),
        3 => Ok([1, s[0], s[1], s[2]]),
        _ => Err(format!("expected a 4-D tensor, got rank {}", s.len())),
    }
}

/// Leading padding for one spatial axis, derived from the output size the
/// converter already recorded (SAME) or zero (VALID).
fn pad_before(same: bool, input: usize, output: usize, stride: usize, kernel: usize, dilation: usize) -> isize {
    if !same {
        return 0;
    }
    let effective = (kernel - 1) * dilation + 1;
    let needed = ((output as isize - 1) * stride as isize + effective as isize - input as isize).max(0);
    needed / 2
}

pub(crate) fn run(it: &Interpreter, op: &Op) -> Result<Vec<f32>, String> {
    let out_t = *op.outputs.first().ok_or("operator has no output")? as usize;
    let out_shape = it.tensors[out_t].shape.clone();
    let o = &op.opts;
    match op.code {
        opcode::CONV_2D => {
            let (x_t, w_t) = (input(op, 0)?, input(op, 1)?);
            let x = it.tensor_values(x_t);
            let [n, h, w, c] = shape4(&it.tensors[x_t].shape)?;
            let ws = &it.tensors[w_t].shape;
            if ws.len() != 4 || ws[3] != c {
                return Err("filter shape does not match the input channels".into());
            }
            let (kh, kw) = (ws[1], ws[2]);
            let [_, oh, ow, oc] = shape4(&out_shape)?;
            let bias = optional_input(op, 2).map(|b| it.tensor_values(b));
            let ph = pad_before(o.padding_same, h, oh, o.stride_h, kh, o.dilation_h);
            let pw = pad_before(o.padding_same, w, ow, o.stride_w, kw, o.dilation_w);
            let mut out = vec![0.0f32; n * oh * ow * oc];
            for b in 0..n {
                for oy in 0..oh {
                    for ox in 0..ow {
                        for f in 0..oc {
                            let mut acc = bias.as_ref().map(|v| v[f]).unwrap_or(0.0);
                            for ky in 0..kh {
                                let iy = (oy * o.stride_h) as isize - ph + (ky * o.dilation_h) as isize;
                                if iy < 0 || iy >= h as isize {
                                    continue;
                                }
                                for kx in 0..kw {
                                    let ix = (ox * o.stride_w) as isize - pw + (kx * o.dilation_w) as isize;
                                    if ix < 0 || ix >= w as isize {
                                        continue;
                                    }
                                    let xb = ((b * h + iy as usize) * w + ix as usize) * c;
                                    let wb = ((f * kh + ky) * kw + kx) * c;
                                    for ic in 0..c {
                                        acc += x[xb + ic] * it.weight(w_t, wb + ic);
                                    }
                                }
                            }
                            out[((b * oh + oy) * ow + ox) * oc + f] = activate(acc, o.activation);
                        }
                    }
                }
            }
            Ok(out)
        }
        opcode::DEPTHWISE_CONV_2D => {
            let (x_t, w_t) = (input(op, 0)?, input(op, 1)?);
            let x = it.tensor_values(x_t);
            let [n, h, w, c] = shape4(&it.tensors[x_t].shape)?;
            let ws = &it.tensors[w_t].shape;
            if ws.len() != 4 {
                return Err("depthwise filter must be 4-D".into());
            }
            let (kh, kw) = (ws[1], ws[2]);
            let [_, oh, ow, oc] = shape4(&out_shape)?;
            let m = (oc / c.max(1)).max(1);
            let bias = optional_input(op, 2).map(|b| it.tensor_values(b));
            let ph = pad_before(o.padding_same, h, oh, o.stride_h, kh, o.dilation_h);
            let pw = pad_before(o.padding_same, w, ow, o.stride_w, kw, o.dilation_w);
            let mut out = vec![0.0f32; n * oh * ow * oc];
            for b in 0..n {
                for oy in 0..oh {
                    for ox in 0..ow {
                        for f in 0..oc {
                            let ic = f / m;
                            let mut acc = bias.as_ref().map(|v| v[f]).unwrap_or(0.0);
                            for ky in 0..kh {
                                let iy = (oy * o.stride_h) as isize - ph + (ky * o.dilation_h) as isize;
                                if iy < 0 || iy >= h as isize {
                                    continue;
                                }
                                for kx in 0..kw {
                                    let ix = (ox * o.stride_w) as isize - pw + (kx * o.dilation_w) as isize;
                                    if ix < 0 || ix >= w as isize {
                                        continue;
                                    }
                                    acc += x[((b * h + iy as usize) * w + ix as usize) * c + ic]
                                        * it.weight(w_t, (ky * kw + kx) * oc + f);
                                }
                            }
                            out[((b * oh + oy) * ow + ox) * oc + f] = activate(acc, o.activation);
                        }
                    }
                }
            }
            Ok(out)
        }
        opcode::FULLY_CONNECTED => {
            let (x_t, w_t) = (input(op, 0)?, input(op, 1)?);
            let x = it.tensor_values(x_t);
            let ws = &it.tensors[w_t].shape;
            if ws.len() != 2 {
                return Err("fully-connected weights must be 2-D".into());
            }
            let (units, depth) = (ws[0], ws[1]);
            if depth == 0 || x.len() % depth != 0 {
                return Err("input size is not a multiple of the weight depth".into());
            }
            let batches = x.len() / depth;
            let bias = optional_input(op, 2).map(|b| it.tensor_values(b));
            let mut out = vec![0.0f32; batches * units];
            for b in 0..batches {
                for u in 0..units {
                    let mut acc = bias.as_ref().map(|v| v[u]).unwrap_or(0.0);
                    for d in 0..depth {
                        acc += x[b * depth + d] * it.weight(w_t, u * depth + d);
                    }
                    out[b * units + u] = activate(acc, o.activation);
                }
            }
            Ok(out)
        }
        opcode::AVERAGE_POOL_2D | opcode::MAX_POOL_2D => {
            let x_t = input(op, 0)?;
            let x = it.tensor_values(x_t);
            let [n, h, w, c] = shape4(&it.tensors[x_t].shape)?;
            let [_, oh, ow, _] = shape4(&out_shape)?;
            let ph = pad_before(o.padding_same, h, oh, o.stride_h, o.filter_h, 1);
            let pw = pad_before(o.padding_same, w, ow, o.stride_w, o.filter_w, 1);
            let max = op.code == opcode::MAX_POOL_2D;
            let mut out = vec![0.0f32; n * oh * ow * c];
            for b in 0..n {
                for oy in 0..oh {
                    for ox in 0..ow {
                        for ch in 0..c {
                            let mut acc = if max { f32::MIN } else { 0.0 };
                            let mut count = 0usize;
                            for ky in 0..o.filter_h {
                                let iy = (oy * o.stride_h) as isize - ph + ky as isize;
                                if iy < 0 || iy >= h as isize {
                                    continue;
                                }
                                for kx in 0..o.filter_w {
                                    let ix = (ox * o.stride_w) as isize - pw + kx as isize;
                                    if ix < 0 || ix >= w as isize {
                                        continue;
                                    }
                                    let v = x[((b * h + iy as usize) * w + ix as usize) * c + ch];
                                    if max {
                                        acc = acc.max(v);
                                    } else {
                                        acc += v;
                                    }
                                    count += 1;
                                }
                            }
                            // Average pooling divides by the in-bounds cells only, as TFLite does.
                            let v = if max { acc } else { acc / count.max(1) as f32 };
                            out[((b * oh + oy) * ow + ox) * c + ch] = activate(v, o.activation);
                        }
                    }
                }
            }
            Ok(out)
        }
        opcode::SOFTMAX => {
            let x_t = input(op, 0)?;
            let x = it.tensor_values(x_t);
            let depth = *it.tensors[x_t].shape.last().unwrap_or(&x.len());
            let mut out = vec![0.0f32; x.len()];
            for row in 0..x.len() / depth.max(1) {
                let s = &x[row * depth..(row + 1) * depth];
                let max = s.iter().cloned().fold(f32::MIN, f32::max);
                let mut sum = 0.0f32;
                for (k, v) in s.iter().enumerate() {
                    let e = libm::expf((v - max) * o.beta);
                    out[row * depth + k] = e;
                    sum += e;
                }
                for k in 0..depth {
                    out[row * depth + k] /= sum;
                }
            }
            Ok(out)
        }
        opcode::ADD | opcode::SUB | opcode::MUL | opcode::DIV | opcode::MAXIMUM | opcode::MINIMUM => {
            let (a_t, b_t) = (input(op, 0)?, input(op, 1)?);
            let (a, b) = (it.tensor_values(a_t), it.tensor_values(b_t));
            let (sa, sb) = (&it.tensors[a_t].shape, &it.tensors[b_t].shape);
            let total: usize = out_shape.iter().product::<usize>().max(1);
            let mut out = vec![0.0f32; total];
            for (k, slot) in out.iter_mut().enumerate() {
                let x = a[broadcast_index(k, &out_shape, sa)];
                let y = b[broadcast_index(k, &out_shape, sb)];
                let v = match op.code {
                    opcode::ADD => x + y,
                    opcode::SUB => x - y,
                    opcode::MUL => x * y,
                    opcode::DIV => x / y,
                    opcode::MAXIMUM => x.max(y),
                    _ => x.min(y),
                };
                *slot = activate(v, o.activation);
            }
            Ok(out)
        }
        opcode::RELU | opcode::RELU6 | opcode::RELU_N1_TO_1 | opcode::LOGISTIC | opcode::TANH
        | opcode::LEAKY_RELU | opcode::HARD_SWISH | opcode::FLOOR => {
            let x = it.tensor_values(input(op, 0)?);
            Ok(x.into_iter()
                .map(|v| match op.code {
                    opcode::RELU => v.max(0.0),
                    opcode::RELU6 => v.clamp(0.0, 6.0),
                    opcode::RELU_N1_TO_1 => v.clamp(-1.0, 1.0),
                    opcode::LOGISTIC => 1.0 / (1.0 + libm::expf(-v)),
                    opcode::TANH => libm::tanhf(v),
                    opcode::LEAKY_RELU => if v >= 0.0 { v } else { v * o.alpha },
                    opcode::HARD_SWISH => v * (v + 3.0).clamp(0.0, 6.0) / 6.0,
                    _ => libm::floorf(v),
                })
                .collect())
        }
        // Pure data movement: the values are unchanged, only the shape (or,
        // for (de)quantise, the encoding the interpreter applies afterwards).
        opcode::RESHAPE | opcode::SQUEEZE | opcode::EXPAND_DIMS | opcode::QUANTIZE | opcode::DEQUANTIZE => {
            Ok(it.tensor_values(input(op, 0)?))
        }
        opcode::MEAN => {
            let x_t = input(op, 0)?;
            let x = it.tensor_values(x_t);
            let shape = it.tensors[x_t].shape.clone();
            let rank = shape.len() as i64;
            let mut reduce = vec![false; shape.len()];
            for a in it.const_ints(input(op, 1)?)? {
                let a = if a < 0 { a + rank } else { a };
                if a < 0 || a >= rank {
                    return Err("MEAN axis out of range".into());
                }
                reduce[a as usize] = true;
            }
            let kept: Vec<usize> = (0..shape.len()).filter(|&d| !reduce[d]).map(|d| shape[d]).collect();
            let out_len: usize = kept.iter().product::<usize>().max(1);
            let mut sums = vec![0.0f32; out_len];
            let mut coord = vec![0usize; shape.len()];
            for v in x.iter() {
                let mut idx = 0usize;
                for d in 0..shape.len() {
                    if !reduce[d] {
                        idx = idx * shape[d] + coord[d];
                    }
                }
                sums[idx] += *v;
                for d in (0..shape.len()).rev() {
                    coord[d] += 1;
                    if coord[d] < shape[d] {
                        break;
                    }
                    coord[d] = 0;
                }
            }
            let count = (x.len() / out_len).max(1) as f32;
            Ok(sums.into_iter().map(|s| s / count).collect())
        }
        opcode::CONCATENATION => {
            let rank = out_shape.len() as i32;
            let axis = if o.axis < 0 { o.axis + rank } else { o.axis };
            if axis < 0 || axis >= rank {
                return Err("concatenation axis out of range".into());
            }
            let axis = axis as usize;
            let outer: usize = out_shape[..axis].iter().product();
            let parts: Vec<(Vec<f32>, usize)> = op
                .inputs
                .iter()
                .filter(|&&t| t >= 0)
                .map(|&t| {
                    let s = &it.tensors[t as usize].shape;
                    let block: usize = s[axis..].iter().product();
                    (it.tensor_values(t as usize), block)
                })
                .collect();
            let mut out = Vec::new();
            for r in 0..outer.max(1) {
                for (vals, block) in parts.iter() {
                    out.extend_from_slice(&vals[r * block..(r + 1) * block]);
                }
            }
            if let Some(act) = Some(o.activation).filter(|&a| a != ACT_NONE) {
                out.iter_mut().for_each(|v| *v = activate(*v, act));
            }
            Ok(out)
        }
        opcode::PAD => {
            let x_t = input(op, 0)?;
            let x = it.tensor_values(x_t);
            let shape = it.tensors[x_t].shape.clone();
            let pads = it.const_ints(input(op, 1)?)?;
            if pads.len() != shape.len() * 2 || out_shape.len() != shape.len() {
                return Err("PAD paddings do not match the input rank".into());
            }
            let total: usize = out_shape.iter().product::<usize>().max(1);
            let mut out = vec![0.0f32; total];
            let mut coord = vec![0usize; out_shape.len()];
            for slot in out.iter_mut() {
                let mut idx = 0usize;
                let mut inside = true;
                for d in 0..shape.len() {
                    let c = coord[d] as i64 - pads[d * 2];
                    if c < 0 || c >= shape[d] as i64 {
                        inside = false;
                        break;
                    }
                    idx = idx * shape[d] + c as usize;
                }
                if inside {
                    *slot = x[idx];
                }
                for d in (0..out_shape.len()).rev() {
                    coord[d] += 1;
                    if coord[d] < out_shape[d] {
                        break;
                    }
                    coord[d] = 0;
                }
            }
            Ok(out)
        }
        // Shape arithmetic, as Keras' Flatten emits it: SHAPE -> STRIDED_SLICE
        // -> PACK -> RESHAPE. Integer tensors ride in f32, exact for any size
        // a microcontroller model has.
        opcode::SHAPE => Ok(it.tensors[input(op, 0)?].shape.iter().map(|&d| d as f32).collect()),
        opcode::STRIDED_SLICE => {
            let x_t = input(op, 0)?;
            let x = it.tensor_values(x_t);
            let shape = it.tensors[x_t].shape.clone();
            if o.ellipsis_mask != 0 || o.new_axis_mask != 0 {
                return Err("STRIDED_SLICE with ellipsis or new-axis masks is not supported".into());
            }
            let begin = it.const_ints(input(op, 1)?)?;
            let end = it.const_ints(input(op, 2)?)?;
            let strides = match optional_input(op, 3) {
                Some(s) => it.const_ints(s)?,
                None => vec![1; shape.len()],
            };
            let mut ranges: Vec<(i64, i64, i64)> = Vec::with_capacity(shape.len());
            for d in 0..shape.len() {
                let dim = shape[d] as i64;
                let st = *strides.get(d).unwrap_or(&1);
                if st == 0 {
                    return Err("STRIDED_SLICE stride of zero".into());
                }
                let norm = |v: i64| if v < 0 { v + dim } else { v };
                let mut b = if d < begin.len() && o.begin_mask & (1 << d) == 0 {
                    norm(begin[d])
                } else if st > 0 {
                    0
                } else {
                    dim - 1
                };
                let mut e = if d < end.len() && o.end_mask & (1 << d) == 0 {
                    norm(end[d])
                } else if st > 0 {
                    dim
                } else {
                    -1
                };
                if st > 0 {
                    b = b.clamp(0, dim);
                    e = e.clamp(0, dim);
                } else {
                    b = b.clamp(-1, dim - 1);
                    e = e.clamp(-1, dim - 1);
                }
                if o.shrink_axis_mask & (1 << d) != 0 {
                    ranges.push((b, b + 1, 1));
                } else {
                    ranges.push((b, e, st));
                }
            }
            let mut out = Vec::new();
            let inside = ranges.iter().all(|&(b, e, s)| if s > 0 { b < e } else { b > e });
            if !inside {
                return Ok(out);
            }
            let mut coord: Vec<i64> = ranges.iter().map(|r| r.0).collect();
            loop {
                let mut idx = 0usize;
                for d in 0..shape.len() {
                    idx = idx * shape[d] + coord[d] as usize;
                }
                out.push(x[idx]);
                // Advance the last dimension first, carrying leftwards.
                let mut d = shape.len();
                loop {
                    if d == 0 {
                        return Ok(out);
                    }
                    d -= 1;
                    let (b, e, s) = ranges[d];
                    coord[d] += s;
                    if (s > 0 && coord[d] < e) || (s < 0 && coord[d] > e) {
                        break;
                    }
                    coord[d] = b;
                }
            }
        }
        opcode::PACK => {
            let parts: Vec<usize> = op.inputs.iter().filter(|&&t| t >= 0).map(|&t| t as usize).collect();
            let first = *parts.first().ok_or("PACK with no inputs")?;
            let in_shape = it.tensors[first].shape.clone();
            let rank = in_shape.len() as i32 + 1;
            let axis = if o.axis < 0 { o.axis + rank } else { o.axis };
            if axis < 0 || axis >= rank {
                return Err("PACK axis out of range".into());
            }
            let outer: usize = in_shape[..axis as usize].iter().product();
            let inner: usize = in_shape[axis as usize..].iter().product();
            let vals: Vec<Vec<f32>> = parts.iter().map(|&t| it.tensor_values(t)).collect();
            let mut out = Vec::with_capacity(outer.max(1) * inner * vals.len());
            for r in 0..outer.max(1) {
                for v in vals.iter() {
                    out.extend_from_slice(&v[r * inner..(r + 1) * inner]);
                }
            }
            Ok(out)
        }
        opcode::TRANSPOSE => {
            let x_t = input(op, 0)?;
            let x = it.tensor_values(x_t);
            let shape = it.tensors[x_t].shape.clone();
            let perm = it.const_ints(input(op, 1)?)?;
            if perm.len() != shape.len() || perm.iter().any(|&p| p < 0 || p as usize >= shape.len()) {
                return Err("TRANSPOSE permutation does not match the rank".into());
            }
            let rank = shape.len();
            let mut in_strides = vec![1usize; rank];
            for d in (0..rank.saturating_sub(1)).rev() {
                in_strides[d] = in_strides[d + 1] * shape[d + 1];
            }
            let out_dims: Vec<usize> = perm.iter().map(|&p| shape[p as usize]).collect();
            let mut out = Vec::with_capacity(x.len());
            let mut coord = vec![0usize; rank];
            for _ in 0..x.len() {
                let idx: usize = (0..rank).map(|d| coord[d] * in_strides[perm[d] as usize]).sum();
                out.push(x[idx]);
                for d in (0..rank).rev() {
                    coord[d] += 1;
                    if coord[d] < out_dims[d] {
                        break;
                    }
                    coord[d] = 0;
                }
            }
            Ok(out)
        }
        opcode::CAST => {
            let x = it.tensor_values(input(op, 0)?);
            let to_int = it.tensors[out_t].dtype != crate::DType::F32;
            Ok(x.into_iter().map(|v| if to_int { libm::truncf(v) } else { v }).collect())
        }
        opcode::ARG_MAX => {
            let x_t = input(op, 0)?;
            let x = it.tensor_values(x_t);
            let shape = it.tensors[x_t].shape.clone();
            let rank = shape.len() as i64;
            let axis = it.const_ints(input(op, 1)?)?.first().copied().unwrap_or(rank - 1);
            let axis = (if axis < 0 { axis + rank } else { axis }) as usize;
            if axis >= shape.len() {
                return Err("ARG_MAX axis out of range".into());
            }
            let outer: usize = shape[..axis].iter().product();
            let n = shape[axis];
            let inner: usize = shape[axis + 1..].iter().product();
            let mut out = Vec::with_capacity(outer.max(1) * inner.max(1));
            for oi in 0..outer.max(1) {
                for i in 0..inner.max(1) {
                    let mut best = 0usize;
                    for k in 1..n {
                        if x[(oi * n + k) * inner + i] > x[(oi * n + best) * inner + i] {
                            best = k;
                        }
                    }
                    out.push(best as f32);
                }
            }
            Ok(out)
        }
        other => Err(format!("operator {other} is not supported")),

    }
}

/// NumPy broadcasting: the flat index into `shape` that output element `k`
/// (of `out`) reads. Shapes are right-aligned; size-1 dimensions repeat.
fn broadcast_index(k: usize, out: &[usize], shape: &[usize]) -> usize {
    let total: usize = shape.iter().product::<usize>().max(1);
    if total == 1 {
        return 0;
    }
    let mut rem = k;
    let mut idx = 0usize;
    let mut stride = 1usize;
    let offset = out.len() as isize - shape.len() as isize;
    for d in (0..out.len()).rev() {
        let c = rem % out[d];
        rem /= out[d];
        let sd = d as isize - offset;
        if sd >= 0 {
            let dim = shape[sd as usize];
            if dim != 1 {
                idx += c * stride;
            }
            stride *= dim;
        }
    }
    idx
}
