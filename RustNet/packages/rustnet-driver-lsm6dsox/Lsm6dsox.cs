using RustNet.Community.Bus;

namespace RustNet.Community.Lsm6dsox;

/// <summary>
/// ST LSM6DSOX 6-axis IMU over I2C (address 0x6A with SA0 low, 0x6B high).
/// Sensitivities from the datasheet: accelerometer 0.061/0.122/0.244/0.488
/// mg/LSB for ±2/4/8/16 g; gyroscope 4.375/8.75/17.5/35/70 mdps/LSB for
/// ±125/250/500/1000/2000 dps; temperature 256 LSB/°C with 0 LSB = 25 °C.
/// </summary>
public class Lsm6dsox
{
    public const int DefaultAddress = 0x6A;
    public const int AlternateAddress = 0x6B;
    public const int WhoAmIValue = 0x6C;

    public const int Accel2G = 2;
    public const int Accel4G = 4;
    public const int Accel8G = 8;
    public const int Accel16G = 16;

    public const int Gyro125 = 125;
    public const int Gyro250 = 250;
    public const int Gyro500 = 500;
    public const int Gyro1000 = 1000;
    public const int Gyro2000 = 2000;

    /// <summary>ODR code (CTRL1_XL / CTRL2_G bits 7:4): 4 = 104 Hz.</summary>
    public const int Odr104Hz = 4;

    private const int RegWhoAmI = 0x0F;
    private const int RegCtrl1Xl = 0x10;
    private const int RegCtrl2G = 0x11;
    private const int RegCtrl3C = 0x12;
    private const int RegStatus = 0x1E;
    private const int RegOutTempL = 0x20;

    private readonly I2cDevice _dev;

    /// <summary>Full scale in g: 2, 4, 8 or 16.</summary>
    public int AccelRangeG = Accel4G;

    /// <summary>Full scale in dps: 125, 250, 500, 1000 or 2000.</summary>
    public int GyroRangeDps = Gyro2000;

    public int OutputDataRate = Odr104Hz;

    public int RawTemperature;
    public int RawGyroX, RawGyroY, RawGyroZ;
    public int RawAccelX, RawAccelY, RawAccelZ;

    /// <summary>Acceleration in g.</summary>
    public double AccelX, AccelY, AccelZ;

    /// <summary>Angular rate in dps (°/s).</summary>
    public double GyroX, GyroY, GyroZ;

    /// <summary>Temperature in °C.</summary>
    public double TemperatureCelsius;

    public Lsm6dsox(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Lsm6dsox(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Lsm6dsox(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check WHO_AM_I, software reset, enable BDU + auto-increment, start both sensors.</summary>
    public bool Begin()
    {
        if (_dev.ReadRegister(RegWhoAmI) != WhoAmIValue)
        {
            return false;
        }
        _dev.WriteRegister(RegCtrl3C, 0x01); // SW_RESET
        for (int i = 0; i < 10 && (_dev.ReadRegister(RegCtrl3C) & 0x01) != 0; i++)
        {
            Timing.SleepMs(1);
        }
        _dev.WriteRegister(RegCtrl3C, 0x44); // BDU | IF_INC
        Configure();
        return true;
    }

    /// <summary>Write CTRL1_XL and CTRL2_G from the range/ODR fields.</summary>
    public void Configure()
    {
        _dev.WriteRegister(RegCtrl1Xl, (OutputDataRate << 4) | (AccelFsBits(AccelRangeG) << 2));
        _dev.WriteRegister(RegCtrl2G, (OutputDataRate << 4) | GyroFsBits(GyroRangeDps));
    }

    /// <summary>FS_XL encoding: 00 = ±2 g, 01 = ±16 g, 10 = ±4 g, 11 = ±8 g.</summary>
    public static int AccelFsBits(int g)
    {
        if (g == 16)
        {
            return 1;
        }
        if (g == 4)
        {
            return 2;
        }
        if (g == 8)
        {
            return 3;
        }
        return 0;
    }

    /// <summary>CTRL2_G bits 3:1 (FS_G[1:0] then FS_125).</summary>
    public static int GyroFsBits(int dps)
    {
        if (dps == 125)
        {
            return 0x02;
        }
        if (dps == 500)
        {
            return 0x04;
        }
        if (dps == 1000)
        {
            return 0x08;
        }
        if (dps == 2000)
        {
            return 0x0C;
        }
        return 0x00; // 250
    }

    /// <summary>g per LSB: 0.061 mg × (range/2).</summary>
    public double AccelGPerLsb => 0.000061 * (AccelRangeG / 2);

    /// <summary>dps per LSB: 4.375 mdps at 125 dps, doubling with each range step.</summary>
    public double GyroDpsPerLsb => 0.004375 * (GyroRangeDps / 125);

    /// <summary>True when STATUS_REG reports new accelerometer (bit 0) and gyroscope (bit 1) data.</summary>
    public bool DataReady() => (_dev.ReadRegister(RegStatus) & 0x03) == 0x03;

    /// <summary>Read temperature, gyroscope and accelerometer in one 14-byte burst from OUT_TEMP_L.</summary>
    public void Read()
    {
        byte[] d = _dev.ReadRegisters(RegOutTempL, 14);
        RawTemperature = Le(d, 0);
        RawGyroX = Le(d, 2);
        RawGyroY = Le(d, 4);
        RawGyroZ = Le(d, 6);
        RawAccelX = Le(d, 8);
        RawAccelY = Le(d, 10);
        RawAccelZ = Le(d, 12);
        double a = AccelGPerLsb;
        AccelX = RawAccelX * a;
        AccelY = RawAccelY * a;
        AccelZ = RawAccelZ * a;
        double g = GyroDpsPerLsb;
        GyroX = RawGyroX * g;
        GyroY = RawGyroY * g;
        GyroZ = RawGyroZ * g;
        TemperatureCelsius = 25.0 + RawTemperature / 256.0;
    }

    private static int Le(byte[] d, int i) => BitOps.ToInt16(d[i] | (d[i + 1] << 8));
}
