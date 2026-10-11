// Numeric kernels: the shape of code Rustzor's AOT mode compiles. The output
// must be identical interpreted, AOT-compiled, and on .NET.
static class Kernels
{
    public static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

    public static int Sieve(int limit)
    {
        var composite = new byte[limit + 1];
        return SieveInto(composite, limit);
    }

    public static int SieveInto(byte[] composite, int limit)
    {
        var count = 0;
        for (var i = 2; i <= limit; i++)
        {
            if (composite[i] != 0) continue;
            count++;
            for (long j = (long)i * i; j <= limit; j += i) composite[j] = 1;
        }
        return count;
    }

    public static double MatMul(double[] a, double[] b, double[] c, int n)
    {
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                var sum = 0.0;
                for (var k = 0; k < n; k++) sum += a[i * n + k] * b[k * n + j];
                c[i * n + j] = sum;
            }
        var trace = 0.0;
        for (var i = 0; i < n; i++) trace += c[i * n + i];
        return trace;
    }

    public static int Mandelbrot(int width, int height, int maxIter)
    {
        var total = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                double cr = x * 3.0 / width - 2.0, ci = y * 2.0 / height - 1.0, zr = 0, zi = 0;
                var n = 0;
                while (n < maxIter && zr * zr + zi * zi < 4.0)
                {
                    var t = zr * zr - zi * zi + cr;
                    zi = 2 * zr * zi + ci;
                    zr = t;
                    n++;
                }
                total += n;
            }
        return total;
    }

    public static long Collatz(long start)
    {
        long steps = 0;
        while (start != 1) { start = (start & 1) == 0 ? start / 2 : 3 * start + 1; steps++; }
        return steps;
    }

    public static void InsertionSort(int[] items)
    {
        for (var i = 1; i < items.Length; i++)
        {
            var v = items[i];
            var j = i - 1;
            while (j >= 0 && items[j] > v) { items[j + 1] = items[j]; j--; }
            items[j + 1] = v;
        }
    }

    public static int Checked(int[] items, int index) => items[index];

    public static int Divide(int a, int b) => a / b;

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static int Classify(int n)
    {
        switch (n % 4) { case 0: return 10; case 1: return 20; case 2: return 30; default: return Math.Max(n, 7); }
    }
}

static class P
{
    static void Main()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine("fib " + Kernels.Fib(24));
        Console.WriteLine("sieve " + Kernels.Sieve(200000));
        const int n = 48;
        var a = new double[n * n]; var b = new double[n * n]; var c = new double[n * n];
        for (var i = 0; i < n * n; i++) { a[i] = i % 7 * 0.5; b[i] = i % 5 * 0.25; }
        Console.WriteLine("matmul " + Kernels.MatMul(a, b, c, n).ToString("F3") + " c[5]=" + c[5]);
        Console.WriteLine("mandel " + Kernels.Mandelbrot(120, 80, 200));
        long best = 0; for (long s = 1; s < 3000; s++) best = Math.Max(best, Kernels.Collatz(s));
        Console.WriteLine("collatz " + best);
        var items = new int[600]; for (var i = 0; i < items.Length; i++) items[i] = (i * 7919) % 1000;
        Kernels.InsertionSort(items);
        Console.WriteLine("sorted " + items[0] + "," + items[299] + "," + items[599]);
        Console.WriteLine("lerp " + Kernels.Lerp(1f, 3f, 0.25f));
        Console.WriteLine("switch " + Kernels.Classify(5) + "," + Kernels.Classify(7) + "," + Kernels.Classify(8));
        try { Kernels.Checked(items, 600); } catch (IndexOutOfRangeException) { Console.WriteLine("bounds ok"); }
        try { Kernels.Divide(1, 0); } catch (DivideByZeroException) { Console.WriteLine("divzero ok"); }

    }
}
