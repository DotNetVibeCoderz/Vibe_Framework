using RustNet.Community.Bus;

namespace RustNet.Community.Amg8833;

/// <summary>
/// Panasonic AMG8833 / AMG88xx "Grid-EYE" 8×8 thermopile array at I2C 0x69
/// (AD_SELECT high) or 0x68. Pixels are 12-bit two's complement at
/// 0.25 °C/LSB (0x80..0xFF, little-endian pairs, row-major); the thermistor
/// is 12-bit sign-magnitude at 0.0625 °C/LSB. The chip has no ID register,
/// so <see cref="Begin"/> can only check that writes are accepted.
/// </summary>
public class Amg8833
{
    public const int DefaultAddress = 0x69;
    public const int AlternateAddress = 0x68;
    public const int Pixels = 64;

    private const int RegPctl = 0x00;
    private const int RegRst = 0x01;
    private const int RegFpsc = 0x02;
    private const int RegIntc = 0x03;
    private const int RegAve = 0x07;
    private const int RegTthl = 0x0E;
    private const int RegPixel0 = 0x80;

    private readonly I2cDevice _dev;

    /// <summary>Last frame, °C, index = row × 8 + column.</summary>
    public double[] Temperatures = new double[Pixels];

    public Amg8833(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Amg8833(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Amg8833(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Normal mode, initial reset, interrupts off, 10 frames/s.</summary>
    public void Begin()
    {
        _dev.WriteRegister(RegPctl, 0x00); // normal mode
        _dev.WriteRegister(RegRst, 0x3F); // initial reset
        Timing.SleepMs(2);
        _dev.WriteRegister(RegIntc, 0x00);
        SetFrameRate(10);
        Timing.SleepMs(100);
    }

    /// <summary>10 or 1 frames per second.</summary>
    public void SetFrameRate(int fps) => _dev.WriteRegister(RegFpsc, fps == 1 ? 0x01 : 0x00);

    /// <summary>Twice moving average (datasheet's documented AVE register unlock sequence).</summary>
    public void SetMovingAverage(bool enabled)
    {
        _dev.WriteRegister(0x1F, 0x50);
        _dev.WriteRegister(0x1F, 0x45);
        _dev.WriteRegister(0x1F, 0x57);
        _dev.WriteRegister(RegAve, enabled ? 0x20 : 0x00);
        _dev.WriteRegister(0x1F, 0x00);
    }

    /// <summary>Power mode: 0x00 normal, 0x10 sleep, 0x20/0x21 stand-by (60 s / 10 s).</summary>
    public void SetPowerMode(int mode) => _dev.WriteRegister(RegPctl, mode);

    /// <summary>12-bit two's complement pixel value to °C (0.25 °C/LSB).</summary>
    public static double PixelToCelsius(int raw) => BitOps.SignExtend(raw, 12) * 0.25;

    /// <summary>12-bit sign-magnitude thermistor value to °C (0.0625 °C/LSB).</summary>
    public static double ThermistorToCelsius(int raw)
    {
        int magnitude = raw & 0x7FF;
        return ((raw & 0x800) != 0 ? -magnitude : magnitude) * 0.0625;
    }

    /// <summary>Read the 64 pixels in one 128-byte transfer into <see cref="Temperatures"/>.</summary>
    public double[] ReadPixels()
    {
        byte[] d = _dev.ReadRegisters(RegPixel0, Pixels * 2);
        for (int i = 0; i < Pixels; i++)
        {
            Temperatures[i] = PixelToCelsius(d[i * 2] | (d[i * 2 + 1] << 8));
        }
        return Temperatures;
    }

    /// <summary>The on-board thermistor (ambient) in °C.</summary>
    public double ReadThermistor()
    {
        byte[] d = _dev.ReadRegisters(RegTthl, 2);
        return ThermistorToCelsius(d[0] | (d[1] << 8));
    }

    /// <summary>Hottest pixel index in the last frame.</summary>
    public int HottestPixel()
    {
        int best = 0;
        for (int i = 1; i < Pixels; i++)
        {
            if (Temperatures[i] > Temperatures[best])
            {
                best = i;
            }
        }
        return best;
    }
}
