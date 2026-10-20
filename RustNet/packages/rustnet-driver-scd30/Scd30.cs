using RustNet.Community.Bus;

namespace RustNet.Community.Scd30;

/// <summary>
/// Sensirion SCD30 NDIR CO2, temperature and humidity module at I2C address
/// 0x61 (needs clock stretching up to 150 ms and at most 100 kHz). Results
/// are big-endian IEEE-754 floats split into two CRC-protected words.
/// </summary>
public class Scd30
{
    public const int DefaultAddress = 0x61;

    public const int CmdStartContinuous = 0x0010;
    public const int CmdStopContinuous = 0x0104;
    public const int CmdMeasurementInterval = 0x4600;
    public const int CmdDataReady = 0x0202;
    public const int CmdReadMeasurement = 0x0300;
    public const int CmdAutomaticSelfCalibration = 0x5306;
    public const int CmdForcedRecalibration = 0x5204;
    public const int CmdTemperatureOffset = 0x5403;
    public const int CmdAltitude = 0x5102;
    public const int CmdFirmwareVersion = 0xD100;
    public const int CmdSoftReset = 0xD304;

    private readonly I2cDevice _dev;

    /// <summary>Firmware version (major &lt;&lt; 8 | minor) read by <see cref="Begin"/>.</summary>
    public int FirmwareVersion;

    /// <summary>CO2 concentration in ppm from the last read.</summary>
    public double Co2Ppm;

    /// <summary>Temperature in °C from the last read.</summary>
    public double TemperatureCelsius;

    /// <summary>Relative humidity in % from the last read.</summary>
    public double HumidityPercent;

    public Scd30(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Scd30(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Read the firmware version; false when nothing CRC-valid answers.</summary>
    public bool Begin()
    {
        try
        {
            FirmwareVersion = ReadWord(CmdFirmwareVersion);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Start continuous measurement with ambient pressure compensation in mbar (700..1400; 0 = off).</summary>
    public void StartContinuous(int ambientPressureMillibar) => WriteCommand(CmdStartContinuous, ambientPressureMillibar);

    public void StopContinuous() => _dev.WriteCommand16(CmdStopContinuous);

    /// <summary>Measurement interval in seconds (2..1800).</summary>
    public void SetMeasurementInterval(int seconds) => WriteCommand(CmdMeasurementInterval, seconds);

    public int GetMeasurementInterval() => ReadWord(CmdMeasurementInterval);

    public bool DataReady() => ReadWord(CmdDataReady) == 1;

    /// <summary>Read CO2 (ppm), temperature (°C) and humidity (%) — three floats, six CRC-checked words.</summary>
    public void ReadMeasurement()
    {
        _dev.WriteCommand16(CmdReadMeasurement);
        Timing.SleepMs(3);
        byte[] r = _dev.Read(18);
        Co2Ppm = FloatAt(r, 0);
        TemperatureCelsius = FloatAt(r, 6);
        HumidityPercent = FloatAt(r, 12);
    }

    public void SetAutomaticSelfCalibration(bool enabled) => WriteCommand(CmdAutomaticSelfCalibration, enabled ? 1 : 0);

    public bool GetAutomaticSelfCalibration() => ReadWord(CmdAutomaticSelfCalibration) == 1;

    /// <summary>Forced recalibration to a known reference CO2 concentration (400..2000 ppm).</summary>
    public void SetForcedRecalibration(int referencePpm) => WriteCommand(CmdForcedRecalibration, referencePpm);

    /// <summary>Temperature offset in °C (stored in 0.01 K steps, positive values only).</summary>
    public void SetTemperatureOffset(double celsius) => WriteCommand(CmdTemperatureOffset, (int)(celsius * 100.0 + 0.5));

    public double GetTemperatureOffset() => ReadWord(CmdTemperatureOffset) / 100.0;

    /// <summary>Altitude above sea level in m, for compensation when no pressure is given.</summary>
    public void SetAltitude(int meters) => WriteCommand(CmdAltitude, meters);

    public void SoftReset() => _dev.WriteCommand16(CmdSoftReset);

    private void WriteCommand(int command, int argument)
    {
        byte[] b = new byte[5];
        b[0] = (byte)(command >> 8);
        b[1] = (byte)command;
        b[2] = (byte)(argument >> 8);
        b[3] = (byte)argument;
        b[4] = (byte)BitOps.Crc8Sensirion(b, 2, 2);
        _dev.Write(b);
    }

    private int ReadWord(int command)
    {
        _dev.WriteCommand16(command);
        Timing.SleepMs(3);
        return Word(_dev.Read(3), 0);
    }

    private static int Word(byte[] r, int offset)
    {
        if (BitOps.Crc8Sensirion(r, offset, 2) != r[offset + 2])
        {
            throw new InvalidOperationException("scd30 crc mismatch");
        }
        return (r[offset] << 8) | r[offset + 1];
    }

    private static double FloatAt(byte[] r, int offset)
    {
        long hi = Word(r, offset);
        long lo = Word(r, offset + 3);
        return SingleFromBits((hi << 16) | lo);
    }

    /// <summary>Decode IEEE-754 single-precision bits (as an unsigned 32-bit value) without BitConverter.</summary>
    public static double SingleFromBits(long bits)
    {
        int sign = (int)((bits >> 31) & 1);
        int exponent = (int)((bits >> 23) & 0xFF);
        long mantissa = bits & 0x7FFFFF;
        double v;
        if (exponent == 0)
        {
            v = mantissa * Math.Pow(2.0, -149);
        }
        else if (exponent == 255)
        {
            v = mantissa == 0 ? double.PositiveInfinity : double.NaN;
        }
        else
        {
            v = (mantissa + 8388608L) * Math.Pow(2.0, exponent - 150);
        }
        return sign == 1 ? -v : v;
    }
}
