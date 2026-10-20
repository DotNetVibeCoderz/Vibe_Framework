using RustNet.Community.Bus;

namespace RustNet.Community.Max44009;

/// <summary>
/// Maxim MAX44009 ambient light sensor over I2C, 0.045 to 188,000 lux.
/// Address 0x4A (A0 low) or 0x4B (A0 high). The lux registers hold a 4-bit
/// exponent and an 8-bit mantissa: lux = 2^exponent × mantissa × 0.045.
/// The chip has no ID register.
/// </summary>
public class Max44009
{
    public const int DefaultAddress = 0x4A;
    public const int AlternateAddress = 0x4B;

    public const int RegInterruptStatus = 0x00;
    public const int RegInterruptEnable = 0x01;
    public const int RegConfiguration = 0x02;
    public const int RegLuxHigh = 0x03;
    public const int RegLuxLow = 0x04;
    public const int RegUpperThreshold = 0x05;
    public const int RegLowerThreshold = 0x06;
    public const int RegThresholdTimer = 0x07;

    private readonly I2cDevice _dev;

    public Max44009(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Max44009(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Max44009(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>True when the device acknowledges a read of its configuration register.</summary>
    public bool Begin()
    {
        try
        {
            _dev.ReadRegister(RegConfiguration);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Configuration: continuous mode (CONT, bit 7) and manual mode (bit 6) with current
    /// division ratio CDR (bit 3) and integration time TIM code (bits 2:0, 800 ms .. 6.25 ms).
    /// </summary>
    public void Configure(bool continuous, bool manual, bool cdr, int integrationTime)
    {
        _dev.WriteRegister(RegConfiguration,
            (continuous ? 0x80 : 0) | (manual ? 0x40 : 0) | (cdr ? 0x08 : 0) | (integrationTime & 0x07));
    }

    /// <summary>Read both lux bytes in one transfer and return lux; -1 on overrange (exponent 15).</summary>
    public double ReadLux()
    {
        byte[] d = _dev.ReadRegisters(RegLuxHigh, 2);
        return ToLux(d[0], d[1]);
    }

    /// <summary>Datasheet: exponent = high[7:4], mantissa = high[3:0] &lt;&lt; 4 | low[3:0]; lux = 2^E · M · 0.045.</summary>
    public static double ToLux(int high, int low)
    {
        int exponent = (high >> 4) & 0x0F;
        if (exponent == 0x0F)
        {
            return -1.0;
        }
        int mantissa = ((high & 0x0F) << 4) | (low & 0x0F);
        return (1 << exponent) * mantissa * 0.045;
    }

    /// <summary>Interrupt thresholds in lux (upper byte only: exponent + 4 MSBs of mantissa) and timer in 100 ms steps.</summary>
    public void SetThresholds(double upperLux, double lowerLux, int timer100ms)
    {
        _dev.WriteRegister(RegUpperThreshold, ThresholdByte(upperLux));
        _dev.WriteRegister(RegLowerThreshold, ThresholdByte(lowerLux));
        _dev.WriteRegister(RegThresholdTimer, timer100ms & 0xFF);
    }

    /// <summary>Encode lux as a threshold byte: smallest exponent whose 4-bit mantissa (× 16) holds the value.</summary>
    public static int ThresholdByte(double lux)
    {
        int m = (int)(lux / 0.045);
        int e = 0;
        while (m > 0xFF && e < 14)
        {
            m = m >> 1;
            e++;
        }
        if (m > 0xFF)
        {
            m = 0xFF;
        }
        return (e << 4) | (m >> 4);
    }

    public void EnableInterrupt(bool enabled) => _dev.WriteRegister(RegInterruptEnable, enabled ? 1 : 0);

    /// <summary>Reading the status register clears the interrupt.</summary>
    public bool InterruptPending() => (_dev.ReadRegister(RegInterruptStatus) & 1) != 0;
}
