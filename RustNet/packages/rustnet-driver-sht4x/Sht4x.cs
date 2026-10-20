using RustNet.Community.Bus;

namespace RustNet.Community.Sht4x;

/// <summary>
/// Sensirion SHT40 / SHT41 / SHT45 (SHT4x) temperature and humidity sensor
/// over I2C. Single-byte commands; every 16-bit word in a reply carries a
/// CRC-8 (poly 0x31, init 0xFF) which is checked.
/// Address 0x44 for the -AD1B parts (0x45 for -BD1B, 0x46 for -CD1B).
/// </summary>
public class Sht4x
{
    public const int DefaultAddress = 0x44;

    /// <summary>Measure T and RH with high precision (max 8.3 ms).</summary>
    public const int PrecisionHigh = 0xFD;
    /// <summary>Medium precision (max 4.5 ms).</summary>
    public const int PrecisionMedium = 0xF6;
    /// <summary>Lowest precision (max 1.6 ms).</summary>
    public const int PrecisionLow = 0xE0;

    public const int CmdReadSerial = 0x89;
    public const int CmdSoftReset = 0x94;

    // Heater pulses: activate the heater, then measure at high precision.
    public const int Heater200mW1s = 0x39;
    public const int Heater200mW100ms = 0x32;
    public const int Heater110mW1s = 0x2F;
    public const int Heater110mW100ms = 0x24;
    public const int Heater20mW1s = 0x1E;
    public const int Heater20mW100ms = 0x15;

    private readonly I2cDevice _dev;

    /// <summary>Measurement command used by <see cref="Measure"/>; one of the Precision* constants.</summary>
    public int Precision = PrecisionHigh;

    /// <summary>32-bit serial number read by <see cref="Begin"/>.</summary>
    public long SerialNumber;

    public int RawTemperature;
    public int RawHumidity;

    /// <summary>Temperature in °C from the last measurement.</summary>
    public double TemperatureCelsius;

    /// <summary>Relative humidity in %, clipped to 0..100 as the datasheet recommends.</summary>
    public double HumidityPercent;

    public Sht4x(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Sht4x(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Sht4x(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Soft-reset and read the serial number. Returns false when nothing CRC-valid answers.</summary>
    public bool Begin()
    {
        try
        {
            _dev.WriteByte(CmdSoftReset);
            Timing.SleepMs(1);
            SerialNumber = ReadSerial();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Read the 32-bit serial number (two CRC-checked words).</summary>
    public long ReadSerial()
    {
        _dev.WriteByte(CmdReadSerial);
        Timing.SleepMs(1);
        byte[] r = _dev.Read(6);
        long hi = Word(r, 0);
        long lo = Word(r, 3);
        return (hi << 16) | lo;
    }

    /// <summary>Measure with the configured <see cref="Precision"/>.</summary>
    public void Measure() => Run(Precision, Precision == PrecisionHigh ? 9 : (Precision == PrecisionMedium ? 5 : 2));

    /// <summary>Fire one heater pulse (Heater* constant) and read the high-precision measurement taken at its end.</summary>
    public void MeasureWithHeater(int heaterCommand)
    {
        bool longPulse = heaterCommand == Heater200mW1s || heaterCommand == Heater110mW1s || heaterCommand == Heater20mW1s;
        Run(heaterCommand, longPulse ? 1100 : 110);
    }

    private void Run(int command, int waitMs)
    {
        _dev.WriteByte(command);
        Timing.SleepMs(waitMs);
        byte[] r = _dev.Read(6);
        RawTemperature = Word(r, 0);
        RawHumidity = Word(r, 3);
        TemperatureCelsius = ConvertTemperature(RawTemperature);
        HumidityPercent = ConvertHumidity(RawHumidity);
    }

    /// <summary>Datasheet 4.6: T = -45 + 175 · S_T / 65535, in °C.</summary>
    public static double ConvertTemperature(int raw) => -45.0 + 175.0 * raw / 65535.0;

    /// <summary>Datasheet 4.6: RH = -6 + 125 · S_RH / 65535, in %, clipped to 0..100.</summary>
    public static double ConvertHumidity(int raw)
    {
        double rh = -6.0 + 125.0 * raw / 65535.0;
        if (rh < 0)
        {
            rh = 0;
        }
        if (rh > 100)
        {
            rh = 100;
        }
        return rh;
    }

    private static int Word(byte[] r, int offset)
    {
        if (BitOps.Crc8Sensirion(r, offset, 2) != r[offset + 2])
        {
            throw new InvalidOperationException("sht4x crc mismatch");
        }
        return (r[offset] << 8) | r[offset + 1];
    }
}
