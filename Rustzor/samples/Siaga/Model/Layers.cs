using System.Globalization;
using Rustzor.Interop;

namespace Siaga.Model;

/// <summary>How a layer is drawn. Colours are CSS (a custom property keeps a built-in layer theme-aware).</summary>
public sealed class LayerStyle
{
    /// <summary>single, categorized, graduated or heat.</summary>
    public string Mode { get; set; } = "single";
    public string? Fill { get; set; }
    public string? Stroke { get; set; }
    public double Width { get; set; } = 1;
    public double Size { get; set; } = 8;
    public string Shape { get; set; } = "circle";
    public string? Field { get; set; }
    public Dictionary<string, string> Cats { get; set; } = new();
    public List<double> Breaks { get; set; } = new();
    public List<string> Colors { get; set; } = new();
    public string Palette { get; set; } = "bahaya";
    public string Classify { get; set; } = "quantile";
    public string? Label { get; set; }
    public double LabelZoom { get; set; }
    public string? LabelFont { get; set; }
    public string? LabelColor { get; set; }
    public int LabelPrio { get; set; } = 1;
    public double[]? Dash { get; set; }
    public string? Casing { get; set; }
    public bool NoHit { get; set; }
    public double MinZoom { get; set; }
    public string? EmphasisField { get; set; }
    public string? EmphasisValue { get; set; }
    public double EmphasisWidth { get; set; }
    public string? EmphasisStroke { get; set; }
    /// <summary>Below this zoom only the emphasised features are drawn (index contours).</summary>
    public double MinorZoom { get; set; }
    public double Radius { get; set; } = 600;
    public double Max { get; set; } = 1;

    public LayerStyle Clone()
    {
        var c = (LayerStyle)MemberwiseCloneShallow();
        c.Cats = new Dictionary<string, string>(Cats);
        c.Breaks = new List<double>(Breaks);
        c.Colors = new List<string>(Colors);
        return c;
    }

    private LayerStyle MemberwiseCloneShallow() => new()
    {
        Mode = Mode, Fill = Fill, Stroke = Stroke, Width = Width, Size = Size, Shape = Shape, Field = Field,
        Palette = Palette, Classify = Classify, Label = Label, LabelZoom = LabelZoom, LabelFont = LabelFont,
        LabelColor = LabelColor, LabelPrio = LabelPrio, Dash = Dash, Casing = Casing, NoHit = NoHit, MinZoom = MinZoom,
        EmphasisField = EmphasisField, EmphasisValue = EmphasisValue, EmphasisWidth = EmphasisWidth, EmphasisStroke = EmphasisStroke, MinorZoom = MinorZoom, Radius = Radius, Max = Max,
    };

    public void Write(JsonWriter w)
    {
        w.BeginObject();
        w.Prop("mode", Mode);
        if (Fill is not null) w.Prop("fill", Fill);
        if (Stroke is not null) w.Prop("stroke", Stroke);
        w.Prop("width", Width).Prop("size", Size).Prop("shape", Shape);
        if (Field is not null) w.Prop("field", Field);
        if (Mode == "categorized")
        {
            w.Name("cats").BeginObject();
            foreach (var kv in Cats) w.Prop(kv.Key, kv.Value);
            w.EndObject();
        }
        if (Mode == "graduated")
        {
            w.Name("breaks").BeginArray();
            foreach (var b in Breaks) w.Number(b);
            w.EndArray();
            w.Name("colors").BeginArray();
            foreach (var c in Colors) w.String(c);
            w.EndArray();
        }
        if (Label is not null)
        {
            w.Prop("label", Label).Prop("labelZoom", LabelZoom).Prop("labelPrio", LabelPrio);
            if (LabelFont is not null) w.Prop("labelFont", LabelFont);
            if (LabelColor is not null) w.Prop("labelColor", LabelColor);
        }
        if (Dash is not null)
        {
            w.Name("dash").BeginArray();
            foreach (var d in Dash) w.Number(d);
            w.EndArray();
        }
        if (Casing is not null) w.Prop("casing", Casing);
        if (NoHit) w.Prop("noHit", true);
        if (MinZoom > 0) w.Prop("minZoom", MinZoom);
        if (EmphasisField is not null)
            w.Name("emphasis").BeginObject().Prop("field", EmphasisField).Prop("value", EmphasisValue).Prop("width", EmphasisWidth).Prop("stroke", EmphasisStroke).EndObject();
        if (MinorZoom > 0) w.Prop("minorZoom", MinorZoom);
        if (Mode == "heat") w.Prop("radius", Radius).Prop("max", Max);
        w.EndObject();
    }
}

/// <summary>A feature's attributes and where it is; the geometry stays in the browser unless a tool needs it.</summary>
public sealed class Feat
{
    public string Id { get; init; } = "";
    public JsonValue Props { get; set; } = JsonValue.NewObject();
    public LonLat Center { get; set; }
    public (double W, double S, double E, double N) Box { get; set; }
    /// <summary>The geometry, for the user's own features (built-in ones are fetched when a query needs them).</summary>
    public Shape? Shape { get; set; }

    public string Text(string field)
    {
        var v = Props[field];
        if (v.IsNull) return "";
        if (v.IsNumber)
        {
            var d = v.AsDouble();
            return Math.Abs(d - Math.Round(d)) < 1e-9 ? Fmt.N(d) : Fmt.Num(d, 2);
        }
        return v.AsString();
    }

    public double Number(string field)
    {
        var v = Props[field];
        if (v.IsNumber) return v.AsDouble();
        return double.TryParse(v.IsString ? v.AsString() : "", NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }
}

/// <summary>A layer on the map: built in (a GeoJSON file in wwwroot/data) or the user's own.</summary>
public sealed class MapLayer
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    /// <summary>polygon, line, point or mixed.</summary>
    public string Kind { get; set; } = "mixed";
    public string? Url { get; init; }
    public bool Visible { get; set; } = true;
    public double Opacity { get; set; } = 1;
    public LayerStyle Style { get; set; } = new();
    public LayerStyle Default { get; set; } = new();
    public bool User { get; init; }
    public string TitleField { get; set; } = "name";
    public List<string> Columns { get; set; } = new();
    public string Note { get; set; } = "";
    /// <summary>Kept out of the attribute table and identify (contours: thousands of vertices, one value).</summary>
    public bool Background { get; init; }
    public List<Feat> Features { get; set; } = new();
    public int Vertices { get; set; }
    public bool Loaded { get; set; }

    public string Title(Feat f)
    {
        var t = f.Text(TitleField);
        return t.Length > 0 ? t : Name + " " + f.Id;
    }

    /// <summary>The numeric fields, for graduated styles.</summary>
    public List<string> NumericFields() => FieldNames().Where(k => Features.Take(40).Any(f => f.Props[k].IsNumber)).ToList();

    public List<string> FieldNames()
    {
        var names = new List<string>();
        foreach (var f in Features.Take(40))
            foreach (var k in f.Props.Keys)
                if (!names.Contains(k)) names.Add(k);
        return names;
    }
}

/// <summary>Colour ramps and category colours.</summary>
public static class Palettes
{
    public static readonly Dictionary<string, string[]> Ramps = new()
    {
        ["bahaya"] = new[] { "#fff1c1", "#fbcf74", "#f39a49", "#dd5a2b", "#a82a1c" },
        ["air"] = new[] { "#e2f0f7", "#a6d3e8", "#5daed4", "#2a78b5", "#164c82" },
        ["lahan"] = new[] { "#eef3d8", "#c9dd98", "#93c060", "#559a3d", "#2c6a2a" },
        ["ungu"] = new[] { "#f0e8f6", "#d2b9e3", "#ae86cc", "#8152ad", "#552b82" },
    };

    public static readonly string[] Qualitative = { "#2f6fb5", "#e2621b", "#2e8b57", "#9c4dcc", "#c69a0e", "#d1495b", "#1f9e9e", "#7a5c3e", "#5865c9", "#8a9a2b", "#c2185b", "#4b7f8c" };

    /// <summary>The Indonesian flood alert levels, from calm to evacuate.</summary>
    public static readonly Dictionary<string, string> Alert = new()
    {
        ["Normal"] = "var(--lvl-normal)",
        ["Waspada"] = "var(--lvl-waspada)",
        ["Siaga"] = "var(--lvl-siaga)",
        ["Awas"] = "var(--lvl-awas)",
    };
}

public static class Fmt
{
    public static readonly CultureInfo Id = CultureInfo.InvariantCulture;
    public static string N(double v) => ((long)Math.Round(v)).ToString("N0", Id).Replace(",", ".");
    public static string Num(double v, int dp) => v.ToString("N" + dp, Id).Replace(",", "#").Replace(".", ",").Replace("#", ".");
}
