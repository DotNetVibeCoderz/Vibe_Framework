using RustNet.Community.Bus;
using RustNet.Community.DisplayCore;

namespace RustNet.Community.Tft;

/// <summary>
/// Base of the SPI colour TFT/OLED drivers. Most controllers here speak MIPI
/// DCS: CASET (0x2A) and RASET (0x2B) set a window, RAMWR (0x2C) starts the
/// pixel stream, MADCTL (0x36) sets scan direction = rotation and RGB/BGR
/// order, COLMOD (0x3A) the pixel format. Subclasses supply the power-up
/// table, the MADCTL value per rotation and any RAM offsets; controllers
/// with another command set (SSD1331, SSD1351, ILI9225) override the
/// window/rotation methods.
/// </summary>
/// <remarks>
/// Init tables are hex strings of entries <c>cmd n args… [delay]</c>: the low
/// 7 bits of <c>n</c> count the parameter bytes, bit 7 means a delay byte
/// follows (milliseconds, 255 = 500 ms). An entry with <c>n</c> = 0xFF is a
/// pure delay (its command byte is ignored). Pixels are RGB565 in wire order
/// (high byte first) — the layout of <see cref="Rgb565Canvas"/>.
/// </remarks>
public abstract class TftDisplay
{
    public DisplayBus Bus;
    public DigitalOutput? Reset;

    /// <summary>Panel size in rotation 0.</summary>
    public int NativeWidth;
    public int NativeHeight;

    /// <summary>Current logical size (swapped in rotations 1 and 3).</summary>
    public int Width;
    public int Height;
    public int Rotation;

    /// <summary>
    /// RAM offsets of the visible glass, Adafruit-style: rotation 0 uses
    /// (ColStart, RowStart), 1 (RowStart, ColStart2), 2 (ColStart2, RowStart2),
    /// 3 (RowStart2, ColStart) as the (x, y) origin.
    /// </summary>
    public int ColStart;
    public int RowStart;
    public int ColStart2;
    public int RowStart2;

    /// <summary>Current window origin in RAM coordinates.</summary>
    public int XStart;
    public int YStart;

    /// <summary>18-bit pixels (3 bytes, RGB666, each channel left-aligned) instead of 16-bit.</summary>
    public bool Rgb666;

    /// <summary>MADCTL value for rotations 0..3.</summary>
    public int[] Madctl = new int[4];

    /// <summary>Send SWRESET (0x01) at power-up when no reset pin is wired.</summary>
    public bool SoftResetWithoutPin = true;

    /// <summary>Pixels per SPI burst for fills and 18-bit conversion.</summary>
    public int ChunkPixels = 256;

    protected TftDisplay(DisplayBus bus, DigitalOutput? reset, int width, int height)
    {
        Bus = bus;
        Reset = reset;
        NativeWidth = width;
        NativeHeight = height;
        Width = width;
        Height = height;
    }

    protected void SetMadctl(int r0, int r1, int r2, int r3)
    {
        Madctl[0] = r0;
        Madctl[1] = r1;
        Madctl[2] = r2;
        Madctl[3] = r3;
    }

    protected void SetOffsets(int colStart, int rowStart, int colStart2, int rowStart2)
    {
        ColStart = colStart;
        RowStart = rowStart;
        ColStart2 = colStart2;
        RowStart2 = rowStart2;
    }

    /// <summary>The power-up table (see the class remarks for its format).</summary>
    public abstract string InitTable();

    /// <summary>Reset (pin pulse, else SWRESET), run the init table, apply <see cref="Rotation"/>.</summary>
    public virtual void Begin()
    {
        if (Reset != null)
        {
            DisplayPins.ResetPulse(Reset, 10, 120);
        }
        else if (SoftResetWithoutPin)
        {
            Bus.Command(0x01);
            Timing.SleepMs(150);
        }
        RunTable(InitTable());
        SetRotation(Rotation);
    }

    /// <summary>Send a command with its parameters (MIPI style: parameters as data).</summary>
    public virtual void CommandWithArgs(int command, byte[] args, int offset, int count)
    {
        Bus.Command(command);
        if (count > 0)
        {
            Bus.WriteData(args, offset, count);
        }
    }

    public void RunTable(string table)
    {
        byte[] t = HexTable.Decode(table);
        int i = 0;
        while (i + 1 < t.Length)
        {
            int cmd = t[i];
            int n = t[i + 1];
            i += 2;
            if (n == 0xFF)
            {
                Timing.SleepMs(t[i]);
                i++;
                continue;
            }
            int count = n & 0x7F;
            CommandWithArgs(cmd, t, i, count);
            i += count;
            if ((n & 0x80) != 0)
            {
                int d = t[i];
                i++;
                Timing.SleepMs(d == 255 ? 500 : d);
            }
        }
    }

    /// <summary>Rotation 0..3 (90° steps clockwise): MADCTL, logical size and RAM offsets.</summary>
    public virtual void SetRotation(int rotation)
    {
        Rotation = rotation & 3;
        byte[] m = new byte[1];
        m[0] = (byte)Madctl[Rotation];
        CommandWithArgs(0x36, m, 0, 1);
        ApplyRotationGeometry();
    }

    protected void ApplyRotationGeometry()
    {
        bool swap = (Rotation & 1) != 0;
        Width = swap ? NativeHeight : NativeWidth;
        Height = swap ? NativeWidth : NativeHeight;
        if (Rotation == 0)
        {
            XStart = ColStart;
            YStart = RowStart;
        }
        else if (Rotation == 1)
        {
            XStart = RowStart;
            YStart = ColStart2;
        }
        else if (Rotation == 2)
        {
            XStart = ColStart2;
            YStart = RowStart2;
        }
        else
        {
            XStart = RowStart2;
            YStart = ColStart;
        }
    }

    protected static byte[] Pair16(int a, int b)
    {
        byte[] r = new byte[4];
        r[0] = (byte)(a >> 8);
        r[1] = (byte)a;
        r[2] = (byte)(b >> 8);
        r[3] = (byte)b;
        return r;
    }

    /// <summary>Address a w×h window at (x, y) in logical coordinates and start a RAM write.</summary>
    public virtual void SetWindow(int x, int y, int w, int h)
    {
        int x0 = x + XStart;
        int y0 = y + YStart;
        CommandWithArgs(0x2A, Pair16(x0, x0 + w - 1), 0, 4);
        CommandWithArgs(0x2B, Pair16(y0, y0 + h - 1), 0, 4);
        Bus.Command(0x2C);
    }

    /// <summary>Stream <paramref name="count"/> RGB565 pixels (wire order) from <paramref name="pixels"/>.</summary>
    public virtual void WritePixels(byte[] pixels, int pixelOffset, int count)
    {
        if (!Rgb666)
        {
            Bus.WriteData(pixels, pixelOffset * 2, count * 2);
            return;
        }
        int chunk = count < ChunkPixels ? count : ChunkPixels;
        byte[] buf = new byte[chunk * 3];
        int done = 0;
        while (done < count)
        {
            int n = count - done < chunk ? count - done : chunk;
            for (int i = 0; i < n; i++)
            {
                int p = (pixelOffset + done + i) * 2;
                int c = (pixels[p] << 8) | pixels[p + 1];
                buf[i * 3] = (byte)Rgb565.Red8(c);
                buf[i * 3 + 1] = (byte)Rgb565.Green8(c);
                buf[i * 3 + 2] = (byte)Rgb565.Blue8(c);
            }
            Bus.WriteData(buf, 0, n * 3);
            done += n;
        }
    }

    /// <summary>Stream one colour <paramref name="count"/> times, in bursts of <see cref="ChunkPixels"/>.</summary>
    public void WriteColor(int color, int count)
    {
        if (count <= 0)
        {
            return;
        }
        int bpp = Rgb666 ? 3 : 2;
        int chunk = count < ChunkPixels ? count : ChunkPixels;
        byte[] buf = new byte[chunk * bpp];
        for (int i = 0; i < chunk; i++)
        {
            if (Rgb666)
            {
                buf[i * 3] = (byte)Rgb565.Red8(color);
                buf[i * 3 + 1] = (byte)Rgb565.Green8(color);
                buf[i * 3 + 2] = (byte)Rgb565.Blue8(color);
            }
            else
            {
                buf[i * 2] = (byte)(color >> 8);
                buf[i * 2 + 1] = (byte)color;
            }
        }
        int left = count;
        while (left > 0)
        {
            int n = left < chunk ? left : chunk;
            Bus.WriteData(buf, 0, n * bpp);
            left -= n;
        }
    }

    /// <summary>Fill a rectangle (clipped to the screen).</summary>
    public void FillRect(int x, int y, int w, int h, int color)
    {
        if (x < 0)
        {
            w += x;
            x = 0;
        }
        if (y < 0)
        {
            h += y;
            y = 0;
        }
        if (x + w > Width)
        {
            w = Width - x;
        }
        if (y + h > Height)
        {
            h = Height - y;
        }
        if (w <= 0 || h <= 0)
        {
            return;
        }
        SetWindow(x, y, w, h);
        WriteColor(color, w * h);
    }

    public void FillScreen(int color) => FillRect(0, 0, Width, Height, color);

    public void DrawPixel(int x, int y, int color) => FillRect(x, y, 1, 1, color);

    /// <summary>Blit w×h RGB565 pixels in wire order (high byte first). Must lie inside the screen.</summary>
    public void DrawImage(int x, int y, int w, int h, byte[] rgb565)
    {
        SetWindow(x, y, w, h);
        WritePixels(rgb565, 0, w * h);
    }

    /// <summary>Send a whole canvas with its top-left at (x, y): one window, one burst.</summary>
    public void Flush(Rgb565Canvas canvas, int x, int y) => DrawImage(x, y, canvas.Width, canvas.Height, canvas.Buffer);

    public virtual void Invert(bool on) => Bus.Command(on ? 0x21 : 0x20);

    public virtual void DisplayOn(bool on) => Bus.Command(on ? 0x29 : 0x28);

    /// <summary>Sleep in (0x10) / out (0x11, then the 120 ms the datasheets require).</summary>
    public virtual void Sleep(bool sleep)
    {
        Bus.Command(sleep ? 0x10 : 0x11);
        Timing.SleepMs(120);
    }
}
