using RustNet.Community.Bus;

namespace RustNet.Community.Bmp180;

/// <summary>
/// Bosch BMP180 (and its predecessor BMP085) temperature and barometric
/// pressure sensor at I2C address 0x77, using the datasheet's integer
/// compensation (section 3.5), so results match the worked example exactly.
/// </summary>
public class Bmp180
{
    public const int DefaultAddress = 0x77;
    public const int ChipIdValue = 0x55;

    /// <summary>Oversampling setting (oss): 0 ultra low power .. 3 ultra high resolution.</summary>
    public const int UltraLowPower = 0;
    public const int Standard = 1;
    public const int HighResolution = 2;
    public const int UltraHighResolution = 3;

    private const int RegCalib = 0xAA;
    private const int RegChipId = 0xD0;
    private const int RegSoftReset = 0xE0;
    private const int RegControl = 0xF4;
    private const int RegData = 0xF6;

    private readonly I2cDevice _dev;

    // Calibration (datasheet names).
    public int AC1, AC2, AC3, AC4, AC5, AC6, B1, B2, MB, MC, MD;

    /// <summary>Oversampling setting 0..3 used for pressure.</summary>
    public int Oversampling = Standard;

    public int RawTemperature;
    public int RawPressure;

    /// <summary>B5 from the temperature calculation, carried into the pressure one.</summary>
    public int B5;

    /// <summary>Last temperature in 0.1 °C.</summary>
    public int TemperatureDeciCelsius;

    /// <summary>Last pressure in Pa.</summary>
    public int PressurePascal;

    public Bmp180(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Bmp180(I2cDevice device)
    {
        _dev = device;
    }

    public double TemperatureCelsius => TemperatureDeciCelsius / 10.0;

    public double PressureHectopascal => PressurePascal / 100.0;

    /// <summary>Check the chip ID (0x55) and read the 11 calibration words. False when no BMP180 answers.</summary>
    public bool Begin()
    {
        if (_dev.ReadRegister(RegChipId) != ChipIdValue)
        {
            return false;
        }
        byte[] c = _dev.ReadRegisters(RegCalib, 22);
        AC1 = S16(c, 0);
        AC2 = S16(c, 2);
        AC3 = S16(c, 4);
        AC4 = U16(c, 6);
        AC5 = U16(c, 8);
        AC6 = U16(c, 10);
        B1 = S16(c, 12);
        B2 = S16(c, 14);
        MB = S16(c, 16);
        MC = S16(c, 18);
        MD = S16(c, 20);
        return true;
    }

    private static int U16(byte[] b, int i) => (b[i] << 8) | b[i + 1];

    private static int S16(byte[] b, int i) => BitOps.ToInt16(U16(b, i));

    public void SoftReset() => _dev.WriteRegister(RegSoftReset, 0xB6);

    /// <summary>Measure temperature (4.5 ms) then pressure (4.5..25.5 ms) and compensate both.</summary>
    public void Measure()
    {
        _dev.WriteRegister(RegControl, 0x2E);
        Timing.SleepMs(5);
        RawTemperature = _dev.ReadUInt16BigEndian(RegData);
        _dev.WriteRegister(RegControl, 0x34 | (Oversampling << 6));
        Timing.SleepMs(Oversampling == 0 ? 5 : (Oversampling == 1 ? 8 : (Oversampling == 2 ? 14 : 26)));
        byte[] d = _dev.ReadRegisters(RegData, 3);
        RawPressure = ((d[0] << 16) | (d[1] << 8) | d[2]) >> (8 - Oversampling);
        TemperatureDeciCelsius = CompensateTemperature(RawTemperature);
        PressurePascal = CompensatePressure(RawPressure, Oversampling);
    }

    /// <summary>Datasheet 3.5: true temperature in 0.1 °C; sets <see cref="B5"/>.</summary>
    public int CompensateTemperature(int ut)
    {
        int x1 = ((ut - AC6) * AC5) >> 15;
        int x2 = (MC << 11) / (x1 + MD);
        B5 = x1 + x2;
        return (B5 + 8) >> 4;
    }

    /// <summary>Datasheet 3.5: true pressure in Pa. Needs <see cref="B5"/> from a temperature first.</summary>
    public int CompensatePressure(int up, int oss)
    {
        int b6 = B5 - 4000;
        int x1 = (B2 * ((b6 * b6) >> 12)) >> 11;
        int x2 = (AC2 * b6) >> 11;
        int x3 = x1 + x2;
        int b3 = ((((AC1 * 4) + x3) << oss) + 2) / 4;
        x1 = (AC3 * b6) >> 13;
        x2 = (B1 * ((b6 * b6) >> 12)) >> 16;
        x3 = ((x1 + x2) + 2) >> 2;
        long b4 = ((long)AC4 * (long)(x3 + 32768)) >> 15;
        long b7 = ((long)up - b3) * (50000 >> oss);
        if (b4 == 0)
        {
            return 0;
        }
        // Datasheet: B7 < 0x80000000 ? B7*2/B4 : B7/B4*2 (tested with a shift: no unsigned constant needed).
        long p = (b7 >> 31) == 0 ? (b7 * 2) / b4 : (b7 / b4) * 2;
        long y1 = (p >> 8) * (p >> 8);
        y1 = (y1 * 3038) >> 16;
        long y2 = (-7357 * p) >> 16;
        return (int)(p + ((y1 + y2 + 3791) >> 4));
    }

    /// <summary>Altitude in m from the last pressure (datasheet 3.6: 44330 · (1 - (p/p0)^(1/5.255))).</summary>
    public double AltitudeMeters(double seaLevelPascal) =>
        44330.0 * (1.0 - Math.Pow(PressurePascal / seaLevelPascal, 1.0 / 5.255));

    /// <summary>Pressure at sea level in Pa from the last pressure and a known altitude (datasheet 3.7).</summary>
    public double SeaLevelPascal(double altitudeMeters) =>
        PressurePascal / Math.Pow(1.0 - altitudeMeters / 44330.0, 5.255);
}
