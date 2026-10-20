using RustNet.Community.Bus;

namespace RustNet.Community.Bme680;

/// <summary>
/// Bosch BME680 and BME688 4-in-1 sensor (gas resistance, temperature,
/// humidity, pressure) over I2C, in forced mode with one heater set-point.
/// Temperature, pressure and humidity use Bosch's integer compensation
/// (BME68x API), so results match the reference driver; gas resistance uses
/// the datasheet floating-point formula for the detected variant.
/// Default address 0x76 (SDO low); 0x77 with SDO high.
/// </summary>
public class Bme680
{
    public const int DefaultAddress = 0x76;
    public const int AlternateAddress = 0x77;
    public const int ChipId = 0x61;

    /// <summary>Variant ID register value: BME680 (gas "low" range).</summary>
    public const int VariantBme680 = 0x00;
    /// <summary>Variant ID register value: BME688 (gas "high" range).</summary>
    public const int VariantBme688 = 0x01;

    // Oversampling codes (osrs_x).
    public const int OversampleSkip = 0;
    public const int Oversample1 = 1;
    public const int Oversample2 = 2;
    public const int Oversample4 = 3;
    public const int Oversample8 = 4;
    public const int Oversample16 = 5;

    private const int RegCoeff3 = 0x00;
    private const int RegField0 = 0x1D;
    private const int RegResHeat0 = 0x5A;
    private const int RegGasWait0 = 0x64;
    private const int RegCtrlGas0 = 0x70;
    private const int RegCtrlGas1 = 0x71;
    private const int RegCtrlHum = 0x72;
    private const int RegCtrlMeas = 0x74;
    private const int RegConfig = 0x75;
    private const int RegCoeff1 = 0x89;
    private const int RegChipId = 0xD0;
    private const int RegReset = 0xE0;
    private const int RegCoeff2 = 0xE1;
    private const int RegVariant = 0xF0;

    private readonly I2cDevice _dev;

    /// <summary>0x00 = BME680, 0x01 = BME688 (read by <see cref="Begin"/>).</summary>
    public int Variant;

    // Calibration (datasheet names).
    public int T1, T2, T3;
    public int P1, P2, P3, P4, P5, P6, P7, P8, P9, P10;
    public int H1, H2, H3, H4, H5, H6, H7;
    public int G1, G2, G3;
    public int ResHeatRange, ResHeatVal, RangeSwitchingError;

    public int TemperatureOversampling = Oversample8;
    public int PressureOversampling = Oversample4;
    public int HumidityOversampling = Oversample2;

    /// <summary>IIR filter coefficient code 0..7 (0 = off).</summary>
    public int Filter = 0;

    /// <summary>Heater target temperature in °C (max 400); 0 disables the gas measurement.</summary>
    public int HeaterTemperature = 320;

    /// <summary>Heater on-time in ms (max 4032).</summary>
    public int HeaterDurationMs = 150;

    /// <summary>Ambient temperature (°C) used for the heater set-point until a measurement has been made.</summary>
    public int AmbientTemperature = 25;

    /// <summary>Fine temperature carried into the pressure/humidity compensation.</summary>
    public int TFine;

    public int RawTemperature, RawPressure, RawHumidity, RawGas, GasRange;

    /// <summary>Last temperature in 0.01 °C.</summary>
    public int TemperatureCentiCelsius;

    /// <summary>Last pressure in Pa.</summary>
    public int PressurePascal;

    /// <summary>Last relative humidity in 0.001 % (milli-percent).</summary>
    public int HumidityMilliPercent;

    /// <summary>Last gas resistance in Ω (0 when the gas reading was not valid).</summary>
    public double GasResistanceOhms;

    /// <summary>True when the last gas reading was valid and the heater reached its set-point.</summary>
    public bool GasValid;

    public Bme680(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Bme680(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Bme680(I2cDevice device)
    {
        _dev = device;
    }

    public double TemperatureCelsius => TemperatureCentiCelsius / 100.0;

    public double PressureHectopascal => PressurePascal / 100.0;

    public double HumidityPercent => HumidityMilliPercent / 1000.0;

    /// <summary>Check the chip ID, soft-reset, read the variant and calibration. False if no BME68x answers.</summary>
    public bool Begin()
    {
        if (_dev.ReadRegister(RegChipId) != ChipId)
        {
            return false;
        }
        _dev.WriteRegister(RegReset, 0xB6);
        Timing.SleepMs(10);
        Variant = _dev.ReadRegister(RegVariant);
        ReadCalibration();
        return true;
    }

    private void ReadCalibration()
    {
        byte[] c1 = _dev.ReadRegisters(RegCoeff1, 25);
        byte[] c2 = _dev.ReadRegisters(RegCoeff2, 16);
        byte[] c3 = _dev.ReadRegisters(RegCoeff3, 5);
        // c1[i] is register 0x89 + i.
        T2 = S16(c1, 1);
        T3 = S8(c1[3]);
        P1 = U16(c1, 5);
        P2 = S16(c1, 7);
        P3 = S8(c1[9]);
        P4 = S16(c1, 11);
        P5 = S16(c1, 13);
        P7 = S8(c1[15]);
        P6 = S8(c1[16]);
        P8 = S16(c1, 19);
        P9 = S16(c1, 21);
        P10 = c1[23];
        // c2[i] is register 0xE1 + i.
        H2 = (c2[0] << 4) | (c2[1] >> 4);
        H1 = (c2[2] << 4) | (c2[1] & 0x0F);
        H3 = S8(c2[3]);
        H4 = S8(c2[4]);
        H5 = S8(c2[5]);
        H6 = c2[6];
        H7 = S8(c2[7]);
        T1 = U16(c2, 8);
        G2 = S16(c2, 10);
        G1 = S8(c2[12]);
        G3 = S8(c2[13]);
        // c3[i] is register 0x00 + i.
        ResHeatVal = S8(c3[0]);
        ResHeatRange = (c3[2] & 0x30) >> 4;
        RangeSwitchingError = S8(c3[4] & 0xF0) / 16;
    }

    private static int U16(byte[] b, int i) => b[i] | (b[i + 1] << 8);

    private static int S16(byte[] b, int i) => BitOps.ToInt16(U16(b, i));

    private static int S8(int v) => v >= 128 ? v - 256 : v;

    /// <summary>Program oversampling, filter and heater, run one forced measurement and compensate it.</summary>
    public void Measure()
    {
        _dev.UpdateRegister(RegConfig, 0x1C, Filter << 2);
        bool gas = HeaterTemperature > 0;
        if (gas)
        {
            _dev.WriteRegister(RegResHeat0, HeaterResistance(HeaterTemperature, AmbientTemperature));
            _dev.WriteRegister(RegGasWait0, GasWait(HeaterDurationMs));
            // run_gas sits at bit 4 on the BME680 and bit 5 on the BME688; nb_conv = 0.
            _dev.WriteRegister(RegCtrlGas1, Variant == VariantBme688 ? 0x20 : 0x10);
            _dev.WriteRegister(RegCtrlGas0, 0x00);
        }
        else
        {
            _dev.WriteRegister(RegCtrlGas1, 0x00);
            _dev.WriteRegister(RegCtrlGas0, 0x08); // heat_off
        }
        // ctrl_hum only takes effect after the following ctrl_meas write.
        _dev.WriteRegister(RegCtrlHum, HumidityOversampling & 0x07);
        _dev.WriteRegister(RegCtrlMeas, (TemperatureOversampling << 5) | (PressureOversampling << 2) | 0x01);
        Timing.SleepMs(MeasurementDurationMs() + (gas ? HeaterDurationMs : 0));
        byte[] d = _dev.ReadRegisters(RegField0, 17);
        for (int i = 0; i < 20 && (d[0] & 0x80) == 0; i++)
        {
            Timing.SleepMs(5);
            d = _dev.ReadRegisters(RegField0, 17);
        }
        if ((d[0] & 0x80) == 0)
        {
            throw new InvalidOperationException("bme680 no new data");
        }
        RawPressure = (d[2] << 12) | (d[3] << 4) | (d[4] >> 4);
        RawTemperature = (d[5] << 12) | (d[6] << 4) | (d[7] >> 4);
        RawHumidity = (d[8] << 8) | d[9];
        int gm = Variant == VariantBme688 ? 15 : 13;
        RawGas = (d[gm] << 2) | (d[gm + 1] >> 6);
        GasRange = d[gm + 1] & 0x0F;
        GasValid = gas && (d[gm + 1] & 0x30) == 0x30; // gas_valid_r and heat_stab_r

        TemperatureCentiCelsius = CompensateTemperature(RawTemperature);
        PressurePascal = CompensatePressure(RawPressure);
        HumidityMilliPercent = CompensateHumidity(RawHumidity);
        GasResistanceOhms = GasValid
            ? (Variant == VariantBme688 ? GasResistanceHigh(RawGas, GasRange) : GasResistanceLow(RawGas, GasRange))
            : 0.0;
        AmbientTemperature = TemperatureCentiCelsius / 100;
    }

    /// <summary>Approximate TPH conversion time in ms (Bosch API: 1963 µs per oversampling cycle + overheads).</summary>
    public int MeasurementDurationMs()
    {
        int cycles = Cycles(TemperatureOversampling) + Cycles(PressureOversampling) + Cycles(HumidityOversampling);
        int us = cycles * 1963 + 477 * 4 + 477 * 5 + 1000;
        return us / 1000 + 1;
    }

    private static int Cycles(int osr) => osr == 0 ? 0 : (1 << (osr - 1));

    /// <summary>Integer temperature compensation: 0.01 °C, sets <see cref="TFine"/>.</summary>
    public int CompensateTemperature(int adc)
    {
        int var1 = (adc >> 3) - (T1 << 1);
        int var2 = (var1 * T2) >> 11;
        int var3 = ((((var1 >> 1) * (var1 >> 1)) >> 12) * (T3 << 4)) >> 14;
        TFine = var2 + var3;
        return ((TFine * 5) + 128) >> 8;
    }

    /// <summary>Integer pressure compensation: Pa. Needs <see cref="TFine"/>.</summary>
    public int CompensatePressure(int adc)
    {
        int var1 = (TFine >> 1) - 64000;
        int var2 = ((((var1 >> 2) * (var1 >> 2)) >> 11) * P6) >> 2;
        var2 = var2 + ((var1 * P5) << 1);
        var2 = (var2 >> 2) + (P4 << 16);
        var1 = (((((var1 >> 2) * (var1 >> 2)) >> 13) * (P3 << 5)) >> 3) + ((P2 * var1) >> 1);
        var1 = var1 >> 18;
        var1 = ((32768 + var1) * P1) >> 15;
        if (var1 == 0)
        {
            return 0;
        }
        int p = 1048576 - adc;
        p = (p - (var2 >> 12)) * 3125;
        if (p >= 0x40000000)
        {
            p = (p / var1) << 1;
        }
        else
        {
            p = (p << 1) / var1;
        }
        var1 = (P9 * (((p >> 3) * (p >> 3)) >> 13)) >> 12;
        var2 = ((p >> 2) * P8) >> 13;
        int var3 = ((p >> 8) * (p >> 8) * (p >> 8) * P10) >> 17;
        return p + ((var1 + var2 + var3 + (P7 << 7)) >> 4);
    }

    /// <summary>Integer humidity compensation: 0.001 %RH, clamped to 0..100 %. Needs <see cref="TFine"/>.</summary>
    public int CompensateHumidity(int adc)
    {
        int ts = ((TFine * 5) + 128) >> 8;
        int var1 = (adc - (H1 * 16)) - (((ts * H3) / 100) >> 1);
        int var2 = (H2 * (((ts * H4) / 100) + (((ts * ((ts * H5) / 100)) >> 6) / 100) + (1 << 14))) >> 10;
        int var3 = var1 * var2;
        int var4 = H6 << 7;
        var4 = (var4 + ((ts * H7) / 100)) >> 4;
        int var5 = ((var3 >> 14) * (var3 >> 14)) >> 10;
        int var6 = (var4 * var5) >> 1;
        int h = (((var3 + var6) >> 10) * 1000) >> 12;
        if (h > 100000)
        {
            h = 100000;
        }
        if (h < 0)
        {
            h = 0;
        }
        return h;
    }

    /// <summary>Datasheet heater set-point: the res_heat_x register value for a target and ambient temperature (°C).</summary>
    public int HeaterResistance(int targetCelsius, int ambientCelsius)
    {
        if (targetCelsius > 400)
        {
            targetCelsius = 400;
        }
        int var1 = ((ambientCelsius * G3) / 10) << 8;
        int var2 = (G1 + 784) * (((((G2 + 154009) * targetCelsius * 5) / 100) + 3276800) / 10);
        int var3 = var1 + (var2 >> 1);
        int var4 = var3 / (ResHeatRange + 4);
        int var5 = (131 * ResHeatVal) + 65536;
        int x100 = ((var4 / var5) - 250) * 34;
        return (x100 + 50) / 100;
    }

    /// <summary>Encode a heater duration (ms) into gas_wait_x: 6-bit value times a 1/4/16/64 multiplier.</summary>
    public static int GasWait(int durationMs)
    {
        if (durationMs >= 0xFC0)
        {
            return 0xFF;
        }
        int factor = 0;
        while (durationMs > 0x3F)
        {
            durationMs = durationMs / 4;
            factor++;
        }
        return durationMs + factor * 64;
    }

    /// <summary>BME680 gas resistance in Ω (datasheet float formula with range-switching error correction).</summary>
    public double GasResistanceLow(int adc, int range)
    {
        double var1 = 1340.0 + 5.0 * RangeSwitchingError;
        double var2 = var1 * (1.0 + K1(range) / 100.0);
        double var3 = 1.0 + K2(range) / 100.0;
        return 1.0 / (var3 * 0.000000125 * (1 << range) * (((adc - 512.0) / var2) + 1.0));
    }

    /// <summary>BME688 gas resistance in Ω (BME68x API float formula).</summary>
    public static double GasResistanceHigh(int adc, int range)
    {
        double var1 = 262144 >> range;
        double var2 = 4096 + (adc - 512) * 3;
        return 1000000.0 * var1 / var2;
    }

    private static double K1(int range)
    {
        if (range == 5 || range == 13)
        {
            return -1.0;
        }
        if (range == 7)
        {
            return -0.8;
        }
        if (range == 10)
        {
            return -0.2;
        }
        if (range == 11)
        {
            return -0.5;
        }
        return 0.0;
    }

    private static double K2(int range)
    {
        if (range == 4)
        {
            return 0.1;
        }
        if (range == 5)
        {
            return 0.7;
        }
        if (range == 7)
        {
            return -0.8;
        }
        if (range == 8)
        {
            return -0.1;
        }
        return 0.0;
    }
}
