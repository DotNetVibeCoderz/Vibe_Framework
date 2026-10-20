using RustNet.Community.Bus;

namespace RustNet.Community.Apa102;

/// <summary>
/// APA102 / APA102C / SK9822 (DotStar) LEDs on SPI (data + clock, no
/// timing constraints). Frame layout from the datasheet:
/// <list type="bullet">
/// <item>start frame: 32 zero bits;</item>
/// <item>one 32-bit LED frame per LED: <c>111</c> + 5-bit global brightness,
/// then blue, green, red bytes;</item>
/// <item>end frame: ones, at least one clock edge per two LEDs to push data
/// through the chain — max(4, ⌈n/16⌉) bytes of 0xFF.</item>
/// </list>
/// </summary>
public class Apa102
{
    private readonly SpiDevice _spi;
    private readonly byte[] _bgr;
    private readonly byte[] _brightness;

    public int Count;

    public Apa102(int bus, int count, int frequencyHz = 4000000) : this(new SpiDevice(bus, -1, frequencyHz, 0), count)
    {
    }

    public Apa102(SpiDevice spi, int count)
    {
        _spi = spi;
        Count = count;
        _bgr = new byte[count * 3];
        _brightness = new byte[count];
        for (int i = 0; i < count; i++)
        {
            _brightness[i] = 31;
        }
    }

    /// <summary>Set a pixel; <paramref name="brightness"/> is the 5-bit global current level 0..31.</summary>
    public void SetPixel(int index, int r, int g, int b, int brightness)
    {
        if (index < 0 || index >= Count)
        {
            return;
        }
        _bgr[index * 3] = (byte)b;
        _bgr[index * 3 + 1] = (byte)g;
        _bgr[index * 3 + 2] = (byte)r;
        _brightness[index] = (byte)(brightness < 0 ? 0 : (brightness > 31 ? 31 : brightness));
    }

    public void SetPixel(int index, int r, int g, int b) => SetPixel(index, r, g, b, 31);

    public void Fill(int r, int g, int b, int brightness)
    {
        for (int i = 0; i < Count; i++)
        {
            SetPixel(i, r, g, b, brightness);
        }
    }

    public void Clear() => Fill(0, 0, 0, 31);

    /// <summary>Number of 0xFF bytes in the end frame for this strip length.</summary>
    public int EndFrameBytes()
    {
        int n = (Count + 15) / 16;
        return n < 4 ? 4 : n;
    }

    /// <summary>The complete SPI frame.</summary>
    public byte[] Encode()
    {
        int end = EndFrameBytes();
        byte[] f = new byte[4 + Count * 4 + end];
        int o = 4;
        for (int i = 0; i < Count; i++)
        {
            f[o] = (byte)(0xE0 | _brightness[i]);
            f[o + 1] = _bgr[i * 3];
            f[o + 2] = _bgr[i * 3 + 1];
            f[o + 3] = _bgr[i * 3 + 2];
            o += 4;
        }
        for (int i = 0; i < end; i++)
        {
            f[o + i] = 0xFF;
        }
        return f;
    }

    public void Show() => _spi.Write(Encode());
}
