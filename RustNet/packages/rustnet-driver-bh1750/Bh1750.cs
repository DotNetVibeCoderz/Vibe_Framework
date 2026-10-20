using RustNet.Community.Bus;

namespace RustNet.Community.Bh1750;

/// <summary>
/// ROHM BH1750FVI digital ambient light sensor over I2C. Address 0x23 (ADDR
/// low) or 0x5C (ADDR high). Opcode-only protocol: a mode byte starts a
/// measurement and a two-byte read returns the count; lux = count / 1.2,
/// scaled by the measurement time register (MTreg, default 69) and halved
/// in the 0.5 lx "H-resolution mode 2".
/// </summary>
public class Bh1750
{
    public const int DefaultAddress = 0x23;
    public const int AlternateAddress = 0x5C;

    public const int PowerDown = 0x00;
    public const int PowerOn = 0x01;
    public const int Reset = 0x07;

    /// <summary>Measurement modes (opcodes).</summary>
    public const int ContinuousHighRes = 0x10;
    public const int ContinuousHighRes2 = 0x11;
    public const int ContinuousLowRes = 0x13;
    public const int OneTimeHighRes = 0x20;
    public const int OneTimeHighRes2 = 0x21;
    public const int OneTimeLowRes = 0x23;

    public const int DefaultMtReg = 69;

    private readonly I2cDevice _dev;

    /// <summary>Mode opcode used by <see cref="ReadLux"/>.</summary>
    public int Mode = OneTimeHighRes;

    /// <summary>Measurement time register (31..254); larger = more sensitive.</summary>
    public int MtReg = DefaultMtReg;

    /// <summary>Raw 16-bit count of the last reading.</summary>
    public int RawCount;

    public Bh1750(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Bh1750(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Bh1750(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Power on and reset the data register. False when the device does not acknowledge.</summary>
    public bool Begin()
    {
        try
        {
            _dev.WriteByte(PowerOn);
            _dev.WriteByte(Reset);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Set MTreg (31..254): sent as 01000_hhh then 011_lllll.</summary>
    public void SetMeasurementTime(int mtReg)
    {
        if (mtReg < 31)
        {
            mtReg = 31;
        }
        if (mtReg > 254)
        {
            mtReg = 254;
        }
        MtReg = mtReg;
        _dev.WriteByte(0x40 | (mtReg >> 5));
        _dev.WriteByte(0x60 | (mtReg & 0x1F));
    }

    /// <summary>Start continuous measurement in a Continuous* mode; then call <see cref="ReadContinuousLux"/>.</summary>
    public void StartContinuous(int mode)
    {
        Mode = mode;
        _dev.WriteByte(mode);
    }

    /// <summary>Read the latest continuous result in lux.</summary>
    public double ReadContinuousLux()
    {
        byte[] r = _dev.Read(2);
        RawCount = (r[0] << 8) | r[1];
        return ToLux(RawCount, Mode, MtReg);
    }

    /// <summary>One measurement in the configured <see cref="Mode"/> (one-time modes power down afterwards), in lux.</summary>
    public double ReadLux()
    {
        _dev.WriteByte(Mode);
        Timing.SleepMs(MeasurementTimeMs());
        return ReadContinuousLux();
    }

    /// <summary>Max measurement time: 180 ms (H-res) or 24 ms (L-res) at MTreg 69, scaled with MTreg.</summary>
    public int MeasurementTimeMs()
    {
        bool low = Mode == ContinuousLowRes || Mode == OneTimeLowRes;
        return ((low ? 24 : 180) * MtReg + 68) / 69;
    }

    /// <summary>Datasheet: lux = count / 1.2 · (69 / MTreg), and / 2 in H-resolution mode 2.</summary>
    public static double ToLux(int count, int mode, int mtReg)
    {
        double lux = count / 1.2 * DefaultMtReg / mtReg;
        if (mode == ContinuousHighRes2 || mode == OneTimeHighRes2)
        {
            lux = lux / 2.0;
        }
        return lux;
    }
}
