using RustNet.Community.Bus;

namespace RustNet.Community.Max6675;

/// <summary>
/// Maxim MAX6675 cold-junction-compensated K-type thermocouple-to-digital
/// converter (0..1023.75 °C, 0.25 °C) on a read-only SPI bus (mode 0, ≤ 4.3 MHz).
/// One 16-bit frame: bit 15 dummy, bits 14:3 temperature, bit 2 = open
/// thermocouple, bit 1 device ID (0), bit 0 three-state. A conversion takes
/// up to 220 ms; reading during one aborts it.
/// </summary>
public class Max6675
{
    private readonly SpiDevice _spi;

    /// <summary>Raw 16-bit frame of the last read.</summary>
    public int RawFrame;

    /// <summary>True when the last read reported no thermocouple attached (T- / T+ open).</summary>
    public bool Open;

    public Max6675(int bus, int chipSelectPin) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0))
    {
    }

    public Max6675(SpiDevice spi)
    {
        _spi = spi;
    }

    /// <summary>Read one frame and return °C, or double.NaN when the thermocouple is open.</summary>
    public double ReadCelsius()
    {
        byte[] r = _spi.Transfer(new byte[2]);
        RawFrame = (r[0] << 8) | r[1];
        return Decode(RawFrame);
    }

    /// <summary>Decode a frame; sets <see cref="Open"/>.</summary>
    public double Decode(int frame)
    {
        Open = (frame & 0x0004) != 0;
        if (Open)
        {
            return double.NaN;
        }
        return ((frame >> 3) & 0x0FFF) * 0.25;
    }

    /// <summary>True when a frame looks like a MAX6675's (dummy bit 15 and device ID bit 1 both zero).</summary>
    public bool IsPresent()
    {
        ReadCelsius();
        return (RawFrame & 0x8002) == 0 && RawFrame != 0;
    }
}
