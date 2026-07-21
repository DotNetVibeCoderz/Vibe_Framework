using RustNet.Drawing;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// Image-decoder tests against ground-truth vectors: a 4x3 image with 12
/// distinct colors, encoded as 24-bit BMP and 16-colour GIF (LZW) by PIL.
/// Expected RGB565 pixels were computed from each format's actual output,
/// so the decoders are validated pixel-exact — including GIF's LZW and
/// palette handling.
/// </summary>
public class DrawingTests
{
    private const string BmpBase64 =
        "Qk1aAAAAAAAAADYAAAAoAAAABAAAAAMAAAABABgAAAAAACQAAADEDgAAxA4AAAAAAAAAAAAAIECAgEAgMmTIyGQy/wD///8A////AAAAAAD/AP8A/wAAAP//";
    private const string GifBase64 =
        "R0lGODdhBAADAIMAAP//////AAD//wD/AMhkMjJkyIBAICBAgP8A//8AAAAA/wAAAAAAAAAAAAAAAAAAACwAAAAABAADAAAIEAATDFAQAIEAAAsMHCBQICAAOw==";

    private static readonly ushort[] Expected =
    {
        63488, 2016, 31, 65504,
        63519, 2047, 65535, 0,
        33284, 8720, 52006, 13113,
    };

    [Fact]
    public void BmpDecodesPixelExact()
    {
        Bitmap bmp = Bmp.Decode(System.Convert.FromBase64String(BmpBase64));
        Assert.Equal(4, bmp.Width);
        Assert.Equal(3, bmp.Height);
        AssertPixels(bmp);
    }

    [Fact]
    public void GifLzwDecodesPixelExact()
    {
        Bitmap gif = Gif.Decode(System.Convert.FromBase64String(GifBase64));
        Assert.Equal(4, gif.Width);
        Assert.Equal(3, gif.Height);
        AssertPixels(gif);
    }

    [Fact]
    public void SniffSelectsDecoder()
    {
        Assert.Equal(4, Bitmap.Decode(System.Convert.FromBase64String(BmpBase64)).Width);
        Assert.Equal(4, Bitmap.Decode(System.Convert.FromBase64String(GifBase64)).Width);
    }

    [Fact]
    public void ToRgb565BytesIsLittleEndian()
    {
        var bmp = new Bitmap(1, 1);
        bmp.SetPixel(0, 0, 0xF800); // red
        byte[] bytes = bmp.ToRgb565Bytes();
        Assert.Equal(new byte[] { 0x00, 0xF8 }, bytes);
    }

    private static void AssertPixels(Bitmap bmp)
    {
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(Expected[y * 4 + x], bmp.GetPixel(x, y));
            }
        }
    }
}
