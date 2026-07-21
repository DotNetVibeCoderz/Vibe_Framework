using RustNet.Core;

namespace RustNet.Media;

/// <summary>Pixel format a camera frame is delivered in.</summary>
public enum PixelFormat
{
    Rgb565 = 0,
    Grayscale = 1,
}

/// <summary>
/// Image sensor capture. Chip-gated (v0.8): available where the board has a
/// camera interface. The virtual device provides a colour-bar test sensor so
/// apps and pipelines can be developed without optics. A captured RGB565
/// frame blits straight to the display via <c>Display.DrawImage</c>.
/// </summary>
public static class Camera
{
    /// <summary>Configure frame size and format. Call before <see cref="Capture"/>.</summary>
    public static void Configure(int width, int height, PixelFormat format = PixelFormat.Rgb565)
        => ConfigureRaw(width, height, (int)format);

    [InternalCall]
    private static void ConfigureRaw(int width, int height, int format)
        => throw new RuntimeOnlyException();

    /// <summary>Capture one frame as raw bytes in the configured format
    /// (RGB565 little-endian: width*height*2 bytes).</summary>
    [InternalCall]
    public static byte[] Capture() => throw new RuntimeOnlyException();

    /// <summary>Configured frame width in pixels.</summary>
    [InternalCall]
    public static int Width() => throw new RuntimeOnlyException();

    /// <summary>Configured frame height in pixels.</summary>
    [InternalCall]
    public static int Height() => throw new RuntimeOnlyException();
}
