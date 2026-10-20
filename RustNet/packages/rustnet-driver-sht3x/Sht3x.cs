using RustNet.Community.Bus;

namespace RustNet.Community.Sht3x;

/// <summary>
/// Sensirion SHT30 / SHT31 / SHT35 (SHT3x-DIS) temperature and humidity
/// sensor over I2C. Uses single-shot measurements without clock stretching,
/// and checks the CRC-8 (poly 0x31, init 0xFF) on every word the chip sends.
/// Default address 0x44 (ADDR low); 0x45 with ADDR high.
/// </summary>
public class Sht3x
{
    public const int DefaultAddress = 0x44;
    public const int AlternateAddress = 0x45;

    /// <summary>Single shot, clock stretching disabled (datasheet table 9).</summary>
    public const int RepeatabilityHigh = 0x2400;
    public const int RepeatabilityMedium = 0x240B;
    public const int RepeatabilityLow = 0x2416;

    public const int CmdSoftReset = 0x30A2;
    public const int CmdHeaterOn = 0x306D;
    public const int CmdHeaterOff = 0x3066;
    public const int CmdReadStatus = 0xF32D;
    public const int CmdClearStatus = 0x3041;
    public const int CmdBreak = 0x3093;

    private readonly I2cDevice _dev;

    /// <summary>Single-shot command used by <see cref="Measure"/>; one of the Repeatability* constants.</summary>
    public int Repeatability = RepeatabilityHigh;

    /// <summary>Raw 16-bit temperature word from the last measurement.</summary>
    public int RawTemperature;

    /// <summary>Raw 16-bit humidity word from the last measurement.</summary>
    public int RawHumidity;

    /// <summary>Temperature in °C from the last measurement.</summary>
    public double TemperatureCelsius;

    /// <summary>Relative humidity in % (0..100) from the last measurement.</summary>
    public double HumidityPercent;

    public Sht3x(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Sht3x(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Sht3x(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Soft-reset the sensor and confirm it answers with a CRC-valid status word.</summary>
    public bool Begin()
    {
        try
        {
            _dev.WriteCommand16(CmdSoftReset);
            Timing.SleepMs(2);
            ReadStatus();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Read the 16-bit status register (bit 13 = heater on, bit 4 = reset detected, bit 0 = write CRC error).</summary>
    public int ReadStatus()
    {
        _dev.WriteCommand16(CmdReadStatus);
        byte[] r = _dev.Read(3);
        return Word(r, 0);
    }

    public void ClearStatus() => _dev.WriteCommand16(CmdClearStatus);

    /// <summary>Switch the internal heater on or off (for plausibility checks / dew removal).</summary>
    public void SetHeater(bool on) => _dev.WriteCommand16(on ? CmdHeaterOn : CmdHeaterOff);

    /// <summary>True when the status register reports the heater running.</summary>
    public bool IsHeaterOn() => (ReadStatus() & 0x2000) != 0;

    /// <summary>Run one single-shot measurement (max 15.5 ms at high repeatability) and update the readings.</summary>
    public void Measure()
    {
        _dev.WriteCommand16(Repeatability);
        Timing.SleepMs(Repeatability == RepeatabilityHigh ? 16 : (Repeatability == RepeatabilityMedium ? 7 : 5));
        byte[] r = _dev.Read(6);
        RawTemperature = Word(r, 0);
        RawHumidity = Word(r, 3);
        TemperatureCelsius = ConvertTemperature(RawTemperature);
        HumidityPercent = ConvertHumidity(RawHumidity);
    }

    /// <summary>Datasheet 4.13: T = -45 + 175 · S_T / (2^16 - 1), in °C.</summary>
    public static double ConvertTemperature(int raw) => -45.0 + 175.0 * raw / 65535.0;

    /// <summary>Datasheet 4.13: RH = 100 · S_RH / (2^16 - 1), in %.</summary>
    public static double ConvertHumidity(int raw) => 100.0 * raw / 65535.0;

    /// <summary>Datasheet 4.12 CRC-8: polynomial 0x31, init 0xFF, no reflection. 0xBEEF gives 0x92.</summary>
    public static int Crc(int msb, int lsb)
    {
        byte[] b = new byte[2];
        b[0] = (byte)msb;
        b[1] = (byte)lsb;
        return BitOps.Crc8Sensirion(b, 0, 2);
    }

    private static int Word(byte[] r, int offset)
    {
        if (BitOps.Crc8Sensirion(r, offset, 2) != r[offset + 2])
        {
            throw new InvalidOperationException("sht3x crc mismatch");
        }
        return (r[offset] << 8) | r[offset + 1];
    }
}
