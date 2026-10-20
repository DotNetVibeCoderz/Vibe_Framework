using RustNet.Community.Bus;

namespace RustNet.Community.Bme280;

/// <summary>
/// Bosch BME280 (temperature, humidity, pressure) and BMP280 (no humidity)
/// over I2C. Readings use the datasheet's fixed-point compensation, so
/// results match Bosch's reference driver bit for bit.
/// Default address 0x76 (SDO low); 0x77 with SDO high.
/// </summary>
public class Bme280
{
    public const int DefaultAddress = 0x76;
    public const int ChipIdBme280 = 0x60;
    public const int ChipIdBmp280 = 0x58;

    // Oversampling settings for ctrl_hum / ctrl_meas.
    public const int OversampleSkip = 0;
    public const int Oversample1 = 1;
    public const int Oversample2 = 2;
    public const int Oversample4 = 3;
    public const int Oversample8 = 4;
    public const int Oversample16 = 5;

    private const int RegCalib00 = 0x88;
    private const int RegChipId = 0xD0;
    private const int RegReset = 0xE0;
    private const int RegCalib26 = 0xE1;
    private const int RegCtrlHum = 0xF2;
    private const int RegStatus = 0xF3;
    private const int RegCtrlMeas = 0xF4;
    private const int RegConfig = 0xF5;
    private const int RegData = 0xF7;

    private readonly I2cDevice _dev;

    public int ChipId;

    /// <summary>True for a BME280; a BMP280 has no humidity sensor.</summary>
    public bool HasHumidity;

    // Calibration (datasheet names).
    public int T1, T2, T3;
    public int P1, P2, P3, P4, P5, P6, P7, P8, P9;
    public int H1, H2, H3, H4, H5, H6;

    /// <summary>Fine temperature carried from the temperature into the pressure/humidity compensation.</summary>
    public int TFine;

    public int TemperatureOversampling = Oversample1;
    public int PressureOversampling = Oversample1;
    public int HumidityOversampling = Oversample1;

    public Bme280(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Bme280(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Bme280(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Identify the chip, reset it and load its calibration. Returns false if no BMx280 answers.</summary>
    public bool Begin()
    {
        ChipId = _dev.ReadRegister(RegChipId);
        if (ChipId != ChipIdBme280 && ChipId != ChipIdBmp280)
        {
            return false;
        }
        HasHumidity = ChipId == ChipIdBme280;
        _dev.WriteRegister(RegReset, 0xB6);
        Timing.SleepMs(3);
        // NVM copy runs after reset; status bit 0 clears when done.
        for (int i = 0; i < 10 && (_dev.ReadRegister(RegStatus) & 0x01) != 0; i++)
        {
            Timing.SleepMs(2);
        }
        ReadCalibration();
        // Standby/filter off, sleep mode until a measurement is requested.
        _dev.WriteRegister(RegConfig, 0x00);
        return true;
    }

    private void ReadCalibration()
    {
        byte[] c = _dev.ReadRegisters(RegCalib00, 26);
        T1 = U16(c, 0);
        T2 = S16(c, 2);
        T3 = S16(c, 4);
        P1 = U16(c, 6);
        P2 = S16(c, 8);
        P3 = S16(c, 10);
        P4 = S16(c, 12);
        P5 = S16(c, 14);
        P6 = S16(c, 16);
        P7 = S16(c, 18);
        P8 = S16(c, 20);
        P9 = S16(c, 22);
        if (!HasHumidity)
        {
            return;
        }
        H1 = c[25];
        byte[] h = _dev.ReadRegisters(RegCalib26, 7);
        H2 = S16(h, 0);
        H3 = h[2];
        H4 = BitOps.SignExtend((h[3] << 4) | (h[4] & 0x0F), 12);
        H5 = BitOps.SignExtend((h[5] << 4) | (h[4] >> 4), 12);
        H6 = h[6] >= 128 ? h[6] - 256 : h[6];
    }

    private static int U16(byte[] b, int i) => b[i] | (b[i + 1] << 8);

    private static int S16(byte[] b, int i) => BitOps.ToInt16(U16(b, i));

    /// <summary>Trigger one forced-mode measurement and wait for it to finish.</summary>
    public void Measure()
    {
        if (HasHumidity)
        {
            // ctrl_hum only takes effect after the following ctrl_meas write.
            _dev.WriteRegister(RegCtrlHum, HumidityOversampling);
        }
        _dev.WriteRegister(RegCtrlMeas, (TemperatureOversampling << 5) | (PressureOversampling << 2) | 0x01);
        for (int i = 0; i < 50; i++)
        {
            Timing.SleepMs(2);
            if ((_dev.ReadRegister(RegStatus) & 0x08) == 0)
            {
                break;
            }
        }
        byte[] d = _dev.ReadRegisters(RegData, HasHumidity ? 8 : 6);
        RawPressure = (d[0] << 12) | (d[1] << 4) | (d[2] >> 4);
        RawTemperature = (d[3] << 12) | (d[4] << 4) | (d[5] >> 4);
        RawHumidity = HasHumidity ? (d[6] << 8) | d[7] : 0;
        TemperatureCentiCelsius = CompensateTemperature(RawTemperature);
        PressureQ24_8 = CompensatePressure(RawPressure);
        HumidityQ22_10 = HasHumidity ? CompensateHumidity(RawHumidity) : 0;
    }

    public int RawTemperature;
    public int RawPressure;
    public int RawHumidity;

    /// <summary>Last temperature in 0.01 °C.</summary>
    public int TemperatureCentiCelsius;

    /// <summary>Last pressure in Pa, Q24.8 fixed point (divide by 256).</summary>
    public long PressureQ24_8;

    /// <summary>Last relative humidity in %, Q22.10 fixed point (divide by 1024).</summary>
    public int HumidityQ22_10;

    public double TemperatureCelsius => TemperatureCentiCelsius / 100.0;

    public double PressurePascal => PressureQ24_8 / 256.0;

    public double PressureHectopascal => PressureQ24_8 / 25600.0;

    public double HumidityPercent => HumidityQ22_10 / 1024.0;

    /// <summary>Altitude in metres from the last pressure (international barometric formula).</summary>
    public double AltitudeMeters(double seaLevelHectopascal) =>
        44330.0 * (1.0 - Math.Pow(PressureHectopascal / seaLevelHectopascal, 0.1903));

    /// <summary>Datasheet 4.2.3: returns 0.01 °C and sets <see cref="TFine"/>.</summary>
    public int CompensateTemperature(int adcT)
    {
        int var1 = (((adcT >> 3) - (T1 << 1)) * T2) >> 11;
        int d = (adcT >> 4) - T1;
        int var2 = (((d * d) >> 12) * T3) >> 14;
        TFine = var1 + var2;
        return (TFine * 5 + 128) >> 8;
    }

    /// <summary>Datasheet 4.2.3 (64-bit): Pa in Q24.8. Needs <see cref="TFine"/> from a temperature first.</summary>
    public long CompensatePressure(int adcP)
    {
        long var1 = (long)TFine - 128000;
        long var2 = var1 * var1 * P6;
        var2 = var2 + ((var1 * P5) << 17);
        var2 = var2 + ((long)P4 << 35);
        var1 = ((var1 * var1 * P3) >> 8) + ((var1 * P2) << 12);
        var1 = ((((long)1 << 47) + var1) * P1) >> 33;
        if (var1 == 0)
        {
            return 0; // avoid division by zero on a blank calibration
        }
        long p = 1048576 - adcP;
        p = (((p << 31) - var2) * 3125) / var1;
        var1 = (P9 * (p >> 13) * (p >> 13)) >> 25;
        var2 = (P8 * p) >> 19;
        return ((p + var1 + var2) >> 8) + ((long)P7 << 4);
    }

    /// <summary>Datasheet 4.2.3: %RH in Q22.10. Needs <see cref="TFine"/> from a temperature first.</summary>
    public int CompensateHumidity(int adcH)
    {
        int v = TFine - 76800;
        v = ((((adcH << 14) - (H4 << 20) - (H5 * v)) + 16384) >> 15)
            * (((((((v * H6) >> 10) * (((v * H3) >> 11) + 32768)) >> 10) + 2097152) * H2 + 8192) >> 14);
        v = v - (((((v >> 15) * (v >> 15)) >> 7) * H1) >> 4);
        if (v < 0)
        {
            v = 0;
        }
        if (v > 419430400)
        {
            v = 419430400;
        }
        return v >> 12;
    }
}
