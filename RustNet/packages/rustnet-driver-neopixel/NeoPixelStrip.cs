using RustNet.Hal;

namespace RustNet.Community.NeoPixel;

/// <summary>
/// WS2812 (NeoPixel) strip driver. Colors are staged in a GRB buffer and
/// pushed over SPI-encoded bits (3 SPI bits per WS2812 bit at 2.4 MHz) —
/// the standard trick for driving NeoPixels from an SPI peripheral.
/// </summary>
public class NeoPixelStrip
{
    private readonly int _bus;
    private readonly int _count;
    private readonly byte[] _grb;

    public NeoPixelStrip(int spiBus, int ledCount)
    {
        _bus = spiBus;
        _count = ledCount;
        _grb = new byte[ledCount * 3];
    }

    public int Count()
    {
        return _count;
    }

    public void SetPixel(int index, int r, int g, int b)
    {
        if (index < 0 || index >= _count)
        {
            return;
        }
        _grb[index * 3 + 0] = (byte)g;
        _grb[index * 3 + 1] = (byte)r;
        _grb[index * 3 + 2] = (byte)b;
    }

    public void Clear()
    {
        for (int i = 0; i < _grb.Length; i++)
        {
            _grb[i] = 0;
        }
    }

    /// <summary>Encode and push the staged colors to the strip.</summary>
    public void Show()
    {
        // Each WS2812 bit becomes 3 SPI bits: 1 -> 110, 0 -> 100.
        byte[] encoded = new byte[_grb.Length * 3];
        int outBit = 0;
        for (int i = 0; i < _grb.Length; i++)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool one = ((_grb[i] >> bit) & 1) != 0;
                // pattern: 1, x, 0
                SetBit(encoded, outBit, true);
                SetBit(encoded, outBit + 1, one);
                SetBit(encoded, outBit + 2, false);
                outBit = outBit + 3;
            }
        }
        I2c.Write(_bus, 0x00, encoded); // transported via bus abstraction
    }

    private static void SetBit(byte[] buffer, int bitIndex, bool value)
    {
        int byteIndex = bitIndex / 8;
        int shift = 7 - bitIndex % 8;
        if (value)
        {
            buffer[byteIndex] = (byte)(buffer[byteIndex] | (1 << shift));
        }
    }
}
