using RustNet.Community.Bus;

namespace RustNet.Community.Bno055;

/// <summary>Calibration levels from CALIB_STAT, each 0 (uncalibrated) .. 3 (fully calibrated).</summary>
public class Bno055Calibration
{
    public int System;
    public int Gyroscope;
    public int Accelerometer;
    public int Magnetometer;

    public bool FullyCalibrated => System == 3 && Gyroscope == 3 && Accelerometer == 3 && Magnetometer == 3;
}

/// <summary>
/// Bosch BNO055 9-axis absolute orientation sensor with on-chip fusion, over
/// I2C at 0x28 (COM3 low) or 0x29. Units are the power-on defaults
/// (UNIT_SEL = 0x80 orientation, m/s², dps, degrees, °C): Euler angles
/// 16 LSB/°, quaternion 2^14 LSB per unit, acceleration/linear/gravity
/// 100 LSB per m/s², gyroscope 16 LSB per dps, magnetometer 16 LSB per µT,
/// temperature 1 °C/LSB.
/// </summary>
public class Bno055
{
    public const int DefaultAddress = 0x28;
    public const int AlternateAddress = 0x29;
    public const int ChipIdValue = 0xA0;

    // Operation modes (OPR_MODE).
    public const int ModeConfig = 0x00;
    public const int ModeAccOnly = 0x01;
    public const int ModeMagOnly = 0x02;
    public const int ModeGyroOnly = 0x03;
    public const int ModeAccMag = 0x04;
    public const int ModeAccGyro = 0x05;
    public const int ModeMagGyro = 0x06;
    public const int ModeAmg = 0x07;
    public const int ModeImu = 0x08;
    public const int ModeCompass = 0x09;
    public const int ModeM4g = 0x0A;
    public const int ModeNdofFmcOff = 0x0B;
    public const int ModeNdof = 0x0C;

    private const int RegChipId = 0x00;
    private const int RegPageId = 0x07;
    private const int RegAccData = 0x08;
    private const int RegMagData = 0x0E;
    private const int RegGyrData = 0x14;
    private const int RegEulData = 0x1A;
    private const int RegQuaData = 0x20;
    private const int RegLiaData = 0x28;
    private const int RegGrvData = 0x2E;
    private const int RegTemp = 0x34;
    private const int RegCalibStat = 0x35;
    private const int RegSelfTest = 0x36;
    private const int RegSysStatus = 0x39;
    private const int RegSysErr = 0x3A;
    private const int RegUnitSel = 0x3B;
    private const int RegOprMode = 0x3D;
    private const int RegPwrMode = 0x3E;
    private const int RegSysTrigger = 0x3F;
    private const int RegOffsets = 0x55;

    /// <summary>Length of the calibration profile (accel, mag, gyro offsets and radii) at 0x55..0x6A.</summary>
    public const int CalibrationProfileLength = 22;

    private readonly I2cDevice _dev;

    public int Mode = ModeConfig;

    /// <summary>Accelerometer, magnetometer, gyroscope and MCU self-test bits (ST_RESULT, 0x0F = all pass).</summary>
    public int SelfTestResult;

    /// <summary>Heading (yaw) 0..360°, roll ±90°, pitch ±180° (degrees, Android orientation).</summary>
    public double Heading, Roll, Pitch;

    /// <summary>Unit quaternion.</summary>
    public double QuatW, QuatX, QuatY, QuatZ;

    public Bno055(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Bno055(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Bno055(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>
    /// Check the chip ID (the part needs up to 850 ms after power-on), reset,
    /// select normal power, page 0, the default units, and enter
    /// <paramref name="mode"/> (e.g. <see cref="ModeNdof"/>).
    /// </summary>
    public bool Begin(int mode)
    {
        int id = _dev.ReadRegister(RegChipId);
        if (id != ChipIdValue)
        {
            Timing.SleepMs(1000);
            id = _dev.ReadRegister(RegChipId);
            if (id != ChipIdValue)
            {
                return false;
            }
        }
        SetMode(ModeConfig);
        _dev.WriteRegister(RegSysTrigger, 0x20); // RST_SYS
        Timing.SleepMs(30);
        for (int i = 0; i < 100 && _dev.ReadRegister(RegChipId) != ChipIdValue; i++)
        {
            Timing.SleepMs(10);
        }
        Timing.SleepMs(50);
        _dev.WriteRegister(RegPwrMode, 0x00); // normal
        _dev.WriteRegister(RegPageId, 0x00);
        _dev.WriteRegister(RegUnitSel, 0x80); // Android orientation, m/s², dps, degrees, °C
        _dev.WriteRegister(RegSysTrigger, 0x00);
        SelfTestResult = _dev.ReadRegister(RegSelfTest) & 0x0F;
        SetMode(mode);
        return true;
    }

    /// <summary>Change OPR_MODE, waiting the datasheet switching time (19 ms to config, 7 ms from it).</summary>
    public void SetMode(int mode)
    {
        _dev.WriteRegister(RegOprMode, mode);
        Timing.SleepMs(mode == ModeConfig ? 19 : 7);
        Mode = mode;
    }

    /// <summary>Use an external 32.768 kHz crystal (SYS_TRIGGER.CLK_SEL). Switches through config mode.</summary>
    public void UseExternalCrystal(bool external)
    {
        int keep = Mode;
        SetMode(ModeConfig);
        _dev.WriteRegister(RegSysTrigger, external ? 0x80 : 0x00);
        Timing.SleepMs(10);
        SetMode(keep);
    }

    /// <summary>Read the Euler angles (heading, roll, pitch) in degrees.</summary>
    public void ReadEuler()
    {
        byte[] d = _dev.ReadRegisters(RegEulData, 6);
        Heading = S16(d, 0) / 16.0;
        Roll = S16(d, 2) / 16.0;
        Pitch = S16(d, 4) / 16.0;
    }

    /// <summary>Read the orientation quaternion (w, x, y, z), scaled by 1/2^14.</summary>
    public void ReadQuaternion()
    {
        byte[] d = _dev.ReadRegisters(RegQuaData, 8);
        double s = 1.0 / 16384.0;
        QuatW = S16(d, 0) * s;
        QuatX = S16(d, 2) * s;
        QuatY = S16(d, 4) * s;
        QuatZ = S16(d, 6) * s;
    }

    /// <summary>Raw acceleration incl. gravity, m/s² (x, y, z).</summary>
    public double[] ReadAcceleration() => Vector(RegAccData, 100.0);

    /// <summary>Linear acceleration (gravity removed by the fusion), m/s².</summary>
    public double[] ReadLinearAcceleration() => Vector(RegLiaData, 100.0);

    /// <summary>Gravity vector, m/s².</summary>
    public double[] ReadGravity() => Vector(RegGrvData, 100.0);

    /// <summary>Angular rate, dps.</summary>
    public double[] ReadGyroscope() => Vector(RegGyrData, 16.0);

    /// <summary>Magnetic field, µT.</summary>
    public double[] ReadMagnetometer() => Vector(RegMagData, 16.0);

    /// <summary>Temperature in °C (signed byte).</summary>
    public int ReadTemperature()
    {
        int t = _dev.ReadRegister(RegTemp);
        return t >= 128 ? t - 256 : t;
    }

    /// <summary>CALIB_STAT: system bits 7:6, gyro 5:4, accel 3:2, mag 1:0.</summary>
    public Bno055Calibration ReadCalibration()
    {
        int c = _dev.ReadRegister(RegCalibStat);
        Bno055Calibration r = new Bno055Calibration();
        r.System = (c >> 6) & 3;
        r.Gyroscope = (c >> 4) & 3;
        r.Accelerometer = (c >> 2) & 3;
        r.Magnetometer = c & 3;
        return r;
    }

    /// <summary>SYS_STATUS: 0 idle, 1 error, 5 fusion running, 6 running without fusion.</summary>
    public int ReadSystemStatus() => _dev.ReadRegister(RegSysStatus);

    /// <summary>SYS_ERR code (valid when the status is 1).</summary>
    public int ReadSystemError() => _dev.ReadRegister(RegSysErr);

    /// <summary>Read the 22-byte calibration profile (switches through config mode).</summary>
    public byte[] ReadCalibrationProfile()
    {
        int keep = Mode;
        SetMode(ModeConfig);
        byte[] p = _dev.ReadRegisters(RegOffsets, CalibrationProfileLength);
        SetMode(keep);
        return p;
    }

    /// <summary>Restore a profile saved with <see cref="ReadCalibrationProfile"/> (switches through config mode).</summary>
    public void WriteCalibrationProfile(byte[] profile)
    {
        int keep = Mode;
        SetMode(ModeConfig);
        _dev.WriteRegisters(RegOffsets, profile);
        SetMode(keep);
    }

    private double[] Vector(int register, double lsbPerUnit)
    {
        byte[] d = _dev.ReadRegisters(register, 6);
        double[] v = new double[3];
        v[0] = S16(d, 0) / lsbPerUnit;
        v[1] = S16(d, 2) / lsbPerUnit;
        v[2] = S16(d, 4) / lsbPerUnit;
        return v;
    }

    private static int S16(byte[] d, int i) => BitOps.ToInt16(d[i] | (d[i + 1] << 8));
}
