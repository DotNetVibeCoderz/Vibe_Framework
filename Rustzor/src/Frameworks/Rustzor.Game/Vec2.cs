namespace Rustzor.Game;

/// <summary>A 2D vector of doubles.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);
    public static readonly Vec2 One = new(1, 1);
    public static readonly Vec2 Up = new(0, -1);
    public static readonly Vec2 Down = new(0, 1);
    public static readonly Vec2 Left = new(-1, 0);
    public static readonly Vec2 Right = new(1, 0);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;
    public double Angle => Math.Atan2(Y, X);

    public Vec2 Normalized()
    {
        var l = Length;
        return l < 1e-12 ? Zero : new Vec2(X / l, Y / l);
    }

    /// <summary>Shortened to at most <paramref name="max"/>.</summary>
    public Vec2 Clamped(double max)
    {
        var l = Length;
        return l <= max || l < 1e-12 ? this : this * (max / l);
    }

    public Vec2 Rotated(double radians)
    {
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        return new Vec2(X * c - Y * s, X * s + Y * c);
    }

    public Vec2 Perpendicular => new(-Y, X);

    public static Vec2 FromAngle(double radians, double length = 1) => new(Math.Cos(radians) * length, Math.Sin(radians) * length);
    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;
    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    public override string ToString() => $"({X:0.##}, {Y:0.##})";
}

/// <summary>An axis-aligned rectangle.</summary>
public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public Vec2 Center => new(X + Width / 2, Y + Height / 2);

    public bool Contains(Vec2 p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public bool Intersects(Rect2 o) => X < o.Right && Right > o.X && Y < o.Bottom && Bottom > o.Y;
    public static Rect2 FromCenter(Vec2 c, double w, double h) => new(c.X - w / 2, c.Y - h / 2, w, h);
}

/// <summary>A small, fast, seedable random generator (xorshift), so a seed replays a game exactly.</summary>
public sealed class Rng
{
    private ulong _state;

    public Rng(ulong seed = 0x9E3779B97F4A7C15)
    {
        // splitmix64, so neighbouring small seeds (1, 2, 3) start far apart.
        var z = seed + 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        z ^= z >> 31;
        _state = z == 0 ? 1 : z;
    }

    public ulong NextULong()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        _state = x;
        return x;
    }

    /// <summary>In [0, 1).</summary>
    public double Next() => (NextULong() >> 11) * (1.0 / (1UL << 53));
    public double Range(double min, double max) => min + (max - min) * Next();
    public int Range(int min, int maxExclusive) => min + (int)(Next() * (maxExclusive - min));
    public bool Chance(double probability) => Next() < probability;
    public T Pick<T>(IReadOnlyList<T> items) => items[Range(0, items.Count)];
    public Vec2 InCircle(double radius) => Vec2.FromAngle(Range(0, Math.PI * 2), Math.Sqrt(Next()) * radius);
}

public static class MathX
{
    public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double InverseLerp(double a, double b, double v) => Math.Abs(b - a) < 1e-12 ? 0 : (v - a) / (b - a);
    /// <summary>Frame-rate independent smoothing toward a target.</summary>
    public static double Damp(double current, double target, double lambda, double dt) => Lerp(current, target, 1 - Math.Exp(-lambda * dt));
    public static double Wrap(double v, double min, double max)
    {
        var range = max - min;
        return range <= 0 ? min : v - range * Math.Floor((v - min) / range);
    }
}
