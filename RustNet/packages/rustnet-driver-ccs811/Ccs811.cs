using RustNet.Community.Bus;

namespace RustNet.Community.Ccs811;

/// <summary>
/// ScioSense (formerly ams) CCS811 metal-oxide gas sensor over I2C,
/// reporting equivalent CO2 (ppm) and total VOC (ppb). Address 0x5A
/// (ADDR low) or 0x5B (ADDR high). WAKE must be held low while talking to it.
/// Allow 20 minutes of run-in after power-up (48 h for a new sensor).
/// </summary>
public class Ccs811
{
    public const int DefaultAddress = 0x5A;
    public const int AlternateAddress = 0x5B;
    public const int HardwareIdValue = 0x81;

    public const int RegStatus = 0x00;
    public const int RegMeasMode = 0x01;
    public const int RegAlgResultData = 0x02;
    public const int RegRawData = 0x03;
    public const int RegEnvData = 0x05;
    public const int RegThresholds = 0x10;
    public const int RegBaseline = 0x11;
    public const int RegHwId = 0x20;
    public const int RegHwVersion = 0x21;
    public const int RegFwBootVersion = 0x23;
    public const int RegFwAppVersion = 0x24;
    public const int RegErrorId = 0xE0;
    public const int RegAppStart = 0xF4;
    public const int RegSwReset = 0xFF;

    /// <summary>STATUS bits.</summary>
    public const int StatusError = 0x01;
    public const int StatusDataReady = 0x08;
    public const int StatusAppValid = 0x10;
    public const int StatusFwMode = 0x80;

    /// <summary>Drive modes (MEAS_MODE bits 6:4).</summary>
    public const int ModeIdle = 0;
    public const int ModeEverySecond = 1;
    public const int ModeEvery10Seconds = 2;
    public const int ModeEvery60Seconds = 3;
    /// <summary>Raw data only, every 250 ms (no eCO2/TVOC).</summary>
    public const int ModeRaw250ms = 4;

    private readonly I2cDevice _dev;

    /// <summary>Equivalent CO2 in ppm (400..32768) from the last read.</summary>
    public int ECo2Ppm;

    /// <summary>Total VOC in ppb (0..32768) from the last read.</summary>
    public int TvocPpb;

    /// <summary>Sensor current in µA (0..63) from the last read.</summary>
    public int CurrentMicroamps;

    /// <summary>10-bit raw ADC reading (1.65 V full scale) from the last read.</summary>
    public int RawAdc;

    /// <summary>ERROR_ID of the last read (0 when no error).</summary>
    public int ErrorId;

    public Ccs811(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Ccs811(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Ccs811(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>
    /// Check HW_ID (0x81), reset, start the application firmware and select a drive mode.
    /// False when the chip is missing, has no valid application, or does not enter application mode.
    /// </summary>
    public bool Begin(int driveMode)
    {
        if (_dev.ReadRegister(RegHwId) != HardwareIdValue)
        {
            return false;
        }
        byte[] reset = new byte[4];
        reset[0] = 0x11;
        reset[1] = 0xE5;
        reset[2] = 0x72;
        reset[3] = 0x8A;
        _dev.WriteRegisters(RegSwReset, reset);
        Timing.SleepMs(2);
        if ((_dev.ReadRegister(RegStatus) & StatusAppValid) == 0)
        {
            return false;
        }
        // APP_START is a register address write with no data.
        _dev.WriteByte(RegAppStart);
        Timing.SleepMs(1);
        if ((_dev.ReadRegister(RegStatus) & StatusFwMode) == 0)
        {
            return false;
        }
        SetDriveMode(driveMode);
        return true;
    }

    /// <summary>Set MEAS_MODE: drive mode (0..4), with the data-ready interrupt optionally enabled.</summary>
    public void SetDriveMode(int mode, bool interrupt = false)
    {
        _dev.WriteRegister(RegMeasMode, ((mode & 7) << 4) | (interrupt ? 0x08 : 0));
    }

    public int ReadStatus() => _dev.ReadRegister(RegStatus);

    /// <summary>True when a new eCO2/TVOC result is waiting.</summary>
    public bool DataReady() => (ReadStatus() & StatusDataReady) != 0;

    /// <summary>Read ALG_RESULT_DATA (eCO2, TVOC, status, error, raw). Throws when the chip flags an error.</summary>
    public void ReadData()
    {
        byte[] d = _dev.ReadRegisters(RegAlgResultData, 8);
        ECo2Ppm = (d[0] << 8) | d[1];
        TvocPpb = (d[2] << 8) | d[3];
        ErrorId = d[5];
        CurrentMicroamps = d[6] >> 2;
        RawAdc = ((d[6] & 0x03) << 8) | d[7];
        if ((d[4] & StatusError) != 0)
        {
            throw new InvalidOperationException($"ccs811 error {ErrorId}");
        }
    }

    /// <summary>
    /// Write ENV_DATA for compensation: humidity in % and temperature in °C, both in 1/512 units
    /// (temperature offset by +25 °C, so 0x6400 means 25 °C).
    /// </summary>
    public void SetEnvironmentalData(double humidityPercent, double temperatureCelsius)
    {
        int h = (int)(humidityPercent * 512.0 + 0.5);
        int t = (int)((temperatureCelsius + 25.0) * 512.0 + 0.5);
        if (h < 0)
        {
            h = 0;
        }
        if (t < 0)
        {
            t = 0;
        }
        byte[] b = new byte[4];
        b[0] = (byte)(h >> 8);
        b[1] = (byte)h;
        b[2] = (byte)(t >> 8);
        b[3] = (byte)t;
        _dev.WriteRegisters(RegEnvData, b);
    }

    /// <summary>The 16-bit algorithm baseline (opaque; save it and restore it after a restart).</summary>
    public int ReadBaseline() => _dev.ReadUInt16BigEndian(RegBaseline);

    public void WriteBaseline(int baseline)
    {
        byte[] b = new byte[2];
        b[0] = (byte)(baseline >> 8);
        b[1] = (byte)baseline;
        _dev.WriteRegisters(RegBaseline, b);
    }

    /// <summary>Interrupt thresholds in ppm eCO2 (low-to-medium, medium-to-high) and hysteresis.</summary>
    public void SetThresholds(int lowToMedium, int mediumToHigh, int hysteresis)
    {
        byte[] b = new byte[5];
        b[0] = (byte)(lowToMedium >> 8);
        b[1] = (byte)lowToMedium;
        b[2] = (byte)(mediumToHigh >> 8);
        b[3] = (byte)mediumToHigh;
        b[4] = (byte)hysteresis;
        _dev.WriteRegisters(RegThresholds, b);
    }

    /// <summary>FW_APP_VERSION as read: high byte = major (bits 7:4) and minor (bits 3:0), low byte = trivial.</summary>
    public int ReadFirmwareAppVersion() => _dev.ReadUInt16BigEndian(RegFwAppVersion);

    public int ReadErrorId() => _dev.ReadRegister(RegErrorId);
}
