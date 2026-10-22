using System.Globalization;

namespace Rupa.Model;

/// <summary>A 2D affine transform: x' = A·x + C·y + E, y' = B·x + D·y + F (SVG's matrix order).</summary>
public readonly struct Affine
{
    public readonly double A, B, C, D, E, F;

    public Affine(double a, double b, double c, double d, double e, double f)
    {
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Affine Scale(double s) => new(s, 0, 0, s, 0, 0);

    /// <summary>Rotation by <paramref name="degrees"/> about (cx, cy).</summary>
    public static Affine Rotate(double degrees, double cx, double cy)
    {
        if (degrees == 0) return Identity;
        var r = degrees * Math.PI / 180;
        var cos = Math.Cos(r);
        var sin = Math.Sin(r);
        return new Affine(cos, sin, -sin, cos, cx - cos * cx + sin * cy, cy - sin * cx - cos * cy);
    }

    /// <summary>This transform applied after <paramref name="inner"/>.</summary>
    public Affine Then(Affine inner) => new(
        A * inner.A + C * inner.B,
        B * inner.A + D * inner.B,
        A * inner.C + C * inner.D,
        B * inner.C + D * inner.D,
        A * inner.E + C * inner.F + E,
        B * inner.E + D * inner.F + F);

    public Pt Apply(Pt p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    public Pt Apply(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);

    public Affine Inverse()
    {
        var det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return Identity;
        var ia = D / det;
        var ib = -B / det;
        var ic = -C / det;
        var id = A / det;
        return new Affine(ia, ib, ic, id, -(ia * E + ic * F), -(ib * E + id * F));
    }

    public string Svg => $"matrix({G.N(A, 5)} {G.N(B, 5)} {G.N(C, 5)} {G.N(D, 5)} {G.N(E)} {G.N(F)})";
}

/// <summary>An axis-aligned box.</summary>
public struct Box
{
    public double X, Y, W, H;

    public Box(double x, double y, double w, double h)
    {
        X = x; Y = y; W = w; H = h;
    }

    public double Right => X + W;
    public double Bottom => Y + H;
    public double Cx => X + W / 2;
    public double Cy => Y + H / 2;

    public bool Contains(Pt p) => p.X >= X && p.X <= X + W && p.Y >= Y && p.Y <= Y + H;

    public bool Intersects(Box o) => X <= o.Right && o.X <= Right && Y <= o.Bottom && o.Y <= Bottom;

    public static Box Union(Box a, Box b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new Box(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    public static Box Around(Pt a, Pt b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>The bounds of four transformed corners.</summary>
    public static Box OfCorners(Affine m, double w, double h)
    {
        var p0 = m.Apply(0, 0);
        var p1 = m.Apply(w, 0);
        var p2 = m.Apply(w, h);
        var p3 = m.Apply(0, h);
        var x0 = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        var y0 = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        var x1 = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        var y1 = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
        return new Box(x0, y0, x1 - x0, y1 - y0);
    }
}

/// <summary>Geometry of layers: transforms, bounds, hit tests, and number formatting for SVG.</summary>
public static class G
{
    /// <summary>A number for markup: invariant, at most <paramref name="digits"/> decimals, no exponent.</summary>
    /// <remarks>
    /// Written out digit by digit: the canvas formats hundreds of numbers a frame,
    /// and this is several times faster than general number formatting.
    /// </remarks>
    public static string N(double v, int digits = 2)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
        long scale = digits switch { 0 => 1, 1 => 10, 2 => 100, 3 => 1000, 4 => 10000, _ => 100000 };
        if (digits > 5) digits = 5;
        var sv = Math.Round(v * scale);
        if (Math.Abs(sv) > 9e15) return Math.Round(v).ToString("F0", CultureInfo.InvariantCulture);
        var k = (long)sv;
        if (k == 0) return "0";
        var buf = new char[24];
        var pos = 24;
        var neg = k < 0;
        var u = neg ? -k : k;
        var started = false;
        for (var i = 0; i < digits; i++)
        {
            var d = (int)(u % 10);
            u /= 10;
            if (!started && d == 0) continue;
            started = true;
            buf[--pos] = (char)('0' + d);
        }
        if (started) buf[--pos] = '.';
        do
        {
            buf[--pos] = (char)('0' + (int)(u % 10));
            u /= 10;
        } while (u > 0);
        if (neg) buf[--pos] = '-';
        return new string(buf, pos, 24 - pos);
    }

    /// <summary>A layer's transform within its parent: move to (X, Y), then turn about the centre.</summary>
    public static Affine Local(Node n) =>
        Affine.Translate(n.X, n.Y).Then(Affine.Rotate(n.Rotation, n.W / 2, n.H / 2));

    public static string LocalSvg(Node n) =>
        n.Rotation == 0
            ? $"translate({N(n.X)} {N(n.Y)})"
            : $"translate({N(n.X)} {N(n.Y)}) rotate({N(n.Rotation)} {N(n.W / 2)} {N(n.H / 2)})";

    /// <summary>From the layer's own space to the canvas.</summary>
    public static Affine World(Node n)
    {
        var m = Local(n);
        for (var p = n.Parent; p is not null; p = p.Parent) m = Local(p).Then(m);
        return m;
    }

    /// <summary>From the parent's space to the canvas (identity at the top level).</summary>
    public static Affine ParentWorld(Node n) => n.Parent is null ? Affine.Identity : World(n.Parent);

    /// <summary>The layer's bounds in its parent's space.</summary>
    public static Box BoundsInParent(Node n) => Box.OfCorners(Local(n), n.W, n.H);

    /// <summary>The layer's bounds on the canvas.</summary>
    public static Box WorldBounds(Node n) => Box.OfCorners(World(n), n.W, n.H);

    /// <summary>Whether a canvas point lands on the layer's own paint (not counting children).</summary>
    public static bool HitShape(Node n, Pt world, double tolerance)
    {
        var p = World(n).Inverse().Apply(world);
        switch (n.Kind)
        {
            case NodeKind.Ellipse:
            {
                var rx = n.W / 2 + tolerance;
                var ry = n.H / 2 + tolerance;
                var dx = (p.X - n.W / 2) / rx;
                var dy = (p.Y - n.H / 2) / ry;
                return dx * dx + dy * dy <= 1;
            }
            case NodeKind.Line:
                return p.X >= -tolerance && p.X <= n.W + tolerance && Math.Abs(p.Y) <= tolerance + n.StrokeWidth / 2;
            case NodeKind.Path:
            {
                var pts = n.Points;
                var reach = tolerance + n.StrokeWidth / 2;
                for (var i = 0; i + 1 < pts.Count; i++)
                    if (SegmentDistance(p, pts[i], pts[i + 1]) <= reach) return true;
                if (n.Closed && pts.Count > 2)
                {
                    if (SegmentDistance(p, pts[^1], pts[0]) <= reach) return true;
                    if (n.Fill != "none" && InPolygon(p, pts)) return true;
                }
                return false;
            }
            default:
                return p.X >= -tolerance && p.X <= n.W + tolerance && p.Y >= -tolerance && p.Y <= n.H + tolerance;
        }
    }

    public static double SegmentDistance(Pt p, Pt a, Pt b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = dx * dx + dy * dy;
        var t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        var x = a.X + t * dx - p.X;
        var y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    public static bool InPolygon(Pt p, List<Pt> pts)
    {
        var inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            if ((pts[i].Y > p.Y) != (pts[j].Y > p.Y) &&
                p.X < (pts[j].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>The SVG path data of a vector layer.</summary>
    public static string PathData(Node n)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < n.Points.Count; i++)
        {
            sb.Append(i == 0 ? 'M' : 'L');
            sb.Append(N(n.Points[i].X)).Append(' ').Append(N(n.Points[i].Y));
        }
        if (n.Closed) sb.Append('Z');
        return sb.ToString();
    }

    public static double Angle(Pt c, Pt p) => Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI;

    public static double Normalize(double degrees)
    {
        var d = degrees % 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return Math.Round(d * 100) / 100;
    }
}
