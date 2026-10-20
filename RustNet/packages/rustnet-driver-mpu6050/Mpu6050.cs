using RustNet.Community.Bus;

namespace RustNet.Community.Mpu6050;

/// <summary>
/// InvenSense MPU-6050 (and MPU-6000 over I2C): 3-axis accelerometer,
/// 3-axis gyroscope and die temperature. Address 0x68 (AD0 low) or 0x69.
/// Scale factors from the register map / product specification:
/// accelerometer 16384/8192/4096/2048 LSB/g for ±2/4/8/16 g, gyroscope
/// 131/65.5/32.8/16.4 LSB/(°/s) for ±250/500/1000/2000 °/s, and
/// temperature °C = raw/340 + 36.53.
/// </summary>
public class Mpu6050
{
    public const int DefaultAddress = 0x68;
    public const int AlternateAddress = 0x69;
    public const int WhoAmIValue = 0x68;

    public const int Accel2G = 0;
    public const int Accel4G = 1;
    public const int Accel8G = 2;
    public const int Accel16G = 3;

    public const int Gyro250 = 0;
    public const int Gyro500 = 1;
    public const int Gyro1000 = 2;
    public const int Gyro2000 = 3;

    private const int RegSmplrtDiv = 0x19;
    private const int RegConfig = 0x1A;
    private const int RegGyroConfig = 0x1B;
    private const int RegAccelConfig = 0x1C;
    private const int RegAccelXOutH = 0x3B;
    private const int RegPwrMgmt1 = 0x6B;
    private const int RegWhoAmI = 0x75;

    private readonly I2cDevice _dev;

    public int AccelRange = Accel2G;
    public int GyroRange = Gyro250;

    /// <summary>Digital low-pass filter setting (CONFIG.DLPF_CFG, 0..6); 3 = 44 Hz accel / 42 Hz gyro.</summary>
    public int LowPassFilter = 3;

    public int RawAccelX, RawAccelY, RawAccelZ;
    public int RawGyroX, RawGyroY, RawGyroZ;
    public int RawTemperature;

    /// <summary>Acceleration in g.</summary>
    public double AccelX, AccelY, AccelZ;

    /// <summary>Angular rate in °/s.</summary>
    public double GyroX, GyroY, GyroZ;

    /// <summary>Die temperature in °C.</summary>
    public double TemperatureCelsius;

    public Mpu6050(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Mpu6050(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Mpu6050(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>
    /// Check WHO_AM_I, reset, wake with the X-gyro PLL as clock, and apply the
    /// ranges, filter and a 1 kHz/(1+7) = 125 Hz sample rate. False if absent.
    /// </summary>
    public bool Begin()
    {
        // WHO_AM_I holds bits 6:1 of the address; AD0 does not change it.
        if ((_dev.ReadRegister(RegWhoAmI) & 0x7E) != WhoAmIValue)
        {
            return false;
        }
        _dev.WriteRegister(RegPwrMgmt1, 0x80); // DEVICE_RESET
        Timing.SleepMs(100);
        _dev.WriteRegister(RegPwrMgmt1, 0x01); // wake, CLKSEL = PLL with X gyro
        _dev.WriteRegister(RegConfig, LowPassFilter & 7);
        _dev.WriteRegister(RegSmplrtDiv, 7);
        SetAccelRange(AccelRange);
        SetGyroRange(GyroRange);
        return true;
    }

    public void SetAccelRange(int range)
    {
        AccelRange = range & 3;
        _dev.WriteRegister(RegAccelConfig, AccelRange << 3);
    }

    public void SetGyroRange(int range)
    {
        GyroRange = range & 3;
        _dev.WriteRegister(RegGyroConfig, GyroRange << 3);
    }

    /// <summary>LSB per g for the current range.</summary>
    public double AccelLsbPerG => 16384.0 / (1 << AccelRange);

    /// <summary>LSB per °/s for the current range (131, 65.5, 32.8, 16.4).</summary>
    public double GyroLsbPerDps
    {
        get
        {
            if (GyroRange == Gyro250)
            {
                return 131.0;
            }
            if (GyroRange == Gyro500)
            {
                return 65.5;
            }
            if (GyroRange == Gyro1000)
            {
                return 32.8;
            }
            return 16.4;
        }
    }

    /// <summary>Read accelerometer, temperature and gyroscope in one 14-byte burst.</summary>
    public void Read()
    {
        byte[] d = _dev.ReadRegisters(RegAccelXOutH, 14);
        RawAccelX = Be(d, 0);
        RawAccelY = Be(d, 2);
        RawAccelZ = Be(d, 4);
        RawTemperature = Be(d, 6);
        RawGyroX = Be(d, 8);
        RawGyroY = Be(d, 10);
        RawGyroZ = Be(d, 12);
        double a = AccelLsbPerG;
        AccelX = RawAccelX / a;
        AccelY = RawAccelY / a;
        AccelZ = RawAccelZ / a;
        double g = GyroLsbPerDps;
        GyroX = RawGyroX / g;
        GyroY = RawGyroY / g;
        GyroZ = RawGyroZ / g;
        TemperatureCelsius = RawTemperature / 340.0 + 36.53;
    }

    /// <summary>Put the chip to sleep (PWR_MGMT_1.SLEEP) or wake it.</summary>
    public void Sleep(bool sleep) => _dev.WriteRegister(RegPwrMgmt1, sleep ? 0x41 : 0x01);

    private static int Be(byte[] d, int i) => BitOps.ToInt16((d[i] << 8) | d[i + 1]);
}
