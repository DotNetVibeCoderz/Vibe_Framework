using RustNet.Community.Bus;

namespace RustNet.Community.Scd4x;

/// <summary>
/// Sensirion SCD40 / SCD41 photoacoustic CO2 sensor (with temperature and
/// humidity) at I2C address 0x62. Commands are 16-bit; arguments and
/// replies are 16-bit words each followed by a CRC-8 (poly 0x31, init 0xFF).
/// Single-shot measurement is available on the SCD41 only.
/// </summary>
public class Scd4x
{
    public const int DefaultAddress = 0x62;

    public const int CmdStartPeriodic = 0x21B1;
    public const int CmdStartLowPowerPeriodic = 0x21AC;
    public const int CmdReadMeasurement = 0xEC05;
    public const int CmdStopPeriodic = 0x3F86;
    public const int CmdSetTemperatureOffset = 0x241D;
    public const int CmdGetTemperatureOffset = 0x2318;
    public const int CmdSetSensorAltitude = 0x2427;
    public const int CmdGetSensorAltitude = 0x2322;
    public const int CmdSetAmbientPressure = 0xE000;
    public const int CmdForcedRecalibration = 0x362F;
    public const int CmdSetAscEnabled = 0x2416;
    public const int CmdGetAscEnabled = 0x2313;
    public const int CmdGetDataReady = 0xE4B8;
    public const int CmdPersistSettings = 0x3615;
    public const int CmdGetSerialNumber = 0x3682;
    public const int CmdSelfTest = 0x3639;
    public const int CmdFactoryReset = 0x3632;
    public const int CmdReinit = 0x3646;
    public const int CmdMeasureSingleShot = 0x219D;
    public const int CmdMeasureSingleShotRhtOnly = 0x2196;
    public const int CmdPowerDown = 0x36E0;
    public const int CmdWakeUp = 0x36F6;

    private readonly I2cDevice _dev;

    /// <summary>48-bit serial number read by <see cref="Begin"/>.</summary>
    public long SerialNumber;

    /// <summary>CO2 in ppm from the last read.</summary>
    public int Co2Ppm;

    /// <summary>Temperature in °C from the last read (T = -45 + 175 · word / 65535).</summary>
    public double TemperatureCelsius;

    /// <summary>Relative humidity in % from the last read (RH = 100 · word / 65535).</summary>
    public double HumidityPercent;

    public Scd4x(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Scd4x(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Stop any periodic measurement (500 ms), then read the serial number. False when nothing CRC-valid answers.</summary>
    public bool Begin()
    {
        try
        {
            _dev.WriteCommand16(CmdStopPeriodic);
            Timing.SleepMs(500);
            byte[] r = ReadWords(CmdGetSerialNumber, 3, 1);
            SerialNumber = ((long)Word(r, 0) << 32) | ((long)Word(r, 3) << 16) | (long)Word(r, 6);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Start periodic measurement: a new result every 5 s (or 30 s in low-power mode).</summary>
    public void StartPeriodic(bool lowPower = false) => _dev.WriteCommand16(lowPower ? CmdStartLowPowerPeriodic : CmdStartPeriodic);

    /// <summary>Stop periodic measurement; the sensor accepts new commands after 500 ms.</summary>
    public void StopPeriodic()
    {
        _dev.WriteCommand16(CmdStopPeriodic);
        Timing.SleepMs(500);
    }

    /// <summary>True when a measurement is waiting (lower 11 bits of the status word non-zero).</summary>
    public bool DataReady() => (Word(ReadWords(CmdGetDataReady, 1, 1), 0) & 0x07FF) != 0;

    /// <summary>Read CO2 (ppm), temperature (°C) and humidity (%).</summary>
    public void ReadMeasurement()
    {
        byte[] r = ReadWords(CmdReadMeasurement, 3, 1);
        Co2Ppm = Word(r, 0);
        TemperatureCelsius = -45.0 + 175.0 * Word(r, 3) / 65535.0;
        HumidityPercent = 100.0 * Word(r, 6) / 65535.0;
    }

    /// <summary>SCD41 only: one on-demand measurement (5 s), then <see cref="ReadMeasurement"/> runs.</summary>
    public void MeasureSingleShot()
    {
        _dev.WriteCommand16(CmdMeasureSingleShot);
        Timing.SleepMs(5000);
        ReadMeasurement();
    }

    /// <summary>SCD41 only: temperature and humidity only (50 ms); CO2 reads as 0.</summary>
    public void MeasureSingleShotRhtOnly()
    {
        _dev.WriteCommand16(CmdMeasureSingleShotRhtOnly);
        Timing.SleepMs(50);
        ReadMeasurement();
    }

    /// <summary>Temperature offset in °C (word = offset · 65535 / 175). Only while idle.</summary>
    public void SetTemperatureOffset(double celsius) =>
        WriteCommand(CmdSetTemperatureOffset, (int)(celsius * 65535.0 / 175.0));

    public double GetTemperatureOffset() => Word(ReadWords(CmdGetTemperatureOffset, 1, 1), 0) * 175.0 / 65535.0;

    /// <summary>Altitude above sea level in m (0..3000). Only while idle.</summary>
    public void SetSensorAltitude(int meters) => WriteCommand(CmdSetSensorAltitude, meters);

    public int GetSensorAltitude() => Word(ReadWords(CmdGetSensorAltitude, 1, 1), 0);

    /// <summary>Ambient pressure in Pa (sent in 100 Pa units); overrides the altitude, allowed while measuring.</summary>
    public void SetAmbientPressure(int pascal) => WriteCommand(CmdSetAmbientPressure, pascal / 100);

    public void SetAutomaticSelfCalibration(bool enabled) => WriteCommand(CmdSetAscEnabled, enabled ? 1 : 0);

    public bool GetAutomaticSelfCalibration() => Word(ReadWords(CmdGetAscEnabled, 1, 1), 0) == 1;

    /// <summary>
    /// Forced recalibration to a reference concentration (ppm) after 3 minutes of operation and
    /// a stop. Returns the correction in ppm, or int.MinValue when the sensor reports failure (0xFFFF).
    /// </summary>
    public int ForcedRecalibration(int referencePpm)
    {
        WriteCommand(CmdForcedRecalibration, referencePpm);
        Timing.SleepMs(400);
        int w = Word(_dev.Read(3), 0);
        return w == 0xFFFF ? int.MinValue : w - 0x8000;
    }

    /// <summary>On-chip self test (10 s). True when no malfunction is detected.</summary>
    public bool SelfTest() => Word(ReadWords(CmdSelfTest, 1, 10000), 0) == 0;

    public void PersistSettings()
    {
        _dev.WriteCommand16(CmdPersistSettings);
        Timing.SleepMs(800);
    }

    public void Reinit()
    {
        _dev.WriteCommand16(CmdReinit);
        Timing.SleepMs(20);
    }

    public void FactoryReset()
    {
        _dev.WriteCommand16(CmdFactoryReset);
        Timing.SleepMs(1200);
    }

    public void PowerDown() => _dev.WriteCommand16(CmdPowerDown);

    /// <summary>Wake from power-down. The sensor does not acknowledge this command.</summary>
    public void WakeUp()
    {
        try
        {
            _dev.WriteCommand16(CmdWakeUp);
        }
        catch (Exception)
        {
        }
        Timing.SleepMs(30);
    }

    private void WriteCommand(int command, int argument)
    {
        byte[] b = new byte[5];
        b[0] = (byte)(command >> 8);
        b[1] = (byte)command;
        b[2] = (byte)(argument >> 8);
        b[3] = (byte)argument;
        b[4] = (byte)BitOps.Crc8Sensirion(b, 2, 2);
        _dev.Write(b);
        Timing.SleepMs(1);
    }

    private byte[] ReadWords(int command, int words, int waitMs)
    {
        _dev.WriteCommand16(command);
        Timing.SleepMs(waitMs);
        return _dev.Read(words * 3);
    }

    private static int Word(byte[] r, int offset)
    {
        if (BitOps.Crc8Sensirion(r, offset, 2) != r[offset + 2])
        {
            throw new InvalidOperationException("scd4x crc mismatch");
        }
        return (r[offset] << 8) | r[offset + 1];
    }
}
