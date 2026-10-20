//! TensorFlow Lite for Microcontrollers, the RustNet way: a dependency-free
//! reader and interpreter for `.tflite` models, `no_std + alloc`, so the same
//! code runs on the virtual device and on silicon.
//!
//! The model is parsed straight from its FlatBuffer (schema v3) with a
//! bounds-checked reader, and the interpreter runs the operators most
//! microcontroller models are made of — convolutions, depthwise
//! convolutions, fully-connected layers, pooling, element-wise arithmetic
//! with broadcasting, the usual activations, softmax, reshapes, padding,
//! mean, concatenation and (de)quantisation — for float32 and int8/uint8
//! models (per-tensor and per-channel quantisation).
//!
//! Quantised models are executed in float with every intermediate tensor
//! re-quantised to its declared scale and zero point. That reproduces the
//! integer pipeline's rounding at tensor boundaries; results agree with
//! TensorFlow's reference kernels to within one quantisation step.
//!
//! Constant tensors (weights) are never copied: kernels read them in place
//! from the model bytes and dequantise on the fly, so a model costs its own
//! size plus its activations.

#![cfg_attr(not(test), no_std)]

extern crate alloc;

mod flatbuf;
mod ops;

use alloc::format;
use alloc::string::String;
use alloc::vec;
use alloc::vec::Vec;

use flatbuf::{Buf, Table};

/// Element type of a tensor (TFLite `TensorType`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DType {
    F32,
    I32,
    U8,
    I64,
    Bool,
    I16,
    I8,
}

impl DType {
    fn from_tflite(t: u8) -> Result<Self, String> {
        Ok(match t {
            0 => DType::F32,
            2 => DType::I32,
            3 => DType::U8,
            4 => DType::I64,
            6 => DType::Bool,
            7 => DType::I16,
            9 => DType::I8,
            other => return Err(format!("unsupported tensor type {other}")),
        })
    }

    /// The code RustNet's managed API reports (TFLite's own numbering).
    pub fn code(self) -> i32 {
        match self {
            DType::F32 => 0,
            DType::I32 => 2,
            DType::U8 => 3,
            DType::I64 => 4,
            DType::Bool => 6,
            DType::I16 => 7,
            DType::I8 => 9,
        }
    }

    pub fn size(self) -> usize {
        match self {
            DType::U8 | DType::I8 | DType::Bool => 1,
            DType::I16 => 2,
            DType::F32 | DType::I32 => 4,
            DType::I64 => 8,
        }
    }

    fn quantized(self) -> bool {
        matches!(self, DType::I8 | DType::U8 | DType::I16)
    }

    fn range(self) -> (f32, f32) {
        match self {
            DType::I8 => (-128.0, 127.0),
            DType::U8 => (0.0, 255.0),
            DType::I16 => (-32768.0, 32767.0),
            _ => (f32::MIN, f32::MAX),
        }
    }
}

/// Static description of one tensor.
#[derive(Debug, Clone)]
pub struct TensorInfo {
    pub name: String,
    pub shape: Vec<usize>,
    pub dtype: DType,
    /// Per-tensor (one entry) or per-channel scales; empty when not quantised.
    pub scale: Vec<f32>,
    pub zero_point: Vec<i64>,
    pub quantized_dim: usize,
    /// Byte range of constant data inside the model, if the tensor is a constant.
    data: Option<(usize, usize)>,
}

impl TensorInfo {
    pub fn len(&self) -> usize {
        self.shape.iter().product::<usize>().max(if self.shape.is_empty() { 1 } else { 0 })
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    fn is_quantized(&self) -> bool {
        self.dtype.quantized() && !self.scale.is_empty()
    }

    /// Scale and zero point of element `i` (handles per-channel quantisation).
    fn qparams(&self, i: usize) -> (f32, f32) {
        if self.scale.len() <= 1 {
            let s = self.scale.first().copied().unwrap_or(1.0);
            let z = self.zero_point.first().copied().unwrap_or(0) as f32;
            return (s, z);
        }
        let inner: usize = self.shape[self.quantized_dim + 1..].iter().product();
        let c = (i / inner.max(1)) % self.shape[self.quantized_dim];
        let z = self.zero_point.get(c).copied().unwrap_or(0) as f32;
        (self.scale[c], z)
    }

    /// Round-trip `v` through this tensor's quantisation (identity for float tensors).
    fn fake_quant(&self, i: usize, v: f32) -> f32 {
        if !self.is_quantized() {
            return v;
        }
        let (s, z) = self.qparams(i);
        let (lo, hi) = self.dtype.range();
        let q = (libm::roundf(v / s) + z).clamp(lo, hi);
        (q - z) * s
    }
}

/// Fused activation codes (TFLite `ActivationFunctionType`).
pub(crate) const ACT_NONE: u8 = 0;
pub(crate) const ACT_RELU: u8 = 1;
pub(crate) const ACT_RELU_N1_TO_1: u8 = 2;
pub(crate) const ACT_RELU6: u8 = 3;
pub(crate) const ACT_TANH: u8 = 4;

/// Builtin operator codes this interpreter executes.
pub mod opcode {
    pub const ADD: i32 = 0;
    pub const AVERAGE_POOL_2D: i32 = 1;
    pub const CONCATENATION: i32 = 2;
    pub const CONV_2D: i32 = 3;
    pub const DEPTHWISE_CONV_2D: i32 = 4;
    pub const DEQUANTIZE: i32 = 6;
    pub const FLOOR: i32 = 8;
    pub const FULLY_CONNECTED: i32 = 9;
    pub const LOGISTIC: i32 = 14;
    pub const MAX_POOL_2D: i32 = 17;
    pub const MUL: i32 = 18;
    pub const RELU: i32 = 19;
    pub const RELU_N1_TO_1: i32 = 20;
    pub const RELU6: i32 = 21;
    pub const RESHAPE: i32 = 22;
    pub const SOFTMAX: i32 = 25;
    pub const TANH: i32 = 28;
    pub const PAD: i32 = 34;
    pub const TRANSPOSE: i32 = 39;
    pub const MEAN: i32 = 40;
    pub const SUB: i32 = 41;
    pub const DIV: i32 = 42;
    pub const SQUEEZE: i32 = 43;
    pub const STRIDED_SLICE: i32 = 45;
    pub const CAST: i32 = 53;
    pub const MAXIMUM: i32 = 55;
    pub const ARG_MAX: i32 = 56;
    pub const MINIMUM: i32 = 57;
    pub const EXPAND_DIMS: i32 = 70;
    pub const SHAPE: i32 = 77;
    pub const PACK: i32 = 83;
    pub const LEAKY_RELU: i32 = 98;
    pub const QUANTIZE: i32 = 114;
    pub const HARD_SWISH: i32 = 117;
}

/// Human-readable name for error messages.
fn op_name(code: i32) -> String {
    let known = match code {
        opcode::ADD => "ADD",
        opcode::AVERAGE_POOL_2D => "AVERAGE_POOL_2D",
        opcode::CONCATENATION => "CONCATENATION",
        opcode::CONV_2D => "CONV_2D",
        opcode::DEPTHWISE_CONV_2D => "DEPTHWISE_CONV_2D",
        opcode::DEQUANTIZE => "DEQUANTIZE",
        opcode::FULLY_CONNECTED => "FULLY_CONNECTED",
        opcode::LOGISTIC => "LOGISTIC",
        opcode::MAX_POOL_2D => "MAX_POOL_2D",
        opcode::MUL => "MUL",
        opcode::RESHAPE => "RESHAPE",
        opcode::SOFTMAX => "SOFTMAX",
        16 => "LSTM",
        23 => "RESIZE_BILINEAR",
        opcode::TRANSPOSE => "TRANSPOSE",
        opcode::STRIDED_SLICE => "STRIDED_SLICE",
        opcode::CAST => "CAST",
        opcode::SHAPE => "SHAPE",
        opcode::PACK => "PACK",
        _ => "",
    };
    if known.is_empty() {
        format!("builtin operator {code}")
    } else {
        known.into()
    }
}

/// Operator options, flattened: each op reads the fields it has.
#[derive(Debug, Clone, Default)]
pub(crate) struct Options {
    pub padding_same: bool,
    pub stride_w: usize,
    pub stride_h: usize,
    pub dilation_w: usize,
    pub dilation_h: usize,
    pub depth_multiplier: usize,
    pub filter_w: usize,
    pub filter_h: usize,
    pub activation: u8,
    pub beta: f32,
    pub alpha: f32,
    pub axis: i32,
    pub keep_dims: bool,
    pub begin_mask: i32,
    pub end_mask: i32,
    pub ellipsis_mask: i32,
    pub new_axis_mask: i32,
    pub shrink_axis_mask: i32,
}

#[derive(Debug, Clone)]
pub(crate) struct Op {
    pub code: i32,
    pub inputs: Vec<i32>,
    pub outputs: Vec<i32>,
    pub opts: Options,
}

/// A loaded model, ready to run.
pub struct Interpreter {
    model: Vec<u8>,
    pub(crate) tensors: Vec<TensorInfo>,
    pub(crate) ops: Vec<Op>,
    inputs: Vec<usize>,
    outputs: Vec<usize>,
    /// Activation values (dequantised) for every non-constant tensor.
    pub(crate) values: Vec<Option<Vec<f32>>>,
    pub description: String,
}

fn fb<T>(r: flatbuf::Result<T>) -> Result<T, String> {
    r.map_err(|e| String::from(e.0))
}

impl Interpreter {
    /// Parse a `.tflite` model. Fails with a clear message on a corrupt
    /// model or one that uses an operator this interpreter does not run.
    pub fn new(model: Vec<u8>) -> Result<Self, String> {
        if model.len() < 8 {
            return Err("model too small".into());
        }
        if &model[4..8] != b"TFL3" {
            return Err("not a TensorFlow Lite model (missing TFL3 identifier)".into());
        }
        let (tensors, ops, inputs, outputs, description) = {
            let buf = Buf { data: &model };
            let root = fb(buf.root())?;
            let description = fb(root.string(3))?;

            let codes = fb(root.vector(1))?.ok_or("model has no operator codes")?;
            let mut opcodes = Vec::with_capacity(codes.len);
            for i in 0..codes.len {
                let c = fb(codes.table(i))?;
                let deprecated = fb(c.u8_or(0, 0))? as i8 as i32;
                let builtin = fb(c.i32_or(3, 0))?;
                if !fb(c.string(1))?.is_empty() {
                    opcodes.push(-1); // custom operator
                } else {
                    opcodes.push(deprecated.max(builtin));
                }
            }

            let buffers = fb(root.vector(4))?.ok_or("model has no buffers")?;
            let subgraphs = fb(root.vector(2))?.ok_or("model has no subgraphs")?;
            if subgraphs.len == 0 {
                return Err("model has no subgraphs".into());
            }
            let sg = fb(subgraphs.table(0))?;

            let tv = fb(sg.vector(0))?.ok_or("subgraph has no tensors")?;
            let mut tensors = Vec::with_capacity(tv.len);
            for i in 0..tv.len {
                tensors.push(Self::parse_tensor(&model, &buffers, fb(tv.table(i))?)?);
            }
            let inputs = Self::indices(fb(sg.vector(1))?, tensors.len())?;
            let outputs = Self::indices(fb(sg.vector(2))?, tensors.len())?;

            let ov = fb(sg.vector(3))?.ok_or("subgraph has no operators")?;
            let mut ops = Vec::with_capacity(ov.len);
            for i in 0..ov.len {
                let o = fb(ov.table(i))?;
                let idx = fb(o.u32_or(0, 0))? as usize;
                let code = *opcodes.get(idx).ok_or("operator code index out of range")?;
                if code < 0 {
                    return Err("custom operators are not supported".into());
                }
                let ins = fb(o.vector(1))?.map(|v| v.i32s()).transpose().map_err(|e| String::from(e.0))?.unwrap_or_default();
                let outs = fb(o.vector(2))?.map(|v| v.i32s()).transpose().map_err(|e| String::from(e.0))?.unwrap_or_default();
                for &t in ins.iter().chain(outs.iter()) {
                    if t >= tensors.len() as i32 {
                        return Err("operator references a tensor that does not exist".into());
                    }
                }
                let opts = Self::parse_options(code, fb(o.table(4))?)?;
                if !ops::supported(code) {
                    return Err(format!("operator {} is not supported by this interpreter", op_name(code)));
                }
                ops.push(Op { code, inputs: ins, outputs: outs, opts });
            }
            (tensors, ops, inputs, outputs, description)
        };
        let values = tensors.iter().map(|t| if t.data.is_some() { None } else { Some(vec![0.0; t.len()]) }).collect();
        Ok(Self { model, tensors, ops, inputs, outputs, values, description })
    }

    fn indices(v: Option<flatbuf::Vector<'_>>, count: usize) -> Result<Vec<usize>, String> {
        let v = match v {
            Some(v) => fb(v.i32s())?,
            None => Vec::new(),
        };
        v.into_iter()
            .map(|i| if i >= 0 && (i as usize) < count { Ok(i as usize) } else { Err(String::from("tensor index out of range")) })
            .collect()
    }

    fn parse_tensor(model: &[u8], buffers: &flatbuf::Vector<'_>, t: Table<'_>) -> Result<TensorInfo, String> {
        let shape = match fb(t.vector(0))? {
            Some(v) => fb(v.i32s())?.into_iter().map(|d| d.max(1) as usize).collect(),
            None => Vec::new(),
        };
        let dtype = DType::from_tflite(fb(t.u8_or(1, 0))?)?;
        let buffer = fb(t.u32_or(2, 0))? as usize;
        let name = fb(t.string(3))?;
        let (mut scale, mut zero_point, mut qdim) = (Vec::new(), Vec::new(), 0usize);
        if let Some(q) = fb(t.table(4))? {
            if let Some(s) = fb(q.vector(2))? {
                scale = fb(s.f32s())?;
            }
            if let Some(z) = fb(q.vector(3))? {
                zero_point = fb(z.i64s())?;
            }
            qdim = fb(q.i32_or(6, 0))?.max(0) as usize;
        }
        let mut data = None;
        if buffer > 0 && buffer < buffers.len {
            let b = fb(buffers.table(buffer))?;
            if let Some(d) = fb(b.vector(0))? {
                if d.len > 0 {
                    let bytes = fb(d.bytes())?;
                    let start = bytes.as_ptr() as usize - model.as_ptr() as usize;
                    data = Some((start, d.len));
                }
            }
        }
        let info = TensorInfo { name, shape, dtype, scale, zero_point, quantized_dim: qdim, data };
        if let Some((_, len)) = info.data {
            if len < info.len() * dtype.size() {
                return Err(format!("constant tensor '{}' is shorter than its shape", info.name));
            }
        }
        if qdim > 0 && qdim >= info.shape.len() && info.scale.len() > 1 {
            return Err("quantized dimension out of range".into());
        }
        Ok(info)
    }

    fn parse_options(code: i32, t: Option<Table<'_>>) -> Result<Options, String> {
        let mut o = Options { stride_w: 1, stride_h: 1, dilation_w: 1, dilation_h: 1, depth_multiplier: 1, beta: 1.0, alpha: 0.2, ..Default::default() };
        let Some(t) = t else {
            return Ok(o);
        };
        let pos = |v: i32| v.max(1) as usize;
        match code {
            opcode::CONV_2D => {
                o.padding_same = fb(t.u8_or(0, 0))? == 0;
                o.stride_w = pos(fb(t.i32_or(1, 1))?);
                o.stride_h = pos(fb(t.i32_or(2, 1))?);
                o.activation = fb(t.u8_or(3, 0))?;
                o.dilation_w = pos(fb(t.i32_or(4, 1))?);
                o.dilation_h = pos(fb(t.i32_or(5, 1))?);
            }
            opcode::DEPTHWISE_CONV_2D => {
                o.padding_same = fb(t.u8_or(0, 0))? == 0;
                o.stride_w = pos(fb(t.i32_or(1, 1))?);
                o.stride_h = pos(fb(t.i32_or(2, 1))?);
                o.depth_multiplier = pos(fb(t.i32_or(3, 1))?);
                o.activation = fb(t.u8_or(4, 0))?;
                o.dilation_w = pos(fb(t.i32_or(5, 1))?);
                o.dilation_h = pos(fb(t.i32_or(6, 1))?);
            }
            opcode::AVERAGE_POOL_2D | opcode::MAX_POOL_2D => {
                o.padding_same = fb(t.u8_or(0, 0))? == 0;
                o.stride_w = pos(fb(t.i32_or(1, 1))?);
                o.stride_h = pos(fb(t.i32_or(2, 1))?);
                o.filter_w = pos(fb(t.i32_or(3, 1))?);
                o.filter_h = pos(fb(t.i32_or(4, 1))?);
                o.activation = fb(t.u8_or(5, 0))?;
            }
            opcode::FULLY_CONNECTED => {
                o.activation = fb(t.u8_or(0, 0))?;
                o.keep_dims = fb(t.bool_or(2, false))?;
            }
            opcode::SOFTMAX => o.beta = fb(t.f32_or(0, 1.0))?,
            opcode::ADD | opcode::SUB | opcode::MUL | opcode::DIV => o.activation = fb(t.u8_or(0, 0))?,
            opcode::CONCATENATION => {
                o.axis = fb(t.i32_or(0, 0))?;
                o.activation = fb(t.u8_or(1, 0))?;
            }
            opcode::MEAN => o.keep_dims = fb(t.bool_or(0, false))?,
            opcode::LEAKY_RELU => o.alpha = fb(t.f32_or(0, 0.2))?,
            opcode::STRIDED_SLICE => {
                o.begin_mask = fb(t.i32_or(0, 0))?;
                o.end_mask = fb(t.i32_or(1, 0))?;
                o.ellipsis_mask = fb(t.i32_or(2, 0))?;
                o.new_axis_mask = fb(t.i32_or(3, 0))?;
                o.shrink_axis_mask = fb(t.i32_or(4, 0))?;
            }
            opcode::PACK => o.axis = fb(t.i32_or(1, 0))?,
            _ => {}
        }
        Ok(o)
    }

    pub fn input_count(&self) -> usize {
        self.inputs.len()
    }

    pub fn output_count(&self) -> usize {
        self.outputs.len()
    }

    pub fn operator_count(&self) -> usize {
        self.ops.len()
    }

    pub fn input(&self, i: usize) -> Result<&TensorInfo, String> {
        self.inputs.get(i).map(|&t| &self.tensors[t]).ok_or_else(|| format!("no input {i}"))
    }

    pub fn output(&self, i: usize) -> Result<&TensorInfo, String> {
        self.outputs.get(i).map(|&t| &self.tensors[t]).ok_or_else(|| format!("no output {i}"))
    }

    /// Set input `i` from real values (quantised on the way in for int8/uint8 inputs).
    pub fn set_input_f32(&mut self, i: usize, data: &[f32]) -> Result<(), String> {
        let t = *self.inputs.get(i).ok_or_else(|| format!("no input {i}"))?;
        let info = &self.tensors[t];
        if data.len() != info.len() {
            return Err(format!("input {i} expects {} values, got {}", info.len(), data.len()));
        }
        let v: Vec<f32> = data.iter().enumerate().map(|(k, &x)| info.fake_quant(k, x)).collect();
        self.values[t] = Some(v);
        Ok(())
    }

    /// Set input `i` from its raw bytes in the tensor's own type (little-endian).
    pub fn set_input_raw(&mut self, i: usize, bytes: &[u8]) -> Result<(), String> {
        let t = *self.inputs.get(i).ok_or_else(|| format!("no input {i}"))?;
        let info = &self.tensors[t];
        if bytes.len() != info.len() * info.dtype.size() {
            return Err(format!("input {i} expects {} bytes, got {}", info.len() * info.dtype.size(), bytes.len()));
        }
        let v = (0..info.len()).map(|k| raw_to_real(info, bytes, k)).collect();
        self.values[t] = Some(v);
        Ok(())
    }

    /// Output `i` as real values (dequantised for quantised outputs).
    pub fn output_f32(&self, i: usize) -> Result<Vec<f32>, String> {
        let t = *self.outputs.get(i).ok_or_else(|| format!("no output {i}"))?;
        Ok(self.tensor_values(t))
    }

    /// Output `i` as raw bytes in the tensor's own type (little-endian).
    pub fn output_raw(&self, i: usize) -> Result<Vec<u8>, String> {
        let t = *self.outputs.get(i).ok_or_else(|| format!("no output {i}"))?;
        let info = &self.tensors[t];
        let vals = self.tensor_values(t);
        let mut out = Vec::with_capacity(vals.len() * info.dtype.size());
        for (k, v) in vals.iter().enumerate() {
            real_to_raw(info, k, *v, &mut out);
        }
        Ok(out)
    }

    /// Run the whole graph once.
    pub fn invoke(&mut self) -> Result<(), String> {
        for n in 0..self.ops.len() {
            let op = self.ops[n].clone();
            let out = ops::run(self, &op).map_err(|e| format!("{} (operator {n}): {e}", op_name(op.code)))?;
            let t = *op.outputs.first().ok_or("operator has no output")? as usize;
            let info = &self.tensors[t];
            if out.len() != info.len() {
                return Err(format!(
                    "{} (operator {n}) produced {} values for a tensor of {}",
                    op_name(op.code),
                    out.len(),
                    info.len()
                ));
            }
            let q: Vec<f32> = out.into_iter().enumerate().map(|(k, v)| info.fake_quant(k, v)).collect();
            self.values[t] = Some(q);
        }
        Ok(())
    }

    /// The real values of any tensor: activations from the value store,
    /// constants dequantised from the model bytes.
    pub(crate) fn tensor_values(&self, t: usize) -> Vec<f32> {
        if let Some(v) = &self.values[t] {
            return v.clone();
        }
        let info = &self.tensors[t];
        match info.data {
            Some((start, len)) => {
                let bytes = &self.model[start..start + len];
                (0..info.len()).map(|k| raw_to_real(info, bytes, k)).collect()
            }
            None => vec![0.0; info.len()],
        }
    }

    /// Read a constant integer tensor (shapes, axes, paddings).
    pub(crate) fn const_ints(&self, t: usize) -> Result<Vec<i64>, String> {
        let info = &self.tensors[t];
        let Some((start, len)) = info.data else {
            return Ok(self.tensor_values(t).into_iter().map(|v| v as i64).collect());
        };
        let b = &self.model[start..start + len];
        Ok((0..info.len())
            .map(|k| match info.dtype {
                DType::I64 => {
                    let mut a = [0u8; 8];
                    a.copy_from_slice(&b[k * 8..k * 8 + 8]);
                    i64::from_le_bytes(a)
                }
                DType::I32 => i32::from_le_bytes([b[k * 4], b[k * 4 + 1], b[k * 4 + 2], b[k * 4 + 3]]) as i64,
                _ => raw_to_real(info, b, k) as i64,
            })
            .collect())
    }

    /// A constant tensor's element `k` without copying the tensor (weights).
    pub(crate) fn weight(&self, t: usize, k: usize) -> f32 {
        let info = &self.tensors[t];
        if let Some(v) = &self.values[t] {
            return v[k];
        }
        match info.data {
            Some((start, len)) => raw_to_real(info, &self.model[start..start + len], k),
            None => 0.0,
        }
    }
}

fn raw_to_real(info: &TensorInfo, b: &[u8], k: usize) -> f32 {
    let raw = match info.dtype {
        DType::F32 => return f32::from_le_bytes([b[k * 4], b[k * 4 + 1], b[k * 4 + 2], b[k * 4 + 3]]),
        DType::I32 => i32::from_le_bytes([b[k * 4], b[k * 4 + 1], b[k * 4 + 2], b[k * 4 + 3]]) as f32,
        DType::I64 => {
            let mut a = [0u8; 8];
            a.copy_from_slice(&b[k * 8..k * 8 + 8]);
            i64::from_le_bytes(a) as f32
        }
        DType::I8 => b[k] as i8 as f32,
        DType::U8 | DType::Bool => b[k] as f32,
        DType::I16 => i16::from_le_bytes([b[k * 2], b[k * 2 + 1]]) as f32,
    };
    if info.scale.is_empty() {
        return raw;
    }
    let (s, z) = info.qparams(k);
    (raw - z) * s
}

fn real_to_raw(info: &TensorInfo, k: usize, v: f32, out: &mut Vec<u8>) {
    let q = if info.is_quantized() {
        let (s, z) = info.qparams(k);
        let (lo, hi) = info.dtype.range();
        (libm::roundf(v / s) + z).clamp(lo, hi)
    } else {
        v
    };
    match info.dtype {
        DType::F32 => out.extend_from_slice(&v.to_le_bytes()),
        DType::I32 => out.extend_from_slice(&(libm::roundf(q) as i32).to_le_bytes()),
        DType::I64 => out.extend_from_slice(&(libm::roundf(q) as i64).to_le_bytes()),
        DType::I8 => out.push(q as i8 as u8),
        DType::U8 | DType::Bool => out.push(q as u8),
        DType::I16 => out.extend_from_slice(&(q as i16).to_le_bytes()),
    }
}
