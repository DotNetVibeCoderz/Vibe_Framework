using RustNet.ML;
using RustNet.Serialization;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// RustNet.ML on the host. The estimators are pure managed code; LibApp
/// trains the same models on the virtual device's interpreter.
/// </summary>
public class MlTests
{
    // Two well-separated blobs per class, generated deterministically.
    private static (double[][] X, int[] Y) Blobs(int perClass, int classes, int seed)
    {
        var rng = new Rng(seed);
        var x = new List<double[]>();
        var y = new List<int>();
        for (int c = 0; c < classes; c++)
        {
            double cx = 4.0 * Math.Cos(2 * Math.PI * c / classes);
            double cy = 4.0 * Math.Sin(2 * Math.PI * c / classes);
            for (int i = 0; i < perClass; i++)
            {
                x.Add(new[] { cx + rng.NextGaussian() * 0.6, cy + rng.NextGaussian() * 0.6 });
                y.Add(c);
            }
        }
        return (x.ToArray(), y.ToArray());
    }

    [Fact]
    public void RngIsDeterministic()
    {
        var a = new Rng(42);
        var b = new Rng(42);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(a.NextDouble(), b.NextDouble());
        }
        double v = new Rng(1).NextDouble();
        Assert.InRange(v, 0.0, 1.0);
        Assert.Equal(10, new Rng(3).Permutation(10).Distinct().Count());
    }

    [Fact]
    public void LinearRegressionRecoversCoefficients()
    {
        var rng = new Rng(7);
        var x = new double[50][];
        var y = new double[50];
        for (int i = 0; i < 50; i++)
        {
            x[i] = new[] { rng.NextDouble() * 10, rng.NextDouble() * 10 };
            y[i] = 3.0 * x[i][0] - 2.0 * x[i][1] + 5.0;
        }
        var exact = new LinearRegression().Fit(x, y);
        Assert.Equal(3.0, exact.Weights[0], 6);
        Assert.Equal(-2.0, exact.Weights[1], 6);
        Assert.Equal(5.0, exact.Bias, 6);
        Assert.Equal(1.0, Metrics.RSquared(exact, x, y), 6);

        var scaler = new StandardScaler().Fit(x);
        var gd = new LinearRegression().FitGradient(scaler.Transform(x), y, 500, 0.1);
        Assert.True(Metrics.MeanSquaredError(gd, scaler.Transform(x), y) < 1e-4);
    }

    [Fact]
    public void LogisticRegressionSeparatesTwoClasses()
    {
        var (x, y) = Blobs(40, 2, 11);
        var m = new LogisticRegression().Fit(x, y, 300, 0.5);
        Assert.True(Metrics.Accuracy(m, x, y) > 0.97);
        var back = LogisticRegression.FromJson(Json.Parse(m.ToJson().ToJson()));
        Assert.Equal(m.PredictProbability(x[0]), back.PredictProbability(x[0]), 9);
    }

    [Fact]
    public void MulticlassModelsAgreeOnBlobs()
    {
        var (x, y) = Blobs(30, 3, 5);
        IClassifier[] models =
        {
            new SoftmaxRegression().Fit(x, y, 300, 0.5),
            new KNearestNeighbors(5).Fit(x, y),
            new GaussianNaiveBayes().Fit(x, y),
            Train(new NeuralNetwork(new[] { 2, 8, 3 }, Activation.Tanh, Activation.Softmax, 1), x, y),
        };
        foreach (var m in models)
        {
            Assert.True(Metrics.Accuracy(m, x, y) > 0.95, m.GetType().Name);
        }
        int[][] cm = Metrics.ConfusionMatrix(models[0], x, y, 3);
        Assert.Equal(90, cm.Sum(r => r.Sum()));
    }

    private static NeuralNetwork Train(NeuralNetwork n, double[][] x, int[] y)
    {
        n.Fit(x, y, 60, 0.05, 2);
        return n;
    }

    [Fact]
    public void NeuralNetworkLearnsXor()
    {
        double[][] x = { new[] { 0.0, 0.0 }, new[] { 0.0, 1.0 }, new[] { 1.0, 0.0 }, new[] { 1.0, 1.0 } };
        int[] y = { 0, 1, 1, 0 };
        var n = new NeuralNetwork(new[] { 2, 6, 2 }, Activation.Tanh, Activation.Softmax, 3);
        n.Fit(x, y, 800, 0.1, 4);
        Assert.Equal(1.0, Metrics.Accuracy(n, x, y));

        var copy = (NeuralNetwork)ModelStore.Load(ModelStore.Save(n.ToJson()));
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(n.Forward(x[i])[1], copy.Forward(x[i])[1], 9);
        }
    }

    [Fact]
    public void NeuralNetworkRegresses()
    {
        var x = new double[40][];
        var t = new double[40][];
        for (int i = 0; i < 40; i++)
        {
            double v = i / 39.0;
            x[i] = new[] { v };
            t[i] = new[] { Math.Sin(v * Math.PI) };
        }
        var n = new NeuralNetwork(new[] { 1, 10, 1 }, Activation.Tanh, Activation.Linear, 9);
        double loss = n.Fit(x, t, 1500, 0.05, 1);
        Assert.True(loss < 0.002, $"loss {loss}");
        Assert.Equal(1.0, n.PredictValue(new[] { 0.5 }), 1);
    }

    [Fact]
    public void KMeansFindsTheBlobs()
    {
        var (x, y) = Blobs(25, 3, 21);
        var km = new KMeans().Fit(x, 3, 50, 4);
        // Every true class maps to exactly one cluster.
        for (int c = 0; c < 3; c++)
        {
            var clusters = Enumerable.Range(0, x.Length).Where(i => y[i] == c).Select(i => km.Predict(x[i])).Distinct();
            Assert.Single(clusters);
        }
        Assert.True(km.Inertia < 75 * 1.0);
        Assert.True(km.DistanceToNearest(new[] { 40.0, 40.0 }) > 30);
        var back = KMeans.FromJson(Json.Parse(km.ToJson().ToJson()));
        Assert.Equal(km.Predict(x[3]), back.Predict(x[3]));
    }

    [Fact]
    public void RecommenderPrefersTheMatchingTaste()
    {
        // Users 0-1 like items 0-2, users 2-3 like items 3-5.
        var users = new List<int>();
        var items = new List<int>();
        var ratings = new List<double>();
        void Rate(int u, int i, double r) { users.Add(u); items.Add(i); ratings.Add(r); }
        for (int u = 0; u < 4; u++)
        {
            for (int i = 0; i < 6; i++)
            {
                bool likes = (u < 2) == (i < 3);
                if (u == 0 && i == 2) continue; // hold out: user 0 should get item 2
                if (u == 3 && i == 5) continue; // hold out: user 3 should get item 5
                Rate(u, i, likes ? 5.0 : 1.0);
            }
        }
        var rec = new Recommender(3).Fit(users.ToArray(), items.ToArray(), ratings.ToArray(), 4, 6, 300, 0.05, 0.01, 8);
        Assert.Equal(2, rec.Recommend(0, 1)[0]);
        Assert.Equal(5, rec.Recommend(3, 1)[0]);
        Assert.True(rec.PredictRating(0, 2) > rec.PredictRating(0, 4));
    }

    [Fact]
    public void JsonUsesInvariantNumbersWhateverTheCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("id-ID");
            var m = new LinearRegression { Weights = new[] { 0.5 }, Bias = 1.25 };
            string json = m.ToJson().ToJson();
            Assert.Contains("0.5", json);
            Assert.Equal(1.25, LinearRegression.FromJson(Json.Parse(json)).Bias);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }
}
