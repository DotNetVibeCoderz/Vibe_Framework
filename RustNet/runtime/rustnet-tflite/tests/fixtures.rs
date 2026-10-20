//! Real `.tflite` models produced by TensorFlow's converter, with reference
//! outputs from TensorFlow's own interpreter (tools/make_fixtures.py).
//! rustnet-tflite must reproduce them: float models to 1e-4, int8 models to
//! within one quantisation step per output.

use std::path::PathBuf;

use rustnet_tflite::{DType, Interpreter};

fn fixture(name: &str, ext: &str) -> Vec<u8> {
    let p: PathBuf = [env!("CARGO_MANIFEST_DIR"), "tests", "models", &format!("{name}.{ext}")].iter().collect();
    std::fs::read(&p).unwrap_or_else(|e| panic!("{}: {e}", p.display()))
}

fn run(name: &str) -> (Interpreter, Vec<u8>, Vec<u8>) {
    let mut m = Interpreter::new(fixture(name, "tflite")).expect("model loads");
    let input = fixture(name, "in");
    m.set_input_raw(0, &input).expect("input fits");
    m.invoke().expect("invoke");
    let got = m.output_raw(0).unwrap();
    (m, got, fixture(name, "out"))
}

fn floats(b: &[u8]) -> Vec<f32> {
    b.chunks_exact(4).map(|c| f32::from_le_bytes([c[0], c[1], c[2], c[3]])).collect()
}

fn assert_close_f32(name: &str, got: &[u8], want: &[u8]) {
    let (g, w) = (floats(got), floats(want));
    assert_eq!(g.len(), w.len(), "{name}: output length");
    for (i, (a, b)) in g.iter().zip(w.iter()).enumerate() {
        let tol = 1e-4 + 1e-4 * b.abs();
        assert!((a - b).abs() <= tol, "{name}[{i}]: got {a}, TensorFlow says {b}");
    }
}

#[test]
fn float_mlp_matches_tensorflow() {
    let (m, got, want) = run("mlp_float");
    assert_eq!(m.input(0).unwrap().shape, vec![1, 4]);
    assert_eq!(m.output(0).unwrap().dtype, DType::F32);
    assert_close_f32("mlp_float", &got, &want);
    let probs = floats(&got);
    assert!((probs.iter().sum::<f32>() - 1.0).abs() < 1e-5, "softmax sums to one");
}

#[test]
fn int8_cnn_matches_tensorflow_within_one_step() {
    let (m, got, want) = run("cnn_int8");
    assert_eq!(m.input(0).unwrap().dtype, DType::I8);
    assert_eq!(m.input(0).unwrap().shape, vec![1, 12, 12, 1]);
    assert_eq!(got.len(), want.len());
    for (i, (a, b)) in got.iter().zip(want.iter()).enumerate() {
        let (a, b) = (*a as i8 as i32, *b as i8 as i32);
        assert!((a - b).abs() <= 1, "cnn_int8[{i}]: got {a}, TensorFlow says {b}");
    }
    // The real-valued view dequantises the same bytes.
    let real = m.output_f32(0).unwrap();
    let q = &m.output(0).unwrap();
    let (s, z) = (q.scale[0], q.zero_point[0] as f32);
    for (k, v) in real.iter().enumerate() {
        assert!((v - (got[k] as i8 as f32 - z) * s).abs() < 1e-6);
    }
}

#[test]
fn float_ops_graph_matches_tensorflow() {
    let (_, got, want) = run("ops_float");
    assert_close_f32("ops_float", &got, &want);
}

#[test]
fn rejects_garbage_and_truncated_models() {
    assert!(Interpreter::new(vec![0u8; 64]).is_err());
    let model = fixture("mlp_float", "tflite");
    for cut in [8usize, 40, model.len() / 2] {
        assert!(Interpreter::new(model[..cut].to_vec()).is_err(), "truncated at {cut} must fail cleanly");
    }
}

#[test]
fn input_size_is_checked() {
    let mut m = Interpreter::new(fixture("mlp_float", "tflite")).unwrap();
    assert!(m.set_input_f32(0, &[1.0, 2.0]).is_err());
    assert!(m.set_input_f32(0, &[1.0, 2.0, 3.0, 4.0]).is_ok());
}
