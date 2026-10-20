using RustNet.Community.DisplayCore;

namespace DriversApp;

/// <summary>Compare a canvas region against ASCII art ('#' = on, '.' = off), rows separated by '/'.</summary>
public static class Art
{
    public static void Matches(string what, Canvas c, int x0, int y0, string art)
    {
        int x = 0;
        int y = 0;
        for (int i = 0; i < art.Length; i++)
        {
            char ch = art[i];
            if (ch == '/')
            {
                y++;
                x = 0;
                continue;
            }
            int want = ch == '#' ? 1 : 0;
            int got = c.GetPixel(x0 + x, y0 + y) != 0 ? 1 : 0;
            if (want != got)
            {
                Check.True($"{what} pixel ({x},{y})", false);
                return;
            }
            x++;
        }
        Check.True(what, true);
    }

    public static int CountOn(Canvas c)
    {
        int n = 0;
        for (int y = 0; y < c.Height; y++)
        {
            for (int x = 0; x < c.Width; x++)
            {
                if (c.GetPixel(x, y) != 0)
                {
                    n++;
                }
            }
        }
        return n;
    }

    public static byte[] Slice(byte[] b, int offset, int count)
    {
        byte[] r = new byte[count];
        for (int i = 0; i < count; i++)
        {
            r[i] = b[offset + i];
        }
        return r;
    }
}
