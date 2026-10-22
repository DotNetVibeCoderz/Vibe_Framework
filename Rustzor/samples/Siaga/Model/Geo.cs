namespace Siaga.Model;

/// <summary>A longitude and latitude in degrees (WGS 84).</summary>
public readonly record struct LonLat(double Lon, double Lat);

/// <summary>
/// The map's arithmetic: Web Mercator, great-circle distance (Haversine),
/// geodesic length and area, point in polygon, and distance from a point to a
/// line or an area. Everything a measurement or a query answers comes from
/// here; the browser module only draws.
/// </summary>
public static class Geo
{
    /// <summary>The WGS 84 semi-major axis, used by Web Mercator and the area formula.</summary>
    public const double EquatorRadius = 6378137;
    /// <summary>The mean Earth radius, used for great-circle distance.</summary>
    public const double MeanRadius = 6371008.8;
    private const double D2R = Math.PI / 180;
    private const double MaxLat = 85.05112878;

    /// <summary>Longitude and latitude to Web Mercator metres (EPSG:3857).</summary>
    public static (double X, double Y) Mercator(LonLat p)
    {
        var lat = Math.Clamp(p.Lat, -MaxLat, MaxLat);
        return (EquatorRadius * p.Lon * D2R, EquatorRadius * Math.Log(Math.Tan(Math.PI / 4 + lat * D2R / 2)));
    }

    public static LonLat FromMercator(double x, double y) =>
        new(x / EquatorRadius / D2R, (2 * Math.Atan(Math.Exp(y / EquatorRadius)) - Math.PI / 2) / D2R);

    /// <summary>Great-circle distance in metres, by the Haversine formula.</summary>
    public static double Distance(LonLat a, LonLat b)
    {
        var dLat = (b.Lat - a.Lat) * D2R;
        var dLon = (b.Lon - a.Lon) * D2R;
        var s = Math.Sin(dLat / 2);
        var t = Math.Sin(dLon / 2);
        var h = s * s + Math.Cos(a.Lat * D2R) * Math.Cos(b.Lat * D2R) * t * t;
        return 2 * MeanRadius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>The length of a line in metres, segment by segment along great circles.</summary>
    public static double Length(IReadOnlyList<LonLat> line)
    {
        var total = 0.0;
        for (var i = 1; i < line.Count; i++) total += Distance(line[i - 1], line[i]);
        return total;
    }

    /// <summary>
    /// The area of a ring on the sphere, in square metres: the sum over edges of
    /// Δλ·(2 + sin φ₁ + sin φ₂), times R²/2. Exact for edges along meridians and
    /// parallels, and the same method GIS tools use for geodesic area.
    /// </summary>
    public static double Area(IReadOnlyList<LonLat> ring)
    {
        var n = ring.Count;
        if (n < 3) return 0;
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            sum += (b.Lon - a.Lon) * D2R * (2 + Math.Sin(a.Lat * D2R) + Math.Sin(b.Lat * D2R));
        }
        return Math.Abs(sum * EquatorRadius * EquatorRadius / 2);
    }

    /// <summary>The perimeter of a ring, closing it.</summary>
    public static double Perimeter(IReadOnlyList<LonLat> ring) =>
        ring.Count < 2 ? 0 : Length(ring) + Distance(ring[^1], ring[0]);

    /// <summary>Whether a point is inside a ring (even-odd rule).</summary>
    public static bool Contains(IReadOnlyList<LonLat> ring, LonLat p)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Lat > p.Lat) != (b.Lat > p.Lat) &&
                p.Lon < (b.Lon - a.Lon) * (p.Lat - a.Lat) / (b.Lat - a.Lat) + a.Lon)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Whether a point is inside a polygon with holes: the first ring is the outline.</summary>
    public static bool Contains(List<List<LonLat>> rings, LonLat p)
    {
        var inside = false;
        foreach (var r in rings) if (Contains(r, p)) inside = !inside;
        return inside;
    }

    /// <summary>
    /// Metres from a point to a line: each segment in a flat projection centred
    /// on the point (good to well under 0.1% within tens of kilometres).
    /// </summary>
    public static double DistanceToLine(LonLat p, IReadOnlyList<LonLat> line)
    {
        if (line.Count == 0) return double.PositiveInfinity;
        if (line.Count == 1) return Distance(p, line[0]);
        var kx = Math.Cos(p.Lat * D2R) * MeanRadius * D2R;
        var ky = MeanRadius * D2R;
        var best = double.PositiveInfinity;
        for (var i = 1; i < line.Count; i++)
        {
            var ax = (line[i - 1].Lon - p.Lon) * kx;
            var ay = (line[i - 1].Lat - p.Lat) * ky;
            var bx = (line[i].Lon - p.Lon) * kx;
            var by = (line[i].Lat - p.Lat) * ky;
            var dx = bx - ax;
            var dy = by - ay;
            var l2 = dx * dx + dy * dy;
            var t = l2 == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / l2, 0, 1);
            var x = ax + t * dx;
            var y = ay + t * dy;
            var d = Math.Sqrt(x * x + y * y);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>Metres from a point to a shape: zero inside an area.</summary>
    public static double DistanceTo(LonLat p, Shape shape)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Point:
                return Distance(p, shape.Parts[0][0]);
            case ShapeKind.Line:
                return shape.Parts.Min(part => DistanceToLine(p, part));
            default:
                if (Contains(shape.Parts, p)) return 0;
                return shape.Parts.Min(ring => DistanceToLine(p, Closed(ring)));
        }
    }

    private static IReadOnlyList<LonLat> Closed(IReadOnlyList<LonLat> ring) =>
        ring.Count > 0 && ring[0] != ring[^1] ? ring.Append(ring[0]).ToList() : ring;

    /// <summary>The centre of a ring's area (planar, which is fine at this size).</summary>
    public static LonLat Centroid(IReadOnlyList<LonLat> ring)
    {
        double a = 0, x = 0, y = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var f = ring[j].Lon * ring[i].Lat - ring[i].Lon * ring[j].Lat;
            a += f;
            x += (ring[j].Lon + ring[i].Lon) * f;
            y += (ring[j].Lat + ring[i].Lat) * f;
        }
        if (Math.Abs(a) < 1e-15) return ring[0];
        return new LonLat(x / (3 * a), y / (3 * a));
    }

    /// <summary>"6°59′24.0″ S, 107°37′12.0″ E".</summary>
    public static string Dms(LonLat p) => Dms(p.Lat, "N", "S") + ", " + Dms(p.Lon, "E", "W");

    private static string Dms(double v, string pos, string neg)
    {
        var a = Math.Abs(v);
        var d = (int)Math.Floor(a);
        var mf = (a - d) * 60;
        var m = (int)Math.Floor(mf);
        var s = (mf - m) * 60;
        return d + "°" + m.ToString("00") + "′" + s.ToString("00.0") + "″ " + (v < 0 ? neg : pos);
    }

    public static string FormatLength(double m) =>
        m >= 1000 ? Fmt.Num(m / 1000, 2) + " km" : Fmt.N(m) + " m";

    public static string FormatArea(double m2) =>
        m2 >= 1_000_000 ? Fmt.Num(m2 / 1_000_000, 3) + " km²" :
        m2 >= 10_000 ? Fmt.Num(m2 / 10_000, 2) + " ha" :
        Fmt.N(m2) + " m²";
}

public enum ShapeKind { Point, Line, Polygon }

/// <summary>A geometry in longitude and latitude: one point, one or more lines, or one polygon's rings.</summary>
public sealed class Shape
{
    public ShapeKind Kind { get; init; }
    public List<List<LonLat>> Parts { get; init; } = new();

    public static Shape Point(LonLat p) => new() { Kind = ShapeKind.Point, Parts = new() { new() { p } } };
    public static Shape Line(List<LonLat> pts) => new() { Kind = ShapeKind.Line, Parts = new() { pts } };
    public static Shape Polygon(List<LonLat> ring) => new() { Kind = ShapeKind.Polygon, Parts = new() { ring } };

    /// <summary>The vertices a person edits: the point, the line, or the outer ring without its closing repeat.</summary>
    public List<LonLat> Outline => Parts.Count == 0 ? new() : Parts[0];

    public string KindName => Kind switch { ShapeKind.Point => "point", ShapeKind.Line => "line", _ => "polygon" };

    public double Length => Kind switch
    {
        ShapeKind.Line => Parts.Sum(Geo.Length),
        ShapeKind.Polygon => Geo.Perimeter(Parts[0]),
        _ => 0,
    };

    public double Area => Kind == ShapeKind.Polygon ? Math.Max(0, Geo.Area(Parts[0]) - Parts.Skip(1).Sum(Geo.Area)) : 0;

    public LonLat Center => Kind switch
    {
        ShapeKind.Point => Parts[0][0],
        ShapeKind.Polygon => Geo.Centroid(Parts[0]),
        _ => Parts[0][Parts[0].Count / 2],
    };

    public (double W, double S, double E, double N) Bounds
    {
        get
        {
            double w = 180, s = 90, e = -180, n = -90;
            foreach (var part in Parts)
                foreach (var p in part)
                {
                    w = Math.Min(w, p.Lon);
                    e = Math.Max(e, p.Lon);
                    s = Math.Min(s, p.Lat);
                    n = Math.Max(n, p.Lat);
                }
            return (w, s, e, n);
        }
    }
}
