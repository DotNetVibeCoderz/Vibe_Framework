using System.Globalization;
using System.Text;
using Rustzor.Browser.Canvas;
using Rustzor.Interop;

namespace Rustzor.Graphics;

public enum NodeKind { Rect, Ellipse, Line, Path, Text, Image, Group }

/// <summary>
/// One shape in a <see cref="Scene2D"/>. Position and size are in world units;
/// rotation turns the shape about its centre. Line and Path points are
/// relative to (X, Y).
/// </summary>
public sealed class Node
{
    private static int _next;

    public string Id { get; set; } = "n" + Interlocked.Increment(ref _next);
    public NodeKind Kind { get; set; }
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>Radians, about the centre.</summary>
    public double Rotation { get; set; }
    public string? Fill { get; set; } = "#d4d4d8";
    public string? Stroke { get; set; }
    public double StrokeWidth { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public double CornerRadius { get; set; }
    public List<(double X, double Y)> Points { get; set; } = new();
    public bool Closed { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 16;
    public string FontFamily { get; set; } = "system-ui, sans-serif";
    public string FontWeight { get; set; } = "400";
    public string? ImageUrl { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public List<Node> Children { get; } = new();
    /// <summary>Anything the app attaches: a layer name, a database id.</summary>
    public Dictionary<string, string> Data { get; } = new();

    public static Node Rect(double x, double y, double w, double h, string? fill = "#d4d4d8", double radius = 0) =>
        new() { Kind = NodeKind.Rect, X = x, Y = y, Width = w, Height = h, Fill = fill, CornerRadius = radius, Name = "Rectangle" };

    public static Node Ellipse(double x, double y, double w, double h, string? fill = "#d4d4d8") =>
        new() { Kind = NodeKind.Ellipse, X = x, Y = y, Width = w, Height = h, Fill = fill, Name = "Ellipse" };

    public static Node Line(double x1, double y1, double x2, double y2, string stroke = "#18181b", double width = 2)
    {
        var n = new Node { Kind = NodeKind.Line, X = Math.Min(x1, x2), Y = Math.Min(y1, y2), Fill = null, Stroke = stroke, StrokeWidth = width, Name = "Line" };
        n.Points.Add((x1 - n.X, y1 - n.Y));
        n.Points.Add((x2 - n.X, y2 - n.Y));
        n.FitPoints();
        return n;
    }

    /// <summary>A polyline or polygon through world points.</summary>
    public static Node Path(IEnumerable<(double X, double Y)> worldPoints, bool closed, string? fill = null, string? stroke = "#18181b", double width = 2)
    {
        var pts = worldPoints.ToList();
        var n = new Node { Kind = NodeKind.Path, Closed = closed, Fill = fill, Stroke = stroke, StrokeWidth = width, Name = closed ? "Polygon" : "Path" };
        if (pts.Count == 0) return n;
        n.X = pts.Min(p => p.X);
        n.Y = pts.Min(p => p.Y);
        foreach (var p in pts) n.Points.Add((p.X - n.X, p.Y - n.Y));
        n.FitPoints();
        return n;
    }

    public static Node Label(double x, double y, string text, double size = 16, string fill = "#18181b") =>
        new() { Kind = NodeKind.Text, X = x, Y = y, Text = text, FontSize = size, Fill = fill, Width = text.Length * size * 0.6, Height = size * 1.3, Name = "Text" };

    public static Node Picture(double x, double y, double w, double h, string url) =>
        new() { Kind = NodeKind.Image, X = x, Y = y, Width = w, Height = h, ImageUrl = url, Fill = null, Name = "Image" };

    public static Node Group(IEnumerable<Node> children)
    {
        var g = new Node { Kind = NodeKind.Group, Fill = null, Name = "Group" };
        g.Children.AddRange(children);
        g.FitChildren();
        return g;
    }

    /// <summary>Width and height from the points' extent.</summary>
    public void FitPoints()
    {
        if (Points.Count == 0) return;
        Width = Math.Max(Points.Max(p => p.X), 1);
        Height = Math.Max(Points.Max(p => p.Y), 1);
    }

    /// <summary>A group's frame from its children's bounds.</summary>
    public void FitChildren()
    {
        if (Children.Count == 0) return;
        var bounds = Children.Select(c => c.WorldBounds()).ToList();
        X = bounds.Min(b => b.X);
        Y = bounds.Min(b => b.Y);
        Width = bounds.Max(b => b.X + b.W) - X;
        Height = bounds.Max(b => b.Y + b.H) - Y;
    }

    /// <summary>Local shape coordinates to world coordinates.</summary>
    public Transform2D Transform =>
        Transform2D.Translation(X + Width / 2, Y + Height / 2) * Transform2D.Rotation(Rotation) * Transform2D.Translation(-Width / 2, -Height / 2);

    /// <summary>The axis-aligned box around the (possibly rotated) shape.</summary>
    public (double X, double Y, double W, double H) WorldBounds()
    {
        var t = Transform;
        var corners = new[] { t.Apply(0, 0), t.Apply(Width, 0), t.Apply(0, Height), t.Apply(Width, Height) };
        var x0 = corners.Min(c => c.X);
        var y0 = corners.Min(c => c.Y);
        return (x0, y0, corners.Max(c => c.X) - x0, corners.Max(c => c.Y) - y0);
    }

    /// <summary>Whether a world point is on the shape; <paramref name="tolerance"/> widens thin strokes.</summary>
    public bool Contains(double wx, double wy, double tolerance = 4)
    {
        if (!Visible) return false;
        var (lx, ly) = Transform.Inverse().Apply(wx, wy);
        switch (Kind)
        {
            case NodeKind.Rect or NodeKind.Text or NodeKind.Image:
                return lx >= -tolerance && ly >= -tolerance && lx <= Width + tolerance && ly <= Height + tolerance;
            case NodeKind.Ellipse:
            {
                var rx = Width / 2 + tolerance;
                var ry = Height / 2 + tolerance;
                var dx = (lx - Width / 2) / rx;
                var dy = (ly - Height / 2) / ry;
                return dx * dx + dy * dy <= 1;
            }
            case NodeKind.Line or NodeKind.Path:
            {
                var tol = Math.Max(tolerance, StrokeWidth / 2 + 2);
                for (var i = 0; i + 1 < Points.Count; i++)
                    if (SegmentDistance(lx, ly, Points[i], Points[i + 1]) <= tol) return true;
                if (Closed && Points.Count > 2)
                {
                    if (SegmentDistance(lx, ly, Points[^1], Points[0]) <= tol) return true;
                    if (Fill is not null && PointInPolygon(lx, ly)) return true;
                }
                return false;
            }
            case NodeKind.Group:
                return Children.Any(c => c.Contains(wx, wy, tolerance));
        }
        return false;
    }

    private bool PointInPolygon(double x, double y)
    {
        var inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var (xi, yi) = Points[i];
            var (xj, yj) = Points[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    private static double SegmentDistance(double px, double py, (double X, double Y) a, (double X, double Y) b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 < 1e-12 ? 0 : Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / len2, 0, 1);
        var cx = a.X + t * dx - px;
        var cy = a.Y + t * dy - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    /// <summary>A deep copy with fresh ids.</summary>
    public Node Clone()
    {
        var copy = CopyChildren(this);
        copy.Id = "n" + Interlocked.Increment(ref _next);
        copy.Points = new List<(double X, double Y)>(Points);
        return copy;
    }

    private Node CopyChildren(Node copy)
    {
        var fresh = new Node
        {
            Id = copy.Id, Kind = copy.Kind, Name = copy.Name, X = copy.X, Y = copy.Y, Width = copy.Width, Height = copy.Height,
            Rotation = copy.Rotation, Fill = copy.Fill, Stroke = copy.Stroke, StrokeWidth = copy.StrokeWidth, Opacity = copy.Opacity,
            CornerRadius = copy.CornerRadius, Points = copy.Points, Closed = copy.Closed, Text = copy.Text, FontSize = copy.FontSize,
            FontFamily = copy.FontFamily, FontWeight = copy.FontWeight, ImageUrl = copy.ImageUrl, Visible = copy.Visible, Locked = copy.Locked,
        };
        foreach (var child in Children) fresh.Children.Add(child.Clone());
        foreach (var kv in Data) fresh.Data[kv.Key] = kv.Value;
        return fresh;
    }
}

/// <summary>Pan and zoom between screen pixels and world units.</summary>
public sealed class Viewport
{
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = 1;
    public double MinZoom { get; set; } = 0.05;
    public double MaxZoom { get; set; } = 64;

    public (double X, double Y) ScreenToWorld(double sx, double sy) => ((sx - OffsetX) / Zoom, (sy - OffsetY) / Zoom);
    public (double X, double Y) WorldToScreen(double wx, double wy) => (wx * Zoom + OffsetX, wy * Zoom + OffsetY);

    public void Pan(double dx, double dy)
    {
        OffsetX += dx;
        OffsetY += dy;
    }

    /// <summary>Zooms by a factor keeping the world point under the cursor fixed.</summary>
    public void ZoomAt(double sx, double sy, double factor)
    {
        var (wx, wy) = ScreenToWorld(sx, sy);
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        OffsetX = sx - wx * Zoom;
        OffsetY = sy - wy * Zoom;
    }

    /// <summary>Fits a world rectangle into a screen area with a margin.</summary>
    public void Fit(double x, double y, double w, double h, double screenW, double screenH, double margin = 40)
    {
        if (w <= 0 || h <= 0) return;
        Zoom = Math.Clamp(Math.Min((screenW - margin * 2) / w, (screenH - margin * 2) / h), MinZoom, MaxZoom);
        OffsetX = (screenW - w * Zoom) / 2 - x * Zoom;
        OffsetY = (screenH - h * Zoom) / 2 - y * Zoom;
    }

    public void Apply(Canvas2D g) => g.SetTransform(Zoom, 0, 0, Zoom, OffsetX, OffsetY);
}

/// <summary>
/// A retained-mode 2D scene: an ordered list of nodes (last drawn on top),
/// rendered to a canvas through a <see cref="Viewport"/>. The base of drawing
/// tools, diagram editors, maps and CAD views.
/// </summary>
public sealed class Scene2D
{
    private readonly Dictionary<string, CanvasImage> _images = new();

    public List<Node> Nodes { get; } = new();
    public Viewport View { get; } = new();
    public string Background { get; set; } = "#ffffff";
    /// <summary>Grid spacing in world units; 0 hides it.</summary>
    public double GridSize { get; set; }
    public string GridColor { get; set; } = "rgba(0,0,0,0.06)";
    public string SelectionColor { get; set; } = "#c2410c";

    public Node Add(Node node)
    {
        Nodes.Add(node);
        return node;
    }

    public void Remove(Node node) => Nodes.Remove(node);
    public Node? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public void BringToFront(Node n)
    {
        if (Nodes.Remove(n)) Nodes.Add(n);
    }

    public void SendToBack(Node n)
    {
        if (Nodes.Remove(n)) Nodes.Insert(0, n);
    }

    /// <summary>The topmost unlocked node at a screen point.</summary>
    public Node? HitTest(double sx, double sy)
    {
        var (wx, wy) = View.ScreenToWorld(sx, sy);
        var tolerance = 4 / View.Zoom;
        for (var i = Nodes.Count - 1; i >= 0; i--)
            if (!Nodes[i].Locked && Nodes[i].Contains(wx, wy, tolerance)) return Nodes[i];
        return null;
    }

    /// <summary>Nodes whose bounds lie inside a world rectangle (marquee selection).</summary>
    public List<Node> NodesInRect(double x, double y, double w, double h)
    {
        var (x0, x1) = w >= 0 ? (x, x + w) : (x + w, x);
        var (y0, y1) = h >= 0 ? (y, y + h) : (y + h, y);
        return Nodes.Where(n =>
        {
            var b = n.WorldBounds();
            return b.X >= x0 && b.Y >= y0 && b.X + b.W <= x1 && b.Y + b.H <= y1;
        }).ToList();
    }

    /// <summary>Bounds of all nodes, for zoom-to-fit.</summary>
    public (double X, double Y, double W, double H) Extent()
    {
        if (Nodes.Count == 0) return (0, 0, 0, 0);
        var all = Nodes.Select(n => n.WorldBounds()).ToList();
        var x = all.Min(b => b.X);
        var y = all.Min(b => b.Y);
        return (x, y, all.Max(b => b.X + b.W) - x, all.Max(b => b.Y + b.H) - y);
    }

    /// <summary>Loads every image node's picture once, so Render can draw it.</summary>
    public async Task LoadImagesAsync()
    {
        foreach (var url in AllNodes(Nodes).Where(n => n.ImageUrl is not null).Select(n => n.ImageUrl!).Distinct())
            if (!_images.ContainsKey(url)) _images[url] = await CanvasImage.LoadAsync(url);
    }

    public void Render(Canvas2D g, IReadOnlyCollection<Node>? selection = null)
    {
        g.ResetTransform().Fill(Background).FillRect(0, 0, g.Width, g.Height);
        View.Apply(g);
        if (GridSize > 0) DrawGrid(g);
        foreach (var n in Nodes) Draw(g, n);
        if (selection is not null)
            foreach (var n in selection) DrawSelection(g, n);
        g.ResetTransform();
    }

    private void DrawGrid(Canvas2D g)
    {
        var step = GridSize;
        while (step * View.Zoom < 8) step *= 2;
        var (x0, y0) = View.ScreenToWorld(0, 0);
        var (x1, y1) = View.ScreenToWorld(g.Width, g.Height);
        g.Stroke(GridColor).LineWidth(1 / View.Zoom).BeginPath();
        for (var x = Math.Floor(x0 / step) * step; x <= x1; x += step) g.MoveTo(x, y0).LineTo(x, y1);
        for (var y = Math.Floor(y0 / step) * step; y <= y1; y += step) g.MoveTo(x0, y).LineTo(x1, y);
        g.StrokePath();
    }

    private void Draw(Canvas2D g, Node n)
    {
        if (!n.Visible) return;
        if (n.Kind == NodeKind.Group)
        {
            foreach (var c in n.Children) Draw(g, c);
            return;
        }
        g.Save().Alpha(n.Opacity).Translate(n.X + n.Width / 2, n.Y + n.Height / 2).Rotate(n.Rotation).Translate(-n.Width / 2, -n.Height / 2);
        switch (n.Kind)
        {
            case NodeKind.Rect:
                g.BeginPath();
                if (n.CornerRadius > 0) g.RoundRect(0, 0, n.Width, n.Height, n.CornerRadius);
                else g.Rect(0, 0, n.Width, n.Height);
                Paint(g, n);
                break;
            case NodeKind.Ellipse:
                g.BeginPath().Ellipse(n.Width / 2, n.Height / 2, Math.Abs(n.Width / 2), Math.Abs(n.Height / 2));
                Paint(g, n);
                break;
            case NodeKind.Line or NodeKind.Path:
                if (n.Points.Count > 1)
                {
                    g.BeginPath().MoveTo(n.Points[0].X, n.Points[0].Y);
                    for (var i = 1; i < n.Points.Count; i++) g.LineTo(n.Points[i].X, n.Points[i].Y);
                    if (n.Closed) g.ClosePath();
                    g.LineJoin("round").LineCap("round");
                    Paint(g, n);
                }
                break;
            case NodeKind.Text:
                g.Font($"{n.FontWeight} {n.FontSize.ToString(CultureInfo.InvariantCulture)}px {n.FontFamily}").TextBaseline("top").TextAlign("left");
                if (n.Fill is not null) g.Fill(n.Fill).FillText(n.Text, 0, 0);
                if (n.Stroke is not null) g.Stroke(n.Stroke).LineWidth(n.StrokeWidth).StrokeText(n.Text, 0, 0);
                break;
            case NodeKind.Image:
                if (n.ImageUrl is not null && _images.TryGetValue(n.ImageUrl, out var img)) g.DrawImage(img, 0, 0, n.Width, n.Height);
                else g.Fill("#e4e4e7").FillRect(0, 0, n.Width, n.Height);
                break;
        }
        g.Restore();
    }

    private static void Paint(Canvas2D g, Node n)
    {
        if (n.Fill is not null) g.Fill(n.Fill).FillPath();
        if (n.Stroke is not null && n.StrokeWidth > 0) g.Stroke(n.Stroke).LineWidth(n.StrokeWidth).StrokePath();
    }

    private void DrawSelection(Canvas2D g, Node n)
    {
        var px = 1 / View.Zoom;
        g.Save().Translate(n.X + n.Width / 2, n.Y + n.Height / 2).Rotate(n.Rotation).Translate(-n.Width / 2, -n.Height / 2);
        g.Stroke(SelectionColor).LineWidth(1.5 * px).StrokeRect(0, 0, n.Width, n.Height);
        var h = 7 * px;
        foreach (var (hx, hy) in Handles(n))
            g.Fill("#ffffff").FillRect(hx - h / 2, hy - h / 2, h, h).StrokeRect(hx - h / 2, hy - h / 2, h, h);
        g.Restore();
    }

    /// <summary>Resize handle positions in the node's local space, clockwise from top-left.</summary>
    public static IEnumerable<(double X, double Y)> Handles(Node n)
    {
        yield return (0, 0);
        yield return (n.Width / 2, 0);
        yield return (n.Width, 0);
        yield return (n.Width, n.Height / 2);
        yield return (n.Width, n.Height);
        yield return (n.Width / 2, n.Height);
        yield return (0, n.Height);
        yield return (0, n.Height / 2);
    }

    private static IEnumerable<Node> AllNodes(IEnumerable<Node> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in AllNodes(n.Children)) yield return c;
        }
    }

    // -- export ------------------------------------------------------------------

    public string ToSvg()
    {
        var (x, y, w, h) = Extent();
        var sb = new StringBuilder();
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(x)} {F(y)} {F(Math.Max(w, 1))} {F(Math.Max(h, 1))}\" width=\"{F(Math.Max(w, 1))}\" height=\"{F(Math.Max(h, 1))}\">");
        foreach (var n in Nodes) Svg(sb, n, F);
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void Svg(StringBuilder sb, Node n, Func<double, string> f)
    {
        if (!n.Visible) return;
        var paint = $" fill=\"{n.Fill ?? "none"}\" stroke=\"{n.Stroke ?? "none"}\" stroke-width=\"{f(n.StrokeWidth)}\" opacity=\"{f(n.Opacity)}\"";
        var transform = $" transform=\"translate({f(n.X + n.Width / 2)} {f(n.Y + n.Height / 2)}) rotate({f(n.Rotation * 180 / Math.PI)}) translate({f(-n.Width / 2)} {f(-n.Height / 2)})\"";
        switch (n.Kind)
        {
            case NodeKind.Rect:
                sb.Append($"<rect width=\"{f(n.Width)}\" height=\"{f(n.Height)}\" rx=\"{f(n.CornerRadius)}\"{paint}{transform}/>");
                break;
            case NodeKind.Ellipse:
                sb.Append($"<ellipse cx=\"{f(n.Width / 2)}\" cy=\"{f(n.Height / 2)}\" rx=\"{f(n.Width / 2)}\" ry=\"{f(n.Height / 2)}\"{paint}{transform}/>");
                break;
            case NodeKind.Line or NodeKind.Path:
                var pts = string.Join(" ", n.Points.Select(p => $"{f(p.X)},{f(p.Y)}"));
                sb.Append($"<{(n.Closed ? "polygon" : "polyline")} points=\"{pts}\"{paint}{transform}/>");
                break;
            case NodeKind.Text:
                sb.Append($"<text y=\"{f(n.FontSize)}\" font-size=\"{f(n.FontSize)}\" font-family=\"{n.FontFamily}\" font-weight=\"{n.FontWeight}\" fill=\"{n.Fill}\"{transform}>{System.Net.WebUtility.HtmlEncode(n.Text)}</text>");
                break;
            case NodeKind.Image:
                sb.Append($"<image href=\"{n.ImageUrl}\" width=\"{f(n.Width)}\" height=\"{f(n.Height)}\"{transform}/>");
                break;
            case NodeKind.Group:
                sb.Append("<g>");
                foreach (var c in n.Children) Svg(sb, c, f);
                sb.Append("</g>");
                break;
        }
    }

    public string ToJson()
    {
        var w = new JsonWriter();
        w.BeginObject().Prop("background", Background).Prop("grid", GridSize).Name("nodes").BeginArray();
        foreach (var n in Nodes) WriteNode(w, n);
        w.EndArray().EndObject();
        return w.ToString();
    }

    public static Scene2D FromJson(string json)
    {
        var v = JsonValue.Parse(json);
        var scene = new Scene2D { Background = v["background"].AsStringOrNull() ?? "#ffffff", GridSize = v["grid"].AsDouble() };
        foreach (var n in v["nodes"].Items) scene.Nodes.Add(ReadNode(n));
        return scene;
    }

    private static void WriteNode(JsonWriter w, Node n)
    {
        w.BeginObject()
            .Prop("id", n.Id).Prop("kind", n.Kind.ToString()).Prop("name", n.Name)
            .Prop("x", n.X).Prop("y", n.Y).Prop("w", n.Width).Prop("h", n.Height).Prop("rot", n.Rotation)
            .Prop("fill", n.Fill).Prop("stroke", n.Stroke).Prop("sw", n.StrokeWidth).Prop("op", n.Opacity).Prop("r", n.CornerRadius)
            .Prop("closed", n.Closed).Prop("text", n.Text).Prop("fs", n.FontSize).Prop("ff", n.FontFamily).Prop("fw", n.FontWeight)
            .Prop("img", n.ImageUrl).Prop("visible", n.Visible).Prop("locked", n.Locked);
        w.Name("pts").BeginArray();
        foreach (var (x, y) in n.Points) w.Number(x).Number(y);
        w.EndArray();
        w.Name("data").BeginObject();
        foreach (var kv in n.Data) w.Prop(kv.Key, kv.Value);
        w.EndObject();
        w.Name("children").BeginArray();
        foreach (var c in n.Children) WriteNode(w, c);
        w.EndArray().EndObject();
    }

    private static Node ReadNode(JsonValue v)
    {
        var n = new Node
        {
            Id = v["id"].AsString(),
            Kind = Enum.TryParse<NodeKind>(v["kind"].AsString(), out var k) ? k : NodeKind.Rect,
            Name = v["name"].AsString(),
            X = v["x"].AsDouble(), Y = v["y"].AsDouble(), Width = v["w"].AsDouble(), Height = v["h"].AsDouble(), Rotation = v["rot"].AsDouble(),
            Fill = v["fill"].AsStringOrNull(), Stroke = v["stroke"].AsStringOrNull(), StrokeWidth = v["sw"].AsDouble(), Opacity = v["op"].AsDouble(),
            CornerRadius = v["r"].AsDouble(), Closed = v["closed"].AsBool(), Text = v["text"].AsString(), FontSize = v["fs"].AsDouble(),
            FontFamily = v["ff"].AsString(), FontWeight = v["fw"].AsString(), ImageUrl = v["img"].AsStringOrNull(),
            Visible = v["visible"].AsBool(), Locked = v["locked"].AsBool(),
        };
        var pts = v["pts"].Items.Select(p => p.AsDouble()).ToList();
        for (var i = 0; i + 1 < pts.Count; i += 2) n.Points.Add((pts[i], pts[i + 1]));
        foreach (var key in v["data"].Keys) n.Data[key] = v["data"][key].AsString();
        foreach (var c in v["children"].Items) n.Children.Add(ReadNode(c));
        return n;
    }
}
