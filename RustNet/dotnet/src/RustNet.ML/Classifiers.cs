using RustNet.Serialization;

namespace RustNet.ML;

/// <summary>
/// k-nearest-neighbours classifier. Training just keeps the samples, so it
/// suits small reference sets (gesture templates, a few dozen calibration
/// readings) where retraining must be instant.
/// </summary>
public class KNearestNeighbors : IClassifier
{
    public int K = 3;
    public double[][] Samples = new double[0][];
    public int[] Labels = new int[0];

    public KNearestNeighbors()
    {
    }

    public KNearestNeighbors(int k)
    {
        K = k;
    }

    public KNearestNeighbors Fit(double[][] x, int[] y)
    {
        Samples = x;
        Labels = y;
        return this;
    }

    public int Predict(double[] features)
    {
        int k = K < Samples.Length ? K : Samples.Length;
        // Keep the k best (distance, label) pairs by insertion — k is small.
        double[] bestD = new double[k];
        int[] bestL = new int[k];
        int filled = 0;
        for (int i = 0; i < Samples.Length; i++)
        {
            double d = Vec.SquaredDistance(Samples[i], features);
            if (filled < k)
            {
                int p = filled;
                while (p > 0 && bestD[p - 1] > d)
                {
                    bestD[p] = bestD[p - 1];
                    bestL[p] = bestL[p - 1];
                    p--;
                }
                bestD[p] = d;
                bestL[p] = Labels[i];
                filled++;
            }
            else if (d < bestD[k - 1])
            {
                int p = k - 1;
                while (p > 0 && bestD[p - 1] > d)
                {
                    bestD[p] = bestD[p - 1];
                    bestL[p] = bestL[p - 1];
                    p--;
                }
                bestD[p] = d;
                bestL[p] = Labels[i];
            }
        }
        // Majority vote; ties go to the class of the nearer neighbour.
        int classes = Vec.ClassCount(Labels);
        int[] votes = new int[classes];
        for (int i = 0; i < filled; i++)
        {
            votes[bestL[i]]++;
        }
        int best = bestL[0];
        for (int c = 0; c < classes; c++)
        {
            if (votes[c] > votes[best])
            {
                best = c;
            }
        }
        return best;
    }

    public JsonValue ToJson()
    {
        JsonValue o = JsonValue.NewObject();
        o.Set("type", "KNearestNeighbors");
        o.Set("k", K);
        o.Set("samples", ModelJson.FromMatrix(Samples));
        o.Set("labels", ModelJson.FromInts(Labels));
        return o;
    }

    public static KNearestNeighbors FromJson(JsonValue o)
    {
        KNearestNeighbors m = new KNearestNeighbors(o.Get("k").AsInt);
        m.Samples = ModelJson.ToMatrix(o.Get("samples"));
        m.Labels = ModelJson.ToInts(o.Get("labels"));
        return m;
    }
}

/// <summary>
/// Gaussian naive Bayes: one mean and variance per feature per class. Trains
/// in a single pass and predicts in O(classes × features) — the cheapest
/// probabilistic classifier there is.
/// </summary>
public class GaussianNaiveBayes : IClassifier
{
    public double[][] Means = new double[0][];
    public double[][] Variances = new double[0][];
    public double[] LogPriors = new double[0];

    public GaussianNaiveBayes Fit(double[][] x, int[] y)
    {
        int d = x[0].Length;
        int k = Vec.ClassCount(y);
        Means = new double[k][];
        Variances = new double[k][];
        LogPriors = new double[k];
        int[] counts = new int[k];
        for (int c = 0; c < k; c++)
        {
            Means[c] = new double[d];
            Variances[c] = new double[d];
        }
        for (int s = 0; s < x.Length; s++)
        {
            counts[y[s]]++;
            for (int j = 0; j < d; j++)
            {
                Means[y[s]][j] += x[s][j];
            }
        }
        for (int c = 0; c < k; c++)
        {
            for (int j = 0; j < d; j++)
            {
                Means[c][j] = counts[c] == 0 ? 0.0 : Means[c][j] / counts[c];
            }
        }
        for (int s = 0; s < x.Length; s++)
        {
            for (int j = 0; j < d; j++)
            {
                double t = x[s][j] - Means[y[s]][j];
                Variances[y[s]][j] += t * t;
            }
        }
        for (int c = 0; c < k; c++)
        {
            for (int j = 0; j < d; j++)
            {
                // A small floor keeps a constant feature from dividing by zero.
                Variances[c][j] = (counts[c] == 0 ? 0.0 : Variances[c][j] / counts[c]) + 1e-9;
            }
            LogPriors[c] = Math.Log((counts[c] + 1.0) / (x.Length + k));
        }
        return this;
    }

    /// <summary>Unnormalised log-posterior for each class.</summary>
    public double[] LogLikelihoods(double[] features)
    {
        double[] r = new double[Means.Length];
        for (int c = 0; c < r.Length; c++)
        {
            double s = LogPriors[c];
            for (int j = 0; j < features.Length; j++)
            {
                double v = Variances[c][j];
                double t = features[j] - Means[c][j];
                s += -0.5 * Math.Log(2.0 * Math.PI * v) - t * t / (2.0 * v);
            }
            r[c] = s;
        }
        return r;
    }

    public double[] PredictProbabilities(double[] features)
    {
        double[] r = LogLikelihoods(features);
        Vec.SoftmaxInPlace(r);
        return r;
    }

    public int Predict(double[] features) => Vec.ArgMax(LogLikelihoods(features));

    public JsonValue ToJson()
    {
        JsonValue o = JsonValue.NewObject();
        o.Set("type", "GaussianNaiveBayes");
        o.Set("means", ModelJson.FromMatrix(Means));
        o.Set("variances", ModelJson.FromMatrix(Variances));
        o.Set("logPriors", ModelJson.FromVector(LogPriors));
        return o;
    }

    public static GaussianNaiveBayes FromJson(JsonValue o)
    {
        GaussianNaiveBayes m = new GaussianNaiveBayes();
        m.Means = ModelJson.ToMatrix(o.Get("means"));
        m.Variances = ModelJson.ToMatrix(o.Get("variances"));
        m.LogPriors = ModelJson.ToVector(o.Get("logPriors"));
        return m;
    }
}

/// <summary>
/// k-means clustering with k-means++ seeding. <see cref="Predict"/> returns
/// the index of the nearest centroid, so a fitted model doubles as a
/// classifier for anomaly or regime detection.
/// </summary>
public class KMeans : IClassifier
{
    public double[][] Centroids = new double[0][];

    /// <summary>Sum of squared distances of the training samples to their centroid after the last fit.</summary>
    public double Inertia;

    /// <summary>Iterations the last fit took before assignments stopped changing.</summary>
    public int Iterations;

    public KMeans Fit(double[][] x, int k, int maxIterations, int seed)
    {
        Rng rng = new Rng(seed);
        int n = x.Length;
        int d = x[0].Length;
        Centroids = new double[k][];
        // k-means++: each next centre is drawn proportional to squared distance.
        Centroids[0] = Vec.Copy(x[rng.Next(n)]);
        double[] dist = new double[n];
        for (int c = 1; c < k; c++)
        {
            double total = 0.0;
            for (int i = 0; i < n; i++)
            {
                double best = Vec.SquaredDistance(x[i], Centroids[0]);
                for (int j = 1; j < c; j++)
                {
                    double dd = Vec.SquaredDistance(x[i], Centroids[j]);
                    if (dd < best)
                    {
                        best = dd;
                    }
                }
                dist[i] = best;
                total += best;
            }
            double target = rng.NextDouble() * total;
            int pick = n - 1;
            double acc = 0.0;
            for (int i = 0; i < n; i++)
            {
                acc += dist[i];
                if (acc >= target)
                {
                    pick = i;
                    break;
                }
            }
            Centroids[c] = Vec.Copy(x[pick]);
        }

        int[] assign = new int[n];
        for (int i = 0; i < n; i++)
        {
            assign[i] = -1;
        }
        Iterations = 0;
        for (int it = 0; it < maxIterations; it++)
        {
            Iterations = it + 1;
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                int c = Predict(x[i]);
                if (c != assign[i])
                {
                    assign[i] = c;
                    changed = true;
                }
            }
            if (!changed)
            {
                break;
            }
            int[] counts = new int[k];
            double[][] sums = new double[k][];
            for (int c = 0; c < k; c++)
            {
                sums[c] = new double[d];
            }
            for (int i = 0; i < n; i++)
            {
                counts[assign[i]]++;
                for (int j = 0; j < d; j++)
                {
                    sums[assign[i]][j] += x[i][j];
                }
            }
            for (int c = 0; c < k; c++)
            {
                if (counts[c] == 0)
                {
                    continue; // an empty cluster keeps its old centre
                }
                for (int j = 0; j < d; j++)
                {
                    Centroids[c][j] = sums[c][j] / counts[c];
                }
            }
        }
        Inertia = 0.0;
        for (int i = 0; i < n; i++)
        {
            Inertia += Vec.SquaredDistance(x[i], Centroids[Predict(x[i])]);
        }
        return this;
    }

    public int Predict(double[] features)
    {
        int best = 0;
        double bestD = Vec.SquaredDistance(features, Centroids[0]);
        for (int c = 1; c < Centroids.Length; c++)
        {
            double d = Vec.SquaredDistance(features, Centroids[c]);
            if (d < bestD)
            {
                bestD = d;
                best = c;
            }
        }
        return best;
    }

    /// <summary>Distance to the nearest centroid — large values flag outliers.</summary>
    public double DistanceToNearest(double[] features) =>
        Math.Sqrt(Vec.SquaredDistance(features, Centroids[Predict(features)]));

    public JsonValue ToJson()
    {
        JsonValue o = JsonValue.NewObject();
        o.Set("type", "KMeans");
        o.Set("centroids", ModelJson.FromMatrix(Centroids));
        o.Set("inertia", Inertia);
        return o;
    }

    public static KMeans FromJson(JsonValue o)
    {
        KMeans m = new KMeans();
        m.Centroids = ModelJson.ToMatrix(o.Get("centroids"));
        m.Inertia = o.Get("inertia").AsDouble;
        return m;
    }
}

/// <summary>
/// Collaborative-filtering recommender by matrix factorisation: each user and
/// item gets a short latent vector, trained by SGD so their dot product (plus
/// biases) reproduces the known ratings. Ratings are (user, item, value)
/// triples; ids are dense 0-based indices.
/// </summary>
public class Recommender
{
    public int Factors = 4;
    public double GlobalMean;
    public double[] UserBias = new double[0];
    public double[] ItemBias = new double[0];
    public double[][] UserFactors = new double[0][];
    public double[][] ItemFactors = new double[0][];

    private int[] _ratedUser = new int[0];
    private int[] _ratedItem = new int[0];

    public Recommender()
    {
    }

    public Recommender(int factors)
    {
        Factors = factors;
    }

    public Recommender Fit(int[] users, int[] items, double[] ratings, int userCount, int itemCount,
        int epochs, double learningRate, double regularization, int seed)
    {
        Rng rng = new Rng(seed);
        GlobalMean = 0.0;
        for (int i = 0; i < ratings.Length; i++)
        {
            GlobalMean += ratings[i];
        }
        GlobalMean /= ratings.Length;
        UserBias = new double[userCount];
        ItemBias = new double[itemCount];
        UserFactors = new double[userCount][];
        ItemFactors = new double[itemCount][];
        for (int u = 0; u < userCount; u++)
        {
            UserFactors[u] = new double[Factors];
            for (int f = 0; f < Factors; f++)
            {
                UserFactors[u][f] = rng.NextGaussian() * 0.1;
            }
        }
        for (int i = 0; i < itemCount; i++)
        {
            ItemFactors[i] = new double[Factors];
            for (int f = 0; f < Factors; f++)
            {
                ItemFactors[i][f] = rng.NextGaussian() * 0.1;
            }
        }
        _ratedUser = users;
        _ratedItem = items;
        for (int e = 0; e < epochs; e++)
        {
            int[] order = rng.Permutation(ratings.Length);
            for (int o = 0; o < order.Length; o++)
            {
                int r = order[o];
                int u = users[r];
                int i = items[r];
                double err = ratings[r] - PredictRating(u, i);
                UserBias[u] += learningRate * (err - regularization * UserBias[u]);
                ItemBias[i] += learningRate * (err - regularization * ItemBias[i]);
                double[] pu = UserFactors[u];
                double[] qi = ItemFactors[i];
                for (int f = 0; f < Factors; f++)
                {
                    double puf = pu[f];
                    pu[f] += learningRate * (err * qi[f] - regularization * puf);
                    qi[f] += learningRate * (err * puf - regularization * qi[f]);
                }
            }
        }
        return this;
    }

    public double PredictRating(int user, int item) =>
        GlobalMean + UserBias[user] + ItemBias[item] + Vec.Dot(UserFactors[user], ItemFactors[item]);

    /// <summary>The <paramref name="count"/> highest-predicted items the user has not rated in training.</summary>
    public int[] Recommend(int user, int count)
    {
        int items = ItemFactors.Length;
        bool[] seen = new bool[items];
        for (int r = 0; r < _ratedUser.Length; r++)
        {
            if (_ratedUser[r] == user)
            {
                seen[_ratedItem[r]] = true;
            }
        }
        List<int> chosen = new List<int>();
        bool[] taken = new bool[items];
        for (int n = 0; n < count; n++)
        {
            int best = -1;
            double bestScore = 0.0;
            for (int i = 0; i < items; i++)
            {
                if (seen[i] || taken[i])
                {
                    continue;
                }
                double s = PredictRating(user, i);
                if (best < 0 || s > bestScore)
                {
                    best = i;
                    bestScore = s;
                }
            }
            if (best < 0)
            {
                break;
            }
            taken[best] = true;
            chosen.Add(best);
        }
        return chosen.ToArray();
    }

    public JsonValue ToJson()
    {
        JsonValue o = JsonValue.NewObject();
        o.Set("type", "Recommender");
        o.Set("factors", Factors);
        o.Set("mean", GlobalMean);
        o.Set("userBias", ModelJson.FromVector(UserBias));
        o.Set("itemBias", ModelJson.FromVector(ItemBias));
        o.Set("userFactors", ModelJson.FromMatrix(UserFactors));
        o.Set("itemFactors", ModelJson.FromMatrix(ItemFactors));
        o.Set("ratedUser", ModelJson.FromInts(_ratedUser));
        o.Set("ratedItem", ModelJson.FromInts(_ratedItem));
        return o;
    }

    public static Recommender FromJson(JsonValue o)
    {
        Recommender m = new Recommender(o.Get("factors").AsInt);
        m.GlobalMean = o.Get("mean").AsDouble;
        m.UserBias = ModelJson.ToVector(o.Get("userBias"));
        m.ItemBias = ModelJson.ToVector(o.Get("itemBias"));
        m.UserFactors = ModelJson.ToMatrix(o.Get("userFactors"));
        m.ItemFactors = ModelJson.ToMatrix(o.Get("itemFactors"));
        if (o.Has("ratedUser"))
        {
            m._ratedUser = ModelJson.ToInts(o.Get("ratedUser"));
            m._ratedItem = ModelJson.ToInts(o.Get("ratedItem"));
        }
        return m;
    }
}
