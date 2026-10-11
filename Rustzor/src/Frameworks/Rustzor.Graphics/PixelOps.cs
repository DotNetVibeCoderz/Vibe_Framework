namespace Rustzor.Graphics;

/// <summary>
/// In-place operations on RGBA pixel buffers (4 bytes per pixel, as
/// <c>Canvas2D.GetPixels</c> returns them). Every method is static over
/// primitive arrays, the shape the Rustzor AOT compiler turns into
/// WebAssembly — build with AOT for interactive image editing.
/// </summary>
public static class PixelOps
{
    private static byte Clamp(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);

    public static void Grayscale(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var y = Clamp(0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2]);
            rgba[i] = y;
            rgba[i + 1] = y;
            rgba[i + 2] = y;
        }
    }

    public static void Invert(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(255 - rgba[i]);
            rgba[i + 1] = (byte)(255 - rgba[i + 1]);
            rgba[i + 2] = (byte)(255 - rgba[i + 2]);
        }
    }

    public static void Sepia(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            double r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            rgba[i] = Clamp(0.393 * r + 0.769 * g + 0.189 * b);
            rgba[i + 1] = Clamp(0.349 * r + 0.686 * g + 0.168 * b);
            rgba[i + 2] = Clamp(0.272 * r + 0.534 * g + 0.131 * b);
        }
    }

    /// <summary><paramref name="amount"/> in -255..255.</summary>
    public static void Brightness(byte[] rgba, double amount)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = Clamp(rgba[i] + amount);
            rgba[i + 1] = Clamp(rgba[i + 1] + amount);
            rgba[i + 2] = Clamp(rgba[i + 2] + amount);
        }
    }

    /// <summary><paramref name="amount"/> in -100..100.</summary>
    public static void Contrast(byte[] rgba, double amount)
    {
        var c = amount * 2.55;
        var f = 259 * (c + 255) / (255 * (259 - c));
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = Clamp(f * (rgba[i] - 128) + 128);
            rgba[i + 1] = Clamp(f * (rgba[i + 1] - 128) + 128);
            rgba[i + 2] = Clamp(f * (rgba[i + 2] - 128) + 128);
        }
    }

    /// <summary>1 keeps colour, 0 is grey, 2 doubles saturation.</summary>
    public static void Saturation(byte[] rgba, double factor)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            double r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            var y = 0.299 * r + 0.587 * g + 0.114 * b;
            rgba[i] = Clamp(y + (r - y) * factor);
            rgba[i + 1] = Clamp(y + (g - y) * factor);
            rgba[i + 2] = Clamp(y + (b - y) * factor);
        }
    }

    public static void Threshold(byte[] rgba, int level)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var v = 0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2] >= level ? (byte)255 : (byte)0;
            rgba[i] = v;
            rgba[i + 1] = v;
            rgba[i + 2] = v;
        }
    }

    /// <summary>Reduces each channel to <paramref name="levels"/> values.</summary>
    public static void Posterize(byte[] rgba, int levels)
    {
        if (levels < 2) levels = 2;
        var step = 255.0 / (levels - 1);
        for (var i = 0; i < rgba.Length; i++)
        {
            if ((i & 3) == 3) continue;
            rgba[i] = Clamp(Math.Round(rgba[i] / step) * step);
        }
    }

    /// <summary>A separable box blur, run twice for a smoother result.</summary>
    public static void BoxBlur(byte[] rgba, int width, int height, int radius)
    {
        if (radius < 1) return;
        var tmp = new byte[rgba.Length];
        for (var pass = 0; pass < 2; pass++)
        {
            BlurLine(rgba, tmp, width, height, radius, true);
            BlurLine(tmp, rgba, width, height, radius, false);
        }
    }

    private static void BlurLine(byte[] src, byte[] dst, int width, int height, int radius, bool horizontal)
    {
        var lines = horizontal ? height : width;
        var length = horizontal ? width : height;
        var window = radius * 2 + 1;
        for (var line = 0; line < lines; line++)
        {
            for (var ch = 0; ch < 4; ch++)
            {
                var sum = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var p = k < 0 ? 0 : k >= length ? length - 1 : k;
                    sum += src[Index(line, p, width, horizontal) + ch];
                }
                for (var p = 0; p < length; p++)
                {
                    dst[Index(line, p, width, horizontal) + ch] = (byte)(sum / window);
                    var outP = p - radius;
                    var inP = p + radius + 1;
                    if (outP < 0) outP = 0;
                    if (inP >= length) inP = length - 1;
                    sum += src[Index(line, inP, width, horizontal) + ch] - src[Index(line, outP, width, horizontal) + ch];
                }
            }
        }
    }

    private static int Index(int line, int p, int width, bool horizontal) => horizontal ? (line * width + p) * 4 : (p * width + line) * 4;

    /// <summary>A 3x3 convolution; kernel is row-major, 9 values.</summary>
    public static void Convolve3(byte[] rgba, int width, int height, double[] kernel, double divisor = 1, double offset = 0)
    {
        var src = (byte[])rgba.Clone();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            double r = 0, g = 0, b = 0;
            for (var ky = -1; ky <= 1; ky++)
            for (var kx = -1; kx <= 1; kx++)
            {
                var sx = x + kx < 0 ? 0 : x + kx >= width ? width - 1 : x + kx;
                var sy = y + ky < 0 ? 0 : y + ky >= height ? height - 1 : y + ky;
                var i = (sy * width + sx) * 4;
                var w = kernel[(ky + 1) * 3 + kx + 1];
                r += src[i] * w;
                g += src[i + 1] * w;
                b += src[i + 2] * w;
            }
            var o = (y * width + x) * 4;
            rgba[o] = Clamp(r / divisor + offset);
            rgba[o + 1] = Clamp(g / divisor + offset);
            rgba[o + 2] = Clamp(b / divisor + offset);
        }
    }

    public static void Sharpen(byte[] rgba, int width, int height) =>
        Convolve3(rgba, width, height, new double[] { 0, -1, 0, -1, 5, -1, 0, -1, 0 });

    public static void EdgeDetect(byte[] rgba, int width, int height) =>
        Convolve3(rgba, width, height, new double[] { -1, -1, -1, -1, 8, -1, -1, -1, -1 });

    public static void Emboss(byte[] rgba, int width, int height) =>
        Convolve3(rgba, width, height, new double[] { -2, -1, 0, -1, 1, 1, 0, 1, 2 }, 1, 0);

    /// <summary>Luminance histogram: 256 counts.</summary>
    public static int[] Histogram(byte[] rgba)
    {
        var h = new int[256];
        for (var i = 0; i < rgba.Length; i += 4) h[Clamp(0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2])]++;
        return h;
    }

    /// <summary>Flood fill from (x, y) with a colour, within a per-channel tolerance; returns pixels changed.</summary>
    public static int FloodFill(byte[] rgba, int width, int height, int x, int y, byte r, byte g, byte b, byte a, int tolerance)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return 0;
        var start = (y * width + x) * 4;
        int sr = rgba[start], sg = rgba[start + 1], sb = rgba[start + 2], sa = rgba[start + 3];
        if (sr == r && sg == g && sb == b && sa == a) return 0;
        // Each pixel is pushed at most once (marked when pushed), so the
        // stack never needs more than one slot per pixel.
        var seen = new bool[width * height];
        var stack = new int[width * height];
        var top = 0;
        stack[top++] = y * width + x;
        seen[y * width + x] = true;
        var changed = 0;
        while (top > 0)
        {
            var p = stack[--top];
            var i = p * 4;
            if (Math.Abs(rgba[i] - sr) > tolerance || Math.Abs(rgba[i + 1] - sg) > tolerance || Math.Abs(rgba[i + 2] - sb) > tolerance || Math.Abs(rgba[i + 3] - sa) > tolerance) continue;
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = a;
            changed++;
            var px = p % width;
            var py = p / width;
            if (px > 0 && !seen[p - 1]) { seen[p - 1] = true; stack[top++] = p - 1; }
            if (px < width - 1 && !seen[p + 1]) { seen[p + 1] = true; stack[top++] = p + 1; }
            if (py > 0 && !seen[p - width]) { seen[p - width] = true; stack[top++] = p - width; }
            if (py < height - 1 && !seen[p + width]) { seen[p + width] = true; stack[top++] = p + width; }
        }
        return changed;
    }
}
