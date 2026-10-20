using RustNet.Community.Bus;

namespace RustNet.Community.Ws2812;

/// <summary>
/// WS2812 / WS2812B / SK6812 (NeoPixel) LEDs driven from the MOSI line of an
/// SPI peripheral clocked at 2.4 MHz. Each SPI bit lasts 417 ns, so every
/// WS2812 data bit becomes three SPI bits: a 1 is sent as <c>110</c>
/// (T1H ≈ 833 ns, T1L ≈ 417 ns) and a 0 as <c>100</c> (T0H ≈ 417 ns,
/// T0L ≈ 833 ns) — inside the datasheet's ±150 ns windows. One colour byte
/// therefore becomes three SPI bytes. Colours go out in G, R, B order, most
/// significant bit first, and the frame ends with a low reset/latch period
/// (≥ 280 µs for the WS2812B; <see cref="ResetBytes"/> zero bytes = 300 µs).
/// The whole frame is one SPI write, so timing does not depend on the
/// interpreter.
/// </summary>
public class Ws2812
{
    public const int SpiFrequencyHz = 2400000;

    /// <summary>Zero bytes appended as the reset latch: 90 × 8 bits / 2.4 MHz = 300 µs.</summary>
    public const int ResetBytes = 90;

    private readonly SpiDevice _spi;
    private readonly byte[] _grb;

    /// <summary>Global brightness 0..255 applied when encoding (255 = as set).</summary>
    public int Brightness = 255;

    /// <summary>Number of LEDs on the strip.</summary>
    public int Count;

    /// <summary>Strip on SPI <paramref name="bus"/> (only MOSI is used; no chip select).</summary>
    public Ws2812(int bus, int count) : this(new SpiDevice(bus, -1, SpiFrequencyHz, 0), count)
    {
    }

    public Ws2812(SpiDevice spi, int count)
    {
        _spi = spi;
        Count = count;
        _grb = new byte[count * 3];
    }

    public void SetPixel(int index, int r, int g, int b)
    {
        if (index < 0 || index >= Count)
        {
            return;
        }
        _grb[index * 3] = (byte)g;
        _grb[index * 3 + 1] = (byte)r;
        _grb[index * 3 + 2] = (byte)b;
    }

    /// <summary>Colour as 0xRRGGBB.</summary>
    public void SetPixel(int index, int rgb) => SetPixel(index, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    /// <summary>Colour of a pixel as 0xRRGGBB.</summary>
    public int GetPixel(int index) => (_grb[index * 3 + 1] << 16) | (_grb[index * 3] << 8) | _grb[index * 3 + 2];

    public void Fill(int r, int g, int b)
    {
        for (int i = 0; i < Count; i++)
        {
            SetPixel(i, r, g, b);
        }
    }

    public void Clear() => Fill(0, 0, 0);

    /// <summary>
    /// 24-bit SPI pattern for one data byte: each bit b (MSB first) becomes
    /// 1b0, i.e. 0b110 for a one and 0b100 for a zero.
    /// </summary>
    public static int EncodeByte(int value)
    {
        int p = 0;
        for (int bit = 7; bit >= 0; bit--)
        {
            p = (p << 3) | (((value >> bit) & 1) != 0 ? 6 : 4);
        }
        return p;
    }

    /// <summary>The complete SPI frame: encoded GRB data followed by the reset latch.</summary>
    public byte[] Encode()
    {
        byte[] frame = new byte[_grb.Length * 3 + ResetBytes];
        int o = 0;
        for (int i = 0; i < _grb.Length; i++)
        {
            int v = _grb[i];
            if (Brightness < 255)
            {
                v = (v * Brightness + 127) / 255;
            }
            int p = EncodeByte(v);
            frame[o] = (byte)(p >> 16);
            frame[o + 1] = (byte)(p >> 8);
            frame[o + 2] = (byte)p;
            o += 3;
        }
        return frame;
    }

    /// <summary>Send the staged colours to the strip.</summary>
    public void Show() => _spi.Write(Encode());
}
