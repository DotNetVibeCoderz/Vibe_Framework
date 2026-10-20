using RustNet.Community.Bus;

namespace RustNet.Community.Mcp3xxx;

/// <summary>
/// Microchip MCP300x / MCP320x successive-approximation SPI ADCs (SPI mode
/// 0,0 or 1,1). Each conversion is one 3-byte transaction framed so the
/// result ends byte-aligned:
/// <code>
/// MCP3004/3008 (10-bit): TX 0x01, SGL/DIFF·D2·D1·D0 0000, 0x00
///                        RX ?, ?????·null·B9·B8, B7..B0
/// MCP3204/3208 (12-bit): TX 0000 01·SGL/DIFF·D2, D1·D0 000000, 0x00
///                        RX ?, ???·null·B11..B8, B7..B0
/// </code>
/// Single-ended inputs are referenced to VSS; pseudo-differential pairs are
/// CH0/CH1, CH2/CH3, … with D0 selecting which side is IN+.
/// </summary>
public class Mcp3xxx
{
    private readonly SpiDevice _spi;

    /// <summary>Input count: 4 or 8.</summary>
    public int Channels;

    /// <summary>Resolution in bits: 10 or 12.</summary>
    public int Resolution;

    /// <summary>Reference voltage (VREF pin) in millivolts.</summary>
    public int ReferenceMillivolts = 3300;

    protected Mcp3xxx(SpiDevice spi, int channels, int resolution)
    {
        _spi = spi;
        Channels = channels;
        Resolution = resolution;
    }

    /// <summary>The three bytes clocked out for a conversion.</summary>
    public byte[] CommandFrame(int channel, bool singleEnded)
    {
        int ch = channel & 7;
        int sgl = singleEnded ? 1 : 0;
        byte[] tx = new byte[3];
        if (Resolution == 10)
        {
            tx[0] = 0x01;
            tx[1] = (byte)((sgl << 7) | (ch << 4));
        }
        else
        {
            tx[0] = (byte)(0x04 | (sgl << 1) | (ch >> 2));
            tx[1] = (byte)((ch & 3) << 6);
        }
        return tx;
    }

    /// <summary>Raw code (0..1023 or 0..4095).</summary>
    public int ReadRaw(int channel, bool singleEnded = true)
    {
        byte[] rx = _spi.Transfer(CommandFrame(channel, singleEnded));
        if (Resolution == 10)
        {
            return ((rx[1] & 0x03) << 8) | rx[2];
        }
        return ((rx[1] & 0x0F) << 8) | rx[2];
    }

    /// <summary>Code to millivolts: code × VREF / 2^n.</summary>
    public double ToMillivolts(int code) => code * (double)ReferenceMillivolts / (1 << Resolution);

    public double ReadMillivolts(int channel) => ToMillivolts(ReadRaw(channel, true));
}

/// <summary>MCP3004: 4 channels, 10-bit.</summary>
public class Mcp3004 : Mcp3xxx
{
    public Mcp3004(int bus, int chipSelectPin) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0))
    {
    }

    public Mcp3004(SpiDevice spi) : base(spi, 4, 10)
    {
    }
}

/// <summary>MCP3008: 8 channels, 10-bit.</summary>
public class Mcp3008 : Mcp3xxx
{
    public Mcp3008(int bus, int chipSelectPin) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0))
    {
    }

    public Mcp3008(SpiDevice spi) : base(spi, 8, 10)
    {
    }
}

/// <summary>MCP3204: 4 channels, 12-bit.</summary>
public class Mcp3204 : Mcp3xxx
{
    public Mcp3204(int bus, int chipSelectPin) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0))
    {
    }

    public Mcp3204(SpiDevice spi) : base(spi, 4, 12)
    {
    }
}

/// <summary>MCP3208: 8 channels, 12-bit.</summary>
public class Mcp3208 : Mcp3xxx
{
    public Mcp3208(int bus, int chipSelectPin) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0))
    {
    }

    public Mcp3208(SpiDevice spi) : base(spi, 8, 12)
    {
    }
}
