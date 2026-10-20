using RustNet.Community.Bus;

namespace RustNet.Community.Pmsa003i;

/// <summary>
/// Plantower PMSA003I laser particulate matter sensor at I2C address 0x12.
/// Each read returns the 32-byte Plantower frame: 0x42 0x4D, a length of 28,
/// thirteen big-endian words and a 16-bit sum of the first 30 bytes.
/// Concentrations are in µg/m³; particle counts are per 0.1 L of air.
/// </summary>
public class Pmsa003i
{
    public const int DefaultAddress = 0x12;

    private readonly I2cDevice _dev;

    /// <summary>PM1.0 / PM2.5 / PM10 in µg/m³, CF=1 "standard particle" values.</summary>
    public int Pm10Standard, Pm25Standard, Pm100Standard;

    /// <summary>PM1.0 / PM2.5 / PM10 in µg/m³ under atmospheric environment (use these outdoors/indoors).</summary>
    public int Pm10Environmental, Pm25Environmental, Pm100Environmental;

    /// <summary>Particles beyond 0.3 / 0.5 / 1.0 / 2.5 / 5.0 / 10 µm in 0.1 L of air.</summary>
    public int Particles03, Particles05, Particles10, Particles25, Particles50, Particles100;

    /// <summary>Version byte and error code byte from the reserved word.</summary>
    public int Version, ErrorCode;

    public Pmsa003i(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Pmsa003i(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>True when a valid frame can be read.</summary>
    public bool Begin()
    {
        try
        {
            Read();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Read and validate one frame (start bytes, length 28, checksum). Throws on a bad frame.</summary>
    public void Read()
    {
        byte[] f = _dev.Read(32);
        if (f[0] != 0x42 || f[1] != 0x4D || Word(f, 2) != 28)
        {
            throw new InvalidOperationException("pmsa003i bad frame header");
        }
        int sum = 0;
        for (int i = 0; i < 30; i++)
        {
            sum += f[i];
        }
        if ((sum & 0xFFFF) != Word(f, 30))
        {
            throw new InvalidOperationException("pmsa003i checksum mismatch");
        }
        Pm10Standard = Word(f, 4);
        Pm25Standard = Word(f, 6);
        Pm100Standard = Word(f, 8);
        Pm10Environmental = Word(f, 10);
        Pm25Environmental = Word(f, 12);
        Pm100Environmental = Word(f, 14);
        Particles03 = Word(f, 16);
        Particles05 = Word(f, 18);
        Particles10 = Word(f, 20);
        Particles25 = Word(f, 22);
        Particles50 = Word(f, 24);
        Particles100 = Word(f, 26);
        Version = f[28];
        ErrorCode = f[29];
    }

    private static int Word(byte[] b, int i) => (b[i] << 8) | b[i + 1];

    /// <summary>
    /// US EPA AQI (2024 breakpoints) for a 24-hour PM2.5 concentration in µg/m³, by linear
    /// interpolation between breakpoints; 500 beyond the scale.
    /// </summary>
    public static int AqiFromPm25(double pm25)
    {
        // EPA: truncate the concentration to one decimal before looking it up.
        pm25 = Math.Floor(pm25 * 10.0) / 10.0;
        if (pm25 <= 9.0)
        {
            return Lerp(pm25, 0.0, 9.0, 0, 50);
        }
        if (pm25 <= 35.4)
        {
            return Lerp(pm25, 9.1, 35.4, 51, 100);
        }
        if (pm25 <= 55.4)
        {
            return Lerp(pm25, 35.5, 55.4, 101, 150);
        }
        if (pm25 <= 125.4)
        {
            return Lerp(pm25, 55.5, 125.4, 151, 200);
        }
        if (pm25 <= 225.4)
        {
            return Lerp(pm25, 125.5, 225.4, 201, 300);
        }
        if (pm25 <= 325.4)
        {
            return Lerp(pm25, 225.5, 325.4, 301, 500);
        }
        return 500;
    }

    private static int Lerp(double c, double cLow, double cHigh, int iLow, int iHigh) =>
        (int)Math.Floor((iHigh - iLow) / (cHigh - cLow) * (c - cLow) + iLow + 0.5);
}
