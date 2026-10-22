using Rustzor.Interop;
using Rustzor.UI;

namespace KamarGelap.Model;

public enum Tool { Move, Marquee, Ellipse, Lasso, Crop, Brush, Eraser, Clone, Gradient, Bucket, Eyedropper, Text, Hand }

/// <summary>One step in the history: what it was called and the document as it was after it.</summary>
public sealed record Step(string Label, string Json);

/// <summary>
/// The editor: the document, the tools and their settings, the history. Pixels are
/// the browser module's business; this class tells it what to composite and when a
/// step ends, and handles the tools whose work is geometry (selections, crop, move,
/// text placement).
/// </summary>
public sealed partial class Editor
{
    public const int MaxSteps = 40;

    public Doc Doc { get; private set; } = new();
    public List<Step> Steps { get; } = new();
    public int Index { get; private set; } = -1;
    /// <summary>Goes up with every step, jump and late redraw, so panels know when the pixels changed.</summary>
    public int Version { get; private set; }

    public Tool Tool { get; private set; } = Tool.Brush;
    public double Size { get; set; } = 60;
    public double Hardness { get; set; } = 55;
    public double Opacity { get; set; } = 100;
    public double Flow { get; set; } = 100;
    public string Fg { get; private set; } = "#f3e2c7";
    public string Bg { get; private set; } = "#1a1412";
    public int Tolerance { get; set; } = 32;
    public string GradientMode { get; set; } = "linear";
    /// <summary>Whether painting goes to the active layer's mask rather than its pixels.</summary>
    public bool OnMask { get; private set; }

    public double Zoom { get; set; } = 1;
    public string? CloneSource { get; private set; }
    public int[]? HistR, HistG, HistB, HistL;

    /// <summary>A crop being drawn, not yet applied.</summary>
    public (double X, double Y, double W, double H)? Crop { get; private set; }
    /// <summary>A selection or lasso being dragged, drawn before it is committed.</summary>
    public Sel? Drafting { get; private set; }

    public event Action? Changed;
    public event Action? Edited;

    public bool CanUndo => Index > 0;
    public bool CanRedo => Index < Steps.Count - 1;
    public bool Ready { get; private set; }

    public void Notify() => Changed?.Invoke();

    // ── the bridge ────────────────────────────────────────────────────────

    public void SyncMeta() => Js.Send("kg.meta", w => { w.BeginObject().Name("doc"); Doc.Write(w); w.EndObject(); });

    public void SyncTool()
    {
        var target = OnMask && Doc.ActiveLayer is { HasMask: true } ? "mask" : "pixels";
        Js.Send("kg.tool", w => w.BeginObject()
            .Prop("tool", ToolSlug(Tool)).Prop("size", Size).Prop("hardness", Hardness / 100).Prop("opacity", Opacity / 100)
            .Prop("flow", Flow / 100).Prop("color", Fg).Prop("color2", Bg).Prop("tolerance", Tolerance).Prop("target", target)
            .Prop("mode", GradientMode).EndObject());
    }

    public static string ToolSlug(Tool t) => t switch
    {
        Tool.Move => "move",
        Tool.Marquee => "marquee",
        Tool.Ellipse => "ellipse",
        Tool.Lasso => "lasso",
        Tool.Crop => "crop",
        Tool.Brush => "brush",
        Tool.Eraser => "eraser",
        Tool.Clone => "clone",
        Tool.Gradient => "gradient",
        Tool.Bucket => "bucket",
        Tool.Eyedropper => "eyedropper",
        Tool.Text => "text",
        _ => "hand",
    };

    // ── history ───────────────────────────────────────────────────────────

    /// <summary>Ends a step: the document as it is now becomes the newest history entry.</summary>
    public void Commit(string label)
    {
        if (Index < Steps.Count - 1) Steps.RemoveRange(Index + 1, Steps.Count - Index - 1);
        Steps.Add(new Step(label, Doc.ToJson()));
        Index = Steps.Count - 1;
        Version++;
        Js.Send("kg.commit", Js.Args("i", Index));
        if (Steps.Count > MaxSteps)
        {
            Steps.RemoveAt(0);
            Index--;
            Js.Send("kg.trim", Js.Args("keep", Steps.Count));
        }
        SyncMeta();
        Changed?.Invoke();
        Edited?.Invoke();
    }

    /// <summary>Starts the history over, with the current document as its first step.</summary>
    public void Reset(string label)
    {
        Steps.Clear();
        Index = -1;
        Crop = null;
        Drafting = null;
        CloneSource = null;
        OnMask = false;
        Commit(label);
    }

    public void JumpTo(int i)
    {
        if (i < 0 || i >= Steps.Count || i == Index) return;
        Js.Send("kg.restore", Js.Args("i", i));
        Index = i;
        Version++;
        Doc = Doc.FromJson(Steps[i].Json);
        Crop = null;
        Drafting = null;
        if (OnMask && Doc.ActiveLayer is not { HasMask: true }) OnMask = false;
        SyncMeta();
        SyncTool();
        Changed?.Invoke();
        Edited?.Invoke();
    }

    /// <summary>Folds work that finished after a document opened (a caption waiting on its font) into the first step.</summary>
    public void RefreshFirstStep()
    {
        if (Index != 0 || Steps.Count != 1) return;
        Steps[0] = new Step(Steps[0].Label, Doc.ToJson());
        Version++;
        Js.Send("kg.commit", Js.Args("i", 0));
        Edited?.Invoke();
    }

    public void Undo() => JumpTo(Index - 1);
    public void Redo() => JumpTo(Index + 1);

    // ── documents ─────────────────────────────────────────────────────────

    public void Load(Doc doc, string label)
    {
        Doc = doc;
        Ready = true;
        SyncMeta();
        SyncTool();
        Reset(label);
    }

    public void NewDocument(int w, int h, string background, string name)
    {
        Js.Send("kg.newDoc", Js.Args("w", w, "h", h));
        var doc = new Doc { Name = name, W = w, H = h };
        var bg = new Layer { Id = doc.NewId(), Name = "Background", Kind = LayerKind.Pixel };
        doc.Layers.Add(bg);
        doc.Active = bg.Id;
        Js.Send("kg.layer", Js.Args("id", bg.Id, "fill", background == "transparent" ? null : background));
        Load(doc, "New " + w + " × " + h + " canvas");
    }

    /// <summary>A new document from an opened picture, at the picture's size.</summary>
    public void FromImage(int w, int h, long bitmap, string name)
    {
        Js.Send("kg.newDoc", Js.Args("w", w, "h", h));
        var doc = new Doc { Name = StripExt(name), W = w, H = h };
        var bg = new Layer { Id = doc.NewId(), Name = StripExt(name), Kind = LayerKind.Pixel };
        doc.Layers.Add(bg);
        doc.Active = bg.Id;
        Js.Send("kg.layer", Js.Args("id", bg.Id));
        Js.Send("kg.placeImage", Js.Args("id", bg.Id, "bitmap", bitmap, "center", false));
        Load(doc, "Opened " + name);
    }

    public void PlaceImage(long bitmap, string name)
    {
        var l = new Layer { Id = Doc.NewId(), Name = StripExt(name), Kind = LayerKind.Pixel };
        Insert(l);
        Js.Send("kg.layer", Js.Args("id", l.Id));
        Js.Send("kg.placeImage", Js.Args("id", l.Id, "bitmap", bitmap, "center", true));
        Commit("Place " + name);
    }

    public static string StripExt(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : name;
    }

    // ── tools and colours ─────────────────────────────────────────────────

    public void SetTool(Tool t)
    {
        if (Crop is not null && t != Tool.Crop) Crop = null;
        Tool = t;
        SyncTool();
        Changed?.Invoke();
    }

    public void SetFg(string hex)
    {
        Fg = hex;
        SyncTool();
        Changed?.Invoke();
    }

    public void SetBg(string hex)
    {
        Bg = hex;
        SyncTool();
        Changed?.Invoke();
    }

    public void SwapColors()
    {
        (Fg, Bg) = (Bg, Fg);
        SyncTool();
        Changed?.Invoke();
    }

    public void DefaultColors()
    {
        Fg = "#000000";
        Bg = "#ffffff";
        SyncTool();
        Changed?.Invoke();
    }

    public void SetPaintTarget(bool mask)
    {
        OnMask = mask && Doc.ActiveLayer is { HasMask: true };
        SyncTool();
        Changed?.Invoke();
    }

    public void ToolSettingsChanged()
    {
        SyncTool();
        Changed?.Invoke();
    }

    // ── messages from the browser module ─────────────────────────────────

    public void OnMessage(JsonValue m)
    {
        switch (m["t"].AsString())
        {
            case "stroke":
                Commit(m["label"].AsString());
                break;
            case "picked":
                if (m["alt"].AsBool()) SetBg(m["color"].AsString());
                else SetFg(m["color"].AsString());
                break;
            case "cloneSrc":
                CloneSource = m["x"].AsInt() + ", " + m["y"].AsInt();
                Toasts.Info("Clone source set at " + CloneSource + ". Paint to copy from there.");
                Changed?.Invoke();
                break;
            case "view":
                Zoom = m["zoom"].AsDouble();
                Changed?.Invoke();
                break;
            case "hist":
                HistR = Ints(m["r"]);
                HistG = Ints(m["g"]);
                HistB = Ints(m["b"]);
                HistL = Ints(m["l"]);
                HistogramChanged?.Invoke();
                break;
            case "warn":
                Toasts.Warning(m["msg"].AsString());
                break;
            case "p":
                Pointer(m["ph"].AsString(), m["x"].AsDouble(), m["y"].AsDouble(), m["s"].AsBool(), m["a"].AsBool());
                break;
            case "key":
                KeyDown(m["k"].AsString(), m["c"].AsString(), m["ctrl"].AsBool(), m["s"].AsBool(), m["a"].AsBool());
                break;
        }
    }

    /// <summary>The histogram redraws on its own, without the rest of the editor.</summary>
    public event Action? HistogramChanged;

    private static int[] Ints(JsonValue v)
    {
        var a = new int[v.Count];
        for (var i = 0; i < a.Length; i++) a[i] = v[i].AsInt();
        return a;
    }
}
