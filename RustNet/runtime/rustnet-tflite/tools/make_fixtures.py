"""Regenerate the .tflite test fixtures and their reference outputs.

Each model is converted by TensorFlow's own converter and run through
TensorFlow's own TFLite interpreter; the Rust tests then require
rustnet-tflite to reproduce those outputs. Run with a TensorFlow install:

    python tools/make_fixtures.py        (from runtime/rustnet-tflite)

Writes tests/models/<name>.tflite, <name>.in (input) and <name>.out
(expected output), both raw little-endian in the tensor's own type.
"""
import os
import numpy as np
import tensorflow as tf

OUT = os.path.join(os.path.dirname(__file__), "..", "tests", "models")
tf.random.set_seed(7)
np.random.seed(7)


def save(name, tflite_bytes, x):
    path = os.path.join(OUT, name)
    with open(path + ".tflite", "wb") as f:
        f.write(tflite_bytes)
    interp = tf.lite.Interpreter(model_content=tflite_bytes)
    interp.allocate_tensors()
    inp = interp.get_input_details()[0]
    out = interp.get_output_details()[0]
    if inp["dtype"] == np.int8:
        s, z = inp["quantization"]
        x = np.clip(np.round(x / s + z), -128, 127).astype(np.int8)
    else:
        x = x.astype(inp["dtype"])
    interp.set_tensor(inp["index"], x)
    interp.invoke()
    y = interp.get_tensor(out["index"])
    x.tofile(path + ".in")
    y.tofile(path + ".out")
    ops = sorted({d["op_name"] for d in interp._get_ops_details()})
    print(f"{name}: {len(tflite_bytes)} bytes, in {x.shape} {x.dtype}, out {y.shape} {y.dtype}, ops {ops}")


def convert(model, int8=False, rep=None):
    conv = tf.lite.TFLiteConverter.from_keras_model(model)
    if int8:
        conv.optimizations = [tf.lite.Optimize.DEFAULT]
        conv.representative_dataset = rep
        conv.target_spec.supported_ops = [tf.lite.OpsSet.TFLITE_BUILTINS_INT8]
        conv.inference_input_type = tf.int8
        conv.inference_output_type = tf.int8
    return conv.convert()


# 1. A float multilayer perceptron: FULLY_CONNECTED + RELU + SOFTMAX.
mlp = tf.keras.Sequential([
    tf.keras.Input(shape=(4,)),
    tf.keras.layers.Dense(8, activation="relu"),
    tf.keras.layers.Dense(3, activation="softmax"),
])
save("mlp_float", convert(mlp), np.array([[0.5, -1.2, 3.3, 0.7]], dtype=np.float32))

# 2. A fully int8-quantised CNN: CONV_2D, DEPTHWISE_CONV_2D, MAX/AVERAGE_POOL,
#    RESHAPE, FULLY_CONNECTED, SOFTMAX, per-channel weights.
cnn = tf.keras.Sequential([
    tf.keras.Input(shape=(12, 12, 1)),
    tf.keras.layers.Conv2D(4, 3, padding="same", activation="relu"),
    tf.keras.layers.DepthwiseConv2D(3, padding="valid", activation="relu6"),
    tf.keras.layers.MaxPooling2D(2),
    tf.keras.layers.Conv2D(6, 3, strides=2, padding="same"),
    tf.keras.layers.AveragePooling2D(2, padding="same"),
    tf.keras.layers.Flatten(),
    tf.keras.layers.Dense(5),
    tf.keras.layers.Softmax(),
])
def rep():
    for _ in range(64):
        yield [np.random.rand(1, 12, 12, 1).astype(np.float32)]
img = np.zeros((1, 12, 12, 1), dtype=np.float32)
for i in range(12):
    for j in range(12):
        img[0, i, j, 0] = ((i * 7 + j * 3) % 11) / 10.0
save("cnn_int8", convert(cnn, True, rep), img)

# 3. Float graph exercising the element-wise and shape operators:
#    ADD/SUB/MUL with broadcasting, PAD, MEAN, CONCATENATION, TANH, LOGISTIC,
#    LEAKY_RELU, HARD_SWISH, MAXIMUM.
x_in = tf.keras.Input(shape=(4, 4, 2))
a = tf.keras.layers.Conv2D(3, 1)(x_in)
b = tf.keras.layers.Lambda(lambda t: tf.tanh(t) * 0.5 + tf.sigmoid(t))(a)
c = tf.keras.layers.Lambda(lambda t: tf.nn.leaky_relu(t, alpha=0.1))(a)
d = tf.keras.layers.Concatenate()([b, c])
e = tf.keras.layers.Lambda(lambda t: tf.pad(t, [[0, 0], [1, 1], [1, 1], [0, 0]]))(d)
f = tf.keras.layers.Lambda(lambda t: t * tf.nn.relu6(t + 3.0) / 6.0)(e)
g = tf.keras.layers.Lambda(lambda t: tf.maximum(t, -0.25) - 0.125)(f)
h = tf.keras.layers.Lambda(lambda t: tf.reduce_mean(t, axis=[1, 2]))(g)
ops_model = tf.keras.Model(x_in, h)
xs = np.linspace(-2.0, 2.0, 32, dtype=np.float32).reshape(1, 4, 4, 2)
save("ops_float", convert(ops_model), xs)
