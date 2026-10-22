using Rustzor.Interop;

namespace Rupa.Model;

/// <summary>A design file: one page of layers, the colour styles, and an index by id.</summary>
public sealed class Doc
{
    public string Id = "";
    public string Name = "Untitled";
    public string PageColor = "#e7ebe8";
    public Node Root = new() { Id = "root", Kind = NodeKind.Group, Name = "Page" };
    public List<ColorStyle> Styles = new();

    private readonly Dictionary<string, Node> _index = new();
    // One clock for every document, so a view never mistakes a layer of a newly opened file for one it already drew.
    private static int _clock;
    private int _ids;

    /// <summary>Layers changed since the canvas last drew; their views render again on their own.</summary>
    public readonly HashSet<string> Dirty = new();

    public Node? Find(string? id) => id is not null && _index.TryGetValue(id, out var n) ? n : null;

    public bool IsTop(Node n) => n.Parent == Root;

    public string NewId() => "n" + (++_ids).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Rebuilds parent links and the index, and marks every layer changed.</summary>
    public void Reindex()
    {
        _index.Clear();
        Root.Parent = null;
        Link(Root);
    }

    private void Link(Node n)
    {
        _index[n.Id] = n;
        n.Rev = ++_clock;
        n.Kids = ++_clock;
        if (n.Id.Length > 1 && n.Id[0] == 'n' && int.TryParse(n.Id.Substring(1), out var k) && k > _ids) _ids = k;
        foreach (var c in n.Children)
        {
            c.Parent = n;
            Link(c);
        }
    }

    public void Add(Node parent, Node child, int index = -1)
    {
        child.Parent = parent;
        if (index < 0 || index > parent.Children.Count) parent.Children.Add(child);
        else parent.Children.Insert(index, child);
        foreach (var d in child.SelfAndDescendants())
        {
            _index[d.Id] = d;
            d.Rev = ++_clock;
            d.Kids = ++_clock;
            foreach (var c in d.Children) c.Parent = d;
        }
        TouchChildren(parent);
    }

    public void Remove(Node n)
    {
        var parent = n.Parent;
        if (parent is null) return;
        parent.Children.Remove(n);
        foreach (var d in n.SelfAndDescendants()) _index.Remove(d.Id);
        n.Parent = null;
        TouchChildren(parent);
    }

    /// <summary>
    /// Marks a layer changed, and every instance of a component it belongs to,
    /// so exactly those views render again: moving one shape among three hundred
    /// redraws one shape.
    /// </summary>
    public void Touch(Node n)
    {
        n.Rev = ++_clock;
        Dirty.Add(n.Id);
        Node? main = null;
        for (var p = n; p is not null; p = p.Parent)
            if (p.IsComponent) main = p;
        if (main is null) return;
        foreach (var inst in InstancesOf(main.Id))
        {
            inst.Rev = ++_clock;
            Dirty.Add(inst.Id);
        }
    }

    /// <summary>Marks a container whose list of children changed.</summary>
    public void TouchChildren(Node n)
    {
        n.Kids = ++_clock;
        Touch(n);
    }

    public void TouchAll()
    {
        foreach (var d in Root.SelfAndDescendants())
        {
            d.Rev = ++_clock;
            d.Kids = ++_clock;
        }
    }

    public List<Node> InstancesOf(string mainId)
    {
        var list = new List<Node>();
        foreach (var d in _index.Values)
            if (d.Kind == NodeKind.Instance && d.MainId == mainId) list.Add(d);
        return list;
    }

    public List<Node> Components()
    {
        var list = new List<Node>();
        foreach (var d in Root.SelfAndDescendants())
            if (d.IsComponent) list.Add(d);
        return list;
    }

    public int Count => _index.Count - 1;

    public ColorStyle? Style(string id)
    {
        foreach (var s in Styles)
            if (s.Id == id) return s;
        return null;
    }

    /// <summary>A paint as a CSS colour, following colour styles.</summary>
    public string Css(string paint)
    {
        if (paint.StartsWith("s:")) return Style(paint.Substring(2))?.Hex ?? "#ff00ff";
        return paint;
    }

    public void TouchStyleUsers(string styleId)
    {
        var key = "s:" + styleId;
        foreach (var d in Root.SelfAndDescendants())
            if (d.Fill == key || d.Stroke == key) Touch(d);
    }

    // ── files ─────────────────────────────────────────────────────────────

    public string ToJson()
    {
        var w = new JsonWriter();
        w.BeginObject();
        w.Prop("format", "rupa/1");
        w.Prop("id", Id);
        w.Prop("name", Name);
        w.Prop("page", PageColor);
        w.Name("styles").BeginArray();
        foreach (var s in Styles) w.BeginObject().Prop("id", s.Id).Prop("name", s.Name).Prop("hex", s.Hex).EndObject();
        w.EndArray();
        w.Name("layers").BeginArray();
        foreach (var c in Root.Children) WriteNode(w, c);
        w.EndArray();
        w.EndObject();
        return w.ToString();
    }

    private static void WriteNode(JsonWriter w, Node n)
    {
        w.BeginObject();
        w.Prop("id", n.Id);
        w.Prop("kind", n.Kind.ToString());
        w.Prop("name", n.Name);
        w.Prop("x", R(n.X)).Prop("y", R(n.Y)).Prop("w", R(n.W)).Prop("h", R(n.H));
        if (n.Rotation != 0) w.Prop("rot", R(n.Rotation));
        if (n.Hidden) w.Prop("hidden", true);
        if (n.Locked) w.Prop("locked", true);
        if (n.Opacity != 1) w.Prop("opacity", R(n.Opacity));
        if (n.Fill != "none") w.Prop("fill", n.Fill);
        if (n.Stroke != "none") w.Prop("stroke", n.Stroke).Prop("strokeWidth", R(n.StrokeWidth));
        if (n.Radius != 0) w.Prop("radius", R(n.Radius));
        switch (n.Kind)
        {
            case NodeKind.Text:
                w.Prop("text", n.Text).Prop("font", n.Font).Prop("size", R(n.FontSize)).Prop("weight", n.Weight)
                    .Prop("lineHeight", R(n.LineHeight)).Prop("tracking", R(n.Tracking)).Prop("align", n.TextAlign.ToString());
                break;
            case NodeKind.Path:
                w.Name("points").BeginArray();
                foreach (var p in n.Points) w.Number(R(p.X)).Number(R(p.Y));
                w.EndArray();
                if (n.Closed) w.Prop("closed", true);
                break;
            case NodeKind.Image:
                w.Prop("href", n.Href);
                break;
            case NodeKind.Frame:
                w.Prop("clip", n.Clip);
                if (n.IsComponent) w.Prop("component", true);
                if (n.Layout != LayoutMode.None)
                    w.Prop("layout", n.Layout.ToString()).Prop("gap", R(n.Gap)).Prop("padding", R(n.Padding))
                        .Prop("crossAlign", n.CrossAlign.ToString()).Prop("mainAlign", n.MainAlign.ToString()).Prop("hug", n.Hug);
                break;
            case NodeKind.Instance:
                w.Prop("main", n.MainId);
                if (n.Overrides.Count > 0)
                {
                    w.Name("overrides").BeginObject();
                    foreach (var kv in n.Overrides) w.Prop(kv.Key, kv.Value);
                    w.EndObject();
                }
                break;
        }
        if (n.Children.Count > 0)
        {
            w.Name("children").BeginArray();
            foreach (var c in n.Children) WriteNode(w, c);
            w.EndArray();
        }
        w.EndObject();
    }

    private static double R(double v) => Math.Round(v * 100) / 100;

    public static Doc FromJson(string json)
    {
        var j = JsonValue.Parse(json);
        if (j["format"].AsStringOrNull() != "rupa/1") throw new FormatException("This is not a Rupa design file.");
        var doc = new Doc
        {
            Id = j["id"].AsStringOrNull() ?? "",
            Name = j["name"].AsStringOrNull() ?? "Untitled",
            PageColor = j["page"].AsStringOrNull() ?? "#e7ebe8",
        };
        foreach (var s in j["styles"].Items)
            doc.Styles.Add(new ColorStyle { Id = s["id"].AsString(), Name = s["name"].AsString(), Hex = s["hex"].AsString() });
        foreach (var l in j["layers"].Items) doc.Root.Children.Add(ReadNode(l));
        doc.Reindex();
        return doc;
    }

    private static Node ReadNode(JsonValue j)
    {
        var n = new Node
        {
            Id = j["id"].AsString(),
            Kind = Enum.Parse<NodeKind>(j["kind"].AsString()),
            Name = j["name"].AsStringOrNull() ?? "",
            X = j["x"].AsDouble(),
            Y = j["y"].AsDouble(),
            W = j["w"].AsDouble(),
            H = j["h"].AsDouble(),
            Rotation = j["rot"].AsDouble(),
            Hidden = j["hidden"].AsBool(),
            Locked = j["locked"].AsBool(),
            Opacity = j.Has("opacity") ? j["opacity"].AsDouble() : 1,
            Fill = j["fill"].AsStringOrNull() ?? "none",
            Stroke = j["stroke"].AsStringOrNull() ?? "none",
            StrokeWidth = j["strokeWidth"].AsDouble(),
            Radius = j["radius"].AsDouble(),
        };
        switch (n.Kind)
        {
            case NodeKind.Text:
                n.Text = j["text"].AsStringOrNull() ?? "";
                n.Font = j["font"].AsStringOrNull() ?? "Plus Jakarta Sans";
                n.FontSize = j["size"].AsDouble();
                n.Weight = j["weight"].AsInt();
                n.LineHeight = j["lineHeight"].AsDouble();
                n.Tracking = j["tracking"].AsDouble();
                n.TextAlign = Enum.Parse<Align>(j["align"].AsStringOrNull() ?? "Start");
                break;
            case NodeKind.Path:
                var pts = j["points"];
                for (var i = 0; i + 1 < pts.Count; i += 2) n.Points.Add(new Pt(pts[i].AsDouble(), pts[i + 1].AsDouble()));
                n.Closed = j["closed"].AsBool();
                break;
            case NodeKind.Image:
                n.Href = j["href"].AsStringOrNull() ?? "";
                break;
            case NodeKind.Frame:
                n.Clip = !j.Has("clip") || j["clip"].AsBool();
                n.IsComponent = j["component"].AsBool();
                if (j.Has("layout"))
                {
                    n.Layout = Enum.Parse<LayoutMode>(j["layout"].AsString());
                    n.Gap = j["gap"].AsDouble();
                    n.Padding = j["padding"].AsDouble();
                    n.CrossAlign = Enum.Parse<Align>(j["crossAlign"].AsStringOrNull() ?? "Start");
                    n.MainAlign = Enum.Parse<Align>(j["mainAlign"].AsStringOrNull() ?? "Start");
                    n.Hug = j["hug"].AsBool();
                }
                break;
            case NodeKind.Instance:
                n.MainId = j["main"].AsString();
                foreach (var k in j["overrides"].Keys) n.Overrides[k] = j["overrides"][k].AsString();
                break;
        }
        foreach (var c in j["children"].Items) n.Children.Add(ReadNode(c));
        return n;
    }

    /// <summary>A deep copy for the undo history.</summary>
    public (Node Root, List<ColorStyle> Styles, string Page) Snapshot()
    {
        var styles = new List<ColorStyle>();
        foreach (var s in Styles) styles.Add(new ColorStyle { Id = s.Id, Name = s.Name, Hex = s.Hex });
        return (Root.Clone(), styles, PageColor);
    }

    public void Restore((Node Root, List<ColorStyle> Styles, string Page) s)
    {
        Root = s.Root.Clone();
        Styles = new List<ColorStyle>();
        foreach (var st in s.Styles) Styles.Add(new ColorStyle { Id = st.Id, Name = st.Name, Hex = st.Hex });
        PageColor = s.Page;
        Reindex();
    }
}
