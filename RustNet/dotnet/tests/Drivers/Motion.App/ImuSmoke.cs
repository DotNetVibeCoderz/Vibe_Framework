using RustNet.Community.Bmi270;
using RustNet.Community.Bno055;
using RustNet.Community.Bus;
using RustNet.Community.Lsm303agr;
using RustNet.Community.Lsm6dsox;
using RustNet.Community.Mpu6050;

namespace DriversApp;

/// <summary>Accelerometer/gyroscope/fusion IMUs. Expected values are the datasheet scale factors applied by hand.</summary>
public static class ImuSmoke
{
    public static void RunMpu6050()
    {
        SimI2cDevice sim = new SimI2cDevice(0x68);
        sim.SetRegister(0x75, 0x68);
        sim.SetRegister16(0x3B, 16384);
        sim.SetRegister16(0x3D, -8192);
        sim.SetRegister16(0x3F, 0);
        sim.SetRegister16(0x41, -3740);
        sim.SetRegister16(0x43, 131);
        sim.SetRegister16(0x45, -262);
        sim.SetRegister16(0x47, 0);
        Mpu6050 m = new Mpu6050(sim);
        Check.True("mpu6050 begin", m.Begin());
        Check.Equal("mpu6050 awake on pll", 0x01, sim.Registers[0x6B]);
        Check.Equal("mpu6050 accel fs", 0x00, sim.Registers[0x1C]);
        m.Read();
        // ±2 g: 16384 LSB/g; ±250 dps: 131 LSB/dps; T = raw/340 + 36.53.
        Check.Near("mpu6050 ax", 1.0, m.AccelX, 1e-9);
        Check.Near("mpu6050 ay", -0.5, m.AccelY, 1e-9);
        Check.Near("mpu6050 gx", 1.0, m.GyroX, 1e-9);
        Check.Near("mpu6050 gy", -2.0, m.GyroY, 1e-9);
        Check.Near("mpu6050 temperature", 25.53, m.TemperatureCelsius, 1e-9);
        m.SetAccelRange(Mpu6050.Accel16G);
        m.SetGyroRange(Mpu6050.Gyro2000);
        Check.Equal("mpu6050 accel fs 16g", 0x18, sim.Registers[0x1C]);
        Check.Equal("mpu6050 gyro fs 2000", 0x18, sim.Registers[0x1B]);
        sim.SetRegister16(0x3B, 2048);
        sim.SetRegister16(0x43, 1640);
        m.Read();
        Check.Near("mpu6050 ax 16g", 1.0, m.AccelX, 1e-9);
        Check.Near("mpu6050 gx 2000", 100.0, m.GyroX, 1e-9);
        Check.True("mpu6050 absent", !new Mpu6050(new SimI2cDevice(0x68)).Begin());
    }

    public static void RunLsm6dsox()
    {
        SimI2cDevice sim = new SimI2cDevice(0x6A);
        sim.SetRegister(0x0F, 0x6C);
        sim.SetRegister16Le(0x20, 256);
        sim.SetRegister16Le(0x22, 1000);
        sim.SetRegister16Le(0x24, -500);
        sim.SetRegister16Le(0x26, 0);
        sim.SetRegister16Le(0x28, 8197);
        sim.SetRegister16Le(0x2A, -8197);
        sim.SetRegister16Le(0x2C, 0);
        Lsm6dsox imu = new Lsm6dsox(sim);
        Check.True("lsm6dsox begin", imu.Begin());
        // 104 Hz, ±4 g (FS_XL=10) and 2000 dps (FS_G=11).
        Check.Equal("lsm6dsox ctrl1_xl", 0x48, sim.Registers[0x10]);
        Check.Equal("lsm6dsox ctrl2_g", 0x4C, sim.Registers[0x11]);
        Check.Equal("lsm6dsox ctrl3_c", 0x44, sim.Registers[0x12]);
        imu.Read();
        // 0.122 mg/LSB at ±4 g; 70 mdps/LSB at 2000 dps; 256 LSB/°C from 25 °C.
        Check.Near("lsm6dsox ax", 1.000034, imu.AccelX, 1e-9);
        Check.Near("lsm6dsox ay", -1.000034, imu.AccelY, 1e-9);
        Check.Near("lsm6dsox gx", 70.0, imu.GyroX, 1e-9);
        Check.Near("lsm6dsox gy", -35.0, imu.GyroY, 1e-9);
        Check.Near("lsm6dsox temperature", 26.0, imu.TemperatureCelsius, 1e-9);
        imu.AccelRangeG = Lsm6dsox.Accel16G;
        imu.GyroRangeDps = Lsm6dsox.Gyro125;
        imu.Configure();
        Check.Equal("lsm6dsox ctrl1_xl 16g", 0x44, sim.Registers[0x10]);
        Check.Equal("lsm6dsox ctrl2_g 125", 0x42, sim.Registers[0x11]);
        imu.Read();
        Check.Near("lsm6dsox ax 16g", 8197 * 0.000488, imu.AccelX, 1e-9);
        Check.Near("lsm6dsox gx 125", 4.375, imu.GyroX, 1e-9);
    }

    public static void RunLsm303agr()
    {
        SimI2cDevice acc = new SimI2cDevice(0x19);
        SimI2cDevice mag = new SimI2cDevice(0x1E);
        acc.SetRegister(0x0F, 0x33);
        mag.SetRegister(0x4F, 0x40);
        // Auto-increment reads address 0x28 | 0x80, so the register file is read from 0xA8.
        acc.SetRegister16Le(0xA8, 1024 << 4);
        acc.SetRegister16Le(0xAA, 0);
        acc.SetRegister16Le(0xAC, -512 << 4);
        mag.SetRegister16Le(0x68, 1000);
        mag.SetRegister16Le(0x6A, -200);
        mag.SetRegister16Le(0x6C, 0);
        Lsm303agr s = new Lsm303agr(acc, mag);
        Check.True("lsm303agr begin", s.Begin());
        Check.Equal("lsm303agr ctrl1_a", 0x57, acc.Registers[0x20]);
        Check.Equal("lsm303agr ctrl4_a", 0x88, acc.Registers[0x23]);
        Check.Equal("lsm303agr cfg_a_m", 0x80, mag.Registers[0x60]);
        s.ReadAccel();
        // High resolution ±2 g: 12-bit, 0.98 mg/digit.
        Check.Equal("lsm303agr raw x", 1024, s.RawAccelX);
        Check.Near("lsm303agr ax", 1.00352, s.AccelX, 1e-9);
        Check.Near("lsm303agr az", -0.50176, s.AccelZ, 1e-9);
        s.ReadMag();
        // 1.5 mgauss/LSB = 0.15 µT.
        Check.Near("lsm303agr mx", 150.0, s.MagX, 1e-9);
        Check.Near("lsm303agr my", -30.0, s.MagY, 1e-9);
        // Level: heading = atan2(-my, mx).
        s.AccelX = 0;
        s.AccelY = 0;
        s.AccelZ = 1;
        s.MagX = 0;
        s.MagY = -20;
        s.MagZ = -40;
        Check.Near("lsm303agr heading east", 90.0, s.HeadingDegrees(), 1e-6);
        s.MagX = 20;
        s.MagY = 0;
        Check.Near("lsm303agr heading north", 0.0, s.HeadingDegrees(), 1e-6);
    }

    public static void RunBno055()
    {
        SimI2cDevice sim = new SimI2cDevice(0x28);
        sim.SetRegister(0x00, 0xA0);
        sim.SetRegister(0x36, 0x0F);
        sim.SetRegister16Le(0x1A, 1440);
        sim.SetRegister16Le(0x1C, -720);
        sim.SetRegister16Le(0x1E, 168);
        sim.SetRegister16Le(0x20, 16384);
        sim.SetRegister16Le(0x22, -8192);
        sim.SetRegister16Le(0x24, 0);
        sim.SetRegister16Le(0x26, 4096);
        sim.SetRegister16Le(0x08, 981);
        sim.SetRegister16Le(0x0E, 400);
        sim.SetRegister16Le(0x14, -32);
        sim.SetRegister(0x34, 0xE7);
        sim.SetRegister(0x35, 0xE4);
        for (int i = 0; i < 22; i++)
        {
            sim.SetRegister(0x55 + i, i + 1);
        }
        Bno055 b = new Bno055(sim);
        Check.True("bno055 begin", b.Begin(Bno055.ModeNdof));
        Check.Equal("bno055 ndof mode", 0x0C, sim.Registers[0x3D]);
        Check.Equal("bno055 self test", 0x0F, b.SelfTestResult);
        bool reset = false;
        for (int i = 0; i < sim.Writes.Count; i++)
        {
            byte[] w = sim.Writes[i];
            if (w.Length == 2 && w[0] == 0x3F && w[1] == 0x20)
            {
                reset = true;
            }
        }
        Check.True("bno055 reset sent", reset);
        // Euler 16 LSB/degree.
        b.ReadEuler();
        Check.Near("bno055 heading", 90.0, b.Heading, 1e-9);
        Check.Near("bno055 roll", -45.0, b.Roll, 1e-9);
        Check.Near("bno055 pitch", 10.5, b.Pitch, 1e-9);
        // Quaternion 2^14 LSB per unit.
        b.ReadQuaternion();
        Check.Near("bno055 qw", 1.0, b.QuatW, 1e-9);
        Check.Near("bno055 qx", -0.5, b.QuatX, 1e-9);
        Check.Near("bno055 qz", 0.25, b.QuatZ, 1e-9);
        Check.Near("bno055 accel", 9.81, b.ReadAcceleration()[0], 1e-9);
        Check.Near("bno055 mag", 25.0, b.ReadMagnetometer()[0], 1e-9);
        Check.Near("bno055 gyro", -2.0, b.ReadGyroscope()[0], 1e-9);
        Check.Equal("bno055 temperature", -25, b.ReadTemperature());
        Bno055Calibration c = b.ReadCalibration();
        Check.Equal("bno055 cal sys", 3, c.System);
        Check.Equal("bno055 cal gyro", 2, c.Gyroscope);
        Check.Equal("bno055 cal accel", 1, c.Accelerometer);
        Check.Equal("bno055 cal mag", 0, c.Magnetometer);
        Check.True("bno055 not fully calibrated", !c.FullyCalibrated);
        byte[] profile = b.ReadCalibrationProfile();
        Check.Equal("bno055 profile length", 22, profile.Length);
        Check.Equal("bno055 profile last", 22, profile[21]);
        Check.Equal("bno055 back in ndof", 0x0C, sim.Registers[0x3D]);
        Check.True("bno055 absent", !new Bno055(new SimI2cDevice(0x28)).Begin(Bno055.ModeNdof));
    }

    public static void RunBmi270()
    {
        SimI2cDevice sim = new SimI2cDevice(0x68);
        sim.SetRegister(0x00, 0x24);
        sim.SetRegister(0x21, 0x01);
        // DATA_8.. accel then gyro, little-endian.
        sim.SetRegister16Le(0x0C, 8192);
        sim.SetRegister16Le(0x0E, -4096);
        sim.SetRegister16Le(0x10, 0);
        sim.SetRegister16Le(0x12, 16384);
        sim.SetRegister16Le(0x14, -1638);
        sim.SetRegister16Le(0x16, 0);
        sim.SetRegister16Le(0x22, 512);
        byte[] blob = new byte[200];
        for (int i = 0; i < blob.Length; i++)
        {
            blob[i] = (byte)(i * 7 + 3);
        }
        Bmi270UploadLog log = new Bmi270UploadLog();
        sim.OnWrite = log.OnWrite;
        List<byte> uploaded = log.Uploaded;
        List<int> words = log.Words;
        Bmi270 imu = new Bmi270(sim);
        imu.ChunkSize = 50;
        Check.True("bmi270 begin", imu.Begin(blob));
        Check.Equal("bmi270 init status", 1, imu.InitStatus);
        Check.Equal("bmi270 uploaded bytes", 200, uploaded.Count);
        bool same = uploaded.Count == 200;
        for (int i = 0; i < uploaded.Count && same; i++)
        {
            same = uploaded[i] == blob[i];
        }
        Check.True("bmi270 blob intact", same);
        // INIT_ADDR counts 16-bit words: chunks at bytes 0, 50, 100, 150.
        Check.Equal("bmi270 chunks", 4, words.Count);
        Check.Equal("bmi270 addr 1", 25, words[1]);
        Check.Equal("bmi270 addr 3", 75, words[3]);
        Check.Equal("bmi270 pwr ctrl", 0x0E, sim.Registers[0x7D]);
        Check.Equal("bmi270 init ctrl", 0x01, sim.Registers[0x59]);
        Check.Equal("bmi270 acc range", 1, sim.Registers[0x41]);
        imu.Read();
        // ±4 g: 8192 LSB/g; ±2000 dps: 16.384 LSB/dps.
        Check.Near("bmi270 ax", 1.0, imu.AccelX, 1e-9);
        Check.Near("bmi270 ay", -0.5, imu.AccelY, 1e-9);
        Check.Near("bmi270 gx", 1000.0, imu.GyroX, 1e-9);
        Check.Near("bmi270 gy", -99.9755859375, imu.GyroY, 1e-9);
        // raw/512 + 23 °C.
        Check.Near("bmi270 temperature", 24.0, imu.ReadTemperatureCelsius(), 1e-9);
        sim.SetRegister16Le(0x22, 0x8000);
        // NaN is the only value unequal to itself (double.IsNaN is not an interpreter intrinsic).
        double invalid = imu.ReadTemperatureCelsius();
        Check.True("bmi270 invalid temperature", invalid != invalid);

        SimI2cDevice bad = new SimI2cDevice(0x68);
        bad.SetRegister(0x00, 0x24);
        bad.SetRegister(0x21, 0x02); // init_err
        Check.True("bmi270 rejected config", !new Bmi270(bad).Begin(blob));
    }
}

/// <summary>Records INIT_ADDR / INIT_DATA traffic of a BMI270 config upload.</summary>
public class Bmi270UploadLog
{
    public List<byte> Uploaded = new List<byte>();
    public List<int> Words = new List<int>();
    public int PendingAddr = -1;

    public void OnWrite(SimI2cDevice d, byte[] w)
    {
        if (w.Length == 3 && w[0] == 0x5B)
        {
            PendingAddr = (w[1] & 0x0F) | (w[2] << 4);
        }
        if (w.Length > 1 && w[0] == 0x5E)
        {
            Words.Add(PendingAddr);
            for (int i = 1; i < w.Length; i++)
            {
                Uploaded.Add(w[i]);
            }
        }
    }
}
