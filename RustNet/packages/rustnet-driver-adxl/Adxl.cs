using RustNet.Community.Bus;

namespace RustNet.Community.Adxl;

/// <summary>Standard gravity, for converting g to m/s².</summary>
public static class Gravity
{
    public const double StandardMs2 = 9.80665;
}

/// <summary>
/// A three-axis analog accelerometer with ratiometric outputs: ADXL335 (±3 g)
/// and ADXL377 (±200 g). Each axis sits at Vs/2 at 0 g and its sensitivity is
/// proportional to the supply: 300 mV/g (ADXL335) or 6.5 mV/g (ADXL377) at
/// Vs = 3.0 V (datasheet typicals). Readings use the ADC's millivolts.
/// </summary>
public class AnalogAccelerometer
{
    private readonly AnalogInput _x;
    private readonly AnalogInput _y;
    private readonly AnalogInput _z;

    /// <summary>Supply voltage Vs in mV (also the ratiometric reference).</summary>
    public double SupplyMillivolts;

    /// <summary>Sensitivity in mV/g at the configured supply.</summary>
    public double MillivoltsPerG;

    /// <summary>Output at 0 g, mV (Vs/2 unless calibrated).</summary>
    public double ZeroGX;
    public double ZeroGY;
    public double ZeroGZ;

    /// <summary>Last reading, in g.</summary>
    public double X;
    public double Y;
    public double Z;

    /// <param name="sensitivityAt3V">Datasheet sensitivity at Vs = 3 V, in mV/g.</param>
    public AnalogAccelerometer(AnalogInput x, AnalogInput y, AnalogInput z, double supplyMillivolts, double sensitivityAt3V)
    {
        _x = x;
        _y = y;
        _z = z;
        SupplyMillivolts = supplyMillivolts;
        MillivoltsPerG = sensitivityAt3V * supplyMillivolts / 3000.0;
        ZeroGX = supplyMillivolts / 2.0;
        ZeroGY = ZeroGX;
        ZeroGZ = ZeroGX;
    }

    /// <summary>ADXL335: ±3 g, 300 mV/g at 3 V.</summary>
    public static AnalogAccelerometer Adxl335(int adcX, int adcY, int adcZ, double supplyMillivolts) =>
        Adxl335(new AnalogInput(adcX), new AnalogInput(adcY), new AnalogInput(adcZ), supplyMillivolts);

    public static AnalogAccelerometer Adxl335(AnalogInput x, AnalogInput y, AnalogInput z, double supplyMillivolts) =>
        new AnalogAccelerometer(x, y, z, supplyMillivolts, 300.0);

    /// <summary>ADXL377: ±200 g, 6.5 mV/g at 3 V.</summary>
    public static AnalogAccelerometer Adxl377(int adcX, int adcY, int adcZ, double supplyMillivolts) =>
        Adxl377(new AnalogInput(adcX), new AnalogInput(adcY), new AnalogInput(adcZ), supplyMillivolts);

    public static AnalogAccelerometer Adxl377(AnalogInput x, AnalogInput y, AnalogInput z, double supplyMillivolts) =>
        new AnalogAccelerometer(x, y, z, supplyMillivolts, 6.5);

    /// <summary>Sample all three axes into <see cref="X"/>/<see cref="Y"/>/<see cref="Z"/> (g).</summary>
    public void Read()
    {
        X = (_x.ReadMillivolts() - ZeroGX) / MillivoltsPerG;
        Y = (_y.ReadMillivolts() - ZeroGY) / MillivoltsPerG;
        Z = (_z.ReadMillivolts() - ZeroGZ) / MillivoltsPerG;
    }

    /// <summary>
    /// Zero X and Y and set Z to +1 g, with the board lying flat and still.
    /// </summary>
    public void CalibrateFlat()
    {
        ZeroGX = _x.ReadMillivolts();
        ZeroGY = _y.ReadMillivolts();
        ZeroGZ = _z.ReadMillivolts() - MillivoltsPerG;
    }
}

/// <summary>
/// ADXL345 three-axis digital accelerometer over I2C (address 0x53 with
/// SDO/ALT low, 0x1D high). Ranges ±2/4/8/16 g; in full-resolution mode
/// (the default here) the scale is 256 LSB/g (3.9 mg/LSB) at every range.
/// </summary>
public class Adxl345
{
    public const int DefaultAddress = 0x53;
    public const int AlternateAddress = 0x1D;
    public const int DeviceId = 0xE5;

    public const int Range2G = 0;
    public const int Range4G = 1;
    public const int Range8G = 2;
    public const int Range16G = 3;

    /// <summary>BW_RATE codes (output data rate): 0x0A = 100 Hz (reset default), 0x0F = 3200 Hz.</summary>
    public const int Rate100Hz = 0x0A;

    private const int RegDevId = 0x00;
    private const int RegBwRate = 0x2C;
    private const int RegPowerCtl = 0x2D;
    private const int RegDataFormat = 0x31;
    private const int RegDataX0 = 0x32;

    private readonly I2cDevice _dev;

    public int Range = Range2G;

    /// <summary>Full resolution: 4 mg/LSB-class scale at all ranges (13-bit at ±16 g).</summary>
    public bool FullResolution = true;

    public int RawX;
    public int RawY;
    public int RawZ;

    /// <summary>Last reading, in g.</summary>
    public double X;
    public double Y;
    public double Z;

    public Adxl345(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Adxl345(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Adxl345(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check DEVID, configure range and data rate, start measuring. False if no ADXL345 answers.</summary>
    public bool Begin()
    {
        if (_dev.ReadRegister(RegDevId) != DeviceId)
        {
            return false;
        }
        _dev.WriteRegister(RegBwRate, Rate100Hz);
        SetRange(Range);
        _dev.WriteRegister(RegPowerCtl, 0x08); // Measure
        return true;
    }

    public void SetRange(int range)
    {
        Range = range & 3;
        _dev.WriteRegister(RegDataFormat, (FullResolution ? 0x08 : 0x00) | Range);
    }

    /// <summary>g per LSB for the current mode: 1/256 in full resolution, else range/512.</summary>
    public double GPerLsb => FullResolution ? 1.0 / 256.0 : (1 << Range) / 256.0;

    /// <summary>Read DATAX0..DATAZ1 in one transfer (little-endian, two's complement).</summary>
    public void Read()
    {
        byte[] d = _dev.ReadRegisters(RegDataX0, 6);
        RawX = BitOps.ToInt16(d[0] | (d[1] << 8));
        RawY = BitOps.ToInt16(d[2] | (d[3] << 8));
        RawZ = BitOps.ToInt16(d[4] | (d[5] << 8));
        double s = GPerLsb;
        X = RawX * s;
        Y = RawY * s;
        Z = RawZ * s;
    }
}

/// <summary>
/// ADXL362 micropower three-axis accelerometer over SPI (mode 0, ≤ 8 MHz).
/// Register access is by command byte: 0x0A write, 0x0B read. 12-bit data at
/// 1/2/4 mg/LSB for ±2/4/8 g, plus a temperature sensor at 0.065 °C/LSB.
/// </summary>
public class Adxl362
{
    public const int DeviceIdAd = 0xAD;
    public const int DeviceIdMst = 0x1D;
    public const int PartId = 0xF2;

    public const int Range2G = 0;
    public const int Range4G = 1;
    public const int Range8G = 2;

    private const int CmdWrite = 0x0A;
    private const int CmdRead = 0x0B;
    private const int RegDevIdAd = 0x00;
    private const int RegXDataL = 0x0E;
    private const int RegSoftReset = 0x1F;
    private const int RegFilterCtl = 0x2C;
    private const int RegPowerCtl = 0x2D;

    private readonly SpiDevice _spi;

    public int Range = Range2G;

    /// <summary>Output data rate code (FILTER_CTL bits 2:0): 3 = 100 Hz (default).</summary>
    public int DataRate = 3;

    public int RawX;
    public int RawY;
    public int RawZ;
    public int RawTemperature;

    /// <summary>Last reading, in g.</summary>
    public double X;
    public double Y;
    public double Z;

    /// <summary>Last die temperature in °C (typical bias 350 LSB at 25 °C, 0.065 °C/LSB; uncalibrated, so only a few °C accurate).</summary>
    public double TemperatureCelsius;

    public Adxl362(int bus, int chipSelectPin) : this(new SpiDevice(bus, chipSelectPin, 4000000, 0))
    {
    }

    public Adxl362(SpiDevice spi)
    {
        _spi = spi;
    }

    public int ReadRegister(int register) => ReadRegisters(register, 1)[0];

    public byte[] ReadRegisters(int register, int count)
    {
        byte[] tx = new byte[count + 2];
        tx[0] = CmdRead;
        tx[1] = (byte)register;
        byte[] rx = _spi.Transfer(tx);
        byte[] r = new byte[count];
        for (int i = 0; i < count; i++)
        {
            r[i] = rx[i + 2];
        }
        return r;
    }

    public void WriteRegister(int register, int value)
    {
        byte[] tx = new byte[3];
        tx[0] = CmdWrite;
        tx[1] = (byte)register;
        tx[2] = (byte)value;
        _spi.Write(tx);
    }

    /// <summary>Check the three ID registers, soft-reset, set range/rate and enter measurement mode.</summary>
    public bool Begin()
    {
        byte[] id = ReadRegisters(RegDevIdAd, 3);
        if (id[0] != DeviceIdAd || id[1] != DeviceIdMst || id[2] != PartId)
        {
            return false;
        }
        WriteRegister(RegSoftReset, 0x52); // 'R'
        Timing.SleepMs(1);
        WriteRegister(RegFilterCtl, ((Range & 3) << 6) | 0x10 | (DataRate & 7)); // HALF_BW
        WriteRegister(RegPowerCtl, 0x02); // measurement mode
        return true;
    }

    /// <summary>g per LSB: 0.001, 0.002 or 0.004.</summary>
    public double GPerLsb => (1 << Range) / 1000.0;

    /// <summary>Read X, Y, Z and temperature (8 bytes from XDATA_L) in one transaction.</summary>
    public void Read()
    {
        byte[] d = ReadRegisters(RegXDataL, 8);
        RawX = BitOps.ToInt16(d[0] | (d[1] << 8));
        RawY = BitOps.ToInt16(d[2] | (d[3] << 8));
        RawZ = BitOps.ToInt16(d[4] | (d[5] << 8));
        RawTemperature = BitOps.ToInt16(d[6] | (d[7] << 8));
        double s = GPerLsb;
        X = RawX * s;
        Y = RawY * s;
        Z = RawZ * s;
        TemperatureCelsius = 25.0 + (RawTemperature - 350) * 0.065;
    }
}
