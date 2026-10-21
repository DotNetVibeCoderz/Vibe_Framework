using RustNet.Graphics;

namespace RustNet.UI;

/// <summary>
/// A drawing surface for RustNet.UI other than the device display — the seam
/// desktop frontends (WinForms, GTK, Skia) use to show the very same paint
/// calls the device makes. The methods are the display's native primitives;
/// composite shapes (rectangle outlines, circles) are built from them exactly
/// as <see cref="Display"/> builds them, so a frontend reproduces the device
/// pixel for pixel by implementing these alone.
/// </summary>
public interface IUiCanvas
{
    void Init(int width, int height);
    void Clear(int rgb565);
    void SetPixel(int x, int y, int rgb565);
    void FillRect(int x, int y, int width, int height, int rgb565);
    void FillCircle(int cx, int cy, int r, int rgb565);
    void DrawLine(int x0, int y0, int x1, int y1, int rgb565);

    /// <summary>The device's built-in 8×8 font, <paramref name="scale"/> pixels per font pixel.</summary>
    void DrawText(int x, int y, string text, int rgb565, int scale);

    void SetClip(int x, int y, int width, int height);
    void ClearClip();

    /// <summary>A frame is complete.</summary>
    void Present();
}

/// <summary>
/// Where RustNet.UI paints: the device display, unless a frontend has set
/// <see cref="Target"/>. On the device this is one null check in front of each
/// display call.
/// </summary>
public static class Paint
{
    /// <summary>A desktop frontend's canvas, or null to draw on the device display.</summary>
    public static IUiCanvas? Target;

    public static void Init(int width, int height)
    {
        if (Target != null)
        {
            Target.Init(width, height);
            return;
        }
        Display.Init(width, height);
    }

    public static void Clear(int color)
    {
        if (Target != null)
        {
            Target.Clear(color);
            return;
        }
        Display.Clear(color);
    }

    public static void FillRect(int x, int y, int width, int height, int color)
    {
        if (Target != null)
        {
            Target.FillRect(x, y, width, height, color);
            return;
        }
        Display.FillRect(x, y, width, height, color);
    }

    public static void FillCircle(int cx, int cy, int r, int color)
    {
        if (Target != null)
        {
            Target.FillCircle(cx, cy, r, color);
            return;
        }
        Display.FillCircle(cx, cy, r, color);
    }

    public static void DrawLine(int x0, int y0, int x1, int y1, int color)
    {
        if (Target != null)
        {
            Target.DrawLine(x0, y0, x1, y1, color);
            return;
        }
        Display.DrawLine(x0, y0, x1, y1, color);
    }

    public static void DrawText(int x, int y, string text, int color, int scale)
    {
        if (Target != null)
        {
            Target.DrawText(x, y, text, color, scale);
            return;
        }
        Display.DrawText(x, y, text, color, scale);
    }

    public static void SetClip(int x, int y, int width, int height)
    {
        if (Target != null)
        {
            Target.SetClip(x, y, width, height);
            return;
        }
        Display.SetClip(x, y, width, height);
    }

    public static void ClearClip()
    {
        if (Target != null)
        {
            Target.ClearClip();
            return;
        }
        Display.ClearClip();
    }

    public static void Present()
    {
        if (Target != null)
        {
            Target.Present();
            return;
        }
        Display.Present();
    }

    /// <summary>Rectangle outline — the same four lines <see cref="Display.DrawRect"/> draws.</summary>
    public static void DrawRect(int x, int y, int width, int height, int color)
    {
        if (Target == null)
        {
            Display.DrawRect(x, y, width, height, color);
            return;
        }
        Target.DrawLine(x, y, x + width - 1, y, color);
        Target.DrawLine(x, y + height - 1, x + width - 1, y + height - 1, color);
        Target.DrawLine(x, y, x, y + height - 1, color);
        Target.DrawLine(x + width - 1, y, x + width - 1, y + height - 1, color);
    }

    /// <summary>Circle outline — the same midpoint walk <see cref="Display.DrawCircle"/> does.</summary>
    public static void DrawCircle(int cx, int cy, int r, int color)
    {
        if (Target == null)
        {
            Display.DrawCircle(cx, cy, r, color);
            return;
        }
        int x = r;
        int y = 0;
        int d = 1 - r;
        while (x >= y)
        {
            Target.SetPixel(cx + x, cy + y, color);
            Target.SetPixel(cx - x, cy + y, color);
            Target.SetPixel(cx + x, cy - y, color);
            Target.SetPixel(cx - x, cy - y, color);
            Target.SetPixel(cx + y, cy + x, color);
            Target.SetPixel(cx - y, cy + x, color);
            Target.SetPixel(cx + y, cy - x, color);
            Target.SetPixel(cx - y, cy - x, color);
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
