using RustNet.Community.Bus;

namespace RustNet.Community.Lsm303agr;

/// <summary>
/// ST LSM303AGR eCompass: accelerometer at I2C 0x19 and magnetometer at
/// 0x1E (two devices on one package). Datasheet sensitivities:
/// accelerometer high-resolution (12-bit) 0.98/1.95/3.9/11.72 mg/digit,
/// normal (10-bit) 3.9/7.82/15.63/46.9, low-power (8-bit)
/// 15.63/31.26/62.52/187.58 for ±2/4/8/16 g; magnetometer 1.5 mgauss/LSB
/// (0.15 µT/LSB).
/// </summary>
public class Lsm303agr
{
    public const int AccelAddress = 0x19;
    public const int MagAddress = 0x1E;
    public const int AccelWhoAmI = 0x33;
    public const int MagWhoAmI = 0x40;

    public const int ModeLowPower = 0;
    public const int ModeNormal = 1;
    public const int ModeHighResolution = 2;

    // Accelerometer.
    private const int RegWhoAmIA = 0x0F;
    private const int RegCtrl1A = 0x20;
    private const int RegCtrl4A = 0x23;
    private const int RegOutXLA = 0x28;
    private const int AutoIncrement = 0x80;

    // Magnetometer.
    private const int RegWhoAmIM = 0x4F;
    private const int RegCfgAM = 0x60;
    private const int RegCfgCM = 0x62;
    private const int RegStatusM = 0x67;
    private const int RegOutXLM = 0x68;

    private readonly I2cDevice _accel;
    private readonly I2cDevice _mag;

    /// <summary>Accelerometer full scale: 2, 4, 8 or 16 g.</summary>
    public int AccelRangeG = 2;

    public int AccelMode = ModeHighResolution;

    public int RawAccelX, RawAccelY, RawAccelZ;
    public int RawMagX, RawMagY, RawMagZ;

    /// <summary>Acceleration in g.</summary>
    public double AccelX, AccelY, AccelZ;

    /// <summary>Magnetic field in µT.</summary>
    public double MagX, MagY, MagZ;

    public Lsm303agr(int bus) : this(new I2cDevice(bus, AccelAddress), new I2cDevice(bus, MagAddress))
    {
    }

    public Lsm303agr(I2cDevice accel, I2cDevice mag)
    {
        _accel = accel;
        _mag = mag;
    }

    /// <summary>Identify both dies; start the accelerometer at 100 Hz and the magnetometer continuous at 10 Hz.</summary>
    public bool Begin()
    {
        if (_accel.ReadRegister(RegWhoAmIA) != AccelWhoAmI || _mag.ReadRegister(RegWhoAmIM) != MagWhoAmI)
        {
            return false;
        }
        ConfigureAccel();
        // Temperature compensation on, 10 Hz, continuous mode; then BDU.
        _mag.WriteRegister(RegCfgAM, 0x80);
        _mag.WriteRegister(RegCfgCM, 0x10);
        return true;
    }

    /// <summary>Write CTRL_REG1_A (100 Hz, XYZ, LPen) and CTRL_REG4_A (BDU, FS, HR).</summary>
    public void ConfigureAccel()
    {
        int fs = AccelRangeG == 16 ? 3 : AccelRangeG == 8 ? 2 : AccelRangeG == 4 ? 1 : 0;
        _accel.WriteRegister(RegCtrl1A, 0x57 | (AccelMode == ModeLowPower ? 0x08 : 0x00));
        _accel.WriteRegister(RegCtrl4A, 0x80 | (fs << 4) | (AccelMode == ModeHighResolution ? 0x08 : 0x00));
    }

    /// <summary>mg per digit for the current mode and range (datasheet table 3).</summary>
    public double AccelMgPerDigit
    {
        get
        {
            int i = AccelRangeG == 16 ? 3 : AccelRangeG == 8 ? 2 : AccelRangeG == 4 ? 1 : 0;
            if (AccelMode == ModeHighResolution)
            {
                return i == 0 ? 0.98 : i == 1 ? 1.95 : i == 2 ? 3.9 : 11.72;
            }
            if (AccelMode == ModeNormal)
            {
                return i == 0 ? 3.9 : i == 1 ? 7.82 : i == 2 ? 15.63 : 46.9;
            }
            return i == 0 ? 15.63 : i == 1 ? 31.26 : i == 2 ? 62.52 : 187.58;
        }
    }

    /// <summary>Read the three left-justified acceleration words (sub-address MSB set for auto-increment).</summary>
    public void ReadAccel()
    {
        byte[] d = _accel.ReadRegisters(RegOutXLA | AutoIncrement, 6);
        int shift = AccelMode == ModeHighResolution ? 4 : AccelMode == ModeNormal ? 6 : 8;
        RawAccelX = BitOps.ToInt16(d[0] | (d[1] << 8)) >> shift;
        RawAccelY = BitOps.ToInt16(d[2] | (d[3] << 8)) >> shift;
        RawAccelZ = BitOps.ToInt16(d[4] | (d[5] << 8)) >> shift;
        double s = AccelMgPerDigit / 1000.0;
        AccelX = RawAccelX * s;
        AccelY = RawAccelY * s;
        AccelZ = RawAccelZ * s;
    }

    /// <summary>True when STATUS_REG_M.Zyxda says a new magnetometer sample is ready.</summary>
    public bool MagDataReady() => (_mag.ReadRegister(RegStatusM) & 0x08) != 0;

    /// <summary>Read the magnetometer (little-endian, 1.5 mG/LSB) into µT.</summary>
    public void ReadMag()
    {
        byte[] d = _mag.ReadRegisters(RegOutXLM, 6);
        RawMagX = BitOps.ToInt16(d[0] | (d[1] << 8));
        RawMagY = BitOps.ToInt16(d[2] | (d[3] << 8));
        RawMagZ = BitOps.ToInt16(d[4] | (d[5] << 8));
        MagX = RawMagX * 0.15;
        MagY = RawMagY * 0.15;
        MagZ = RawMagZ * 0.15;
    }

    /// <summary>
    /// Tilt-compensated heading in degrees 0..360 (0 = magnetic north along +X)
    /// from the last accelerometer and magnetometer readings (NXP AN4248).
    /// </summary>
    public double HeadingDegrees()
    {
        double roll = Math.Atan2(AccelY, AccelZ);
        double pitch = Math.Atan2(-AccelX, AccelY * Math.Sin(roll) + AccelZ * Math.Cos(roll));
        double bx = MagX * Math.Cos(pitch) + MagY * Math.Sin(pitch) * Math.Sin(roll) + MagZ * Math.Sin(pitch) * Math.Cos(roll);
        double by = MagY * Math.Cos(roll) - MagZ * Math.Sin(roll);
        double h = Math.Atan2(-by, bx) * 180.0 / Math.PI;
        return h < 0 ? h + 360.0 : h;
    }
}
