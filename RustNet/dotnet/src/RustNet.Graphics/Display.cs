using RustNet.Core;

namespace RustNet.Graphics;

/// <summary>RGB565 colors.</summary>
public static class Color
{
    public const int Black = 0x0000;
    public const int White = 0xFFFF;
    public const int Red = 0xF800;
    public const int Green = 0x07E0;
    public const int Blue = 0x001F;
    public const int Yellow = 0xFFE0;
    public const int Cyan = 0x07FF;
    public const int Magenta = 0xF81F;

    public static int FromRgb(int r, int g, int b)
        => ((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3);
}

/// <summary>
/// Drawing surface backed by the runtime's double-buffered framebuffer
/// (physical panel on hardware, virtual panel on the simulator that tools
/// can capture).
/// </summary>
/// <summary>Panel controller the display is wired to. On the virtual device
/// the driver is advisory (the framebuffer is the panel); on real silicon it
/// selects the SPI/parallel command set.</summary>
public enum PanelDriver
{
    Auto = 0,
    Ssd1306 = 1,   // I2C/SPI OLED
    St7735 = 2,    // SPI TFT
    Ili9341 = 3,   // SPI TFT
    Generic = 4,   // parallel/RGB
}

public static class Display
{
    [InternalCall]
    public static void Init(int width, int height) => throw new RuntimeOnlyException();

    /// <summary>Configure the panel: driver, physical size and clockwise
    /// rotation (0/90/180/270°). Replaces the drawing surface — call before
    /// drawing. <see cref="Width"/>/<see cref="Height"/> then report the
    /// rotation-aware logical size.</summary>
    public static void Configure(PanelDriver driver, int width, int height, int rotation = 0)
        => ConfigurePanel((int)driver, width, height, rotation);

    [InternalCall]
    private static void ConfigurePanel(int driver, int width, int height, int rotation)
        => throw new RuntimeOnlyException();

    /// <summary>Logical drawing width (accounts for rotation).</summary>
    [InternalCall]
    public static int Width() => throw new RuntimeOnlyException();

    /// <summary>Logical drawing height (accounts for rotation).</summary>
    [InternalCall]
    public static int Height() => throw new RuntimeOnlyException();

    /// <summary>Constrain subsequent drawing to a rectangle (logical coords).</summary>
    [InternalCall]
    public static void SetClip(int x, int y, int width, int height)
        => throw new RuntimeOnlyException();

    /// <summary>Remove the clip rectangle.</summary>
    [InternalCall]
    public static void ClearClip() => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Clear(int color) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void SetPixel(int x, int y, int color) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void FillRect(int x, int y, int width, int height, int color)
        => throw new RuntimeOnlyException();

    /// <summary>Filled circle, rendered natively (integer scanline in the
    /// runtime) — much faster than the same loop in app code, so it stays
    /// smooth for per-frame animation.</summary>
    [InternalCall]
    public static void FillCircle(int cx, int cy, int r, int color)
        => throw new RuntimeOnlyException();

    [InternalCall]
    public static void DrawLine(int x0, int y0, int x1, int y1, int color)
        => throw new RuntimeOnlyException();

    [InternalCall]
    public static void DrawText(int x, int y, string text, int color, int scale)
        => throw new RuntimeOnlyException();

    /// <summary>Blit a decoded RGB565 image (w*h, row-major, little-endian
    /// bytes) at (x, y) in a single call.</summary>
    [InternalCall]
    public static void DrawImage(int x, int y, int width, int height, byte[] rgb565)
        => throw new RuntimeOnlyException();

    /// <summary>Fill a rectangle with a linear gradient interpolating
    /// <paramref name="color0"/> → <paramref name="color1"/>.
    /// <paramref name="vertical"/> runs top→bottom, else left→right.</summary>
    [InternalCall]
    public static void FillGradient(int x, int y, int width, int height,
        int color0, int color1, bool vertical) => throw new RuntimeOnlyException();

    /// <summary>Alpha-blend a decoded RGB565 image over the background with a
    /// global <paramref name="alpha"/> (0 = transparent, 255 = opaque).</summary>
    [InternalCall]
    public static void BlendImage(int x, int y, int width, int height,
        byte[] rgb565, int alpha) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Present() => throw new RuntimeOnlyException();

    // Convenience drawing built on the primitives (compiled to RNX as
    // ordinary managed code).

    public static void DrawRect(int x, int y, int width, int height, int color)
    {
        DrawLine(x, y, x + width - 1, y, color);
        DrawLine(x, y + height - 1, x + width - 1, y + height - 1, color);
        DrawLine(x, y, x, y + height - 1, color);
        DrawLine(x + width - 1, y, x + width - 1, y + height - 1, color);
    }

    public static void DrawCircle(int cx, int cy, int r, int color)
    {
        int x = r;
        int y = 0;
        int d = 1 - r;
        while (x >= y)
        {
            SetPixel(cx + x, cy + y, color);
            SetPixel(cx - x, cy + y, color);
            SetPixel(cx + x, cy - y, color);
            SetPixel(cx - x, cy - y, color);
            SetPixel(cx + y, cy + x, color);
            SetPixel(cx - y, cy + x, color);
            SetPixel(cx + y, cy - x, color);
            SetPixel(cx - y, cy - x, color);
            y = y + 1;
            if (d < 0)
            {
                d = d + 2 * y + 1;
            }
            else
            {
                x = x - 1;
                d = d + 2 * (y - x) + 1;
            }
        }
    }
}
