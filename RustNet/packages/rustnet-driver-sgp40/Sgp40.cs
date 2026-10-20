using RustNet.Community.Bus;

namespace RustNet.Community.Sgp40;

/// <summary>
/// Sensirion SGP40 metal-oxide VOC sensor at I2C address 0x59. Returns the
/// raw SRAW_VOC ticks (proportional to the logarithm of the MOX resistance).
/// Turn the ticks into Sensirion's 1..500 VOC Index with their open-source
/// gas index algorithm, sampling once per second. Every word is CRC-8
/// protected (poly 0x31, init 0xFF).
/// </summary>
public class Sgp40
{
    public const int DefaultAddress = 0x59;

    public const int CmdMeasureRaw = 0x260F;
    public const int CmdSelfTest = 0x280E;
    public const int CmdHeaterOff = 0x3615;
    public const int CmdSerialNumber = 0x3682;

    /// <summary>Self-test result when all tests passed.</summary>
    public const int SelfTestPassed = 0xD400;

    private readonly I2cDevice _dev;

    /// <summary>48-bit serial number read by <see cref="Begin"/>.</summary>
    public long SerialNumber;

    public Sgp40(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Sgp40(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Read the serial number; false when nothing CRC-valid answers.</summary>
    public bool Begin()
    {
        try
        {
            _dev.WriteCommand16(CmdSerialNumber);
            Timing.SleepMs(1);
            byte[] r = _dev.Read(9);
            SerialNumber = ((long)Word(r, 0) << 32) | ((long)Word(r, 3) << 16) | (long)Word(r, 6);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Raw VOC ticks without compensation (sends the datasheet defaults: 50 %RH, 25 °C).</summary>
    public int MeasureRaw() => MeasureRawTicks(0x8000, 0x6666);

    /// <summary>Raw VOC ticks compensated for the given relative humidity (%) and temperature (°C).</summary>
    public int MeasureRaw(double humidityPercent, double temperatureCelsius) =>
        MeasureRawTicks(HumidityTicks(humidityPercent), TemperatureTicks(temperatureCelsius));

    /// <summary>sgp40_measure_raw_signal: command, RH ticks + CRC, T ticks + CRC; result after 30 ms.</summary>
    public int MeasureRawTicks(int humidityTicks, int temperatureTicks)
    {
        byte[] b = new byte[8];
        b[0] = 0x26;
        b[1] = 0x0F;
        PutWord(b, 2, humidityTicks);
        PutWord(b, 5, temperatureTicks);
        _dev.Write(b);
        Timing.SleepMs(30);
        return Word(_dev.Read(3), 0);
    }

    /// <summary>RH ticks = RH% · 65535 / 100, rounded and clamped (50 % gives 0x8000).</summary>
    public static int HumidityTicks(double humidityPercent) => Clamp((int)(humidityPercent * 65535.0 / 100.0 + 0.5));

    /// <summary>T ticks = (T + 45) · 65535 / 175, rounded and clamped (25 °C gives 0x6666).</summary>
    public static int TemperatureTicks(double temperatureCelsius) =>
        Clamp((int)((temperatureCelsius + 45.0) * 65535.0 / 175.0 + 0.5));

    private static int Clamp(int v) => v < 0 ? 0 : (v > 65535 ? 65535 : v);

    /// <summary>Run the built-in self test (320 ms). True when it reports 0xD400.</summary>
    public bool SelfTest()
    {
        _dev.WriteCommand16(CmdSelfTest);
        Timing.SleepMs(320);
        return Word(_dev.Read(3), 0) == SelfTestPassed;
    }

    /// <summary>Switch the hotplate off and return to idle.</summary>
    public void HeaterOff() => _dev.WriteCommand16(CmdHeaterOff);

    private static void PutWord(byte[] b, int offset, int value)
    {
        b[offset] = (byte)(value >> 8);
        b[offset + 1] = (byte)value;
        b[offset + 2] = (byte)BitOps.Crc8Sensirion(b, offset, 2);
    }

    private static int Word(byte[] r, int offset)
    {
        if (BitOps.Crc8Sensirion(r, offset, 2) != r[offset + 2])
        {
            throw new InvalidOperationException("sgp40 crc mismatch");
        }
        return (r[offset] << 8) | r[offset + 1];
    }
}
