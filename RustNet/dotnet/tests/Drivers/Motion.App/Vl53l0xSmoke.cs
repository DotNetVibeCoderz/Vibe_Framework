using RustNet.Community.Bus;
using RustNet.Community.Vl53l0x;

namespace DriversApp;

/// <summary>
/// A VL53L0X register model with its 0xFF page select, holding the values
/// ST's default tuning leaves behind, the NVM SPAD info and a result.
/// </summary>
public class SimVl53l0x : I2cDevice
{
    public byte[][] Pages = new byte[256][];
    public int Page;
    public int Pointer;
    public List<string> Log = new List<string>();

    public SimVl53l0x()
    {
        Address = 0x29;
        for (int i = 0; i < 256; i++)
        {
            Pages[i] = new byte[256];
        }
    }

    public byte[] P(int page) => Pages[page];

    public override void Write(byte[] data)
    {
        Pointer = data[0];
        if (data.Length == 1)
        {
            return;
        }
        if (Pointer == 0xFF && data.Length == 2)
        {
            Page = data[1];
            return;
        }
        for (int i = 1; i < data.Length; i++)
        {
            Pages[Page][(Pointer + i - 1) & 0xFF] = data[i];
        }
        if (data.Length == 2)
        {
            Log.Add($"{Page}:{Pointer}={data[1]}");
        }
        // NVM read handshake: the strobe register reads non-zero once the read is done.
        if (Page == 7 && Pointer == 0x83 && data.Length == 2 && data[1] == 0x00)
        {
            Pages[7][0x83] = 0x10;
        }
        // SYSRANGE_START's start bit clears itself when the measurement begins.
        if (Page == 0 && Pointer == 0x00)
        {
            Pages[0][0x00] = (byte)(data[1] & 0xFE);
        }
    }

    public override byte[] Read(int length)
    {
        byte[] r = new byte[length];
        for (int i = 0; i < length; i++)
        {
            r[i] = Pages[Page][(Pointer + i) & 0xFF];
        }
        return r;
    }
}

public static class Vl53l0xSmoke
{
    public static void Run()
    {
        SimVl53l0x sim = new SimVl53l0x();
        sim.P(0)[0xC0] = 0xEE;
        sim.P(1)[0x91] = 0x3C; // stop variable
        sim.P(7)[0x92] = 0x84; // aperture SPADs, count 4
        for (int i = 0; i < 6; i++)
        {
            sim.P(0)[0xB0 + i] = 0xFF;
        }
        sim.P(0)[0x13] = 0x07; // interrupt status: sample ready
        sim.P(0)[0x1E] = 0x01;
        sim.P(0)[0x1F] = 0x2C;

        Vl53l0x tof = new Vl53l0x(sim);
        Check.True("vl53l0x begin", tof.Begin(true));
        Check.Equal("vl53l0x spad count", 4, tof.SpadCount);
        Check.True("vl53l0x aperture", tof.SpadTypeIsAperture);
        // Aperture SPADs start at 12; only 4 stay enabled: bits 12..15.
        Check.Equal("vl53l0x spad map 0", 0x00, sim.P(0)[0xB0]);
        Check.Equal("vl53l0x spad map 1", 0xF0, sim.P(0)[0xB1]);
        Check.Equal("vl53l0x spad map 2", 0x00, sim.P(0)[0xB2]);
        Check.Equal("vl53l0x 2v8 io", 0x01, sim.P(0)[0x89] & 0x01);
        // Signal rate limit 0.25 MCPS in Q9.7 = 32.
        Check.Equal("vl53l0x signal limit", 32, (sim.P(0)[0x44] << 8) | sim.P(0)[0x45]);
        // Tuning settings landed on the right pages (0x46 differs per page).
        Check.Equal("vl53l0x tuning p0 0x46", 0x25, sim.P(0)[0x46]);
        Check.Equal("vl53l0x tuning p1 0x46", 0x05, sim.P(1)[0x46]);
        // With the default tuning, ST's budget is ~33 ms (33849 µs computed from
        // the VCSEL periods 14/10 PCLK and the default timeouts), and keeping it
        // with MSRC/TCC disabled re-encodes the final range timeout as 0x0294.
        Check.Equal("vl53l0x timing budget", 33849, tof.TimingBudgetMicroseconds);
        Check.Equal("vl53l0x final timeout", 0x0294, (sim.P(0)[0x71] << 8) | sim.P(0)[0x72]);
        Check.Equal("vl53l0x sequence", 0xE8, sim.P(0)[0x01]);
        Check.True("vl53l0x vhv calibration", sim.Log.Contains("0:0=65"));
        Check.True("vl53l0x phase calibration", sim.Log.Contains("0:0=1"));

        // Decode/encode against the datasheet-style encoding: (LSB << MSB) + 1.
        Check.Equal("vl53l0x decode timeout", 0x96 + 1, Vl53l0x.DecodeTimeout(0x0096));
        Check.Equal("vl53l0x decode timeout 2", (0xFE << 1) + 1, Vl53l0x.DecodeTimeout(0x01FE));
        Check.Equal("vl53l0x encode roundtrip", 0x01FE, Vl53l0x.EncodeTimeout(Vl53l0x.DecodeTimeout(0x01FE)));
        Check.Equal("vl53l0x macro period 14", 53384, Vl53l0x.CalcMacroPeriod(14));

        sim.Log.Clear();
        int mm = tof.ReadRangeSingleMillimeters();
        Check.Equal("vl53l0x range", 300, mm);
        Check.True("vl53l0x stop variable restored", sim.Log.Contains("1:145=60"));
        Check.True("vl53l0x interrupt cleared", sim.Log.Contains("0:11=1"));
        Check.True("vl53l0x not timed out", !tof.TimedOut);

        SimVl53l0x blank = new SimVl53l0x();
        Check.True("vl53l0x absent", !new Vl53l0x(blank).Begin(false));
    }
}
