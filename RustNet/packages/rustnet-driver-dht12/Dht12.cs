using RustNet.Community.Bus;

namespace RustNet.Community.Dht12;

/// <summary>
/// Aosong DHT12 temperature and humidity sensor in I2C mode (address 0x5C).
/// Registers 0..4 hold humidity integer/decimal, temperature integer/decimal
/// (bit 7 of the decimal byte is the sign) and a checksum equal to the low
/// byte of the sum of the first four. Read no more often than every 2 s.
/// </summary>
public class Dht12
{
    public const int DefaultAddress = 0x5C;

    private readonly I2cDevice _dev;

    /// <summary>Relative humidity in % (0.1 % resolution) from the last read.</summary>
    public double HumidityPercent;

    /// <summary>Temperature in °C (0.1 °C resolution) from the last read.</summary>
    public double TemperatureCelsius;

    public Dht12(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Dht12(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>True when the sensor answers with a valid checksum.</summary>
    public bool Begin()
    {
        try
        {
            Measure();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Read the five data registers and decode them; throws on a checksum mismatch.</summary>
    public void Measure()
    {
        byte[] r = _dev.ReadRegisters(0x00, 5);
        if (((r[0] + r[1] + r[2] + r[3]) & 0xFF) != r[4])
        {
            throw new InvalidOperationException("dht12 checksum mismatch");
        }
        HumidityPercent = r[0] + r[1] * 0.1;
        double t = r[2] + (r[3] & 0x7F) * 0.1;
        TemperatureCelsius = (r[3] & 0x80) != 0 ? -t : t;
    }
}
