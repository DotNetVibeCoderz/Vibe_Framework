using RustNet.Community.Bus;

namespace RustNet.Community.Bmi270;

/// <summary>
/// Bosch BMI270 6-axis IMU over I2C (address 0x68 with SDO low, 0x69 high).
///
/// The BMI270 does not measure anything until its 8 KB microcode ("config
/// file") has been uploaded after every power-up. That blob is Bosch's
/// (bmi270_config_file[] in BMI270_SensorAPI, BSD-3-Clause) and is not
/// shipped with this package: pass it to <see cref="Begin(byte[])"/>, e.g.
/// read from a file or an embedded resource.
///
/// Scale: accelerometer 2^15 / range LSB per g (16384 at ±2 g), gyroscope
/// 2^15 / range LSB per dps (16.384 at ±2000 dps), temperature
/// raw / 512 + 23 °C.
/// </summary>
public class Bmi270
{
    public const int DefaultAddress = 0x68;
    public const int AlternateAddress = 0x69;
    public const int ChipIdValue = 0x24;

    /// <summary>The Bosch config file is exactly this long.</summary>
    public const int ConfigFileLength = 8192;

    // ACC_RANGE codes.
    public const int Accel2G = 0;
    public const int Accel4G = 1;
    public const int Accel8G = 2;
    public const int Accel16G = 3;

    // GYR_RANGE codes.
    public const int Gyro2000 = 0;
    public const int Gyro1000 = 1;
    public const int Gyro500 = 2;
    public const int Gyro250 = 3;
    public const int Gyro125 = 4;

    private const int RegChipId = 0x00;
    private const int RegErr = 0x02;
    private const int RegStatus = 0x03;
    private const int RegData8 = 0x0C;
    private const int RegInternalStatus = 0x21;
    private const int RegTemperature0 = 0x22;
    private const int RegAccConf = 0x40;
    private const int RegAccRange = 0x41;
    private const int RegGyrConf = 0x42;
    private const int RegGyrRange = 0x43;
    private const int RegInitCtrl = 0x59;
    private const int RegInitAddr0 = 0x5B;
    private const int RegInitData = 0x5E;
    private const int RegPwrConf = 0x7C;
    private const int RegPwrCtrl = 0x7D;
    private const int RegCmd = 0x7E;

    private readonly I2cDevice _dev;

    /// <summary>Bytes per INIT_DATA burst (must be even; limited by the I2C driver's buffer).</summary>
    public int ChunkSize = 64;

    public int AccelRange = Accel4G;
    public int GyroRange = Gyro2000;

    /// <summary>INTERNAL_STATUS.message after the upload: 1 = init OK.</summary>
    public int InitStatus;

    public int RawAccelX, RawAccelY, RawAccelZ;
    public int RawGyroX, RawGyroY, RawGyroZ;

    /// <summary>Acceleration in g.</summary>
    public double AccelX, AccelY, AccelZ;

    /// <summary>Angular rate in dps.</summary>
    public double GyroX, GyroY, GyroZ;

    public Bmi270(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Bmi270(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Bmi270(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>CHIP_ID check only (no configuration).</summary>
    public bool IsPresent() => _dev.ReadRegister(RegChipId) == ChipIdValue;

    /// <summary>
    /// Datasheet "quick start" initialisation: soft reset, disable advanced
    /// power save, upload <paramref name="configFile"/>, check that
    /// INTERNAL_STATUS reports "init OK", then enable accelerometer, gyroscope
    /// and temperature in performance mode (accel 100 Hz, gyro 200 Hz).
    /// Returns false when the chip is absent or the upload is rejected.
    /// </summary>
    public bool Begin(byte[] configFile)
    {
        if (!IsPresent())
        {
            return false;
        }
        _dev.WriteRegister(RegCmd, 0xB6); // softreset
        Timing.SleepMs(2);
        _dev.WriteRegister(RegPwrConf, 0x00); // adv_power_save off
        Timing.SleepMs(1);
        _dev.WriteRegister(RegInitCtrl, 0x00);
        UploadConfig(configFile);
        _dev.WriteRegister(RegInitCtrl, 0x01);
        Timing.SleepMs(20);
        InitStatus = _dev.ReadRegister(RegInternalStatus) & 0x0F;
        if (InitStatus != 1)
        {
            return false;
        }
        _dev.WriteRegister(RegPwrCtrl, 0x0E); // acc_en | gyr_en | temp_en
        _dev.WriteRegister(RegAccConf, 0xA8); // filter_perf, bwp normal, odr 100 Hz
        _dev.WriteRegister(RegGyrConf, 0xE9); // filter_perf, noise_perf, bwp normal, odr 200 Hz
        _dev.WriteRegister(RegPwrConf, 0x02); // fifo_self_wake_up, adv_power_save off
        SetAccelRange(AccelRange);
        SetGyroRange(GyroRange);
        return true;
    }

    /// <summary>Burst the config file into INIT_DATA, setting INIT_ADDR (in 16-bit words) before each chunk.</summary>
    public void UploadConfig(byte[] configFile)
    {
        int chunk = ChunkSize & ~1;
        for (int offset = 0; offset < configFile.Length; offset += chunk)
        {
            int n = configFile.Length - offset < chunk ? configFile.Length - offset : chunk;
            byte[] addr = new byte[2];
            addr[0] = (byte)((offset / 2) & 0x0F);
            addr[1] = (byte)((offset / 2) >> 4);
            _dev.WriteRegisters(RegInitAddr0, addr);
            byte[] data = new byte[n];
            for (int i = 0; i < n; i++)
            {
                data[i] = configFile[offset + i];
            }
            _dev.WriteRegisters(RegInitData, data);
        }
    }

    public void SetAccelRange(int range)
    {
        AccelRange = range & 3;
        _dev.WriteRegister(RegAccRange, AccelRange);
    }

    public void SetGyroRange(int range)
    {
        GyroRange = range > 4 ? 4 : range;
        _dev.WriteRegister(RegGyrRange, GyroRange);
    }

    /// <summary>LSB per g: 16384, 8192, 4096, 2048.</summary>
    public double AccelLsbPerG => 16384.0 / (1 << AccelRange);

    /// <summary>LSB per dps: 32768 / (2000 >> code).</summary>
    public double GyroLsbPerDps => 32768.0 / (2000 >> GyroRange);

    /// <summary>STATUS: bit 7 drdy_acc, bit 6 drdy_gyr.</summary>
    public int ReadStatus() => _dev.ReadRegister(RegStatus);

    /// <summary>ERR_REG (bit 0 fatal, bits 4:1 internal error code).</summary>
    public int ReadError() => _dev.ReadRegister(RegErr);

    /// <summary>Read accelerometer (DATA_8..13) and gyroscope (DATA_14..19) in one 12-byte burst.</summary>
    public void Read()
    {
        byte[] d = _dev.ReadRegisters(RegData8, 12);
        RawAccelX = Le(d, 0);
        RawAccelY = Le(d, 2);
        RawAccelZ = Le(d, 4);
        RawGyroX = Le(d, 6);
        RawGyroY = Le(d, 8);
        RawGyroZ = Le(d, 10);
        double a = AccelLsbPerG;
        AccelX = RawAccelX / a;
        AccelY = RawAccelY / a;
        AccelZ = RawAccelZ / a;
        double g = GyroLsbPerDps;
        GyroX = RawGyroX / g;
        GyroY = RawGyroY / g;
        GyroZ = RawGyroZ / g;
    }

    /// <summary>
    /// Temperature in °C; NaN when the sensor reports 0x8000 (no valid value).
    /// Test with <c>t != t</c>: double.IsNaN is not an interpreter intrinsic.
    /// </summary>
    public double ReadTemperatureCelsius()
    {
        byte[] d = _dev.ReadRegisters(RegTemperature0, 2);
        int raw = d[0] | (d[1] << 8);
        if (raw == 0x8000)
        {
            return double.NaN;
        }
        return BitOps.ToInt16(raw) / 512.0 + 23.0;
    }

    private static int Le(byte[] d, int i) => BitOps.ToInt16(d[i] | (d[i + 1] << 8));
}
