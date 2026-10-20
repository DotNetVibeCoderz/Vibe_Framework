using RustNet.Community.Bus;
using RustNet.Community.Tm1637;

namespace DriversApp;

/// <summary>An output pin that appends (id*2 + level) to a shared log, so edge order across pins is visible.</summary>
public class LogPin : DigitalOutput
{
    public List<int> Log;
    public int Id;

    public LogPin(List<int> log, int id)
    {
        Log = log;
        Id = id;
        State = true;
    }

    public override void Write(bool high)
    {
        State = high;
        Log.Add(Id * 2 + (high ? 1 : 0));
    }
}

public static class Tm1637Smoke
{
    private const int Clk = 0;
    private const int Dio = 1;

    /// <summary>Parse "C0 D1 ..." into log codes.</summary>
    private static List<int> Events(string spec)
    {
        List<int> r = new List<int>();
        for (int i = 0; i + 1 < spec.Length; i++)
        {
            char p = spec[i];
            if (p == 'C' || p == 'D')
            {
                r.Add((p == 'C' ? Clk : Dio) * 2 + (spec[i + 1] - '0'));
                i++;
            }
        }
        return r;
    }

    /// <summary>
    /// Independent protocol decoder: a start is DIO falling while CLK is high,
    /// a stop DIO rising while CLK is high; between them data bits are
    /// sampled on CLK rising edges, LSB first, every ninth bit being the ACK
    /// slot. Returns the frames as lists of bytes (count prefix, then bytes).
    /// </summary>
    private static List<int> Decode(List<int> log)
    {
        // Wire model: DIO on the wire is low when the driver drives it low or
        // when the chip acknowledges (from the falling CLK after bit 8 until
        // the falling CLK that ends the ACK clock).
        List<int> frames = new List<int>();
        bool clk = true;
        bool drv = true;
        bool chipLow = false;
        bool wire = true;
        bool inFrame = false;
        bool ackClocked = false;
        int bits = 0;
        int value = 0;
        int countAt = -1;
        for (int i = 0; i < log.Count; i++)
        {
            int pin = log[i] / 2;
            bool level = (log[i] & 1) != 0;
            if (pin == Dio)
            {
                drv = level;
            }
            else
            {
                if (clk && !level && inFrame)
                {
                    if (ackClocked)
                    {
                        chipLow = false;
                        ackClocked = false;
                        bits = 0;
                        value = 0;
                    }
                    else if (bits == 8)
                    {
                        chipLow = true;
                    }
                }
                if (!clk && level && inFrame)
                {
                    if (bits < 8)
                    {
                        if (wire)
                        {
                            value |= 1 << bits;
                        }
                        bits++;
                    }
                    else
                    {
                        frames.Add(value);
                        frames[countAt] = frames[countAt] + 1;
                        ackClocked = true;
                    }
                }
                clk = level;
            }
            bool now = drv && !chipLow;
            if (now != wire && clk)
            {
                if (!now)
                {
                    inFrame = true;
                    bits = 0;
                    value = 0;
                    countAt = frames.Count;
                    frames.Add(0);
                }
                else
                {
                    inFrame = false;
                }
            }
            wire = now;
        }
        return frames;
    }

    public static void Run()
    {
        List<int> log = new List<int>();
        LogPin clk = new LogPin(log, Clk);
        LogPin dio = new LogPin(log, Dio);
        SimDigitalInput ack = new SimDigitalInput();
        ack.Source = null;
        ack.Level = false; // chip pulls DIO low on the ninth clock = ACK
        Tm1637 tm = new Tm1637(clk, dio, ack, 4);

        // One framed byte (data command 0x40), edge by edge, from the datasheet's
        // timing: start, 8 bits LSB first (data changes while CLK low), ACK clock, stop.
        tm.Frame(RustNet.Community.DisplayCore.HexTable.Decode("40"));
        Expect.Compare("tm1637 bit-level 0x40",
            Events("D0 C0 D0 C1 C0 D0 C1 C0 D0 C1 C0 D0 C1 C0 D0 C1 C0 D0 C1 C0 D1 C1 C0 D0 C1 C0 D1 C1 D0 C0 D0 C1 D1"),
            log, 0, true);
        Check.True("tm1637 ack", tm.LastAck);

        // Full update "0123" at brightness 7: three frames, decoded independently.
        log.Clear();
        tm.SetSegments(RustNet.Community.Segments.SevenSegment.EncodeText("0123", 4), 0);
        List<int> f = Decode(log);
        Expect.Compare("tm1637 frames", Expect.Parse("01 40 05 C0 3F 06 5B 4F 01 8F"), f, 0, true);

        log.Clear();
        tm.SetBrightness(2, true);
        Expect.Compare("tm1637 brightness 2", Expect.Parse("01 8A"), Decode(log), 0, true);
        log.Clear();
        tm.SetBrightness(0, false);
        Expect.Compare("tm1637 display off", Expect.Parse("01 80"), Decode(log), 0, true);

        log.Clear();
        tm.SetBrightness(7, true);
        log.Clear();
        tm.ShowTime(12, 34, true);
        Expect.Compare("tm1637 12:34", Expect.Parse("01 40 05 C0 06 DB 4F 66 01 8F"), Decode(log), 0, true);

        ack.Level = true;
        tm.Frame(RustNet.Community.DisplayCore.HexTable.Decode("40"));
        Check.True("tm1637 nack seen", !tm.LastAck && tm.Nacks == 1);
    }
}
