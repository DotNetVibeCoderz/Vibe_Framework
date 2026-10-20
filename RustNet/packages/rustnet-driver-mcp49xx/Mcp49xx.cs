using RustNet.Community.Bus;

namespace RustNet.Community.Mcp49xx;

/// <summary>
/// Microchip MCP4921 (one channel) and MCP4922 (two channels) 12-bit SPI
/// DACs. Each write is one 16-bit word, MSB first (datasheet register 5-1):
/// <code>
/// bit 15  A/B   0 = DAC A, 1 = DAC B (MCP4922 only)
/// bit 14  BUF   1 = VREF input buffered
/// bit 13  GA    1 = 1× gain, 0 = 2× gain
/// bit 12  SHDN  1 = output active, 0 = shut down (high impedance)
/// 11..0   D11..D0
/// </code>
/// Vout = VREF × D / 4096 × G. The new value reaches the output on CS rising
/// edge when LDAC is held low, or on an LDAC low pulse.
/// </summary>
public class Mcp49xx
{
    public const int ChannelA = 0;
    public const int ChannelB = 1;

    private readonly SpiDevice _spi;
    private readonly DigitalOutput? _ldac;

    /// <summary>1 (MCP4921) or 2 (MCP4922).</summary>
    public int Channels;

    /// <summary>Use the buffered VREF input.</summary>
    public bool Buffered;

    /// <summary>2× output gain (GA = 0).</summary>
    public bool DoubleGain;

    /// <summary>VREF in millivolts.</summary>
    public int ReferenceMillivolts = 3300;

    protected Mcp49xx(SpiDevice spi, DigitalOutput? ldac, int channels)
    {
        _spi = spi;
        _ldac = ldac;
        Channels = channels;
    }

    /// <summary>Compose a command word.</summary>
    public static int CommandWord(int channel, int value, bool buffered, bool doubleGain, bool active)
    {
        if (value < 0)
        {
            value = 0;
        }
        if (value > 4095)
        {
            value = 4095;
        }
        int w = value;
        if (channel == ChannelB)
        {
            w |= 0x8000;
        }
        if (buffered)
        {
            w |= 0x4000;
        }
        if (!doubleGain)
        {
            w |= 0x2000;
        }
        if (active)
        {
            w |= 0x1000;
        }
        return w;
    }

    private void Send(int word)
    {
        byte[] b = new byte[2];
        b[0] = (byte)(word >> 8);
        b[1] = (byte)word;
        _spi.Write(b);
        if (_ldac != null)
        {
            _ldac.Write(false);
            _ldac.Write(true);
        }
    }

    /// <summary>Set a channel's 12-bit code.</summary>
    public void SetValue(int channel, int value)
    {
        if (channel != ChannelA && Channels < 2)
        {
            channel = ChannelA;
        }
        Send(CommandWord(channel, value, Buffered, DoubleGain, true));
    }

    /// <summary>Set a channel's output in millivolts (code = round(mV × 4096 / (VREF × G))).</summary>
    public void SetMillivolts(int channel, double millivolts)
    {
        double fs = ReferenceMillivolts * (DoubleGain ? 2.0 : 1.0);
        SetValue(channel, (int)(millivolts * 4096.0 / fs + 0.5));
    }

    /// <summary>Shut a channel down (SHDN = 0, output high impedance with 500 kΩ to VSS).</summary>
    public void Shutdown(int channel) => Send(CommandWord(channel, 0, Buffered, DoubleGain, false));
}

/// <summary>MCP4921: single 12-bit DAC.</summary>
public class Mcp4921 : Mcp49xx
{
    public Mcp4921(int bus, int chipSelectPin) : this(new SpiDevice(bus, chipSelectPin, 10000000, 0), null)
    {
    }

    /// <param name="ldac">LDAC pin, or null when LDAC is tied low.</param>
    public Mcp4921(SpiDevice spi, DigitalOutput? ldac) : base(spi, ldac, 1)
    {
    }
}

/// <summary>MCP4922: dual 12-bit DAC.</summary>
public class Mcp4922 : Mcp49xx
{
    public Mcp4922(int bus, int chipSelectPin) : this(new SpiDevice(bus, chipSelectPin, 10000000, 0), null)
    {
    }

    public Mcp4922(SpiDevice spi, DigitalOutput? ldac) : base(spi, ldac, 2)
    {
    }
}
