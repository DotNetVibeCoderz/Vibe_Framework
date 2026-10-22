namespace Rupa.Model;

public enum NodeKind { Frame, Group, Rect, Ellipse, Line, Path, Text, Image, Instance }

public enum LayoutMode { None, Row, Column }

public enum Align { Start, Center, End }

public struct Pt
{
    public double X, Y;
    public Pt(double x, double y) { X = x; Y = y; }
}

/// <summary>
/// One layer. Geometry is in the parent's space: (X, Y) is the top-left of the
/// unrotated box, and the box turns by <see cref="Rotation"/> degrees about its
/// centre. Children of frames and groups are positioned relative to them.
/// </summary>
public sealed class Node
{
    public string Id = "";
    public NodeKind Kind;
    public string Name = "";
    public double X, Y, W, H, Rotation;
    public bool Hidden, Locked;
    public double Opacity = 1;

    // Paint: "#rrggbb", "#rrggbbaa", "none", or "s:<style id>" for a colour style.
    public string Fill = "none";
    public string Stroke = "none";
    public double StrokeWidth;
    public double Radius;

    // Text
    public string Text = "";
    public string Font = "Plus Jakarta Sans";
    public double FontSize = 16;
    public int Weight = 400;
    public double LineHeight = 1.3;
    public double Tracking;
    public Align TextAlign = Align.Start;

    // Path: points in the node's local space, and whether the outline closes.
    public List<Pt> Points = new();
    public bool Closed;

    // Image
    public string Href = "";

    // Frame
    public bool Clip = true;
    public bool IsComponent;
    public LayoutMode Layout;
    public double Gap;
    public double Padding;
    public Align CrossAlign = Align.Start;
    public Align MainAlign = Align.Start;
    public bool Hug;

    // Instance: the main component, and text overrides keyed by the main's text layer ids.
    public string MainId = "";
    public Dictionary<string, string> Overrides = new();

    public List<Node> Children = new();
    public Node? Parent;

    /// <summary>Changes whenever this layer changes; its view renders again, nothing else does.</summary>
    public int Rev;

    /// <summary>Changes when the list of children changes (added, removed, reordered).</summary>
    public int Kids;

    public bool IsContainer => Kind == NodeKind.Frame || Kind == NodeKind.Group;

    public bool HasFill => Kind is NodeKind.Frame or NodeKind.Rect or NodeKind.Ellipse or NodeKind.Path or NodeKind.Text;

    public bool HasStroke => Kind is not NodeKind.Group and not NodeKind.Instance and not NodeKind.Text;

    public bool HasRadius => Kind is NodeKind.Frame or NodeKind.Rect or NodeKind.Image;

    /// <summary>A copy of this layer and everything inside it, keeping ids.</summary>
    public Node Clone()
    {
        var n = new Node
        {
            Id = Id, Kind = Kind, Name = Name, X = X, Y = Y, W = W, H = H, Rotation = Rotation,
            Hidden = Hidden, Locked = Locked, Opacity = Opacity, Fill = Fill, Stroke = Stroke,
            StrokeWidth = StrokeWidth, Radius = Radius, Text = Text, Font = Font, FontSize = FontSize,
            Weight = Weight, LineHeight = LineHeight, Tracking = Tracking, TextAlign = TextAlign,
            Points = new List<Pt>(Points), Closed = Closed, Href = Href, Clip = Clip, IsComponent = IsComponent,
            Layout = Layout, Gap = Gap, Padding = Padding, CrossAlign = CrossAlign, MainAlign = MainAlign, Hug = Hug, MainId = MainId,
            Overrides = CopyOverrides(), Rev = Rev, Kids = Kids,
            Children = new List<Node>(Children.Count),
        };
        foreach (var c in Children)
        {
            var cc = c.Clone();
            cc.Parent = n;
            n.Children.Add(cc);
        }
        return n;
    }

    private Dictionary<string, string> CopyOverrides()
    {
        var d = new Dictionary<string, string>();
        foreach (var kv in Overrides) d[kv.Key] = kv.Value;
        return d;
    }

    /// <summary>This layer and every layer inside it, depth first.</summary>
    public List<Node> SelfAndDescendants()
    {
        var all = new List<Node>();
        Collect(all);
        return all;
    }

    private void Collect(List<Node> into)
    {
        into.Add(this);
        foreach (var c in Children) c.Collect(into);
    }

    public bool IsInside(Node ancestor)
    {
        for (var p = Parent; p is not null; p = p.Parent)
            if (p == ancestor) return true;
        return false;
    }

    public static string KindLabel(NodeKind k) => k switch
    {
        NodeKind.Frame => "Frame",
        NodeKind.Group => "Group",
        NodeKind.Rect => "Rectangle",
        NodeKind.Ellipse => "Ellipse",
        NodeKind.Line => "Line",
        NodeKind.Path => "Vector",
        NodeKind.Text => "Text",
        NodeKind.Image => "Image",
        _ => "Instance",
    };
}

public sealed class ColorStyle
{
    public string Id = "";
    public string Name = "";
    public string Hex = "#000000";
}
