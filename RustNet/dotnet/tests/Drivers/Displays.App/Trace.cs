using RustNet.Community.Bus;

namespace DriversApp;

/// <summary>
/// An SPI simulator that tags every byte with the D/C pin level at the moment
/// it was clocked out: entry = byte | 0x100 when D/C was high (data). That is
/// what lets a test assert command-vs-data per byte, independent of how the
/// driver grouped them into transactions.
/// </summary>
public class TraceSpi : SimSpiDevice
{
    public DigitalOutput? Dc;
    public List<int> Trace = new List<int>();

    public TraceSpi()
    {
        // Assign every reference field: on the interpreter an unassigned one
        // reads as integer 0 and a null comparison on it throws.
        Dc = null;
        Responder = null;
    }

    public override byte[] Transfer(byte[] data)
    {
        int flag = Dc != null && Dc.State ? 0x100 : 0;
        for (int i = 0; i < data.Length; i++)
        {
            Trace.Add(flag | data[i]);
        }
        return base.Transfer(data);
    }
}

/// <summary>
/// Expected-traffic specs: hex bytes, with the standalone tokens <c>C</c> (following
/// bytes are commands, D/C low) and <c>D</c> (data, D/C high). For example
/// <c>"C 2A D 00 0A 00 13"</c>. A token <c>*N</c> repeats the previous byte
/// N more times.
/// </summary>
public static class Expect
{
    public static List<int> Parse(string spec)
    {
        List<int> r = new List<int>();
        int flag = 0;
        int i = 0;
        while (i < spec.Length)
        {
            char c = spec[i];
            if (c == ' ' || c == ',' || c == '\n')
            {
                i++;
                continue;
            }
            if (c == '|')
            {
                r.Add(0x200);
                i++;
                continue;
            }
            bool standalone = i + 1 >= spec.Length || spec[i + 1] == ' ' || spec[i + 1] == '|';
            if (c == 'C' && standalone)
            {
                flag = 0;
                i++;
                continue;
            }
            if (c == 'D' && standalone)
            {
                flag = 0x100;
                i++;
                continue;
            }
            if (c == '*')
            {
                int n = 0;
                i++;
                while (i < spec.Length && spec[i] >= '0' && spec[i] <= '9')
                {
                    n = n * 10 + (spec[i] - '0');
                    i++;
                }
                int last = r[r.Count - 1];
                for (int k = 0; k < n; k++)
                {
                    r.Add(last);
                }
                continue;
            }
            int v = (Hex(spec[i]) << 4) | Hex(spec[i + 1]);
            r.Add(flag | v);
            i += 2;
        }
        return r;
    }

    private static int Hex(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }
        return c - 'A' + 10;
    }

    private static string Show(int v)
    {
        string kind = (v & 0x100) != 0 ? "D" : "C";
        int b = v & 0xFF;
        return $"{kind}:{b}";
    }

    /// <summary>Trace from <paramref name="start"/> to the end must equal <paramref name="spec"/> exactly.</summary>
    public static void Trace(string what, string spec, TraceSpi spi, int start)
    {
        Compare(what, Parse(spec), spi.Trace, start, true);
    }

    /// <summary>Trace from <paramref name="start"/> must begin with <paramref name="spec"/>.</summary>
    public static void TracePrefix(string what, string spec, TraceSpi spi, int start)
    {
        Compare(what, Parse(spec), spi.Trace, start, false);
    }

    public static void Compare(string what, List<int> expected, List<int> actual, int start, bool exactLength)
    {
        int n = actual.Count - start;
        if (exactLength && n != expected.Count)
        {
            Check.Equal($"{what} length", expected.Count, n);
        }
        for (int i = 0; i < expected.Count; i++)
        {
            if (start + i >= actual.Count)
            {
                Check.True($"{what} truncated at {i}", false);
                return;
            }
            if (actual[start + i] != expected[i])
            {
                Check.True($"{what} byte {i} expected {Show(expected[i])} got {Show(actual[start + i])}", false);
                return;
            }
        }
        Check.True(what, true);
    }

    /// <summary>Raw bytes (no D/C) must equal the hex spec.</summary>
    public static void Bytes(string what, string hex, byte[] actual)
    {
        List<int> e = Parse(hex);
        List<int> a = new List<int>();
        for (int i = 0; i < actual.Length; i++)
        {
            a.Add(actual[i]);
        }
        Compare(what, e, a, 0, true);
    }

    /// <summary>Concatenation of every I2C write, each prefixed with "|" boundary marker 0x200.</summary>
    public static List<int> I2cWrites(SimI2cDevice dev, int fromWrite)
    {
        List<int> r = new List<int>();
        for (int w = fromWrite; w < dev.Writes.Count; w++)
        {
            r.Add(0x200);
            byte[] b = dev.Writes[w];
            for (int i = 0; i < b.Length; i++)
            {
                r.Add(b[i]);
            }
        }
        return r;
    }

    /// <summary>Spec for I2C writes: "| 00 AE D5 | 40 FF" — '|' starts a new write.</summary>
    public static List<int> ParseI2c(string spec) => Parse(spec);

    public static void I2c(string what, string spec, SimI2cDevice dev, int fromWrite)
    {
        Compare(what, ParseI2c(spec), I2cWrites(dev, fromWrite), 0, true);
    }
}

/// <summary>An SPI display rig: tracing SPI device, D/C pin, reset pin and the bus over them.</summary>
public class SpiRig
{
    public TraceSpi Spi;
    public SimDigitalOutput Dc;
    public SimDigitalOutput Reset;
    public RustNet.Community.DisplayCore.SpiDisplayBus Bus;

    public SpiRig()
    {
        Spi = new TraceSpi();
        Dc = new SimDigitalOutput();
        Reset = new SimDigitalOutput();
        Spi.Dc = Dc;
        Bus = new RustNet.Community.DisplayCore.SpiDisplayBus(Spi, Dc);
    }

    public int Mark => Spi.Trace.Count;
}

/// <summary>
/// Counts requested sleep time. An instance-method delegate on purpose:
/// captured lambdas and static method groups compile to compiler-generated
/// nested classes (&lt;&gt;c__DisplayClass0_0, &lt;&gt;c, &lt;&gt;O) whose names collide
/// across types in the MetadataProcessor.
/// </summary>
public class SleepCounter
{
    public int Total;

    public void Sleep(int ms) => Total += ms;

    public void Ignore(int ms)
    {
    }
}
