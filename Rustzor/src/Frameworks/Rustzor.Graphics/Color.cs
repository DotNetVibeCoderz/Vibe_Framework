using System.Globalization;

namespace Rustzor.Graphics;

/// <summary>An RGBA colour with 0–255 channels and 0–1 alpha.</summary>
public readonly record struct Color(byte R, byte G, byte B, double A = 1)
{
    public static readonly Color Black = new(0, 0, 0);
    public static readonly Color White = new(255, 255, 255);
    public static readonly Color Transparent = new(0, 0, 0, 0);

    /// <summary>Parses "#rgb", "#rrggbb", "#rrggbbaa", "rgb(r,g,b)" and "rgba(r,g,b,a)".</summary>
    public static Color Parse(string text)
    {
        var s = text.Trim();
        if (s.StartsWith('#'))
        {
            var hex = s[1..];
            if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            var a = hex.Length >= 8 ? Convert.ToByte(hex[6..8], 16) / 255.0 : 1;
            return new Color(r, g, b, a);
        }
        var open = s.IndexOf('(');
        if (open > 0 && s.EndsWith(')'))
        {
            var parts = s[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
            double P(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
            return new Color((byte)P(0), (byte)P(1), (byte)P(2), parts.Length > 3 ? P(3) : 1);
        }
        return Black;
    }

    /// <summary>Hue in degrees, saturation and lightness in 0–1.</summary>
    public static Color FromHsl(double h, double s, double l, double a = 1)
    {
        h = ((h % 360) + 360) % 360 / 360;
        double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        double r, g, b;
        if (s == 0) r = g = b = l;
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3);
            g = Hue(p, q, h);
            b = Hue(p, q, h - 1.0 / 3);
        }
        return new Color((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255), a);
    }

    public (double H, double S, double L) ToHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public Color WithAlpha(double a) => this with { A = a };

    public static Color Lerp(Color x, Color y, double t) => new(
        (byte)Math.Round(x.R + (y.R - x.R) * t),
        (byte)Math.Round(x.G + (y.G - x.G) * t),
        (byte)Math.Round(x.B + (y.B - x.B) * t),
        x.A + (y.A - x.A) * t);

    /// <summary>Relative luminance (WCAG), for choosing readable text colours.</summary>
    public double Luminance
    {
        get
        {
            static double C(byte v)
            {
                var c = v / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * C(R) + 0.7152 * C(G) + 0.0722 * C(B);
        }
    }

    public string ToHex() => A >= 1 ? $"#{R:x2}{G:x2}{B:x2}" : $"#{R:x2}{G:x2}{B:x2}{(byte)Math.Round(A * 255):x2}";

    public string ToCss() => A >= 1 ? ToHex() : string.Create(CultureInfo.InvariantCulture, $"rgba({R},{G},{B},{A:0.###})");

    /// <summary>For WebGL uniforms: r, g, b, a in 0–1.</summary>
    public (double R, double G, double B, double A) ToUnit() => (R / 255.0, G / 255.0, B / 255.0, A);

    public override string ToString() => ToCss();
}

/// <summary>A 2D affine transform: [a c e; b d f; 0 0 1], as Canvas uses it.</summary>
public readonly record struct Transform2D(double A, double B, double C, double D, double E, double F)
{
    public static readonly Transform2D Identity = new(1, 0, 0, 1, 0, 0);

    public static Transform2D Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Transform2D Scaling(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);
    public static Transform2D Rotation(double radians) => new(Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), 0, 0);

    /// <summary>Applies <paramref name="first"/>, then <paramref name="then"/>.</summary>
    public static Transform2D operator *(Transform2D then, Transform2D first) => new(
        then.A * first.A + then.C * first.B,
        then.B * first.A + then.D * first.B,
        then.A * first.C + then.C * first.D,
        then.B * first.C + then.D * first.D,
        then.A * first.E + then.C * first.F + then.E,
        then.B * first.E + then.D * first.F + then.F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    public Transform2D Inverse()
    {
        var det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return Identity;
        var inv = 1 / det;
        return new Transform2D(D * inv, -B * inv, -C * inv, A * inv, (C * F - D * E) * inv, (B * E - A * F) * inv);
    }
}
