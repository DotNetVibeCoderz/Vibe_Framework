using RustNet.Community.Bus;

namespace RustNet.Community.Tmp102;

/// <summary>
/// Texas Instruments TMP102 digital temperature sensor over I2C, 0.0625 °C
/// per LSB. Address 0x48 (ADD0 to GND), 0x49 (V+), 0x4A (SDA), 0x4B (SCL).
/// Normal mode gives a 12-bit left-justified result; extended mode (EM) a
/// 13-bit one with bit 0 of the low byte set, reaching 150 °C.
/// </summary>
public class Tmp102
{
    public const int DefaultAddress = 0x48;

    public const int RegTemperature = 0x00;
    public const int RegConfiguration = 0x01;
    public const int RegTLow = 0x02;
    public const int RegTHigh = 0x03;

    /// <summary>Configuration register reset value.</summary>
    public const int DefaultConfiguration = 0x60A0;

    /// <summary>Conversion rate codes (CR1:CR0): 0.25, 1, 4 (default), 8 Hz.</summary>
    public const int Rate025Hz = 0;
    public const int Rate1Hz = 1;
    public const int Rate4Hz = 2;
    public const int Rate8Hz = 3;

    private readonly I2cDevice _dev;

    public Tmp102(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Tmp102(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Tmp102(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>True when the device answers and its read-only resolution bits (R1:R0 = 11) are set.</summary>
    public bool Begin()
    {
        try
        {
            return (ReadConfiguration() & 0x6000) == 0x6000;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public int ReadConfiguration() => _dev.ReadUInt16BigEndian(RegConfiguration);

    public void WriteConfiguration(int value) => WriteRegister16(RegConfiguration, value);

    /// <summary>Temperature in °C (handles both normal and extended mode via bit 0).</summary>
    public double ReadTemperatureCelsius() => Decode(_dev.ReadUInt16BigEndian(RegTemperature));

    /// <summary>Decode a temperature/limit register: EM bit (bit 0) set means 13-bit, else 12-bit; 0.0625 °C/LSB.</summary>
    public static double Decode(int register16)
    {
        int s = BitOps.ToInt16(register16);
        int v = (register16 & 0x01) != 0 ? s >> 3 : s >> 4;
        return v * 0.0625;
    }

    /// <summary>Encode °C into a 12-bit (or 13-bit when extended) limit register.</summary>
    public static int Encode(double celsius, bool extended)
    {
        int v = (int)Math.Floor(celsius / 0.0625 + 0.5);
        return extended ? ((v << 3) | 0x01) & 0xFFFF : (v << 4) & 0xFFF0;
    }

    /// <summary>Enable/disable extended (13-bit, up to 150 °C) mode (EM, bit 4).</summary>
    public void SetExtendedMode(bool extended)
    {
        int c = ReadConfiguration();
        WriteConfiguration(extended ? (c | 0x0010) : (c & ~0x0010));
    }

    /// <summary>Conversion rate code (bits 7:6).</summary>
    public void SetConversionRate(int rate)
    {
        int c = ReadConfiguration();
        WriteConfiguration((c & ~0x00C0) | ((rate & 3) << 6));
    }

    /// <summary>Shutdown mode (SD, bit 8): no conversions, ~0.5 µA.</summary>
    public void SetShutdown(bool shutdown)
    {
        int c = ReadConfiguration();
        WriteConfiguration(shutdown ? (c | 0x0100) : (c & ~0x0100));
    }

    /// <summary>In shutdown mode: start one conversion (OS, bit 15) and read it after ~26 ms.</summary>
    public double OneShotCelsius()
    {
        int c = ReadConfiguration();
        WriteConfiguration(c | 0x8100);
        Timing.SleepMs(30);
        return ReadTemperatureCelsius();
    }

    /// <summary>Alert limits in °C (THIGH asserts, TLOW releases in comparator mode).</summary>
    public void SetLimits(double low, double high)
    {
        bool extended = (ReadConfiguration() & 0x0010) != 0;
        WriteRegister16(RegTLow, Encode(low, extended));
        WriteRegister16(RegTHigh, Encode(high, extended));
    }

    /// <summary>The AL bit (bit 5): reflects the alert state.</summary>
    public bool AlertActive(bool polarityHigh)
    {
        bool al = (ReadConfiguration() & 0x0020) != 0;
        return polarityHigh ? al : !al;
    }

    private void WriteRegister16(int register, int value)
    {
        byte[] b = new byte[3];
        b[0] = (byte)register;
        b[1] = (byte)(value >> 8);
        b[2] = (byte)value;
        _dev.Write(b);
    }
}
