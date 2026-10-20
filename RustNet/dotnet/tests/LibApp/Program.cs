using RustNet.AI.TensorFlow;
using RustNet.ML;
using RustNet.Native;
using RustNet.Sensors.Gnss;
using RustNet.Serialization;

namespace LibApp;

/// <summary>
/// Post-1.0 libraries on the interpreter: the GNSS NMEA decoder, RustNet.ML
/// and the culture-invariant number formatting they rely on. Every section
/// prints a marker the E2E test asserts on; numbers are printed as scaled
/// integers so the assertion does not depend on float formatting.
/// </summary>
public static class Program
{
    public static void Main()
    {
        Console.WriteLine("LibApp starting");
        Maths();
        Dialect();
        Gnss();
        Ml();
        NativeCalls();
        TensorFlowLite();
        Console.WriteLine("LibApp finished");
    }

    /// <summary>Two classes with their own lambdas (compiler-generated nested types), and an unassigned reference field.</summary>
    private static void Dialect()
    {
        int a = new LambdaOwnerA().Run(3);
        int b = new LambdaOwnerB().Run(4);
        Holder h = new Holder();
        double nan = 0.0 / 0.0;
        char c = 'Q';
        long idx = 2;
        int[] arr = new int[3];
        arr[idx] = 9;
        string glued = "x" + c + "y";
        Console.WriteLine($"dialect2 nan={(double.IsNaN(nan) ? "yes" : "no")} inf={(double.IsInfinity(1.0 / 0.0) ? "yes" : "no")} char={glued} longidx={arr[idx]} groups={GroupA.Run()},{GroupB.Run()}");
        Console.WriteLine($"dialect lambdas={a},{b} nullfield={(h.Child == null ? "null" : "set")} notnull={(h.Child != null ? "yes" : "no")} type={new LambdaOwnerA().GetType().Name}");
    }

    private static int Milli(double v) => (int)Math.Round(v * 1000.0);

    private static void Maths()
    {
        Console.WriteLine($"math tanh={Milli(Math.Tanh(0.5))} asin={Milli(Math.Asin(0.5))} sign={Math.Sign(-3.5)} log2={Milli(Math.Log(8.0, 2.0))} round={Math.Round(2.5)} trunc={Math.Truncate(-2.7)}");
        double half = 0.5;
        Console.WriteLine("invariant=" + half.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void Gnss()
    {
        NmeaParser p = new NmeaParser();
        int positions = 0;
        int satellites = 0;
        p.PositionReceived = f => positions++;
        p.SatellitesInView = s => satellites = s.Count;
        p.Feed("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47\r\n");
        p.Feed("$GPRMC,123519,A,4807.038,N,011");
        p.Feed("31.000,E,022.4,084.4,230394,003.1,W*6A\r\n");
        p.Feed("$GPGSV,2,1,08,01,40,083,46,02,17,308,41,12,07,344,39,14,22,228,45*75\r\n");
        p.Feed("$GPGSV,2,2,08,15,10,050,,17,55,120,38,19,05,220,30,24,70,010,48*7D\r\n");
        p.Feed("$GPGGA,bad*00\r\n");
        GnssFix fix = p.Fix;
        Console.WriteLine($"gnss lat={Milli(fix.Latitude)} lon={Milli(fix.Longitude)} alt={Milli(fix.AltitudeMeters)} sats={fix.SatellitesUsed}");
        Console.WriteLine($"gnss date={fix.DateText()} time={fix.TimeText()} kmh={Milli(fix.SpeedKmh)} inview={satellites} pos={positions} bad={p.ChecksumErrors}");
    }

    /// <summary>TensorFlow Lite: a TensorFlow-converted int8 CNN and a float MLP.</summary>
    private static void TensorFlowLite()
    {
        Interpreter cnn = new Interpreter(RustNet.Resources.Resource.GetBytes("cnn_int8.tflite"));
        int[] shape = cnn.InputShape(0);
        Console.WriteLine($"tflite cnn ops={cnn.OperatorCount} in={shape[0]}x{shape[1]}x{shape[2]}x{shape[3]} type={cnn.InputType(0)}");
        cnn.SetInputRaw(0, RustNet.Resources.Resource.GetBytes("cnn_int8.in"));
        cnn.Invoke();
        byte[] raw = cnn.GetOutputRaw(0);
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append((int)(sbyte)raw[i]);
        }
        Console.WriteLine("tflite cnn out=" + sb.ToString() + " class=" + cnn.ArgMax(0));
        cnn.Dispose();

        Interpreter mlp = new Interpreter(RustNet.Resources.Resource.GetBytes("mlp_float.tflite"));
        float[] x = new float[4];
        x[0] = 0.5f;
        x[1] = -1.2f;
        x[2] = 3.3f;
        x[3] = 0.7f;
        mlp.SetInput(0, x);
        mlp.Invoke();
        float[] p = mlp.GetOutput(0);
        Console.WriteLine($"tflite mlp p0={Milli(p[0])} p1={Milli(p[1])} p2={Milli(p[2])} sum={Milli(p[0] + p[1] + p[2])}");
        try
        {
            new Interpreter(new byte[16]);
            Console.WriteLine("tflite garbage=accepted");
        }
        catch (Exception ex)
        {
            Console.WriteLine("tflite garbage=" + (ex.Message.Contains("TensorFlow Lite") || ex.Message.Contains("model") ? "rejected" : ex.Message));
        }
    }

    /// <summary>NPC: the E2E test pushes the signed sample module before starting this app.</summary>
    private static void NativeCalls()
    {
        if (!Npc.IsSupported())
        {
            Console.WriteLine("npc unsupported");
            return;
        }
        NativeProcedure sum;
        try
        {
            sum = new NativeProcedure("sample", "rustnet_sum_u32");
        }
        catch (Exception ex)
        {
            Console.WriteLine("npc absent: " + ex.Message);
            return;
        }
        int[] values = new int[3];
        values[0] = 40;
        values[1] = 2;
        values[2] = 100000;
        Console.WriteLine($"npc sum={sum.InvokeInt32(values)}");

        byte[] text = System.Text.Encoding.UTF8.GetBytes("123456789");
        byte[] crc = new NativeProcedure("sample", "rustnet_crc32").Invoke(text);
        long c = (crc[0] | (crc[1] << 8) | (crc[2] << 16) | (crc[3] << 24)) & 0xFFFFFFFFL;
        Console.WriteLine($"npc crc32={c}");

        byte[] rev = new NativeProcedure("sample", "rustnet_reverse").Invoke(System.Text.Encoding.UTF8.GetBytes("RustNet"));
        Console.WriteLine("npc reverse=" + System.Text.Encoding.UTF8.GetString(rev));

        try
        {
            new NativeProcedure("sample", "rustnet_fail").Invoke(new byte[0]);
            Console.WriteLine("npc fail=missing");
        }
        catch (Exception ex)
        {
            Console.WriteLine("npc fail=" + (ex.Message.Contains("-7") ? "code-7" : ex.Message));
        }
        try
        {
            Npc.Load("sample", "no_such_symbol");
        }
        catch (Exception ex)
        {
            Console.WriteLine("npc missing=" + (ex.Message.Contains("no procedure") ? "reported" : ex.Message));
        }
    }

    private static double[][] Blobs(int perClass, int[] labels)
    {
        Rng rng = new Rng(5);
        double[][] x = new double[perClass * 3][];
        for (int c = 0; c < 3; c++)
        {
            double cx = c == 1 ? 4.0 : (c == 2 ? -2.0 : -2.0);
            double cy = c == 1 ? 0.0 : (c == 2 ? 3.5 : -3.5);
            for (int i = 0; i < perClass; i++)
            {
                int k = c * perClass + i;
                double[] v = new double[2];
                v[0] = cx + rng.NextGaussian() * 0.6;
                v[1] = cy + rng.NextGaussian() * 0.6;
                x[k] = v;
                labels[k] = c;
            }
        }
        return x;
    }

    private static void Ml()
    {
        // Linear regression, closed form: y = 3a - 2b + 5.
        double[][] lx = new double[12][];
        double[] ly = new double[12];
        for (int i = 0; i < 12; i++)
        {
            double[] v = new double[2];
            v[0] = i;
            v[1] = (i * 7) % 5;
            lx[i] = v;
            ly[i] = 3.0 * v[0] - 2.0 * v[1] + 5.0;
        }
        LinearRegression lin = new LinearRegression().Fit(lx, ly);
        Console.WriteLine($"ml linear w0={Milli(lin.Weights[0])} w1={Milli(lin.Weights[1])} b={Milli(lin.Bias)}");

        int[] y = new int[45];
        double[][] x = Blobs(15, y);
        SoftmaxRegression soft = new SoftmaxRegression().Fit(x, y, 60, 0.5);
        GaussianNaiveBayes nb = new GaussianNaiveBayes().Fit(x, y);
        KNearestNeighbors knn = new KNearestNeighbors(3).Fit(x, y);
        Console.WriteLine($"ml acc softmax={Milli(Metrics.Accuracy(soft, x, y))} bayes={Milli(Metrics.Accuracy(nb, x, y))} knn={Milli(Metrics.Accuracy(knn, x, y))}");

        KMeans km = new KMeans().Fit(x, 3, 20, 4);
        int a = km.Predict(x[0]);
        int b = km.Predict(x[15]);
        int c = km.Predict(x[30]);
        bool distinct = a != b && b != c && a != c;
        Console.WriteLine($"ml kmeans distinct={distinct} iters={(km.Iterations > 0 ? "ok" : "none")}");

        // XOR with a tiny network, then a JSON round trip.
        double[][] xx = new double[4][];
        int[] xy = new int[4];
        for (int i = 0; i < 4; i++)
        {
            double[] v = new double[2];
            v[0] = i >> 1;
            v[1] = i & 1;
            xx[i] = v;
            xy[i] = (i >> 1) ^ (i & 1);
        }
        int[] sizes = new int[3];
        sizes[0] = 2;
        sizes[1] = 6;
        sizes[2] = 2;
        NeuralNetwork net = new NeuralNetwork(sizes, Activation.Tanh, Activation.Softmax, 3);
        net.Fit(xx, xy, 800, 0.1, 4);
        NeuralNetwork copy = (NeuralNetwork)ModelStore.Load(ModelStore.Save(net.ToJson()));
        Console.WriteLine($"ml xor acc={Milli(Metrics.Accuracy(net, xx, xy))} copy={Milli(Metrics.Accuracy(copy, xx, xy))}");

        // Recommender: users 0-1 like items 0-2, users 2-3 like 3-5; user 0 has not rated item 2.
        List<int> users = new List<int>();
        List<int> items = new List<int>();
        List<double> ratings = new List<double>();
        for (int u = 0; u < 4; u++)
        {
            for (int i = 0; i < 6; i++)
            {
                if ((u == 0 && i == 2) || (u == 3 && i == 5))
                {
                    continue;
                }
                bool likes = (u < 2) == (i < 3);
                users.Add(u);
                items.Add(i);
                ratings.Add(likes ? 5.0 : 1.0);
            }
        }
        Recommender rec = new Recommender(3).Fit(users.ToArray(), items.ToArray(), ratings.ToArray(), 4, 6, 150, 0.05, 0.01, 8);
        Console.WriteLine($"ml recommend u0={rec.Recommend(0, 1)[0]} u3={rec.Recommend(3, 1)[0]}");
    }
}

public class LambdaOwnerA
{
    public int Run(int k)
    {
        Func<int, int> twice = x => x * 2 + k;
        Func<int> fixedValue = () => 7;
        return twice(10) + fixedValue();
    }
}

public class LambdaOwnerB
{
    public int Run(int k)
    {
        Func<int, int> thrice = x => x * 3 + k;
        Func<int> fixedValue = () => 1;
        return thrice(10) + fixedValue();
    }
}

public class Holder
{
    public Holder? Child;
}

public static class GroupA
{
    private static int Twice(int x) => x * 2;

    public static int Run()
    {
        Func<int, int> f = Twice; // cached in a compiler-generated <>O class
        return f(5);
    }
}

public static class GroupB
{
    private static int Thrice(int x) => x * 3;

    public static int Run()
    {
        Func<int, int> f = Thrice;
        return f(5);
    }
}
