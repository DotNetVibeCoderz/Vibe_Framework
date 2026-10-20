using RustNet.Community.Bus;

namespace RustNet.Community.Tcs34725;

/// <summary>
/// ams TCS3472x colour light-to-digital converter (TCS34721/34723/34725/34727)
/// with red, green, blue and clear channels at I2C address 0x29. Register
/// accesses use the command bit 0x80 (with auto-increment 0x20 for block
/// reads). Lux and correlated colour temperature follow ams design note DN40.
/// </summary>
public class Tcs34725
{
    public const int DefaultAddress = 0x29;
    /// <summary>ID register: 0x44 for TCS34721/34725, 0x4D for TCS34723/34727.</summary>
    public const int Id34725 = 0x44;
    public const int Id34727 = 0x4D;

    private const int Command = 0x80;
    private const int AutoIncrement = 0x20;
    private const int RegEnable = 0x00;
    private const int RegAtime = 0x01;
    private const int RegWtime = 0x03;
    private const int RegConfig = 0x0D;
    private const int RegControl = 0x0F;
    private const int RegId = 0x12;
    private const int RegStatus = 0x13;
    private const int RegCData = 0x14;

    public const int Gain1x = 0;
    public const int Gain4x = 1;
    public const int Gain16x = 2;
    public const int Gain60x = 3;

    /// <summary>DN40 coefficients for the TCS3472x (open air, no glass).</summary>
    public const double DeviceFactor = 310.0;
    public const double GlassAttenuation = 1.0;
    public const double RCoef = 0.136;
    public const double GCoef = 1.0;
    public const double BCoef = -0.444;
    public const double CtCoef = 3810.0;
    public const double CtOffset = 1391.0;

    private readonly I2cDevice _dev;

    public int Id;

    /// <summary>Integration cycles 1..256 (2.4 ms each); ATIME = 256 - cycles.</summary>
    public int IntegrationCycles = 64;

    public int Gain = Gain4x;

    /// <summary>Raw counts from the last read.</summary>
    public int Clear, Red, Green, Blue;

    public Tcs34725(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Tcs34725(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check the ID, set integration time and gain, power on and enable the ADC.</summary>
    public bool Begin()
    {
        try
        {
            Id = _dev.ReadRegister(Command | RegId);
        }
        catch (Exception)
        {
            return false;
        }
        if (Id != Id34725 && Id != Id34727)
        {
            return false;
        }
        Configure(IntegrationCycles, Gain);
        _dev.WriteRegister(Command | RegEnable, 0x01); // PON
        Timing.SleepMs(3);
        _dev.WriteRegister(Command | RegEnable, 0x03); // PON | AEN
        return true;
    }

    public void Configure(int integrationCycles, int gain)
    {
        if (integrationCycles < 1)
        {
            integrationCycles = 1;
        }
        if (integrationCycles > 256)
        {
            integrationCycles = 256;
        }
        IntegrationCycles = integrationCycles;
        Gain = gain;
        _dev.WriteRegister(Command | RegAtime, 256 - integrationCycles);
        _dev.WriteRegister(Command | RegControl, gain & 0x03);
    }

    public double IntegrationTimeMs => IntegrationCycles * 2.4;

    public int GainFactor => Gain == Gain60x ? 60 : (Gain == Gain16x ? 16 : (Gain == Gain4x ? 4 : 1));

    /// <summary>Wait for AVALID and read C, R, G, B (8 bytes, little-endian) in one transfer.</summary>
    public void Read()
    {
        Timing.SleepMs((int)IntegrationTimeMs + 3);
        for (int i = 0; i < 10 && (_dev.ReadRegister(Command | RegStatus) & 0x01) == 0; i++)
        {
            Timing.SleepMs(5);
        }
        byte[] d = _dev.ReadRegisters(Command | AutoIncrement | RegCData, 8);
        Clear = d[0] | (d[1] << 8);
        Red = d[2] | (d[3] << 8);
        Green = d[4] | (d[5] << 8);
        Blue = d[6] | (d[7] << 8);
    }

    /// <summary>True when the clear channel reached its maximum count (lower gain or time).</summary>
    public bool Saturated()
    {
        int max = IntegrationCycles >= 64 ? 65535 : 1024 * IntegrationCycles;
        return Clear >= max;
    }

    /// <summary>DN40 IR component: (R + G + B - C) / 2.</summary>
    public double InfraredComponent => (Red + Green + Blue - Clear) / 2.0;

    /// <summary>DN40 illuminance in lux: (0.136 R' + 1.0 G' - 0.444 B') / (ATIME_ms · gain / (GA · DF)).</summary>
    public double Lux()
    {
        double ir = InfraredComponent;
        double cpl = IntegrationTimeMs * GainFactor / (GlassAttenuation * DeviceFactor);
        double g1 = RCoef * (Red - ir) + GCoef * (Green - ir) + BCoef * (Blue - ir);
        return g1 / cpl;
    }

    /// <summary>DN40 correlated colour temperature in K: 3810 · B' / R' + 1391.</summary>
    public double ColorTemperatureKelvin()
    {
        double ir = InfraredComponent;
        double r = Red - ir;
        if (r <= 0)
        {
            return 0.0;
        }
        return CtCoef * (Blue - ir) / r + CtOffset;
    }

    /// <summary>Wait time between cycles (WTIME = 256 - cycles, 2.4 ms each), WEN enabled.</summary>
    public void SetWaitTime(int cycles)
    {
        _dev.WriteRegister(Command | RegWtime, 256 - cycles);
        _dev.WriteRegister(Command | RegConfig, 0x00);
        _dev.WriteRegister(Command | RegEnable, 0x0B);
    }
}
