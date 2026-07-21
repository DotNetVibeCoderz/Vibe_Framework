using RustNet.Devices;
using RustNet.Hal;
using RustNet.IO;

namespace SampleApp;

/// <summary>
/// End-to-end sample compiled to RNX by the MetadataProcessor and executed
/// by the Rust interpreter in tests. Exercises arithmetic, strings, arrays,
/// objects, driver classes, HAL calls and the filesystem.
/// </summary>
public static class Program
{
    public static void Main()
    {
        Console.WriteLine("SampleApp starting");

        // Arithmetic + loop
        int fib = Fib(16);
        Console.WriteLine(string.Concat("fib(16)=", fib.ToString()));

        // Arrays
        int[] values = new int[6];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i * i;
        }
        int sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum = sum + values[i];
        }
        Console.WriteLine(string.Concat("sum=", sum.ToString()));

        // Objects + driver classes
        Led led = new Led(13);
        led.On();
        led.Toggle();
        led.Toggle();

        GpsNmeaParser gps = new GpsNmeaParser();
        bool ok = gps.Parse("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47");
        if (ok && gps.HasFix)
        {
            Console.WriteLine(string.Concat("gps sats=", gps.Satellites.ToString()));
        }

        // HAL + filesystem
        int mv = Adc.ReadMillivolts(0);
        FileSystem.WriteAllText("/data/sample.txt", string.Concat("adc=", mv.ToString()));
        string readBack = FileSystem.ReadAllText("/data/sample.txt");
        Console.WriteLine(string.Concat("file: ", readBack));

        // v0.2: exceptions with try/catch/finally
        string caught = "none";
        try
        {
            Boom();
        }
        catch (InvalidOperationException ex)
        {
            caught = ex.Message;
        }
        finally
        {
            Console.WriteLine("finally ran");
        }
        Console.WriteLine(string.Concat("caught: ", caught));

        // v0.2: collections + foreach
        List<int> list = new List<int>();
        for (int i = 1; i <= 5; i++)
        {
            list.Add(i * 10);
        }
        int listSum = 0;
        foreach (int v in list)
        {
            listSum = listSum + v;
        }
        Console.WriteLine(string.Concat("listSum=", listSum.ToString(), " count=", list.Count.ToString()));

        Dictionary<string, int> dict = new Dictionary<string, int>();
        dict["alpha"] = 1;
        dict["beta"] = 2;
        Console.WriteLine(string.Concat("dict beta=", dict["beta"].ToString(), " has alpha=", dict.ContainsKey("alpha").ToString()));

        // v0.2: delegates + LINQ
        Func<int, bool> isEven = x => x % 2 == 0;
        int evenSum = list.Where(isEven).Sum();
        Console.WriteLine(string.Concat("evenSum=", evenSum.ToString()));

        // v0.2: StringBuilder + Regex
        var sb = new System.Text.StringBuilder();
        sb.Append("sb:");
        sb.Append(42);
        Console.WriteLine(sb.ToString());
        bool matched = System.Text.RegularExpressions.Regex.IsMatch("sensor-042", "^sensor-\\d+$");
        Console.WriteLine(string.Concat("regex=", matched.ToString()));

        // v0.2: string interpolation (DefaultInterpolatedStringHandler)
        int reading = 21;
        Console.WriteLine($"interp temp={reading}C");

        RustNet.Diagnostics.Log.Info("sample app done");
        Console.WriteLine("SampleApp finished");
    }

    private static void Boom()
    {
        throw new InvalidOperationException("boom");
    }

    private static int Fib(int n)
    {
        int a = 0;
        int b = 1;
        for (int i = 0; i < n; i++)
        {
            int t = a + b;
            a = b;
            b = t;
        }
        return a;
    }
}
