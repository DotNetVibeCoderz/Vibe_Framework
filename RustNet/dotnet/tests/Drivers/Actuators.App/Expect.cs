using RustNet.Community.Bus;

namespace DriversApp;

/// <summary>
/// Frame and pin-history assertions for the actuator smoke tests. Expected
/// values are written as hex strings ("A2 0A 14") or level strings ("1010"),
/// taken from datasheet register maps or computed independently.
/// </summary>
public static class Expect
{
    private const string Digits = "0123456789ABCDEF";

    /// <summary>
    /// A SimSpiDevice whose Responder is always assigned (answers zeros).
    /// SimSpiDevice never stores Responder itself, and on the interpreter a
    /// never-stored reference field cannot be compared with null, so a bare
    /// SimSpiDevice throws "expected int32, got Null" on its first transfer.
    /// </summary>
    public static SimSpiDevice Spi()
    {
        SimSpiDevice s = new SimSpiDevice();
        s.Responder = new ZeroChip().Respond;
        return s;
    }

    public static string Hex(byte[]? data)
    {
        if (data == null)
        {
            return "null";
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(Digits[(data[i] >> 4) & 0xF]);
            sb.Append(Digits[data[i] & 0xF]);
        }
        return sb.ToString();
    }

    /// <summary>Compare a byte array with a space-separated hex string.</summary>
    public static void Bytes(string what, string expectedHex, byte[]? actual)
    {
        string got = Hex(actual);
        if (got == expectedHex)
        {
            Check.Passed++;
            return;
        }
        Check.Failed++;
        Console.WriteLine("FAIL " + what + ": expected " + expectedHex + ", got " + got);
    }

    /// <summary>Compare the <paramref name="index"/>-th recorded I2C write.</summary>
    public static void Write(string what, string expectedHex, SimI2cDevice sim, int index)
    {
        Bytes(what, expectedHex, index < sim.Writes.Count ? sim.Writes[index] : null);
    }

    /// <summary>Compare the most recent I2C write.</summary>
    public static void LastWrite(string what, string expectedHex, SimI2cDevice sim)
    {
        Bytes(what, expectedHex, sim.Writes.Count > 0 ? sim.Writes[sim.Writes.Count - 1] : null);
    }

    /// <summary>Compare the most recent SPI transaction.</summary>
    public static void LastSpi(string what, string expectedHex, SimSpiDevice sim)
    {
        Bytes(what, expectedHex, sim.Transactions.Count > 0 ? sim.Transactions[sim.Transactions.Count - 1] : null);
    }

    /// <summary>Compare a pin history with a string of '0'/'1'.</summary>
    public static void Levels(string what, string expected, List<bool> history)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < history.Count; i++)
        {
            sb.Append(history[i] ? '1' : '0');
        }
        string got = sb.ToString();
        if (got == expected)
        {
            Check.Passed++;
            return;
        }
        Check.Failed++;
        Console.WriteLine("FAIL " + what + ": expected " + expected + ", got " + got);
    }

    /// <summary>Compare a duty history with a comma-separated list.</summary>
    public static void Duties(string what, string expected, List<int> history)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < history.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(history[i]);
        }
        string got = sb.ToString();
        if (got == expected)
        {
            Check.Passed++;
            return;
        }
        Check.Failed++;
        Console.WriteLine("FAIL " + what + ": expected " + expected + ", got " + got);
    }

    public static void Text(string what, string expected, string got)
    {
        if (got == expected)
        {
            Check.Passed++;
            return;
        }
        Check.Failed++;
        Console.WriteLine("FAIL " + what + ": expected " + expected + ", got " + got);
    }

    /// <summary>Record every Timing.SleepMs into a list until <see cref="StopRecordingSleeps"/>.</summary>
    public static List<int> RecordSleeps()
    {
        SleepRecorder r = new SleepRecorder();
        Timing.SleepOverride = r.Record;
        return r.Sleeps;
    }

    public static void StopRecordingSleeps() => Timing.SleepOverride = new SleepRecorder().Ignore;

    public static int Sum(List<int> values)
    {
        int s = 0;
        for (int i = 0; i < values.Count; i++)
        {
            s += values[i];
        }
        return s;
    }
}

/// <summary>
/// Sleep hooks as instance methods. The MetadataProcessor names nested types
/// by their simple name, so compiler-generated lambda classes (&lt;&gt;c,
/// &lt;&gt;c__DisplayClass…) from different classes collide; instance method
/// groups create delegates without any generated class.
/// </summary>
public class SleepRecorder
{
    public List<int> Sleeps = new List<int>();

    public void Record(int ms) => Sleeps.Add(ms);

    public void Ignore(int ms)
    {
    }
}

/// <summary>An SPI slave that shifts out zeros.</summary>
public class ZeroChip
{
    public byte[] Respond(byte[] tx) => new byte[tx.Length];
}
