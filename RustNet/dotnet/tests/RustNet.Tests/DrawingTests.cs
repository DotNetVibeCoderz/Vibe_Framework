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

    // 2x2 32-bit BMPs: fully opaque, partly masked, and one whose alpha is
    // zero everywhere — which no writer means literally.
    private const string Bmp32Opaque = "Qk1GAAAAAAAAADYAAAAoAAAAAgAAAAIAAAABACAAAAAAABAAAAATCwAAEwsAAAAAAAAAAAAAAAD//wD/AP8AAP//AP8A/w==";
    private const string Bmp32Masked = "Qk1GAAAAAAAAADYAAAAoAAAAAgAAAAIAAAABACAAAAAAABAAAAATCwAAEwsAAAAAAAAAAAAAAAD/gAD/AP8AAP//AP8AAA==";
    private const string Bmp32AllZero = "Qk1GAAAAAAAAADYAAAAoAAAAAgAAAAIAAAABACAAAAAAABAAAAATCwAAEwsAAAAAAAAAAAAAAAD/AAD/AAAAAP8AAP8AAA==";

    /// <summary>An image with no transparency carries no alpha channel, so it
    /// costs nothing — most images on a device are opaque.</summary>
    [Fact]
    public void OpaqueImagesCarryNoAlphaChannel()
    {
        Bitmap bmp = Bitmap.Decode(System.Convert.FromBase64String(Bmp32Opaque));
        Assert.False(bmp.HasAlpha);
        Assert.Equal(255, bmp.GetAlpha(0, 0));
        Assert.Empty(bmp.ToAlphaBytes());
    }

    /// <summary>The fourth byte of a 32-bit BMP used to be stepped over, so a
    /// file with transparency decoded fully opaque and said nothing.</summary>
    [Fact]
    public void ThirtyTwoBitBmpKeepsItsCoverage()
    {
        Bitmap bmp = Bitmap.Decode(System.Convert.FromBase64String(Bmp32Masked));
        Assert.True(bmp.HasAlpha);
        Assert.Equal(255, bmp.GetAlpha(0, 0));
        Assert.Equal(0, bmp.GetAlpha(1, 0));
        Assert.Equal(128, bmp.GetAlpha(0, 1));
        Assert.Equal(4, bmp.ToAlphaBytes().Length);
    }

    /// <summary>Plenty of writers leave the alpha byte at zero meaning
    /// "unused". Honouring that literally makes the whole image invisible, so
    /// an all-zero channel is read as no channel.</summary>
    [Fact]
    public void AnAllZeroAlphaChannelIsTreatedAsAbsent()
    {
        Bitmap bmp = Bitmap.Decode(System.Convert.FromBase64String(Bmp32AllZero));
        Assert.False(bmp.HasAlpha);
        Assert.Equal(255, bmp.GetAlpha(0, 0));
    }

    /// <summary>The channel is created on demand and starts opaque: making one
    /// pixel transparent must not erase every other one.</summary>
    [Fact]
    public void TheAlphaChannelAppearsOnFirstUseAndStartsOpaque()
    {
        Bitmap bmp = new Bitmap(2, 2);
        Assert.False(bmp.HasAlpha);

        bmp.SetAlpha(1, 1, 0);
        Assert.True(bmp.HasAlpha);
        Assert.Equal(0, bmp.GetAlpha(1, 1));
        Assert.Equal(255, bmp.GetAlpha(0, 0));

        bmp.ClearAlpha();
        Assert.False(bmp.HasAlpha);
    }

    /// <summary>Setting full coverage on an opaque image allocates nothing:
    /// the common path stays free.</summary>
    [Fact]
    public void SettingFullCoverageDoesNotCreateAChannel()
    {
        Bitmap bmp = new Bitmap(2, 2);
        bmp.SetAlpha(0, 0, 255);
        Assert.False(bmp.HasAlpha);
    }

    /// <summary>The GIF in these tests declares no transparent index, and a
    /// decoder that reads one anyway would make index 0 vanish everywhere.</summary>
    [Fact]
    public void AGifWithoutATransparentIndexStaysOpaque()
    {
        Bitmap bmp = Bitmap.Decode(System.Convert.FromBase64String(GifBase64));
        Assert.False(bmp.HasAlpha);
    }
}
