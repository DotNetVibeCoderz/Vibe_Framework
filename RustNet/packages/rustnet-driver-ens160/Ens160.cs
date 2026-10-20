using RustNet.Community.Bus;

namespace RustNet.Community.Ens160;

/// <summary>
/// ScioSense ENS160 digital metal-oxide multi-gas sensor over I2C, reporting
/// the UBA air quality index (1..5), TVOC (ppb) and equivalent CO2 (ppm).
/// Address 0x53 (ADDR high, most breakouts) or 0x52 (ADDR low). Multi-byte
/// registers are little-endian.
/// </summary>
public class Ens160
{
    public const int DefaultAddress = 0x53;
    public const int AlternateAddress = 0x52;
    public const int PartIdValue = 0x0160;

    public const int RegPartId = 0x00;
    public const int RegOpMode = 0x10;
    public const int RegConfig = 0x11;
    public const int RegCommand = 0x12;
    public const int RegTempIn = 0x13;
    public const int RegRhIn = 0x15;
    public const int RegDeviceStatus = 0x20;
    public const int RegDataAqi = 0x21;
    public const int RegDataTvoc = 0x22;
    public const int RegDataEco2 = 0x24;
    public const int RegDataT = 0x30;
    public const int RegDataRh = 0x32;
    public const int RegDataMisr = 0x38;
    public const int RegGprWrite = 0x40;
    public const int RegGprRead = 0x48;

    public const int OpModeDeepSleep = 0x00;
    public const int OpModeIdle = 0x01;
    public const int OpModeStandard = 0x02;
    public const int OpModeReset = 0xF0;

    public const int CommandNop = 0x00;
    public const int CommandGetAppVersion = 0x0E;
    public const int CommandClearGpr = 0xCC;

    /// <summary>DEVICE_STATUS bits.</summary>
    public const int StatusNewGpr = 0x01;
    public const int StatusNewData = 0x02;
    public const int StatusError = 0x40;
    public const int StatusOpModeRunning = 0x80;

    /// <summary>Validity flag (DEVICE_STATUS bits 3:2).</summary>
    public const int ValidityNormal = 0;
    public const int ValidityWarmUp = 1;
    public const int ValidityInitialStartUp = 2;
    public const int ValidityInvalid = 3;

    private readonly I2cDevice _dev;

    /// <summary>Firmware version (major &lt;&lt; 16 | minor &lt;&lt; 8 | release) read by <see cref="Begin"/>.</summary>
    public int FirmwareVersion;

    /// <summary>UBA air quality index 1 (excellent) .. 5 (unhealthy) from the last read.</summary>
    public int Aqi;

    /// <summary>TVOC in ppb from the last read.</summary>
    public int TvocPpb;

    /// <summary>Equivalent CO2 in ppm from the last read.</summary>
    public int ECo2Ppm;

    /// <summary>Validity flag (0 normal .. 3 invalid) from the last read.</summary>
    public int Validity;

    public Ens160(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Ens160(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Ens160(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check PART_ID (0x0160), reset, read the firmware version and enter standard (gas sensing) mode.</summary>
    public bool Begin()
    {
        try
        {
            if (_dev.ReadUInt16LittleEndian(RegPartId) != PartIdValue)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
        _dev.WriteRegister(RegOpMode, OpModeReset);
        Timing.SleepMs(10);
        _dev.WriteRegister(RegOpMode, OpModeIdle);
        Timing.SleepMs(10);
        _dev.WriteRegister(RegCommand, CommandClearGpr);
        Timing.SleepMs(10);
        _dev.WriteRegister(RegCommand, CommandGetAppVersion);
        Timing.SleepMs(10);
        byte[] v = _dev.ReadRegisters(RegGprRead + 4, 3);
        FirmwareVersion = (v[0] << 16) | (v[1] << 8) | v[2];
        _dev.WriteRegister(RegOpMode, OpModeStandard);
        Timing.SleepMs(10);
        return true;
    }

    public void SetOperatingMode(int mode) => _dev.WriteRegister(RegOpMode, mode);

    public int ReadStatus() => _dev.ReadRegister(RegDeviceStatus);

    /// <summary>True when new AQI/TVOC/eCO2 data is waiting.</summary>
    public bool DataReady() => (ReadStatus() & StatusNewData) != 0;

    /// <summary>Read status, AQI, TVOC and eCO2 in one transfer (registers 0x20..0x25).</summary>
    public void ReadData()
    {
        byte[] d = _dev.ReadRegisters(RegDeviceStatus, 6);
        if ((d[0] & StatusError) != 0)
        {
            throw new InvalidOperationException("ens160 error");
        }
        Validity = (d[0] >> 2) & 0x03;
        Aqi = d[1] & 0x07;
        TvocPpb = d[2] | (d[3] << 8);
        ECo2Ppm = d[4] | (d[5] << 8);
    }

    /// <summary>
    /// Feed ambient conditions for compensation: TEMP_IN = (T + 273.15) · 64 (Kelvin, 1/64 K),
    /// RH_IN = RH · 512 (1/512 %).
    /// </summary>
    public void SetCompensation(double temperatureCelsius, double humidityPercent)
    {
        int t = (int)((temperatureCelsius + 273.15) * 64.0 + 0.5);
        int h = (int)(humidityPercent * 512.0 + 0.5);
        byte[] b = new byte[4];
        b[0] = (byte)t;
        b[1] = (byte)(t >> 8);
        b[2] = (byte)h;
        b[3] = (byte)(h >> 8);
        _dev.WriteRegisters(RegTempIn, b);
    }

    /// <summary>Compensation temperature the sensor is using, in °C (DATA_T).</summary>
    public double ReadCompensationTemperature() => _dev.ReadUInt16LittleEndian(RegDataT) / 64.0 - 273.15;

    /// <summary>Compensation humidity the sensor is using, in % (DATA_RH).</summary>
    public double ReadCompensationHumidity() => _dev.ReadUInt16LittleEndian(RegDataRh) / 512.0;
}
