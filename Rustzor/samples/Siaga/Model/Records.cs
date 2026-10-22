using System.Globalization;
using System.Text;
using Rustzor.Interop;

namespace Siaga.Model;

/// <summary>A feature the user drew or imported, as stored in IndexedDB.</summary>
public sealed class UserRecord
{
    public string Id { get; set; } = "";
    public string Layer { get; set; } = "";
    public string Kind { get; set; } = "point";
    /// <summary>Parts split by '|', points by ';', "lon,lat" each.</summary>
    public string Coords { get; set; } = "";
    /// <summary>The properties as a JSON object.</summary>
    public string Props { get; set; } = "{}";
    public long Seq { get; set; }
}

/// <summary>A layer of the user's own features.</summary>
public sealed class LayerRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#e2621b";
    public string Source { get; set; } = "";
    public long Seq { get; set; }
}

/// <summary>A saved setting: the view, and how each built-in layer is shown.</summary>
public sealed class PrefRecord
{
    public string Id { get; set; } = "";
    public string Json { get; set; } = "";
}

/// <summary>Reading and writing GeoJSON and CSV, and the compact coordinate text the store keeps.</summary>
public static class GeoText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Encode(Shape s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Parts.Count; i++)
        {
            if (i > 0) sb.Append('|');
            var part = s.Parts[i];
            for (var j = 0; j < part.Count; j++)
            {
                if (j > 0) sb.Append(';');
                sb.Append(R(part[j].Lon)).Append(',').Append(R(part[j].Lat));
            }
        }
        return sb.ToString();
    }

    public static Shape Decode(string kind, string text)
    {
        var parts = new List<List<LonLat>>();
        foreach (var part in text.Split('|'))
        {
            var pts = new List<LonLat>();
            foreach (var p in part.Split(';'))
            {
                var comma = p.IndexOf(',');
                if (comma < 0) continue;
                pts.Add(new LonLat(double.Parse(p.Substring(0, comma), Inv), double.Parse(p.Substring(comma + 1), Inv)));
            }
            parts.Add(pts);
        }
        return new Shape { Kind = kind == "point" ? ShapeKind.Point : kind == "line" ? ShapeKind.Line : ShapeKind.Polygon, Parts = parts };
    }

    private static string R(double v) => Math.Round(v, 7).ToString("R", Inv);

    /// <summary>A GeoJSON geometry for a shape; polygon rings are closed as the format requires.</summary>
    public static void WriteGeometry(JsonWriter w, Shape s)
    {
        w.BeginObject();
        switch (s.Kind)
        {
            case ShapeKind.Point:
                w.Prop("type", "Point").Name("coordinates");
                Pos(w, s.Parts[0][0]);
                break;
            case ShapeKind.Line:
                if (s.Parts.Count == 1)
                {
                    w.Prop("type", "LineString").Name("coordinates");
                    Line(w, s.Parts[0], false);
                }
                else
                {
                    w.Prop("type", "MultiLineString").Name("coordinates").BeginArray();
                    foreach (var p in s.Parts) Line(w, p, false);
                    w.EndArray();
                }
                break;
            default:
                w.Prop("type", "Polygon").Name("coordinates").BeginArray();
                foreach (var r in s.Parts) Line(w, r, true);
                w.EndArray();
                break;
        }
        w.EndObject();
    }

    private static void Pos(JsonWriter w, LonLat p) => w.BeginArray().Raw(R(p.Lon)).Raw(R(p.Lat)).EndArray();

    private static void Line(JsonWriter w, List<LonLat> pts, bool close)
    {
        w.BeginArray();
        foreach (var p in pts) Pos(w, p);
        if (close && pts.Count > 0 && pts[0] != pts[^1]) Pos(w, pts[0]);
        w.EndArray();
    }

    /// <summary>A FeatureCollection of features with shapes.</summary>
    public static string Collection(IEnumerable<Feat> features)
    {
        var w = new JsonWriter();
        w.BeginObject().Prop("type", "FeatureCollection").Name("features").BeginArray();
        foreach (var f in features)
        {
            if (f.Shape is null) continue;
            w.BeginObject().Prop("type", "Feature").Prop("id", f.Id).Name("properties").Value(f.Props).Name("geometry");
            WriteGeometry(w, f.Shape);
            w.EndObject();
        }
        w.EndArray().EndObject();
        return w.ToString();
    }

    /// <summary>
    /// The features in a GeoJSON text: a FeatureCollection, one Feature or a bare
    /// geometry. Multi-geometries become one feature per part, so each can be
    /// edited on its own.
    /// </summary>
    public static List<(Shape Shape, JsonValue Props)> ParseGeoJson(string text)
    {
        var root = JsonValue.Parse(text);
        var outp = new List<(Shape, JsonValue)>();
        IEnumerable<JsonValue> features = root["type"].AsStringOrNull() switch
        {
            "FeatureCollection" => root["features"].Items,
            "Feature" => new[] { root },
            _ => new[] { Wrap(root) },
        };
        foreach (var f in features)
        {
            var props = f["properties"].IsObject ? f["properties"] : JsonValue.NewObject();
            var g = f["geometry"];
            var c = g["coordinates"];
            switch (g["type"].AsStringOrNull())
            {
                case "Point":
                    outp.Add((Shape.Point(Pt(c)), props));
                    break;
                case "MultiPoint":
                    foreach (var p in c.Items) outp.Add((Shape.Point(Pt(p)), props));
                    break;
                case "LineString":
                    outp.Add((Shape.Line(Pts(c, false)), props));
                    break;
                case "MultiLineString":
                    foreach (var l in c.Items) outp.Add((Shape.Line(Pts(l, false)), props));
                    break;
                case "Polygon":
                    outp.Add((PolygonOf(c), props));
                    break;
                case "MultiPolygon":
                    foreach (var p in c.Items) outp.Add((PolygonOf(p), props));
                    break;
            }
        }
        return outp;
    }

    private static JsonValue Wrap(JsonValue geometry)
    {
        var f = JsonValue.NewObject();
        f["type"] = JsonValue.From("Feature");
        f["geometry"] = geometry;
        return f;
    }

    private static Shape PolygonOf(JsonValue rings) => new()
    {
        Kind = ShapeKind.Polygon,
        Parts = rings.Items.Select(r => Pts(r, true)).ToList(),
    };

    private static LonLat Pt(JsonValue c) => new(c[0].AsDouble(), c[1].AsDouble());

    /// <summary>A line's points; a ring loses its closing repeat (the shape closes itself).</summary>
    private static List<LonLat> Pts(JsonValue c, bool ring)
    {
        var pts = c.Items.Select(Pt).ToList();
        if (ring && pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    /// <summary>Points from a CSV with latitude and longitude columns (comma or semicolon separated).</summary>
    public static List<(Shape Shape, JsonValue Props)> ParseCsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) throw new FormatException("The file has no rows under its header.");
        var sep = lines[0].Count(ch => ch == ';') > lines[0].Count(ch => ch == ',') ? ';' : ',';
        var head = SplitCsv(lines[0], sep).Select(h => h.Trim()).ToList();
        var lat = head.FindIndex(h => h.ToLowerInvariant() is "lat" or "latitude" or "lintang" or "y");
        var lon = head.FindIndex(h => h.ToLowerInvariant() is "lon" or "lng" or "long" or "longitude" or "bujur" or "x");
        if (lat < 0 || lon < 0) throw new FormatException("No latitude and longitude columns: name them lat and lon.");
        var outp = new List<(Shape, JsonValue)>();
        foreach (var line in lines.Skip(1))
        {
            var cells = SplitCsv(line, sep);
            if (cells.Count <= Math.Max(lat, lon)) continue;
            if (!double.TryParse(cells[lat].Trim().Replace(',', '.'), NumberStyles.Float, Inv, out var la) ||
                !double.TryParse(cells[lon].Trim().Replace(',', '.'), NumberStyles.Float, Inv, out var lo)) continue;
            var props = JsonValue.NewObject();
            for (var i = 0; i < head.Count && i < cells.Count; i++)
            {
                if (i == lat || i == lon) continue;
                var v = cells[i].Trim();
                props[head[i]] = double.TryParse(v, NumberStyles.Float, Inv, out var d) ? JsonValue.From(d) : JsonValue.From(v);
            }
            outp.Add((Shape.Point(new LonLat(lo, la)), props));
        }
        return outp;
    }

    private static List<string> SplitCsv(string line, char sep)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == sep) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}
