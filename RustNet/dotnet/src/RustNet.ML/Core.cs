using RustNet.Serialization;

namespace RustNet.ML;

/// <summary>Anything that maps a feature vector to a class label.</summary>
public interface IClassifier
{
    int Predict(double[] features);
}

/// <summary>Anything that maps a feature vector to a number.</summary>
public interface IRegressor
{
    double Predict(double[] features);
}

/// <summary>
/// A small, seedable xorshift generator. <see cref="System.Random"/> is not
/// used because its sequence differs between the desktop CLR and the device,
/// and a model trained with a seed should come out the same on both.
/// </summary>
public class Rng
{
    private ulong _state;

    public Rng(int seed)
    {
        _state = (ulong)(seed == 0 ? 0x2545F491 : seed) * 0x9E3779B97F4A7C15UL;
        if (_state == 0)
        {
            _state = 0x9E3779B97F4A7C15UL;
        }
    }

    private ulong NextBits()
    {
        ulong x = _state;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        _state = x;
        return x;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (double)(NextBits() >> 11) / 9007199254740992.0;

    /// <summary>Uniform integer in [0, max).</summary>
    public int Next(int max) => max <= 0 ? 0 : (int)((NextBits() >> 33) % (ulong)max);

    /// <summary>Normally distributed (Box–Muller), mean 0 and standard deviation 1.</summary>
    public double NextGaussian()
    {
        double u1 = NextDouble();
        if (u1 < 1e-12)
        {
            u1 = 1e-12;
        }
        double u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>A random permutation of 0..n-1 (Fisher–Yates).</summary>
    public int[] Permutation(int n)
    {
        int[] p = new int[n];
        for (int i = 0; i < n; i++)
        {
            p[i] = i;
        }
        for (int i = n - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            int t = p[i];
            p[i] = p[j];
            p[j] = t;
        }
        return p;
    }
}

/// <summary>Vector helpers shared by the estimators.</summary>
public static class Vec
{
    public static double Dot(double[] a, double[] b)
    {
        double s = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }
        return s;
    }

    public static double SquaredDistance(double[] a, double[] b)
    {
        double s = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            s += d * d;
        }
        return s;
    }

    public static double[] Copy(double[] a)
    {
        double[] r = new double[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            r[i] = a[i];
        }
        return r;
    }

    public static int ArgMax(double[] a)
    {
        int best = 0;
        for (int i = 1; i < a.Length; i++)
        {
            if (a[i] > a[best])
            {
                best = i;
            }
        }
        return best;
    }

    public static double Sigmoid(double z)
    {
        if (z >= 0)
        {
            return 1.0 / (1.0 + Math.Exp(-z));
        }
        double e = Math.Exp(z);
        return e / (1.0 + e);
    }

    /// <summary>Numerically stable softmax, in place.</summary>
    public static void SoftmaxInPlace(double[] z)
    {
        double max = z[0];
        for (int i = 1; i < z.Length; i++)
        {
            if (z[i] > max)
            {
                max = z[i];
            }
        }
        double sum = 0.0;
        for (int i = 0; i < z.Length; i++)
        {
            z[i] = Math.Exp(z[i] - max);
            sum += z[i];
        }
        for (int i = 0; i < z.Length; i++)
        {
            z[i] = z[i] / sum;
        }
    }

    /// <summary>Number of distinct classes, assuming labels 0..k-1.</summary>
    public static int ClassCount(int[] labels)
    {
        int max = -1;
        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] > max)
            {
                max = labels[i];
            }
        }
        return max + 1;
    }
}

/// <summary>Accuracy and error metrics.</summary>
public static class Metrics
{
    public static double Accuracy(IClassifier model, double[][] x, int[] y)
    {
        int correct = 0;
        for (int i = 0; i < x.Length; i++)
        {
            if (model.Predict(x[i]) == y[i])
            {
                correct++;
            }
        }
        return x.Length == 0 ? 0.0 : (double)correct / x.Length;
    }

    public static double MeanSquaredError(IRegressor model, double[][] x, double[] y)
    {
        double s = 0.0;
        for (int i = 0; i < x.Length; i++)
        {
            double d = model.Predict(x[i]) - y[i];
            s += d * d;
        }
        return x.Length == 0 ? 0.0 : s / x.Length;
    }

    /// <summary>Coefficient of determination; 1 is a perfect fit.</summary>
    public static double RSquared(IRegressor model, double[][] x, double[] y)
    {
        double mean = 0.0;
        for (int i = 0; i < y.Length; i++)
        {
            mean += y[i];
        }
        mean /= y.Length;
        double ssRes = 0.0;
        double ssTot = 0.0;
        for (int i = 0; i < x.Length; i++)
        {
            double d = model.Predict(x[i]) - y[i];
            ssRes += d * d;
            double t = y[i] - mean;
            ssTot += t * t;
        }
        return ssTot == 0.0 ? 1.0 : 1.0 - ssRes / ssTot;
    }

    /// <summary>counts[actual][predicted].</summary>
    public static int[][] ConfusionMatrix(IClassifier model, double[][] x, int[] y, int classes)
    {
        int[][] m = new int[classes][];
        for (int i = 0; i < classes; i++)
        {
            m[i] = new int[classes];
        }
        for (int i = 0; i < x.Length; i++)
        {
            int p = model.Predict(x[i]);
            if (y[i] >= 0 && y[i] < classes && p >= 0 && p < classes)
            {
                m[y[i]][p]++;
            }
        }
        return m;
    }
}

/// <summary>
/// Standardises features to zero mean and unit variance. Fit on the training
/// set, then transform every vector the model sees — including at inference
/// time, which is why it serialises alongside the model.
/// </summary>
public class StandardScaler
{
    public double[] Mean = new double[0];
    public double[] Scale = new double[0];

    public StandardScaler Fit(double[][] x)
    {
        int d = x[0].Length;
        Mean = new double[d];
        Scale = new double[d];
        for (int i = 0; i < x.Length; i++)
        {
            for (int j = 0; j < d; j++)
            {
                Mean[j] += x[i][j];
            }
        }
        for (int j = 0; j < d; j++)
        {
            Mean[j] /= x.Length;
        }
        for (int i = 0; i < x.Length; i++)
        {
            for (int j = 0; j < d; j++)
            {
                double t = x[i][j] - Mean[j];
                Scale[j] += t * t;
            }
        }
        for (int j = 0; j < d; j++)
        {
            double sd = Math.Sqrt(Scale[j] / x.Length);
            Scale[j] = sd < 1e-12 ? 1.0 : sd;
        }
        return this;
    }

    public double[] Transform(double[] v)
    {
        double[] r = new double[v.Length];
        for (int j = 0; j < v.Length; j++)
        {
            r[j] = (v[j] - Mean[j]) / Scale[j];
        }
        return r;
    }

    public double[][] Transform(double[][] x)
    {
        double[][] r = new double[x.Length][];
        for (int i = 0; i < x.Length; i++)
        {
            r[i] = Transform(x[i]);
        }
        return r;
    }

    public JsonValue ToJson()
    {
        JsonValue o = JsonValue.NewObject();
        o.Set("type", "StandardScaler");
        o.Set("mean", ModelJson.FromVector(Mean));
        o.Set("scale", ModelJson.FromVector(Scale));
        return o;
    }

    public static StandardScaler FromJson(JsonValue o)
    {
        StandardScaler s = new StandardScaler();
        s.Mean = ModelJson.ToVector(o.Get("mean"));
        s.Scale = ModelJson.ToVector(o.Get("scale"));
        return s;
    }
}

/// <summary>Rescales each feature into [0, 1] by the training minimum and maximum.</summary>
public class MinMaxScaler
{
    public double[] Min = new double[0];
    public double[] Range = new double[0];

    public MinMaxScaler Fit(double[][] x)
    {
        int d = x[0].Length;
        Min = Vec.Copy(x[0]);
        double[] max = Vec.Copy(x[0]);
        for (int i = 1; i < x.Length; i++)
        {
            for (int j = 0; j < d; j++)
            {
                if (x[i][j] < Min[j])
                {
                    Min[j] = x[i][j];
                }
                if (x[i][j] > max[j])
                {
                    max[j] = x[i][j];
                }
            }
        }
        Range = new double[d];
        for (int j = 0; j < d; j++)
        {
            double r = max[j] - Min[j];
            Range[j] = r < 1e-12 ? 1.0 : r;
        }
        return this;
    }

    public double[] Transform(double[] v)
    {
        double[] r = new double[v.Length];
        for (int j = 0; j < v.Length; j++)
        {
            r[j] = (v[j] - Min[j]) / Range[j];
        }
        return r;
    }

    public double[][] Transform(double[][] x)
    {
        double[][] r = new double[x.Length][];
        for (int i = 0; i < x.Length; i++)
        {
            r[i] = Transform(x[i]);
        }
        return r;
    }

    public JsonValue ToJson()
    {
        JsonValue o = JsonValue.NewObject();
        o.Set("type", "MinMaxScaler");
        o.Set("min", ModelJson.FromVector(Min));
        o.Set("range", ModelJson.FromVector(Range));
        return o;
    }

    public static MinMaxScaler FromJson(JsonValue o)
    {
        MinMaxScaler s = new MinMaxScaler();
        s.Min = ModelJson.ToVector(o.Get("min"));
        s.Range = ModelJson.ToVector(o.Get("range"));
        return s;
    }
}

/// <summary>JSON encoding of vectors and matrices, shared by every model's ToJson/FromJson.</summary>
public static class ModelJson
{
    public static JsonValue FromVector(double[] v)
    {
        JsonValue a = JsonValue.NewArray();
        for (int i = 0; i < v.Length; i++)
        {
            a.Add(JsonValue.FromNumber(v[i]));
        }
        return a;
    }

    public static double[] ToVector(JsonValue a)
    {
        double[] v = new double[a.Count];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = a.At(i).AsDouble;
        }
        return v;
    }

    public static JsonValue FromMatrix(double[][] m)
    {
        JsonValue a = JsonValue.NewArray();
        for (int i = 0; i < m.Length; i++)
        {
            a.Add(FromVector(m[i]));
        }
        return a;
    }

    public static double[][] ToMatrix(JsonValue a)
    {
        double[][] m = new double[a.Count][];
        for (int i = 0; i < m.Length; i++)
        {
            m[i] = ToVector(a.At(i));
        }
        return m;
    }

    public static JsonValue FromInts(int[] v)
    {
        JsonValue a = JsonValue.NewArray();
        for (int i = 0; i < v.Length; i++)
        {
            a.Add(JsonValue.FromNumber(v[i]));
        }
        return a;
    }

    public static int[] ToInts(JsonValue a)
    {
        int[] v = new int[a.Count];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = a.At(i).AsInt;
        }
        return v;
    }
}
