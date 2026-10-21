namespace RustNet.UI.Frontends;

/// <summary>
/// An RGB565 framebuffer that paints exactly what the device's
/// <c>rustnet-gfx</c> framebuffer paints: the same Bresenham lines, the same
/// filled-circle test, the same 8×8 font, the same clip semantics. A desktop
/// frontend renders a RustNet.UI tree into this (via <see cref="Paint.Target"/>)
/// and only has to show <see cref="Pixels"/>. Equivalence with the device is
/// checked against the virtual device's own framebuffer in the test suite.
/// </summary>
public sealed class FramebufferCanvas : IUiCanvas
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Row-major RGB565 pixels, <see cref="Width"/> × <see cref="Height"/>.</summary>
    public ushort[] Pixels { get; private set; } = [];

    /// <summary>Raised by <see cref="Present"/> — a frame is complete.</summary>
    public event Action? Presented;

    private (int X, int Y, int W, int H)? _clip;

    public FramebufferCanvas(int width, int height)
    {
        Init(width, height);
    }

    public void Init(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new ushort[Width * Height];
        _clip = null;
    }

    public void Clear(int rgb565) => Array.Fill(Pixels, (ushort)rgb565);

    public void SetPixel(int x, int y, int rgb565)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }
        if (_clip is { } c && (x < c.X || y < c.Y || x >= c.X + c.W || y >= c.Y + c.H))
        {
            return;
        }
        Pixels[y * Width + x] = (ushort)rgb565;
    }

    public ushort GetPixel(int x, int y) => Pixels[y * Width + x];

    public void FillRect(int x, int y, int width, int height, int rgb565)
    {
        for (int yy = y; yy < y + height; yy++)
        {
            for (int xx = x; xx < x + width; xx++)
            {
                SetPixel(xx, yy, rgb565);
            }
        }
    }

    public void FillCircle(int cx, int cy, int r, int rgb565)
    {
        for (int yy = -r; yy <= r; yy++)
        {
            for (int xx = -r; xx <= r; xx++)
            {
                if (xx * xx + yy * yy <= r * r)
                {
                    SetPixel(cx + xx, cy + yy, rgb565);
                }
            }
        }
    }

    public void DrawLine(int x0, int y0, int x1, int y1, int rgb565)
    {
        int x = x0, y = y0;
        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            SetPixel(x, y, rgb565);
            if (x == x1 && y == y1)
            {
                return;
            }
            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    public void DrawText(int x, int y, string text, int rgb565, int scale)
    {
        scale = Math.Max(1, scale);
        int cursor = x;
        foreach (char ch in text)
        {
            var glyph = Font8x8.Glyph(ch);
            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    if ((glyph[row] & (1 << col)) != 0)
                    {
                        FillRect(cursor + col * scale, y + row * scale, scale, scale, rgb565);
                    }
                }
            }
            cursor += 8 * scale;
        }
    }

    public void SetClip(int x, int y, int width, int height) => _clip = (x, y, Math.Max(0, width), Math.Max(0, height));

    public void ClearClip() => _clip = null;

    public void Present() => Presented?.Invoke();

    /// <summary>The frame as 32-bit BGRA (little-endian ARGB), the layout WinForms, GTK/Cairo and Skia all take.</summary>
    public uint[] ToBgra32()
    {
        var out32 = new uint[Pixels.Length];
        for (int i = 0; i < Pixels.Length; i++)
        {
            out32[i] = Rgb565ToArgb(Pixels[i]);
        }
        return out32;
    }

    /// <summary>Expand RGB565 to opaque ARGB8888, replicating the high bits into the low ones.</summary>
    public static uint Rgb565ToArgb(ushort v)
    {
        uint r = (uint)(v >> 11) & 0x1F;
        uint g = (uint)(v >> 5) & 0x3F;
        uint b = (uint)v & 0x1F;
        r = (r << 3) | (r >> 2);
        g = (g << 2) | (g >> 4);
        b = (b << 3) | (b >> 2);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }
}
