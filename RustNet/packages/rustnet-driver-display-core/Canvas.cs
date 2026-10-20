namespace RustNet.Community.DisplayCore;

/// <summary>RGB565 colour helpers (5 bits red, 6 green, 5 blue; red in the top bits).</summary>
public static class Rgb565
{
    public const int Black = 0x0000;
    public const int White = 0xFFFF;
    public const int Red = 0xF800;
    public const int Green = 0x07E0;
    public const int Blue = 0x001F;
    public const int Yellow = 0xFFE0;
    public const int Cyan = 0x07FF;
    public const int Magenta = 0xF81F;

    /// <summary>Pack 8-bit channels into RGB565 (truncating, as the panels do).</summary>
    public static int FromRgb(int r, int g, int b) => ((r & 0xF8) << 8) | ((g & 0xFC) << 3) | ((b & 0xFF) >> 3);

    /// <summary>The 8-bit channel values an 18-bit (RGB666) panel is sent for an RGB565 colour:
    /// each channel left-aligned in its byte, as ILI9488/ILI9481 expect over SPI.</summary>
    public static int Red8(int c) => (c >> 8) & 0xF8;

    public static int Green8(int c) => (c >> 3) & 0xFC;

    public static int Blue8(int c) => (c << 3) & 0xF8;
}

/// <summary>Colours of a 1-bit canvas.</summary>
public static class MonoColor
{
    /// <summary>Pixel off (dark on an OLED, paper on an e-paper panel).</summary>
    public const int Off = 0;

    /// <summary>Pixel on (lit on an OLED, ink on an e-paper panel).</summary>
    public const int On = 1;

    /// <summary>Flip whatever is there.</summary>
    public const int Invert = 2;
}

/// <summary>
/// A drawing surface. Subclasses store pixels in the layout their controller
/// wants (so a flush is one copy-free bus burst) and implement
/// <see cref="SetPixel"/>/<see cref="GetPixel"/>; everything else — lines,
/// rectangles, circles, text — is built on those. Out-of-range pixels are
/// clipped silently.
/// </summary>
public abstract class Canvas
{
    public int Width;
    public int Height;

    /// <summary>Text colour used by <see cref="Text"/> for the glyph background; -1 = transparent.</summary>
    public int TextBackground = -1;

    public abstract void SetPixel(int x, int y, int color);

    public abstract int GetPixel(int x, int y);

    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public virtual void FillRect(int x, int y, int w, int h, int color)
    {
        int x0 = x < 0 ? 0 : x;
        int y0 = y < 0 ? 0 : y;
        int x1 = x + w > Width ? Width : x + w;
        int y1 = y + h > Height ? Height : y + h;
        for (int yy = y0; yy < y1; yy++)
        {
            for (int xx = x0; xx < x1; xx++)
            {
                SetPixel(xx, yy, color);
            }
        }
    }

    public virtual void Clear(int color) => FillRect(0, 0, Width, Height, color);

    public void HLine(int x, int y, int w, int color) => FillRect(x, y, w, 1, color);

    public void VLine(int x, int y, int h, int color) => FillRect(x, y, 1, h, color);

    /// <summary>Bresenham line, both end points included.</summary>
    public void Line(int x0, int y0, int x1, int y1, int color)
    {
        int dx = x1 > x0 ? x1 - x0 : x0 - x1;
        int dy = y1 > y0 ? y0 - y1 : y1 - y0;
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            SetPixel(x0, y0, color);
            if (x0 == x1 && y0 == y1)
            {
                return;
            }
            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>Rectangle outline.</summary>
    public void Rect(int x, int y, int w, int h, int color)
    {
        if (w <= 0 || h <= 0)
        {
            return;
        }
        HLine(x, y, w, color);
        if (h > 1)
        {
            HLine(x, y + h - 1, w, color);
        }
        if (h > 2)
        {
            VLine(x, y + 1, h - 2, color);
            if (w > 1)
            {
                VLine(x + w - 1, y + 1, h - 2, color);
            }
        }
    }

    /// <summary>Midpoint circle outline of radius <paramref name="r"/> centred on (cx, cy).</summary>
    public void Circle(int cx, int cy, int r, int color)
    {
        int x = r;
        int y = 0;
        int err = 1 - r;
        while (x >= y)
        {
            // Each octant point once, so MonoColor.Invert never flips a pixel twice.
            if (y == 0)
            {
                SetPixel(cx + x, cy, color);
                SetPixel(cx - x, cy, color);
                SetPixel(cx, cy + x, color);
                SetPixel(cx, cy - x, color);
            }
            else
            {
                SetPixel(cx + x, cy + y, color);
                SetPixel(cx - x, cy + y, color);
                SetPixel(cx + x, cy - y, color);
                SetPixel(cx - x, cy - y, color);
                if (x != y)
                {
                    SetPixel(cx + y, cy + x, color);
                    SetPixel(cx - y, cy + x, color);
                    SetPixel(cx + y, cy - x, color);
                    SetPixel(cx - y, cy - x, color);
                }
            }
            y++;
            if (err < 0)
            {
                err += 2 * y + 1;
            }
            else
            {
                x--;
                err += 2 * (y - x) + 1;
            }
        }
    }

    /// <summary>Filled circle: every pixel with (dx² + dy²) ≤ r².</summary>
    public void FillCircle(int cx, int cy, int r, int color)
    {
        int rr = r * r;
        for (int dy = -r; dy <= r; dy++)
        {
            int half = 0;
            while ((half + 1) * (half + 1) + dy * dy <= rr)
            {
                half++;
            }
            HLine(cx - half, cy + dy, half * 2 + 1, color);
        }
    }

    /// <summary>Draw one 5×7 glyph with its top-left at (x, y), scaled by an integer factor.</summary>
    public void Char(int x, int y, char c, int color, int scale)
    {
        if (scale < 1)
        {
            scale = 1;
        }
        for (int col = 0; col < Font5x7.CellWidth; col++)
        {
            int bits = col < Font5x7.GlyphWidth ? Font5x7.Column(c, col) : 0;
            for (int row = 0; row < Font5x7.CellHeight; row++)
            {
                bool on = ((bits >> row) & 1) != 0;
                if (on || TextBackground >= 0)
                {
                    int px = x + col * scale;
                    int py = y + row * scale;
                    int v = on ? color : TextBackground;
                    if (scale == 1)
                    {
                        SetPixel(px, py, v);
                    }
                    else
                    {
                        FillRect(px, py, scale, scale, v);
                    }
                }
            }
        }
    }

    /// <summary>Draw a string on one line; each character advances 6×scale pixels. Returns the x after the text.</summary>
    public int Text(int x, int y, string text, int color, int scale)
    {
        if (scale < 1)
        {
            scale = 1;
        }
        for (int i = 0; i < text.Length; i++)
        {
            Char(x, y, text[i], color, scale);
            x += Font5x7.CellWidth * scale;
        }
        return x;
    }
}

/// <summary>
/// 1 bit per pixel in SSD1306 page layout: byte <c>x + (y / 8) * Width</c>,
/// bit <c>y % 8</c> (bit 0 = top row of the page). This is the GDDRAM layout
/// of SSD1306/SSD1309/SH1106/SH1107/CH1115/UC1609/ST7565, so a page is
/// streamed to the controller as-is. Height is rounded up to whole pages.
/// </summary>
public class PageMonoCanvas : Canvas
{
    public byte[] Buffer;
    public int Pages;

    public PageMonoCanvas(int width, int height)
    {
        Width = width;
        Height = height;
        Pages = (height + 7) / 8;
        Buffer = new byte[width * Pages];
    }

    public override void SetPixel(int x, int y, int color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }
        int i = x + (y >> 3) * Width;
        int bit = 1 << (y & 7);
        if (color == MonoColor.Invert)
        {
            Buffer[i] = (byte)(Buffer[i] ^ bit);
        }
        else if (color != 0)
        {
            Buffer[i] = (byte)(Buffer[i] | bit);
        }
        else
        {
            Buffer[i] = (byte)(Buffer[i] & ~bit);
        }
    }

    public override int GetPixel(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return 0;
        }
        return (Buffer[x + (y >> 3) * Width] >> (y & 7)) & 1;
    }

    public override void Clear(int color)
    {
        byte v = (byte)(color == MonoColor.Off ? 0x00 : 0xFF);
        for (int i = 0; i < Buffer.Length; i++)
        {
            Buffer[i] = color == MonoColor.Invert ? (byte)(Buffer[i] ^ 0xFF) : v;
        }
    }
}

/// <summary>
/// 1 bit per pixel, row-major, most significant bit = leftmost pixel: byte
/// <c>y * Stride + x / 8</c>, bit <c>0x80 &gt;&gt; (x % 8)</c>. The RAM layout of
/// e-paper controllers (SSD16xx, UC81xx/IL03xx). A set bit means "on" (ink);
/// drivers invert while sending when the controller wants 0 = black.
/// </summary>
public class RowMonoCanvas : Canvas
{
    public byte[] Buffer;
    public int Stride;

    public RowMonoCanvas(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = (width + 7) / 8;
        Buffer = new byte[Stride * height];
    }

    public override void SetPixel(int x, int y, int color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }
        int i = y * Stride + (x >> 3);
        int bit = 0x80 >> (x & 7);
        if (color == MonoColor.Invert)
        {
            Buffer[i] = (byte)(Buffer[i] ^ bit);
        }
        else if (color != 0)
        {
            Buffer[i] = (byte)(Buffer[i] | bit);
        }
        else
        {
            Buffer[i] = (byte)(Buffer[i] & ~bit);
        }
    }

    public override int GetPixel(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return 0;
        }
        return (Buffer[y * Stride + (x >> 3)] >> (7 - (x & 7))) & 1;
    }

    public override void Clear(int color)
    {
        byte v = (byte)(color == MonoColor.Off ? 0x00 : 0xFF);
        for (int i = 0; i < Buffer.Length; i++)
        {
            Buffer[i] = color == MonoColor.Invert ? (byte)(Buffer[i] ^ 0xFF) : v;
        }
    }
}

/// <summary>
/// RGB565 pixels, row-major, two bytes per pixel in <b>wire order</b>
/// (high byte first) — exactly what MIPI-DCS panels expect after RAMWR with
/// COLMOD = 16 bit, so a flush is a single burst. (The built-in
/// <c>RustNet.Graphics.Display.DrawImage</c> takes little-endian bytes; use
/// <see cref="SwapBytes"/> to convert.)
/// </summary>
public class Rgb565Canvas : Canvas
{
    public byte[] Buffer;

    public Rgb565Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        Buffer = new byte[width * height * 2];
    }

    public override void SetPixel(int x, int y, int color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }
        int i = (y * Width + x) * 2;
        Buffer[i] = (byte)(color >> 8);
        Buffer[i + 1] = (byte)color;
    }

    public override int GetPixel(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return 0;
        }
        int i = (y * Width + x) * 2;
        return (Buffer[i] << 8) | Buffer[i + 1];
    }

    public override void FillRect(int x, int y, int w, int h, int color)
    {
        int x0 = x < 0 ? 0 : x;
        int y0 = y < 0 ? 0 : y;
        int x1 = x + w > Width ? Width : x + w;
        int y1 = y + h > Height ? Height : y + h;
        byte hi = (byte)(color >> 8);
        byte lo = (byte)color;
        for (int yy = y0; yy < y1; yy++)
        {
            int i = (yy * Width + x0) * 2;
            for (int xx = x0; xx < x1; xx++)
            {
                Buffer[i] = hi;
                Buffer[i + 1] = lo;
                i += 2;
            }
        }
    }

    /// <summary>Swap every byte pair in place (little-endian RGB565 ⇄ wire order).</summary>
    public static void SwapBytes(byte[] data)
    {
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            byte t = data[i];
            data[i] = data[i + 1];
            data[i + 1] = t;
        }
    }
}
