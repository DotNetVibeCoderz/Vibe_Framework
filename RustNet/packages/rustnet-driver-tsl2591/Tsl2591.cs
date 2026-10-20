using RustNet.Community.Bus;

namespace RustNet.Community.Tsl2591;

/// <summary>
/// ams TSL2591 high dynamic range light-to-digital converter at I2C address
/// 0x29. Channel 0 is full spectrum, channel 1 infrared. Every register
/// access is prefixed by the command byte 0xA0 | register (CMD + normal
/// transaction). Lux follows ams's reference formula with device factor 408.
/// </summary>
public class Tsl2591
{
    public const int DefaultAddress = 0x29;
    public const int IdValue = 0x50;

    private const int Command = 0xA0;
    private const int RegEnable = 0x00;
    private const int RegControl = 0x01;
    private const int RegId = 0x12;
    private const int RegStatus = 0x13;
    private const int RegC0DataL = 0x14;

    /// <summary>Gain codes (CONTROL bits 5:4).</summary>
    public const int GainLow = 0x00;
    public const int GainMedium = 0x10;
    public const int GainHigh = 0x20;
    public const int GainMax = 0x30;

    /// <summary>Integration time codes (CONTROL bits 2:0): (code + 1) · 100 ms.</summary>
    public const int Time100ms = 0;
    public const int Time200ms = 1;
    public const int Time300ms = 2;
    public const int Time400ms = 3;
    public const int Time500ms = 4;
    public const int Time600ms = 5;

    /// <summary>Lux device factor from the ams reference driver.</summary>
    public const double LuxDf = 408.0;

    private readonly I2cDevice _dev;

    public int Gain = GainMedium;
    public int IntegrationTime = Time100ms;

    /// <summary>Full-spectrum (CH0) and infrared (CH1) counts of the last read.</summary>
    public int FullSpectrum, Infrared;

    public Tsl2591(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Tsl2591(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check ID (0x50), program gain/time and power on with the ALS enabled.</summary>
    public bool Begin()
    {
        try
        {
            if (_dev.ReadRegister(Command | RegId) != IdValue)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
        Configure(Gain, IntegrationTime);
        Enable();
        return true;
    }

    public void Configure(int gain, int integrationTime)
    {
        Gain = gain;
        IntegrationTime = integrationTime;
        _dev.WriteRegister(Command | RegControl, gain | integrationTime);
    }

    /// <summary>PON | AEN.</summary>
    public void Enable() => _dev.WriteRegister(Command | RegEnable, 0x03);

    public void Disable() => _dev.WriteRegister(Command | RegEnable, 0x00);

    /// <summary>Gain factor: 1, 25, 428 or 9876 (typical values from the datasheet).</summary>
    public double GainFactor => Gain == GainMax ? 9876.0 : (Gain == GainHigh ? 428.0 : (Gain == GainMedium ? 25.0 : 1.0));

    public int IntegrationTimeMs => (IntegrationTime + 1) * 100;

    /// <summary>Wait for a valid result (AVALID) and read both channels in one 4-byte transfer.</summary>
    public void Read()
    {
        Timing.SleepMs(IntegrationTimeMs + 20);
        for (int i = 0; i < 10 && (_dev.ReadRegister(Command | RegStatus) & 0x01) == 0; i++)
        {
            Timing.SleepMs(20);
        }
        byte[] d = _dev.ReadRegisters(Command | RegC0DataL, 4);
        FullSpectrum = d[0] | (d[1] << 8);
        Infrared = d[2] | (d[3] << 8);
    }

    /// <summary>Lux from the last read; -1 when a channel saturated (lower the gain).</summary>
    public double Lux() => CalculateLux(FullSpectrum, Infrared, IntegrationTimeMs, GainFactor);

    /// <summary>lux = (CH0 - CH1) · (1 - CH1/CH0) / (time · gain / 408).</summary>
    public static double CalculateLux(int ch0, int ch1, double timeMs, double gain)
    {
        if (ch0 >= 0xFFFF || ch1 >= 0xFFFF)
        {
            return -1.0;
        }
        if (ch0 == 0)
        {
            return 0.0;
        }
        double cpl = timeMs * gain / LuxDf;
        return (ch0 - ch1) * (1.0 - (double)ch1 / ch0) / cpl;
    }
}
