using RustNet.Serialization;

namespace RustNet.ML;

/// <summary>
/// Ordinary least squares, y ≈ w·x + b. <see cref="Fit"/> solves the normal
/// equations exactly (fine up to a few dozen features); <see cref="FitGradient"/>
/// trains by mini-batch-free gradient descent for when memory is tighter than time,
/// and <see cref="Update"/> learns online, one sample at a time.
/// </summary>
public class LinearRegression : IRegressor
{
    public double[] Weights = new double[0];
    public double Bias;

    /// <summary>L2 penalty (ridge). Zero is plain least squares.</summary>
    public double L2;

    public LinearRegression Fit(double[][] x, double[] y)
    {
        int d = x[0].Length;
        int n = d + 1;
        // Augmented [XᵀX + λI | Xᵀy] with a bias column of ones.
        double[][] a = new double[n][];
        for (int i = 0; i < n; i++)
        {
            a[i] = new double[n + 1];
        }
        for (int s = 0; s < x.Length; s++)
        {
            for (int i = 0; i < n; i++)
            {
                double xi = i < d ? x[s][i] : 1.0;
                for (int j = 0; j < n; j++)
                {
                    double xj = j < d ? x[s][j] : 1.0;
                    a[i][j] += xi * xj;
                }
                a[i][n] += xi * y[s];
            }
        }
        for (int i = 0; i < d; i++)
        {
            a[i][i] += L2;
        }
        double[] sol = Solver.Solve(a);
        Weights = new double[d];
        for (int i = 0; i < d; i++)
        {
            Weights[i] = sol[i];
        }
        Bias = sol[d];
        return this;
    }

    public LinearRegression FitGradient(double[][] x, double[] y, int epochs, double learningRate)
    {
        int d = x[0].Length;
        if (Weights.Length != d)
        {
            Weights = new double[d];
        }
        double[] grad = new double[d];
        for (int e = 0; e < epochs; e++)
        {
            for (int j = 0; j < d; j++)
            {
                grad[j] = 0.0;
            }
            double gb = 0.0;
            for (int s = 0; s < x.Length; s++)
            {
                double err = Predict(x[s]) - y[s];
                for (int j = 0; j < d; j++)
                {
                    grad[j] += err * x[s][j];
                }
                gb += err;
            }
            for (int j = 0; j < d; j++)
            {
                Weights[j] -= learningRate * (grad[j] / x.Length + L2 * Weights[j]);
            }
            Bias -= learningRate * gb / x.Length;
        }
        return this;
    }

    /// <summary>One stochastic gradient step on a single sample (online learning).</summary>
    public void Update(double[] features, double target, double learningRate)
    {
        if (Weights.Length != features.Length)
        {
            Weights = new double[features.Length];
        }
        double err = Predict(features) - target;
        for (int j = 0; j < features.Length; j++)
        {
            Weights[j] -= learningRate * (err * features[j] + L2 * Weights[j]);
        }
        Bias -= learningRate * err;
    }

    public double Predict(double[] features) => Vec.Dot(Weights, features) + Bias;

    public JsonValue ToJson()
    {
        JsonValue o = JsonValue.NewObject();
        o.Set("type", "LinearRegression");
        o.Set("weights", ModelJson.FromVector(Weights));
        o.Set("bias", Bias);
        o.Set("l2", L2);
        return o;
    }

    public static LinearRegression FromJson(JsonValue o)
    {
        LinearRegression m = new LinearRegression();
        m.Weights = ModelJson.ToVector(o.Get("weights"));
        m.Bias = o.Get("bias").AsDouble;
        m.L2 = o.Get("l2").AsDouble;
        return m;
    }
}

/// <summary>Binary logistic regression trained by gradient descent; labels are 0 or 1.</summary>
public class LogisticRegression : IClassifier
{
    public double[] Weights = new double[0];
    public double Bias;
    public double L2;

    /// <summary>Probability above which <see cref="Predict"/> answers 1.</summary>
    public double Threshold = 0.5;

    public LogisticRegression Fit(double[][] x, int[] y, int epochs, double learningRate)
    {
        int d = x[0].Length;
        Weights = new double[d];
        Bias = 0.0;
        double[] grad = new double[d];
        for (int e = 0; e < epochs; e++)
        {
            for (int j = 0; j < d; j++)
            {
                grad[j] = 0.0;
            }
            double gb = 0.0;
            for (int s = 0; s < x.Length; s++)
            {
                double err = PredictProbability(x[s]) - y[s];
                for (int j = 0; j < d; j++)
                {
                    grad[j] += err * x[s][j];
                }
                gb += err;
            }
            for (int j = 0; j < d; j++)
            {
                Weights[j] -= learningRate * (grad[j] / x.Length + L2 * Weights[j]);
            }
            Bias -= learningRate * gb / x.Length;
        }
        return this;
    }

    /// <summary>One stochastic gradient step on a single labelled sample.</summary>
    public void Update(double[] features, int label, double learningRate)
    {
        if (Weights.Length != features.Length)
        {
            Weights = new double[features.Length];
        }
        double err = PredictProbability(features) - label;
        for (int j = 0; j < features.Length; j++)
        {
            Weights[j] -= learningRate * (err * features[j] + L2 * Weights[j]);
        }
        Bias -= learningRate * err;
    }

    public double PredictProbability(double[] features) => Vec.Sigmoid(Vec.Dot(Weights, features) + Bias);

    public int Predict(double[] features) => PredictProbability(features) >= Threshold ? 1 : 0;

    public JsonValue ToJson()
    {
        JsonValue o = JsonValue.NewObject();
        o.Set("type", "LogisticRegression");
        o.Set("weights", ModelJson.FromVector(Weights));
        o.Set("bias", Bias);
        o.Set("l2", L2);
        o.Set("threshold", Threshold);
        return o;
    }

    public static LogisticRegression FromJson(JsonValue o)
    {
        LogisticRegression m = new LogisticRegression();
        m.Weights = ModelJson.ToVector(o.Get("weights"));
        m.Bias = o.Get("bias").AsDouble;
        m.L2 = o.Get("l2").AsDouble;
        m.Threshold = o.Get("threshold").AsDouble;
        return m;
    }
}

/// <summary>Multiclass (multinomial) logistic regression; labels are 0..k-1.</summary>
public class SoftmaxRegression : IClassifier
{
    /// <summary>Weights[class][feature].</summary>
    public double[][] Weights = new double[0][];
    public double[] Bias = new double[0];
    public double L2;

    public SoftmaxRegression Fit(double[][] x, int[] y, int epochs, double learningRate)
    {
        int d = x[0].Length;
        int k = Vec.ClassCount(y);
        Weights = new double[k][];
        double[][] grad = new double[k][];
        for (int c = 0; c < k; c++)
        {
            Weights[c] = new double[d];
            grad[c] = new double[d];
        }
        Bias = new double[k];
        double[] gb = new double[k];
        for (int e = 0; e < epochs; e++)
        {
            for (int c = 0; c < k; c++)
            {
                gb[c] = 0.0;
                for (int j = 0; j < d; j++)
                {
                    grad[c][j] = 0.0;
                }
            }
            for (int s = 0; s < x.Length; s++)
            {
                double[] p = PredictProbabilities(x[s]);
                for (int c = 0; c < k; c++)
                {
                    double err = p[c] - (y[s] == c ? 1.0 : 0.0);
                    for (int j = 0; j < d; j++)
                    {
                        grad[c][j] += err * x[s][j];
                    }
                    gb[c] += err;
                }
            }
            for (int c = 0; c < k; c++)
            {
                for (int j = 0; j < d; j++)
                {
                    Weights[c][j] -= learningRate * (grad[c][j] / x.Length + L2 * Weights[c][j]);
                }
                Bias[c] -= learningRate * gb[c] / x.Length;
            }
        }
        return this;
    }

    public double[] PredictProbabilities(double[] features)
    {
        double[] z = new double[Weights.Length];
        for (int c = 0; c < z.Length; c++)
        {
            z[c] = Vec.Dot(Weights[c], features) + Bias[c];
        }
        Vec.SoftmaxInPlace(z);
        return z;
    }

    public int Predict(double[] features) => Vec.ArgMax(PredictProbabilities(features));

    public JsonValue ToJson()
    {
        JsonValue o = JsonValue.NewObject();
        o.Set("type", "SoftmaxRegression");
        o.Set("weights", ModelJson.FromMatrix(Weights));
        o.Set("bias", ModelJson.FromVector(Bias));
        o.Set("l2", L2);
        return o;
    }

    public static SoftmaxRegression FromJson(JsonValue o)
    {
        SoftmaxRegression m = new SoftmaxRegression();
        m.Weights = ModelJson.ToMatrix(o.Get("weights"));
        m.Bias = ModelJson.ToVector(o.Get("bias"));
        m.L2 = o.Get("l2").AsDouble;
        return m;
    }
}

/// <summary>Dense linear-system solver used by the closed-form fits.</summary>
public static class Solver
{
    /// <summary>
    /// Solve the augmented system a (n rows × n+1 columns) by Gaussian
    /// elimination with partial pivoting. The matrix is overwritten.
    /// A singular column yields a zero coefficient rather than an exception.
    /// </summary>
    public static double[] Solve(double[][] a)
    {
        int n = a.Length;
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(a[r][col]) > Math.Abs(a[pivot][col]))
                {
                    pivot = r;
                }
            }
            double[] tmp = a[col];
            a[col] = a[pivot];
            a[pivot] = tmp;
            double p = a[col][col];
            if (Math.Abs(p) < 1e-12)
            {
                continue;
            }
            for (int r = 0; r < n; r++)
            {
                if (r == col)
                {
                    continue;
                }
                double factor = a[r][col] / p;
                if (factor == 0.0)
                {
                    continue;
                }
                for (int c = col; c <= n; c++)
                {
                    a[r][c] -= factor * a[col][c];
                }
            }
        }
        double[] x = new double[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = Math.Abs(a[i][i]) < 1e-12 ? 0.0 : a[i][n] / a[i][i];
        }
        return x;
    }
}
