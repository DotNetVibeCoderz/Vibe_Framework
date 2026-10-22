using Rustzor.Interop;

namespace KamarGelap.Model;

public enum LayerKind { Pixel, Text, Adjust }

public enum AdjustKind { BrightnessContrast, Levels, HueSaturation, BlackWhite }

/// <summary>One layer's description. Its pixels live in the browser module, keyed by <see cref="Id"/>.</summary>
public sealed class Layer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public LayerKind Kind { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public double Opacity { get; set; } = 100;
    public string Blend { get; set; } = "normal";
    public int X { get; set; }
    public int Y { get; set; }
    public bool HasMask { get; set; }
    public bool MaskOn { get; set; } = true;

    public AdjustKind Adjust { get; set; }
    public double[] P { get; set; } = new double[6];

    public string Text { get; set; } = "";
    public string Font { get; set; } = "DM Serif Display";
    public double Size { get; set; } = 72;
    public int Weight { get; set; } = 400;
    public bool Italic { get; set; }
    public string Color { get; set; } = "#ffffff";
    public double TX { get; set; }
    public double TY { get; set; }
    public string Align { get; set; } = "left";
    /// <summary>The text's measured box, for the move tool and the outline.</summary>
    public double BW { get; set; }
    public double BH { get; set; }

    public Layer Copy() => new()
    {
        Id = Id, Name = Name, Kind = Kind, Visible = Visible, Locked = Locked, Opacity = Opacity, Blend = Blend, X = X, Y = Y,
        HasMask = HasMask, MaskOn = MaskOn, Adjust = Adjust, P = (double[])P.Clone(), Text = Text, Font = Font, Size = Size, Weight = Weight,
        Italic = Italic, Color = Color, TX = TX, TY = TY, Align = Align, BW = BW, BH = BH,
    };

    public static readonly string[] Blends = { "normal", "multiply", "screen", "overlay", "darken", "lighten", "soft-light", "difference" };

    public static string BlendLabel(string b) => b switch
    {
        "soft-light" => "Soft light",
        _ => char.ToUpperInvariant(b[0]) + b.Substring(1),
    };

    public static string AdjustSlug(AdjustKind k) => k switch
    {
        AdjustKind.BrightnessContrast => "brightness-contrast",
        AdjustKind.Levels => "levels",
        AdjustKind.HueSaturation => "hue-saturation",
        _ => "black-white",
    };

    public static string AdjustLabel(AdjustKind k) => k switch
    {
        AdjustKind.BrightnessContrast => "Brightness / Contrast",
        AdjustKind.Levels => "Levels",
        AdjustKind.HueSaturation => "Hue / Saturation",
        _ => "Black & White",
    };

    public static double[] AdjustDefaults(AdjustKind k) => k switch
    {
        AdjustKind.BrightnessContrast => new double[] { 0, 0, 0, 0, 0, 0 },
        AdjustKind.Levels => new double[] { 0, 255, 1, 0, 255, 0 },
        AdjustKind.HueSaturation => new double[] { 0, 0, 0, 0, 0, 0 },
        _ => new double[] { 30, 59, 11, 30, 0, 0 },
    };
}

/// <summary>A selection in document pixels: a rectangle, an ellipse or a lasso polygon.</summary>
public sealed class Sel
{
    public string Kind { get; set; } = "rect";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public List<double> Pts { get; set; } = new();
    public bool Inverted { get; set; }

    public Sel Copy() => new() { Kind = Kind, X = X, Y = Y, W = W, H = H, Pts = new List<double>(Pts), Inverted = Inverted };

    /// <summary>The selection's bounds (the lasso's points included).</summary>
    public (double X, double Y, double W, double H) Bounds()
    {
        if (Kind != "lasso") return (X, Y, W, H);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        for (var i = 0; i + 1 < Pts.Count; i += 2)
        {
            x0 = Math.Min(x0, Pts[i]); x1 = Math.Max(x1, Pts[i]);
            y0 = Math.Min(y0, Pts[i + 1]); y1 = Math.Max(y1, Pts[i + 1]);
        }
        return x0 > x1 ? (0, 0, 0, 0) : (x0, y0, x1 - x0, y1 - y0);
    }
}

/// <summary>The document: its size and its layer stack, bottom first.</summary>
public sealed class Doc
{
    public string Name { get; set; } = "Untitled";
    public int W { get; set; }
    public int H { get; set; }
    public List<Layer> Layers { get; set; } = new();
    public string? Active { get; set; }
    public Sel? Sel { get; set; }
    public int Next { get; set; } = 1;

    public Layer? ActiveLayer => Layers.Find(l => l.Id == Active);

    public string NewId() => "L" + Next++;

    // ── JSON: what the browser module composites from, and what history and files keep ──

    public string ToJson()
    {
        var w = new JsonWriter();
        Write(w);
        return w.ToString();
    }

    public void Write(JsonWriter w)
    {
        w.BeginObject().Prop("name", Name).Prop("w", W).Prop("h", H).Prop("next", Next);
        w.Name("active"); if (Active is null) w.Null(); else w.String(Active);
        w.Name("sel");
        if (Sel is null) w.Null();
        else
        {
            w.BeginObject().Prop("kind", Sel.Kind).Prop("x", Sel.X).Prop("y", Sel.Y).Prop("w", Sel.W).Prop("h", Sel.H).Prop("inv", Sel.Inverted);
            w.Name("pts").BeginArray();
            foreach (var p in Sel.Pts) w.Number(Math.Round(p, 2));
            w.EndArray().EndObject();
        }
        w.Name("layers").BeginArray();
        foreach (var l in Layers)
        {
            w.BeginObject().Prop("id", l.Id).Prop("name", l.Name).Prop("kind", l.Kind == LayerKind.Pixel ? "pixel" : l.Kind == LayerKind.Text ? "text" : "adjust")
                .Prop("visible", l.Visible).Prop("locked", l.Locked).Prop("opacity", l.Opacity).Prop("blend", l.Blend)
                .Prop("x", l.X).Prop("y", l.Y).Prop("mask", l.HasMask).Prop("maskOn", l.MaskOn);
            if (l.Kind == LayerKind.Adjust)
            {
                w.Prop("adjust", Layer.AdjustSlug(l.Adjust));
                w.Name("p").BeginArray();
                foreach (var v in l.P) w.Number(v);
                w.EndArray();
            }
            if (l.Kind == LayerKind.Text)
                w.Prop("text", l.Text).Prop("font", l.Font).Prop("size", l.Size).Prop("weight", l.Weight).Prop("italic", l.Italic)
                    .Prop("color", l.Color).Prop("tx", l.TX).Prop("ty", l.TY).Prop("align", l.Align).Prop("bw", l.BW).Prop("bh", l.BH);
            w.EndObject();
        }
        w.EndArray().EndObject();
    }

    public static Doc FromJson(string json) => From(JsonValue.Parse(json));

    public static Doc From(JsonValue v)
    {
        var d = new Doc
        {
            Name = v["name"].AsStringOrNull() ?? "Untitled",
            W = v["w"].AsInt(),
            H = v["h"].AsInt(),
            Next = Math.Max(1, v["next"].AsInt()),
            Active = v["active"].AsStringOrNull(),
        };
        var s = v["sel"];
        if (s.IsObject)
        {
            d.Sel = new Sel { Kind = s["kind"].AsString(), X = s["x"].AsDouble(), Y = s["y"].AsDouble(), W = s["w"].AsDouble(), H = s["h"].AsDouble(), Inverted = s["inv"].AsBool() };
            foreach (var p in s["pts"].Items) d.Sel.Pts.Add(p.AsDouble());
        }
        foreach (var j in v["layers"].Items)
        {
            var kind = j["kind"].AsString();
            var l = new Layer
            {
                Id = j["id"].AsString(),
                Name = j["name"].AsString(),
                Kind = kind == "text" ? LayerKind.Text : kind == "adjust" ? LayerKind.Adjust : LayerKind.Pixel,
                Visible = j["visible"].AsBool(),
                Locked = j["locked"].AsBool(),
                Opacity = j["opacity"].AsDouble(),
                Blend = j["blend"].AsStringOrNull() ?? "normal",
                X = j["x"].AsInt(),
                Y = j["y"].AsInt(),
                HasMask = j["mask"].AsBool(),
                MaskOn = !j.Has("maskOn") || j["maskOn"].AsBool(),
            };
            if (l.Kind == LayerKind.Adjust)
            {
                l.Adjust = j["adjust"].AsString() switch
                {
                    "brightness-contrast" => AdjustKind.BrightnessContrast,
                    "levels" => AdjustKind.Levels,
                    "hue-saturation" => AdjustKind.HueSaturation,
                    _ => AdjustKind.BlackWhite,
                };
                var i = 0;
                foreach (var p in j["p"].Items)
                    if (i < 6) l.P[i++] = p.AsDouble();
            }
            if (l.Kind == LayerKind.Text)
            {
                l.Text = j["text"].AsString();
                l.Font = j["font"].AsString();
                l.Size = j["size"].AsDouble();
                l.Weight = j["weight"].AsInt();
                l.Italic = j["italic"].AsBool();
                l.Color = j["color"].AsString();
                l.TX = j["tx"].AsDouble();
                l.TY = j["ty"].AsDouble();
                l.Align = j["align"].AsStringOrNull() ?? "left";
                l.BW = j["bw"].AsDouble();
                l.BH = j["bh"].AsDouble();
            }
            d.Layers.Add(l);
        }
        if (d.Active is not null && d.ActiveLayer is null) d.Active = d.Layers.Count > 0 ? d.Layers[^1].Id : null;
        return d;
    }
}
