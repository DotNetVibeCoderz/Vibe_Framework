using RustNet.Serialization;

namespace RustNet.ML;

/// <summary>Activation functions for <see cref="NeuralNetwork"/> layers.</summary>
public static class Activation
{
    public const int Linear = 0;
    public const int Sigmoid = 1;
    public const int Tanh = 2;
    public const int Relu = 3;

    /// <summary>Output layer only: turns the outputs into class probabilities (trained with cross-entropy).</summary>
    public const int Softmax = 4;

    public static double Apply(int kind, double z)
    {
        if (kind == Sigmoid)
        {
            return Vec.Sigmoid(z);
        }
        if (kind == Tanh)
        {
            return Math.Tanh(z);
        }
        if (kind == Relu)
        {
            return z > 0 ? z : 0.0;
        }
        return z;
    }

    /// <summary>Derivative expressed through the activation's output a = f(z).</summary>
    public static double DerivativeFromOutput(int kind, double a)
    {
        if (kind == Sigmoid)
        {
            return a * (1.0 - a);
        }
        if (kind == Tanh)
        {
            return 1.0 - a * a;
        }
        if (kind == Relu)
        {
            return a > 0 ? 1.0 : 0.0;
        }
        return 1.0;
    }
}

/// <summary>
/// A small fully-connected feed-forward network trained by stochastic
/// gradient descent with momentum. Sized for microcontrollers: a few layers of
/// tens of neurons. With a Softmax output it is a classifier (labels 0..k-1);
/// with a Linear output it is a regressor (squared-error loss).
/// </summary>
public class NeuralNetwork : IClassifier
{
    /// <summary>Neurons per layer, input layer first.</summary>
    public int[] Sizes = new int[0];

    /// <summary>Activation of each non-input layer.</summary>
    public int[] Activations = new int[0];

    /// <summary>Weights[layer][neuron][input].</summary>
    public double[][][] Weights = new double[0][][];

    /// <summary>Biases[layer][neuron].</summary>
    public double[][] Biases = new double[0][];

    public double Momentum = 0.9;
    public double L2;

    private double[][][] _velW = new double[0][][];
    private double[][] _velB = new double[0][];
    private double[][] _outputs = new double[0][];
    private double[][] _deltas = new double[0][];

    public NeuralNetwork()
    {
    }

    /// <summary>
    /// <paramref name="sizes"/> = neurons per layer (input first);
    /// <paramref name="hidden"/> = activation of the hidden layers;
    /// <paramref name="output"/> = activation of the output layer.
    /// Weights start He/Xavier-scaled from <paramref name="seed"/>.
    /// </summary>
    public NeuralNetwork(int[] sizes, int hidden, int output, int seed)
    {
        Sizes = sizes;
        int layers = sizes.Length - 1;
        Activations = new int[layers];
        for (int l = 0; l < layers; l++)
        {
            Activations[l] = l == layers - 1 ? output : hidden;
        }
        Rng rng = new Rng(seed);
        Weights = new double[layers][][];
        Biases = new double[layers][];
        for (int l = 0; l < layers; l++)
        {
            int fanIn = sizes[l];
            double scale = Activations[l] == Activation.Relu ? Math.Sqrt(2.0 / fanIn) : Math.Sqrt(1.0 / fanIn);
            Weights[l] = new double[sizes[l + 1]][];
            Biases[l] = new double[sizes[l + 1]];
            for (int n = 0; n < sizes[l + 1]; n++)
            {
                Weights[l][n] = new double[fanIn];
                for (int i = 0; i < fanIn; i++)
                {
                    Weights[l][n][i] = rng.NextGaussian() * scale;
                }
            }
        }
        Prepare();
    }

    private void Prepare()
    {
        int layers = Weights.Length;
        _velW = new double[layers][][];
        _velB = new double[layers][];
        _outputs = new double[layers + 1][];
        _deltas = new double[layers][];
        for (int l = 0; l < layers; l++)
        {
            _velW[l] = new double[Weights[l].Length][];
            for (int n = 0; n < Weights[l].Length; n++)
            {
                _velW[l][n] = new double[Weights[l][n].Length];
            }
            _velB[l] = new double[Biases[l].Length];
            _outputs[l + 1] = new double[Biases[l].Length];
            _deltas[l] = new double[Biases[l].Length];
        }
    }

    /// <summary>Run the network; the returned array is reused by the next call — copy it to keep it.</summary>
    public double[] Forward(double[] input)
    {
        _outputs[0] = input;
        for (int l = 0; l < Weights.Length; l++)
        {
            double[] prev = _outputs[l];
            double[] outp = _outputs[l + 1];
            int act = Activations[l];
            for (int n = 0; n < outp.Length; n++)
            {
                double z = Biases[l][n] + Vec.Dot(Weights[l][n], prev);
                outp[n] = act == Activation.Softmax ? z : Activation.Apply(act, z);
            }
            if (act == Activation.Softmax)
            {
                Vec.SoftmaxInPlace(outp);
            }
        }
        return _outputs[Weights.Length];
    }

    public int Predict(double[] features) => Vec.ArgMax(Forward(features));

    /// <summary>Regression output (first output neuron).</summary>
    public double PredictValue(double[] features) => Forward(features)[0];

    /// <summary>One SGD step towards <paramref name="target"/>. Returns the sample's loss before the step.</summary>
    public double TrainSample(double[] input, double[] target, double learningRate)
    {
        double[] y = Forward(input);
        int last = Weights.Length - 1;
        double loss = 0.0;
        // Output delta: softmax+cross-entropy and linear+MSE both reduce to (y - t).
        for (int n = 0; n < y.Length; n++)
        {
            double diff = y[n] - target[n];
            int act = Activations[last];
            if (act == Activation.Softmax)
            {
                _deltas[last][n] = diff;
                if (target[n] > 0)
                {
                    loss -= target[n] * Math.Log(y[n] < 1e-15 ? 1e-15 : y[n]);
                }
            }
            else
            {
                _deltas[last][n] = diff * Activation.DerivativeFromOutput(act, y[n]);
                loss += 0.5 * diff * diff;
            }
        }
        for (int l = last - 1; l >= 0; l--)
        {
            double[] outp = _outputs[l + 1];
            double[][] nextW = Weights[l + 1];
            double[] nextD = _deltas[l + 1];
            for (int n = 0; n < outp.Length; n++)
            {
                double s = 0.0;
                for (int m = 0; m < nextD.Length; m++)
                {
                    s += nextW[m][n] * nextD[m];
                }
                _deltas[l][n] = s * Activation.DerivativeFromOutput(Activations[l], outp[n]);
            }
        }
        for (int l = 0; l <= last; l++)
        {
            double[] prev = _outputs[l];
            for (int n = 0; n < Weights[l].Length; n++)
            {
                double d = _deltas[l][n];
                double[] w = Weights[l][n];
                double[] v = _velW[l][n];
                for (int i = 0; i < w.Length; i++)
                {
                    v[i] = Momentum * v[i] - learningRate * (d * prev[i] + L2 * w[i]);
                    w[i] += v[i];
                }
                _velB[l][n] = Momentum * _velB[l][n] - learningRate * d;
                Biases[l][n] += _velB[l][n];
            }
        }
        return loss;
    }

    /// <summary>Train a classifier for <paramref name="epochs"/> shuffled passes. Returns the last epoch's mean loss.</summary>
    public double Fit(double[][] x, int[] y, int epochs, double learningRate, int seed)
    {
        int k = Sizes[Sizes.Length - 1];
        double[][] targets = new double[x.Length][];
        for (int s = 0; s < x.Length; s++)
        {
            targets[s] = new double[k];
            targets[s][y[s]] = 1.0;
        }
        return Fit(x, targets, epochs, learningRate, seed);
    }

    /// <summary>Train against explicit target vectors (regression or soft labels).</summary>
    public double Fit(double[][] x, double[][] targets, int epochs, double learningRate, int seed)
    {
        Rng rng = new Rng(seed);
        double mean = 0.0;
        for (int e = 0; e < epochs; e++)
        {
            int[] order = rng.Permutation(x.Length);
            double total = 0.0;
            for (int o = 0; o < order.Length; o++)
            {
                total += TrainSample(x[order[o]], targets[order[o]], learningRate);
            }
            mean = total / x.Length;
        }
        return mean;
    }

    public JsonValue ToJson()
    {
        JsonValue o = JsonValue.NewObject();
        o.Set("type", "NeuralNetwork");
        o.Set("sizes", ModelJson.FromInts(Sizes));
        o.Set("activations", ModelJson.FromInts(Activations));
        JsonValue w = JsonValue.NewArray();
        for (int l = 0; l < Weights.Length; l++)
        {
            w.Add(ModelJson.FromMatrix(Weights[l]));
        }
        o.Set("weights", w);
        o.Set("biases", ModelJson.FromMatrix(Biases));
        o.Set("momentum", Momentum);
        o.Set("l2", L2);
        return o;
    }

    public static NeuralNetwork FromJson(JsonValue o)
    {
        NeuralNetwork m = new NeuralNetwork();
        m.Sizes = ModelJson.ToInts(o.Get("sizes"));
        m.Activations = ModelJson.ToInts(o.Get("activations"));
        JsonValue w = o.Get("weights");
        m.Weights = new double[w.Count][][];
        for (int l = 0; l < w.Count; l++)
        {
            m.Weights[l] = ModelJson.ToMatrix(w.At(l));
        }
        m.Biases = ModelJson.ToMatrix(o.Get("biases"));
        m.Momentum = o.Get("momentum").AsDouble;
        m.L2 = o.Get("l2").AsDouble;
        m.Prepare();
        return m;
    }
}

/// <summary>Loads any model saved by a ToJson() in this library, by its "type" field.</summary>
public static class ModelStore
{
    public static string Save(JsonValue model) => model.ToJson();

    /// <summary>Returns the model object (cast to the concrete type you saved).</summary>
    public static object Load(string json)
    {
        JsonValue o = Json.Parse(json);
        string type = o.Get("type").AsString;
        if (type == "LinearRegression")
        {
            return LinearRegression.FromJson(o);
        }
        if (type == "LogisticRegression")
        {
            return LogisticRegression.FromJson(o);
        }
        if (type == "SoftmaxRegression")
        {
            return SoftmaxRegression.FromJson(o);
        }
        if (type == "KNearestNeighbors")
        {
            return KNearestNeighbors.FromJson(o);
        }
        if (type == "GaussianNaiveBayes")
        {
            return GaussianNaiveBayes.FromJson(o);
        }
        if (type == "KMeans")
        {
            return KMeans.FromJson(o);
        }
        if (type == "Recommender")
        {
            return Recommender.FromJson(o);
        }
        if (type == "NeuralNetwork")
        {
            return NeuralNetwork.FromJson(o);
        }
        if (type == "StandardScaler")
        {
            return StandardScaler.FromJson(o);
        }
        if (type == "MinMaxScaler")
        {
            return MinMaxScaler.FromJson(o);
        }
        throw new InvalidOperationException("unknown model type: " + type);
    }
}
