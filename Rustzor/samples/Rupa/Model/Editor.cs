namespace Rupa.Model;

public enum Tool { Move, Frame, Rect, Ellipse, Line, Pen, Text, Hand }

/// <summary>A line or a measurement drawn over the canvas while dragging, in screen pixels.</summary>
public readonly record struct Guide(double X1, double Y1, double X2, double Y2, string? Label);

/// <summary>
/// The open design file and everything about editing it: selection, viewport,
/// tools, undo history and the commands the menus, panels and shortcuts call.
/// </summary>
public sealed partial class Editor
{
    /// <summary>The editor on screen; layer views look their layers up here.</summary>
    public static Editor? Current;

    public Doc Doc;
    public readonly List<Node> Selection = new();
    public Tool Tool = Tool.Move;

    // Viewport: screen = world × Zoom + Pan.
    public double Zoom = 1, PanX, PanY, ViewW = 800, ViewH = 600;

    public Node? Hover;
    public string? EditingTextId;

    /// <summary>Panels that list layers re-render only when this changes (not while dragging).</summary>
    public int LayersRev;

    /// <summary>Raised after every change the screen should show.</summary>
    public event Action? Changed;

    /// <summary>Raised after a change worth saving.</summary>
    public event Action? Edited;

    /// <summary>Measures text: (font, size, weight, tracking, text) → (width of the widest line, line count).</summary>
    public Func<string, double, int, double, string, (double W, int Lines)> Measure = (_, size, _, _, text) =>
    {
        var lines = text.Split('\n');
        var w = 0.0;
        foreach (var l in lines) w = Math.Max(w, l.Length * size * 0.55);
        return (w, lines.Length);
    };

    private readonly List<(Node Root, List<ColorStyle> Styles, string Page)> _undo = new();
    private readonly List<(Node Root, List<ColorStyle> Styles, string Page)> _redo = new();
    private string? _lastChangeKey;
    private DateTime _lastChangeAt;
    private List<Node> _clipboard = new();

    public Editor(Doc doc)
    {
        Doc = doc;
        LayOutAll();
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public Node? Single => Selection.Count == 1 ? Selection[0] : null;

    public void Notify() => Changed?.Invoke();

    // ── history ───────────────────────────────────────────────────────────

    /// <summary>
    /// Records the document before a change. Changes with the same key in quick
    /// succession (dragging a slider, typing in a field) undo as one.
    /// </summary>
    public void Checkpoint(string? key = null)
    {
        var now = DateTime.UtcNow;
        if (key is not null && key == _lastChangeKey && (now - _lastChangeAt).TotalMilliseconds < 1200)
        {
            _lastChangeAt = now;
            return;
        }
        _lastChangeKey = key;
        _lastChangeAt = now;
        _undo.Add(Doc.Snapshot());
        if (_undo.Count > 80) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Doc.Snapshot());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Doc.Snapshot());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
    }

    private void Restore((Node Root, List<ColorStyle> Styles, string Page) s)
    {
        var ids = Selection.ConvertAll(n => n.Id);
        Doc.Restore(s);
        Selection.Clear();
        foreach (var id in ids)
            if (Doc.Find(id) is { } n) Selection.Add(n);
        EditingTextId = null;
        _lastChangeKey = null;
        Changes(structure: true);
    }

    /// <summary>After any edit: keeps groups, auto layout and instances in shape, then redraws and saves.</summary>
    public void Changes(bool structure = false)
    {
        SyncInstances();
        if (structure) LayersRev++;
        Changed?.Invoke();
        Edited?.Invoke();
    }

    // ── selection ─────────────────────────────────────────────────────────

    public void Select(Node? n, bool add = false)
    {
        if (!add) Selection.Clear();
        if (n is not null)
        {
            if (add && Selection.Contains(n)) Selection.Remove(n);
            else if (!Selection.Contains(n))
            {
                // A selection stays within one parent.
                if (Selection.Count > 0 && Selection[0].Parent != n.Parent) Selection.Clear();
                Selection.Add(n);
            }
        }
        EditingTextId = null;
        LayersRev++;
    }

    public void SelectAll()
    {
        var parent = Selection.Count > 0 ? Selection[0].Parent ?? Doc.Root : Doc.Root;
        Selection.Clear();
        foreach (var c in parent.Children)
            if (!c.Hidden && !c.Locked) Selection.Add(c);
        LayersRev++;
    }

    /// <summary>The container whose children a click selects: the parent of the current selection inside a group.</summary>
    public Node? Context
    {
        get
        {
            var s = Selection.Count > 0 ? Selection[0] : null;
            return s?.Parent is { } p && p != Doc.Root && (p.Kind == NodeKind.Group || !Doc.IsTop(p)) ? p : null;
        }
    }

    // ── viewport ──────────────────────────────────────────────────────────

    public Pt ToWorld(double sx, double sy) => new((sx - PanX) / Zoom, (sy - PanY) / Zoom);

    public Pt ToScreen(Pt w) => new(w.X * Zoom + PanX, w.Y * Zoom + PanY);

    public void ZoomAt(double factor, double sx, double sy)
    {
        var z = Math.Clamp(Zoom * factor, 0.02, 64);
        var w = ToWorld(sx, sy);
        Zoom = z;
        PanX = sx - w.X * z;
        PanY = sy - w.Y * z;
    }

    public void ZoomTo(Box b, double maxZoom = 1)
    {
        if (b.W <= 0 || b.H <= 0) return;
        var margin = Math.Min(80, Math.Min(ViewW, ViewH) * 0.08);
        var z = Math.Min((ViewW - margin * 2) / b.W, (ViewH - margin * 2) / b.H);
        Zoom = Math.Clamp(Math.Min(z, maxZoom), 0.02, 64);
        PanX = ViewW / 2 - b.Cx * Zoom;
        PanY = ViewH / 2 - b.Cy * Zoom;
    }

    public void ZoomToFit()
    {
        Box? all = null;
        foreach (var c in Doc.Root.Children)
        {
            if (c.Hidden) continue;
            var b = G.WorldBounds(c);
            all = all is null ? b : Box.Union(all.Value, b);
        }
        if (all is not null) ZoomTo(all.Value, 2);
    }

    public void ZoomToSelection()
    {
        if (SelectionBounds() is { } b) ZoomTo(b, 4);
    }

    /// <summary>
    /// The selection's bounds on the canvas. The canvas asks several times a
    /// frame, so the answer is kept until a selected layer changes; while layers
    /// are dragged it is the drag's own box, which moves with them.
    /// </summary>
    public Box? SelectionBounds()
    {
        if (DragBounds() is { } dragging) return dragging;
        long key = Selection.Count;
        foreach (var n in Selection) key = key * 31 + n.Rev;
        if (key == _boundsKey && Selection.Count > 0) return _bounds;
        Box? all = null;
        foreach (var n in Selection)
        {
            var b = G.WorldBounds(n);
            all = all is null ? b : Box.Union(all.Value, b);
        }
        _boundsKey = key;
        _bounds = all;
        return all;
    }

    private long _boundsKey = -1;
    private Box? _bounds;

    // ── commands ──────────────────────────────────────────────────────────

    public void Delete()
    {
        if (Selection.Count == 0) return;
        Checkpoint();
        foreach (var n in Selection.ToArray())
        {
            if (n.IsComponent)
                foreach (var inst in Doc.InstancesOf(n.Id)) Detach(inst, select: false);
            var parent = n.Parent;
            Doc.Remove(n);
            if (parent is not null) Reflow(parent);
        }
        Selection.Clear();
        Changes(structure: true);
    }

    public void Duplicate()
    {
        if (Selection.Count == 0) return;
        Checkpoint();
        var copies = new List<Node>();
        foreach (var n in Selection)
        {
            var parent = n.Parent ?? Doc.Root;
            var copy = Copy(n);
            var top = Doc.IsTop(n) && n.Kind == NodeKind.Frame;
            copy.X += top ? n.W + 40 : 10;
            copy.Y += top ? 0 : 10;
            if (copy.IsComponent) copy.IsComponent = false;
            Doc.Add(parent, copy, parent.Children.IndexOf(n) + 1);
            copies.Add(copy);
            Reflow(parent);
        }
        Selection.Clear();
        Selection.AddRange(copies);
        Changes(structure: true);
    }

    /// <summary>A deep copy with fresh ids, named like the original.</summary>
    public Node Copy(Node n)
    {
        var c = n.Clone();
        foreach (var d in c.SelfAndDescendants()) d.Id = Doc.NewId();
        return c;
    }

    public void CopyToClipboard()
    {
        _clipboard = Selection.ConvertAll(n => n.Clone());
    }

    public void Cut()
    {
        CopyToClipboard();
        Delete();
    }

    public void Paste()
    {
        if (_clipboard.Count == 0) return;
        Checkpoint();
        // Into the selected frame, or beside the originals.
        var target = Single is { Kind: NodeKind.Frame } f ? f : Selection.Count > 0 ? Selection[0].Parent ?? Doc.Root : Doc.Root;
        Selection.Clear();
        foreach (var n in _clipboard)
        {
            var copy = Copy(n);
            copy.IsComponent = false;
            if (Doc.Find(n.Id) is { } original && original.Parent == target)
            {
                copy.X += 10;
                copy.Y += 10;
            }
            else if (target == Doc.Root)
            {
                var c = ToWorld(ViewW / 2, ViewH / 2);
                copy.X = Math.Round(c.X - copy.W / 2);
                copy.Y = Math.Round(c.Y - copy.H / 2);
            }
            Doc.Add(target, copy);
            Selection.Add(copy);
        }
        Reflow(target);
        Changes(structure: true);
    }

    public void Group()
    {
        if (Selection.Count == 0) return;
        Checkpoint();
        var parent = Selection[0].Parent ?? Doc.Root;
        var members = new List<Node>(Selection);
        members.Sort((a, b) => parent.Children.IndexOf(a).CompareTo(parent.Children.IndexOf(b)));
        var index = parent.Children.IndexOf(members[^1]) - (members.Count - 1);
        var box = G.BoundsInParent(members[0]);
        foreach (var m in members) box = Box.Union(box, G.BoundsInParent(m));
        var g = new Node { Id = Doc.NewId(), Kind = NodeKind.Group, Name = "Group " + Doc.NewId().Substring(1), X = box.X, Y = box.Y, W = box.W, H = box.H };
        foreach (var m in members) Doc.Remove(m);
        Doc.Add(parent, g, Math.Max(0, index));
        foreach (var m in members)
        {
            m.X -= box.X;
            m.Y -= box.Y;
            Doc.Add(g, m);
        }
        Selection.Clear();
        Selection.Add(g);
        Reflow(parent);
        Changes(structure: true);
    }

    public void Ungroup()
    {
        var groups = Selection.FindAll(n => n.Kind == NodeKind.Group || (n.Kind == NodeKind.Frame && !Doc.IsTop(n) && !n.IsComponent));
        if (groups.Count == 0) return;
        Checkpoint();
        Selection.Clear();
        foreach (var g in groups)
        {
            var parent = g.Parent ?? Doc.Root;
            var index = parent.Children.IndexOf(g);
            var m = G.Local(g);
            var kids = new List<Node>(g.Children);
            Doc.Remove(g);
            foreach (var k in kids)
            {
                // Bake the group's offset (and any turn) into each child.
                var c = m.Apply(G.Local(k).Apply(k.W / 2, k.H / 2));
                k.Rotation = G.Normalize(k.Rotation + g.Rotation);
                k.X = c.X - k.W / 2;
                k.Y = c.Y - k.H / 2;
                Doc.Add(parent, k, index++);
                Selection.Add(k);
            }
            Reflow(parent);
        }
        Changes(structure: true);
    }

    /// <summary>Moves the selection up or down the stack; to the very top or bottom with <paramref name="all"/>.</summary>
    public void Arrange(int direction, bool all)
    {
        if (Selection.Count == 0) return;
        Checkpoint();
        var parent = Selection[0].Parent ?? Doc.Root;
        var list = parent.Children;
        var order = new List<Node>(Selection);
        order.Sort((a, b) => list.IndexOf(a).CompareTo(list.IndexOf(b)));
        if (direction > 0) order.Reverse();
        foreach (var n in order)
        {
            var i = list.IndexOf(n);
            var j = all ? (direction > 0 ? list.Count - 1 : 0) : Math.Clamp(i + direction, 0, list.Count - 1);
            if (i == j) continue;
            list.RemoveAt(i);
            list.Insert(j, n);
        }
        Doc.TouchChildren(parent);
        Reflow(parent);
        Changes(structure: true);
    }

    public void Nudge(double dx, double dy)
    {
        if (Selection.Count == 0) return;
        Checkpoint("nudge");
        foreach (var n in Selection)
        {
            if (n.Locked) continue;
            n.X += dx;
            n.Y += dy;
            Doc.Touch(n);
        }
        Reflow(Selection[0].Parent ?? Doc.Root);
        Changes();
    }

    public void ToggleHidden(Node n)
    {
        Checkpoint();
        n.Hidden = !n.Hidden;
        if (n.Hidden) Selection.Remove(n);
        Doc.Touch(n);
        if (n.Parent is { } p) Reflow(p);
        Changes(structure: true);
    }

    public void ToggleLocked(Node n)
    {
        Checkpoint();
        n.Locked = !n.Locked;
        if (n.Locked) Selection.Remove(n);
        Doc.Touch(n);
        Changes(structure: true);
    }

    public void Rename(Node n, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == n.Name) return;
        Checkpoint();
        n.Name = name;
        Doc.Touch(n);
        Changes(structure: true);
    }

    /// <summary>Turns the selected frame into a component, or wraps the selection in a new component.</summary>
    public void MakeComponent()
    {
        if (Selection.Count == 0) return;
        if (Single is { Kind: NodeKind.Frame, IsComponent: false } f)
        {
            Checkpoint();
            f.IsComponent = true;
            Doc.Touch(f);
            Changes(structure: true);
            return;
        }
        if (Single is { IsComponent: true }) return;
        Group();
        var g = Single!;
        g.Kind = NodeKind.Frame;
        g.IsComponent = true;
        g.Clip = false;
        g.Name = "Component " + g.Id.Substring(1);
        Doc.Touch(g);
        Changes(structure: true);
    }

    /// <summary>A new instance of a component, beside it or into the selected frame.</summary>
    public Node CreateInstance(Node main, Node? into = null, double? x = null, double? y = null)
    {
        Checkpoint();
        var parent = into ?? main.Parent ?? Doc.Root;
        var inst = new Node
        {
            Id = Doc.NewId(),
            Kind = NodeKind.Instance,
            Name = main.Name,
            MainId = main.Id,
            W = main.W,
            H = main.H,
            X = x ?? (parent == main.Parent ? main.X + main.W + 32 : 16),
            Y = y ?? (parent == main.Parent ? main.Y : 16),
        };
        Doc.Add(parent, inst);
        Reflow(parent);
        Selection.Clear();
        Selection.Add(inst);
        Changes(structure: true);
        return inst;
    }

    /// <summary>Turns an instance into an ordinary frame with copies of the component's layers.</summary>
    public void Detach(Node inst, bool select = true)
    {
        if (inst.Kind != NodeKind.Instance || Doc.Find(inst.MainId) is not { } main) return;
        if (select) Checkpoint();
        var parent = inst.Parent ?? Doc.Root;
        var index = parent.Children.IndexOf(inst);
        var copy = main.Clone();
        foreach (var d in copy.SelfAndDescendants())
        {
            if (d.Kind == NodeKind.Text && inst.Overrides.TryGetValue(d.Id, out var text)) d.Text = text;
            d.Id = Doc.NewId();
        }
        copy.IsComponent = false;
        copy.Name = inst.Name;
        copy.X = inst.X;
        copy.Y = inst.Y;
        copy.Rotation = inst.Rotation;
        copy.Opacity = inst.Opacity;
        Doc.Remove(inst);
        Doc.Add(parent, copy, index);
        if (select)
        {
            Selection.Clear();
            Selection.Add(copy);
            Changes(structure: true);
        }
    }

    public void SetOverride(Node inst, string textId, string text)
    {
        Checkpoint("ovr:" + inst.Id + textId);
        var main = Doc.Find(textId);
        if (main is not null && main.Text == text) inst.Overrides.Remove(textId);
        else inst.Overrides[textId] = text;
        Doc.Touch(inst);
        Changes();
    }

    private void SyncInstances()
    {
        foreach (var main in Doc.Components())
            foreach (var inst in Doc.InstancesOf(main.Id))
            {
                if (inst.W == main.W && inst.H == main.H) continue;
                inst.W = main.W;
                inst.H = main.H;
                Doc.Touch(inst);
                if (inst.Parent is { } p) Reflow(p);
            }
    }

    /// <summary>Places images (dropped, pasted or picked) centred on a canvas point, inside the frame there.</summary>
    public void PlaceImages(List<(string Name, string Url, double W, double H)> images, Pt world)
    {
        if (images.Count == 0) return;
        Checkpoint();
        var parent = FrameAt(world);
        var local = G.World(parent).Inverse().Apply(world);
        Selection.Clear();
        var offset = 0.0;
        foreach (var (name, url, w0, h0) in images)
        {
            // Large photos come in at a size that fits the frame or the view.
            var limit = parent != Doc.Root ? Math.Min(parent.W, parent.H) * 0.8 : 480 / Zoom;
            var s = Math.Min(1, limit / Math.Max(w0, h0));
            var w = Math.Round(Math.Max(1, w0 * s));
            var h = Math.Round(Math.Max(1, h0 * s));
            var n = new Node
            {
                Id = Doc.NewId(),
                Kind = NodeKind.Image,
                Name = name.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(name) : "Image",
                Href = url,
                W = w,
                H = h,
                X = Math.Round(local.X - w / 2 + offset),
                Y = Math.Round(local.Y - h / 2 + offset),
            };
            Doc.Add(parent, n);
            Selection.Add(n);
            offset += 16;
        }
        Reflow(parent);
        Changes(structure: true);
    }

    /// <summary>A frame of a few hundred shapes beside the page, to feel how dragging copes.</summary>
    public Node AddStressFrame(int count)
    {
        Checkpoint();
        var right = 0.0;
        foreach (var c in Doc.Root.Children) right = Math.Max(right, G.WorldBounds(c).Right);
        var f = new Node { Id = Doc.NewId(), Kind = NodeKind.Frame, Name = "Performance test (" + count + " shapes)", X = right + 120, Y = 0, W = 1200, H = 860, Fill = "#ffffff" };
        Doc.Add(Doc.Root, f);
        var rnd = new Random(2210);
        var styles = Doc.Styles.Count > 0 ? Doc.Styles : new List<ColorStyle> { new() { Id = "", Hex = "#d4127a" } };
        for (var i = 0; i < count; i++)
        {
            var kind = i % 3 == 0 ? NodeKind.Ellipse : NodeKind.Rect;
            var size = 18 + rnd.Next(46);
            var st = styles[rnd.Next(styles.Count)];
            var n = new Node
            {
                Id = Doc.NewId(),
                Kind = kind,
                Name = (kind == NodeKind.Ellipse ? "Dot " : "Tile ") + (i + 1),
                X = 20 + rnd.Next(1120),
                Y = 20 + rnd.Next(780),
                W = size,
                H = kind == NodeKind.Ellipse ? size : 14 + rnd.Next(40),
                Rotation = i % 7 == 0 ? rnd.Next(60) - 30 : 0,
                Radius = kind == NodeKind.Rect ? rnd.Next(10) : 0,
                Fill = st.Id.Length > 0 ? "s:" + st.Id : st.Hex,
                Opacity = 0.65 + rnd.Next(35) / 100.0,
            };
            Doc.Add(f, n);
        }
        Selection.Clear();
        Selection.Add(f);
        ZoomTo(G.WorldBounds(f), 1);
        Changes(structure: true);
        return f;
    }

    // ── layout ────────────────────────────────────────────────────────────

    /// <summary>After a child changes: re-runs auto layout and re-fits groups, up the tree.</summary>
    public void Reflow(Node container)
    {
        for (var c = container; c is not null && c != Doc.Root; c = c.Parent)
        {
            if (c.Kind == NodeKind.Group) FitGroup(c);
            else if (c.Kind == NodeKind.Frame && c.Layout != LayoutMode.None) LayOut(c);
            else break;
        }
    }

    private void FitGroup(Node g)
    {
        if (g.Children.Count == 0)
        {
            Doc.Remove(g);
            Selection.Remove(g);
            return;
        }
        var box = G.BoundsInParent(g.Children[0]);
        foreach (var c in g.Children) box = Box.Union(box, G.BoundsInParent(c));
        if (Math.Abs(box.X) < 1e-9 && Math.Abs(box.Y) < 1e-9 && Math.Abs(box.W - g.W) < 1e-9 && Math.Abs(box.H - g.H) < 1e-9) return;
        // Turned groups keep their children's frame of reference; only the size follows.
        if (g.Rotation == 0)
        {
            foreach (var c in g.Children)
            {
                c.X -= box.X;
                c.Y -= box.Y;
            }
            g.X += box.X;
            g.Y += box.Y;
        }
        g.W = box.W;
        g.H = box.H;
        Doc.Touch(g);
        foreach (var c in g.Children) Doc.Touch(c);
    }

    public void LayOut(Node f)
    {
        var row = f.Layout == LayoutMode.Row;
        var pos = f.Padding;
        var cross = 0.0;
        foreach (var c in f.Children)
        {
            if (c.Hidden) continue;
            cross = Math.Max(cross, row ? c.H : c.W);
        }
        if (f.Hug) cross = Math.Max(cross, 0);
        var inner = f.Hug ? cross : (row ? f.H : f.W) - f.Padding * 2;
        if (!f.Hug && f.MainAlign != Align.Start)
        {
            // Centre or push the run along the main axis.
            var run = 0.0;
            var count = 0;
            foreach (var c in f.Children)
            {
                if (c.Hidden) continue;
                run += row ? c.W : c.H;
                count++;
            }
            run += Math.Max(0, count - 1) * f.Gap;
            var room = (row ? f.W : f.H) - f.Padding * 2 - run;
            pos += f.MainAlign == Align.Center ? room / 2 : room;
        }
        foreach (var c in f.Children)
        {
            if (c.Hidden) continue;
            var size = row ? c.W : c.H;
            var across = row ? c.H : c.W;
            var off = f.CrossAlign switch
            {
                Align.Center => (inner - across) / 2,
                Align.End => inner - across,
                _ => 0,
            };
            var nx = row ? pos : f.Padding + off;
            var ny = row ? f.Padding + off : pos;
            if (c.X != nx || c.Y != ny || c.Rotation != 0)
            {
                c.X = nx;
                c.Y = ny;
                c.Rotation = 0;
                Doc.Touch(c);
            }
            pos += size + f.Gap;
        }
        if (f.Hug)
        {
            var main = Math.Max(pos - f.Gap + f.Padding, f.Padding * 2);
            var w = row ? main : cross + f.Padding * 2;
            var h = row ? cross + f.Padding * 2 : main;
            if (f.W != w || f.H != h)
            {
                f.W = w;
                f.H = h;
            }
        }
        Doc.Touch(f);
    }

    // ── text ──────────────────────────────────────────────────────────────

    /// <summary>Sizes a text layer to its content.</summary>
    public void Fit(Node t)
    {
        if (t.Kind != NodeKind.Text) return;
        var (w, lines) = Measure(t.Font, t.FontSize, t.Weight, t.Tracking, t.Text.Length == 0 ? " " : t.Text);
        var nw = Math.Ceiling(w);
        var nh = Math.Round(lines * t.FontSize * t.LineHeight, 2);
        if (nw == t.W && nh == t.H) return;
        // Keep the anchored edge in place for centred and right-aligned text.
        if (t.TextAlign == Align.Center) t.X += (t.W - nw) / 2;
        else if (t.TextAlign == Align.End) t.X += t.W - nw;
        t.W = nw;
        t.H = nh;
        Doc.Touch(t);
    }

    public void FitAllText()
    {
        foreach (var d in Doc.Root.SelfAndDescendants())
            if (d.Kind == NodeKind.Text) Fit(d);
        LayOutAll();
    }

    /// <summary>Runs every auto layout, innermost first, then again once instances have their components' sizes.</summary>
    public void LayOutAll()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            var all = Doc.Root.SelfAndDescendants();
            for (var i = all.Count - 1; i >= 0; i--)
            {
                var n = all[i];
                if (n.Kind == NodeKind.Frame && n.Layout != LayoutMode.None) LayOut(n);
                else if (n.Kind == NodeKind.Group && n != Doc.Root) FitGroup(n);
            }
            SyncInstances();
        }
    }

    /// <summary>Opens a text layer for typing, over the canvas.</summary>
    public void BeginTextEdit(Node t, bool checkpoint = true)
    {
        if (t.Kind != NodeKind.Text || t.Locked) return;
        if (checkpoint) Checkpoint();
        Selection.Clear();
        Selection.Add(t);
        EditingTextId = t.Id;
        Doc.Touch(t);
        LayersRev++;
    }

    public void EditText(string text)
    {
        if (Doc.Find(EditingTextId) is not { } t) return;
        t.Text = text.Replace("\r", "");
        Fit(t);
        Doc.Touch(t);
        if (t.Parent is { } p) Reflow(p);
        SyncInstances();
        Changed?.Invoke();
    }

    /// <summary>Closes the text being typed; an empty text layer goes away.</summary>
    public void EndTextEdit()
    {
        if (Doc.Find(EditingTextId) is not { } t)
        {
            EditingTextId = null;
            return;
        }
        EditingTextId = null;
        if (t.Text.Trim().Length == 0)
        {
            var parent = t.Parent;
            Doc.Remove(t);
            Selection.Remove(t);
            if (parent is not null) Reflow(parent);
        }
        else
        {
            if (t.Name.StartsWith("Text ")) t.Name = t.Text.Split('\n')[0].Length > 28 ? t.Text.Split('\n')[0].Substring(0, 28) + "…" : t.Text.Split('\n')[0];
            Doc.Touch(t);
        }
        Changes(structure: true);
    }

    public string TextOf(Node t, Node? instance) =>
        instance is not null && instance.Overrides.TryGetValue(t.Id, out var o) ? o : t.Text;
}
