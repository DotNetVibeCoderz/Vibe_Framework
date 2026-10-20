using RustNet.Community.Bus;

namespace RustNet.Community.Veml7700;

/// <summary>
/// Vishay VEML7700 high-accuracy ambient light sensor at I2C address 0x10.
/// 16-bit registers are little-endian (LSB first). Lux = ALS count ×
/// resolution, where the resolution is 0.0042 lx/count at gain 2 and 800 ms
/// and scales inversely with gain and integration time (Vishay application
/// note "Designing the VEML7700 into an application").
/// </summary>
public class Veml7700
{
    public const int DefaultAddress = 0x10;
    public const int DeviceIdValue = 0x81;

    public const int RegAlsConf = 0x00;
    public const int RegAlsWh = 0x01;
    public const int RegAlsWl = 0x02;
    public const int RegPowerSaving = 0x03;
    public const int RegAls = 0x04;
    public const int RegWhite = 0x05;
    public const int RegAlsInt = 0x06;
    public const int RegId = 0x07;

    /// <summary>Gain codes (ALS_CONF bits 12:11).</summary>
    public const int Gain1 = 0;
    public const int Gain2 = 1;
    public const int Gain1_8 = 2;
    public const int Gain1_4 = 3;

    /// <summary>Integration time codes (ALS_CONF bits 9:6).</summary>
    public const int It25ms = 0x0C;
    public const int It50ms = 0x08;
    public const int It100ms = 0x00;
    public const int It200ms = 0x01;
    public const int It400ms = 0x02;
    public const int It800ms = 0x03;

    private readonly I2cDevice _dev;

    public int Gain = Gain1_8;
    public int IntegrationTime = It100ms;

    /// <summary>lx per count at gain 2 and 800 ms.</summary>
    public double MaxResolution = 0.0042;

    /// <summary>Raw ALS and WHITE counts of the last read.</summary>
    public int Als, White;

    public Veml7700(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Veml7700(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check the device ID (low byte 0x81), then configure and power on.</summary>
    public bool Begin()
    {
        try
        {
            if ((_dev.ReadUInt16LittleEndian(RegId) & 0xFF) != DeviceIdValue)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
        Configure(Gain, IntegrationTime);
        return true;
    }

    /// <summary>Write ALS_CONF with the gain and integration time codes, persistence 1, interrupt off, powered on.</summary>
    public void Configure(int gain, int integrationTime)
    {
        Gain = gain;
        IntegrationTime = integrationTime;
        WriteRegister16(RegAlsConf, ((gain & 3) << 11) | ((integrationTime & 0x0F) << 6));
        Timing.SleepMs(3);
    }

    /// <summary>Shut down (ALS_SD) to save power.</summary>
    public void Shutdown() => WriteRegister16(RegAlsConf, ((Gain & 3) << 11) | ((IntegrationTime & 0x0F) << 6) | 0x01);

    public void WriteRegister16(int register, int value)
    {
        byte[] b = new byte[3];
        b[0] = (byte)register;
        b[1] = (byte)value;
        b[2] = (byte)(value >> 8);
        _dev.Write(b);
    }

    public double GainFactor => Gain == Gain2 ? 2.0 : (Gain == Gain1_8 ? 0.125 : (Gain == Gain1_4 ? 0.25 : 1.0));

    public int IntegrationTimeMs
    {
        get
        {
            if (IntegrationTime == It25ms)
            {
                return 25;
            }
            if (IntegrationTime == It50ms)
            {
                return 50;
            }
            return 100 << IntegrationTime;
        }
    }

    /// <summary>lx per count for the current gain and integration time.</summary>
    public double Resolution => MaxResolution * (800.0 / IntegrationTimeMs) * (2.0 / GainFactor);

    /// <summary>Read ALS and WHITE counts and return lux (uncorrected).</summary>
    public double ReadLux()
    {
        Als = _dev.ReadUInt16LittleEndian(RegAls);
        White = _dev.ReadUInt16LittleEndian(RegWhite);
        return Als * Resolution;
    }

    /// <summary>Vishay's non-linearity correction for readings above ~1000 lx (gain 1/4 or 1/8).</summary>
    public static double CorrectHighLux(double lux) =>
        6.0135e-13 * lux * lux * lux * lux - 9.3924e-9 * lux * lux * lux + 8.1488e-5 * lux * lux + 1.0023 * lux;
}
