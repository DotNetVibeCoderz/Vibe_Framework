using RustNet.Community.Bus;

namespace RustNet.Community.Magnetometer;

/// <summary>Heading helper shared by the magnetometers.</summary>
public static class Compass
{
    /// <summary>
    /// Heading in degrees 0..360 for a level sensor, from the horizontal field
    /// components: atan2(y, x), plus a declination (east positive).
    /// </summary>
    public static double Heading(double x, double y, double declinationDegrees)
    {
        double h = Math.Atan2(y, x) * 180.0 / Math.PI + declinationDegrees;
        while (h < 0)
        {
            h += 360.0;
        }
        while (h >= 360.0)
        {
            h -= 360.0;
        }
        return h;
    }
}

/// <summary>
/// Honeywell HMC5883L (and pin-compatible register clones) 3-axis
/// magnetometer at I2C 0x1E. Gain settings (CRB bits 7:5) 0..7 give
/// 1370/1090/820/660/440/390/330/230 LSB per gauss for ±0.88..±8.1 Ga.
/// Data registers are ordered X, Z, Y, big-endian; -4096 flags overflow.
/// </summary>
public class Hmc5883l
{
    public const int DefaultAddress = 0x1E;

    private const int RegConfigA = 0x00;
    private const int RegConfigB = 0x01;
    private const int RegMode = 0x02;
    private const int RegDataXMsb = 0x03;
    private const int RegStatus = 0x09;
    private const int RegIdA = 0x0A;

    private readonly I2cDevice _dev;

    /// <summary>Gain code 0..7; 1 (±1.3 Ga, 1090 LSB/Ga) is the reset default.</summary>
    public int Gain = 1;

    public int RawX, RawY, RawZ;

    /// <summary>True when the last reading had an axis at -4096 (ADC overflow).</summary>
    public bool Overflow;

    /// <summary>Field in µT (1 gauss = 100 µT).</summary>
    public double X, Y, Z;

    public Hmc5883l(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Hmc5883l(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check the "H43" ID, set 8-sample averaging at 15 Hz, the gain, and continuous mode.</summary>
    public bool Begin()
    {
        byte[] id = _dev.ReadRegisters(RegIdA, 3);
        if (id[0] != 'H' || id[1] != '4' || id[2] != '3')
        {
            return false;
        }
        _dev.WriteRegister(RegConfigA, 0x70); // MA=11 (8 samples), DO=100 (15 Hz), normal
        SetGain(Gain);
        _dev.WriteRegister(RegMode, 0x00); // continuous
        return true;
    }

    public void SetGain(int gain)
    {
        Gain = gain & 7;
        _dev.WriteRegister(RegConfigB, Gain << 5);
    }

    /// <summary>LSB per gauss for the current gain.</summary>
    public int LsbPerGauss
    {
        get
        {
            if (Gain == 0)
            {
                return 1370;
            }
            if (Gain == 1)
            {
                return 1090;
            }
            if (Gain == 2)
            {
                return 820;
            }
            if (Gain == 3)
            {
                return 660;
            }
            if (Gain == 4)
            {
                return 440;
            }
            if (Gain == 5)
            {
                return 390;
            }
            if (Gain == 6)
            {
                return 330;
            }
            return 230;
        }
    }

    /// <summary>STATUS.RDY (bit 0).</summary>
    public bool DataReady() => (_dev.ReadRegister(RegStatus) & 0x01) != 0;

    /// <summary>Read all six data registers (X, Z, Y order on the wire).</summary>
    public void Read()
    {
        byte[] d = _dev.ReadRegisters(RegDataXMsb, 6);
        RawX = BitOps.ToInt16((d[0] << 8) | d[1]);
        RawZ = BitOps.ToInt16((d[2] << 8) | d[3]);
        RawY = BitOps.ToInt16((d[4] << 8) | d[5]);
        Overflow = RawX == -4096 || RawY == -4096 || RawZ == -4096;
        double s = 100.0 / LsbPerGauss;
        X = RawX * s;
        Y = RawY * s;
        Z = RawZ * s;
    }

    /// <summary>Level-sensor heading in degrees from the last reading.</summary>
    public double HeadingDegrees(double declinationDegrees) => Compass.Heading(X, Y, declinationDegrees);
}

/// <summary>
/// ST LIS3MDL 3-axis magnetometer at I2C 0x1C (SDO low) or 0x1E. Full
/// scales ±4/8/12/16 gauss at 6842/3421/2281/1711 LSB per gauss;
/// temperature 8 LSB/°C around 25 °C.
/// </summary>
public class Lis3mdl
{
    public const int DefaultAddress = 0x1C;
    public const int AlternateAddress = 0x1E;
    public const int WhoAmIValue = 0x3D;

    private const int RegWhoAmI = 0x0F;
    private const int RegCtrl1 = 0x20;
    private const int RegCtrl2 = 0x21;
    private const int RegCtrl3 = 0x22;
    private const int RegCtrl4 = 0x23;
    private const int RegCtrl5 = 0x24;
    private const int RegStatus = 0x27;
    private const int RegOutXL = 0x28;
    private const int RegTempOutL = 0x2E;
    private const int AutoIncrement = 0x80;

    private readonly I2cDevice _dev;

    /// <summary>Full scale in gauss: 4, 8, 12 or 16.</summary>
    public int RangeGauss = 4;

    public int RawX, RawY, RawZ;

    /// <summary>Field in µT.</summary>
    public double X, Y, Z;

    public Lis3mdl(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Lis3mdl(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Lis3mdl(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>
    /// Check WHO_AM_I, soft-reset, then ultra-high-performance XY and Z at
    /// 80 Hz with the temperature sensor on, BDU, and continuous conversion.
    /// </summary>
    public bool Begin()
    {
        if (_dev.ReadRegister(RegWhoAmI) != WhoAmIValue)
        {
            return false;
        }
        _dev.WriteRegister(RegCtrl2, 0x04); // SOFT_RST
        Timing.SleepMs(10);
        _dev.WriteRegister(RegCtrl1, 0xFC); // TEMP_EN, OM=11 (UHP), DO=111 (80 Hz)
        SetRange(RangeGauss);
        _dev.WriteRegister(RegCtrl4, 0x0C); // OMZ=11 (UHP)
        _dev.WriteRegister(RegCtrl5, 0x40); // BDU
        _dev.WriteRegister(RegCtrl3, 0x00); // continuous
        return true;
    }

    public void SetRange(int gauss)
    {
        RangeGauss = gauss;
        int fs = gauss >= 16 ? 3 : gauss >= 12 ? 2 : gauss >= 8 ? 1 : 0;
        _dev.WriteRegister(RegCtrl2, fs << 5);
    }

    /// <summary>LSB per gauss for the current full scale.</summary>
    public int LsbPerGauss => RangeGauss >= 16 ? 1711 : RangeGauss >= 12 ? 2281 : RangeGauss >= 8 ? 3421 : 6842;

    /// <summary>STATUS_REG.ZYXDA (bit 3).</summary>
    public bool DataReady() => (_dev.ReadRegister(RegStatus) & 0x08) != 0;

    /// <summary>Read OUT_X_L..OUT_Z_H (sub-address MSB set for auto-increment).</summary>
    public void Read()
    {
        byte[] d = _dev.ReadRegisters(RegOutXL | AutoIncrement, 6);
        RawX = BitOps.ToInt16(d[0] | (d[1] << 8));
        RawY = BitOps.ToInt16(d[2] | (d[3] << 8));
        RawZ = BitOps.ToInt16(d[4] | (d[5] << 8));
        double s = 100.0 / LsbPerGauss;
        X = RawX * s;
        Y = RawY * s;
        Z = RawZ * s;
    }

    /// <summary>Die temperature, °C (25 °C + raw/8).</summary>
    public double ReadTemperatureCelsius()
    {
        byte[] d = _dev.ReadRegisters(RegTempOutL | AutoIncrement, 2);
        return 25.0 + BitOps.ToInt16(d[0] | (d[1] << 8)) / 8.0;
    }

    public double HeadingDegrees(double declinationDegrees) => Compass.Heading(X, Y, declinationDegrees);
}

/// <summary>
/// NXP MAG3110 3-axis magnetometer at I2C 0x0E: ±1000 µT, 0.1 µT/LSB,
/// big-endian data, user offsets, and an uncalibrated die temperature
/// (1 °C/LSB).
/// </summary>
public class Mag3110
{
    public const int DefaultAddress = 0x0E;
    public const int WhoAmIValue = 0xC4;

    private const int RegDrStatus = 0x00;
    private const int RegOutXMsb = 0x01;
    private const int RegWhoAmI = 0x07;
    private const int RegOffXMsb = 0x09;
    private const int RegDieTemp = 0x0F;
    private const int RegCtrl1 = 0x10;
    private const int RegCtrl2 = 0x11;

    private readonly I2cDevice _dev;

    /// <summary>
    /// CTRL_REG1 DR/OS bits (7:3): 0x00 = 80 Hz output with 16× oversampling
    /// (1280 Hz ADC rate); higher codes trade rate for noise.
    /// </summary>
    public int RateCode;

    public int RawX, RawY, RawZ;

    /// <summary>Field in µT.</summary>
    public double X, Y, Z;

    public Mag3110(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Mag3110(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check WHO_AM_I, enable automatic sensor resets, and start active mode.</summary>
    public bool Begin()
    {
        if (_dev.ReadRegister(RegWhoAmI) != WhoAmIValue)
        {
            return false;
        }
        _dev.WriteRegister(RegCtrl1, 0x00); // standby while configuring
        _dev.WriteRegister(RegCtrl2, 0x80); // AUTO_MRST_EN, corrected (user offsets applied)
        _dev.WriteRegister(RegCtrl1, ((RateCode & 0x1F) << 3) | 0x01); // AC = active
        return true;
    }

    /// <summary>DR_STATUS.ZYXDR (bit 3).</summary>
    public bool DataReady() => (_dev.ReadRegister(RegDrStatus) & 0x08) != 0;

    public void Read()
    {
        byte[] d = _dev.ReadRegisters(RegOutXMsb, 6);
        RawX = BitOps.ToInt16((d[0] << 8) | d[1]);
        RawY = BitOps.ToInt16((d[2] << 8) | d[3]);
        RawZ = BitOps.ToInt16((d[4] << 8) | d[5]);
        X = RawX * 0.1;
        Y = RawY * 0.1;
        Z = RawZ * 0.1;
    }

    /// <summary>Write the user offsets (raw LSB, applied before the output registers). Device must be in standby.</summary>
    public void SetOffsets(int x, int y, int z)
    {
        byte[] b = new byte[6];
        // OFF_x registers hold the offset shifted left by one (bit 0 unused).
        b[0] = (byte)((x << 1) >> 8);
        b[1] = (byte)(x << 1);
        b[2] = (byte)((y << 1) >> 8);
        b[3] = (byte)(y << 1);
        b[4] = (byte)((z << 1) >> 8);
        b[5] = (byte)(z << 1);
        _dev.WriteRegisters(RegOffXMsb, b);
    }

    /// <summary>Die temperature in °C (signed, uncalibrated offset).</summary>
    public int ReadDieTemperature()
    {
        int t = _dev.ReadRegister(RegDieTemp);
        return t >= 128 ? t - 256 : t;
    }

    public double HeadingDegrees(double declinationDegrees) => Compass.Heading(X, Y, declinationDegrees);
}
