using System.Text;
using RustNet.Core;
using RustNet.Hal;
using RustNet.Threading;

namespace LanguageTour;

/// <summary>Board facts the firmware knows and the app does not.</summary>
internal static class Board
{
    /// <summary>The user LED, as a HAL pin index.</summary>
    [InternalCall]
    public static int UserLed() => throw new RuntimeOnlyException();
}

internal interface ISensor
{
    string Label();
    int Read();
}

internal class Thermometer : ISensor
{
    private readonly int _celsius;

    public Thermometer(int celsius) => _celsius = celsius;

    public string Label() => "temp";

    public int Read() => _celsius;

    public override string ToString() => $"{Label()}={Read()}C";
}

internal class Hygrometer : ISensor
{
    private readonly int _percent;

    public Hygrometer(int percent) => _percent = percent;

    public string Label() => "humidity";

    public int Read() => _percent;

    public override string ToString() => $"{Label()}={Read()}pct";
}

/// <summary>User generic — erased by the runtime, but type-checked by Roslyn.</summary>
internal class Reading<T>
{
    private readonly T _value;

    public Reading(T value) => _value = value;

    public T Value() => _value;
}

/// <summary>
/// Exercises the C# feature surface on-chip and reports the result on the
/// LED, so the run can be judged without a serial adapter: a calm 1 Hz pulse
/// means every check passed, and anything else blinks the failure count.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static void Check(string what, bool ok)
    {
        if (ok)
        {
            _passed++;
        }
        else
        {
            _failed++;
        }

        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
    }

    private static void Main()
    {
        int led = Board.UserLed();
        Gpio.SetMode(led, PinMode.Output);

        Console.WriteLine("RustNet C# language tour, interpreted on bare-metal ARM");

        int answer = 42;
        Check("string interpolation", $"answer={answer}" == "answer=42");

        List<int> squares = new List<int>();
        for (int i = 1; i <= 5; i++)
        {
            squares.Add(i * i);
        }

        int total = 0;
        foreach (int v in squares)
        {
            total += v;
        }

        Check("List<T> + foreach", total == 55);

        Dictionary<string, int> pins = new Dictionary<string, int>();
        pins["led"] = led;
        pins["uart"] = 7;
        Check("Dictionary<K,V>", pins.ContainsKey("uart") && pins["uart"] == 7);

        // squares are 1 4 9 16 25; the even ones doubled are 8 and 32.
        List<int> doubled = squares.Where(v => v % 2 == 0).Select(v => v * 2).ToList();
        Check("LINQ Where/Select/Sum", doubled.Count == 2 && doubled.Sum() == 40);

        List<int> descending = squares.OrderBy(v => -v).ToList();
        Check("LINQ OrderBy", descending[0] == 25 && descending[4] == 1);

        Func<int, int> triple = x => x * 3;
        Check("lambda + delegate", triple(7) == 21);

        List<ISensor> sensors = new List<ISensor>();
        sensors.Add(new Thermometer(23));
        sensors.Add(new Hygrometer(61));

        StringBuilder report = new StringBuilder();
        foreach (ISensor s in sensors)
        {
            report.Append(s.ToString());
            report.Append(" ");
        }

        Check("interface dispatch + ToString override", report.ToString() == "temp=23C humidity=61pct ");

        Reading<string> boxed = new Reading<string>("ok");
        Check("user generics", boxed.Value() == "ok");

        string handled = "none";
        try
        {
            throw new Exception("overheat");
        }
        catch (Exception ex) when (ex.Message == "overheat")
        {
            handled = "filtered";
        }

        Check("try/catch with a when filter", handled == "filtered");

        Console.WriteLine($"passed {_passed}, failed {_failed}");

        Report(led);
    }

    /// <summary>
    /// A calm 1 Hz pulse means everything passed. Otherwise the failure count
    /// is blinked quickly, then a long pause before repeating.
    /// </summary>
    private static void Report(int led)
    {
        while (true)
        {
            if (_failed == 0)
            {
                Gpio.Write(led, true);
                Sleep.Ms(200);
                Gpio.Write(led, false);
                Sleep.Ms(800);
            }
            else
            {
                for (int i = 0; i < _failed; i++)
                {
                    Gpio.Write(led, true);
                    Sleep.Ms(80);
                    Gpio.Write(led, false);
                    Sleep.Ms(160);
                }

                Sleep.Ms(1500);
            }
        }
    }
}
