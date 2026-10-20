# Machine learning on the device

Two libraries, for two jobs:

- **`RustNet.ML`** — small estimators written in managed code that *train and
  infer on the device*: learn a sensor's normal behaviour in the field, adapt
  to a user, classify a gesture from a handful of examples.
- **`RustNet.AI.TensorFlow`** — run a model *trained elsewhere* with
  TensorFlow and converted to `.tflite`, in float32 or int8.

## RustNet.ML

| Class | What |
|---|---|
| `LinearRegression` | least squares — exact (`Fit`, normal equations), gradient descent (`FitGradient`) or online (`Update`); optional L2 |
| `LogisticRegression` | binary classifier, batch or online |
| `SoftmaxRegression` | multiclass logistic regression |
| `KNearestNeighbors` | instant "training", majority vote of the k nearest |
| `GaussianNaiveBayes` | one-pass multiclass classifier with probabilities |
| `KMeans` | k-means++ clustering; `DistanceToNearest` flags outliers |
| `Recommender` | matrix-factorisation collaborative filtering (users × items ratings) |
| `NeuralNetwork` | feed-forward network, SGD with momentum; Softmax output for classification, Linear for regression |
| `StandardScaler`, `MinMaxScaler`, `Metrics`, `Rng` | preprocessing, accuracy / MSE / R² / confusion matrix, a seedable generator |

```csharp
using RustNet.ML;

var net = new NeuralNetwork(new[] { 2, 6, 2 }, Activation.Tanh, Activation.Softmax, seed: 3);
net.Fit(x, labels, epochs: 800, learningRate: 0.1, seed: 4);
int cls = net.Predict(sample);

string json = ModelStore.Save(net.ToJson());              // persist with RustNet.IO
var again = (NeuralNetwork)ModelStore.Load(json);
```

Every model serialises to JSON through `RustNet.Serialization` (numbers are
written culture-invariantly, so a model saved on a desktop whose locale writes
`0,5` loads on the device). `Rng` replaces `System.Random` so a seed gives the
same model on the desktop and on the device. Data is `double[][]`; there are
no hidden allocations in prediction beyond the result.

## TensorFlow Lite

```csharp
using RustNet.AI.TensorFlow;
using RustNet.Resources;

var model = new Interpreter(Resource.GetBytes("model.tflite"));   // EmbeddedResource in the .csproj
model.SetInput(0, features);            // float[]; quantised for you if the input is int8
model.Invoke();
int cls = model.ArgMax(0);
float[] scores = model.GetOutput(0);    // dequantised; GetOutputRaw for the int8 bytes
model.Dispose();
```

The interpreter is `runtime/rustnet-tflite`: a dependency-free `no_std + alloc`
Rust crate that reads the `.tflite` FlatBuffer directly. Supported operators:
CONV_2D, DEPTHWISE_CONV_2D, FULLY_CONNECTED, AVERAGE/MAX_POOL_2D,
ADD/SUB/MUL/DIV/MAXIMUM/MINIMUM (with broadcasting), RELU, RELU6,
RELU_N1_TO_1, LOGISTIC, TANH, LEAKY_RELU, HARD_SWISH, FLOOR, SOFTMAX, RESHAPE,
SQUEEZE, EXPAND_DIMS, PAD, MEAN, CONCATENATION, TRANSPOSE, CAST, ARG_MAX,
QUANTIZE, DEQUANTIZE, and the SHAPE → STRIDED_SLICE → PACK chain Keras emits
for `Flatten`. A model that uses anything else is rejected when it is loaded,
with the operator named. Custom operators are not supported.

Quantised models run in float with every tensor re-quantised to its own scale
and zero point at each operator boundary, which reproduces the integer
pipeline's rounding where it matters. Weights are read in place from the model
bytes, never copied.

**Verified against TensorFlow.** `runtime/rustnet-tflite/tools/make_fixtures.py`
converts three Keras models with TensorFlow's converter and records TensorFlow's
own interpreter outputs: a float MLP, a fully int8-quantised CNN with
per-channel weights, and a float graph of thirteen element-wise and shape
operators. `cargo test -p rustnet-tflite` requires float outputs within 1e-4
and int8 outputs within one quantisation step; the E2E suite runs the CNN and
the MLP through the C# API on the virtual device and checks the same.

Built into the host virtual device and the ESP32 firmware (feature `tflite`);
on firmware built without it, `new Interpreter(...)` throws `NotSupported`.
