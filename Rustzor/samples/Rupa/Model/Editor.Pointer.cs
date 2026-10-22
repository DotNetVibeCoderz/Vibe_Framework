namespace Rupa.Model;

public enum Handle { None, NW, N, NE, E, SE, S, SW, W, Rotate }

/// <summary>Pointer gestures on the canvas: picking, moving with snapping, resizing, turning, drawing, panning.</summary>
public sealed partial class Editor
{
    private enum Gesture { None, Pan, Marquee, Move, Resize, Rotate, Create }

    private sealed class Orig
    {
        public Node N = null!;
        public double X, Y, W, H, Rot, FontSize;
        public List<Pt>? Points;
        public List<Orig>? Kids;
    }

    private Gesture _g;
    private Pt _downScreen, _downWorld;
    private bool _moved;
    private readonly List<Orig> _orig = new();
    private Box _origBox;
    private Handle _handle;
    private Node? _creating;
    private Node? _marqueeIn;
    private Node? _clickTarget;
    private bool _marqueeAdd;
    private readonly List<Node> _marqueeBase = new();
    private double _rotStart;
    private bool _space;
    private bool _checkpointed;

    /// <summary>The rubber-band rectangle while selecting by area, in screen pixels.</summary>
    public Box? Marquee;

    /// <summary>Snapping lines and spacing while dragging, in screen pixels.</summary>
    public readonly List<Guide> Guides = new();

    /// <summary>The points of the path being drawn with the pen, on the canvas.</summary>
    public readonly List<Pt> Pen = new();
    public Pt PenCursor;

    /// <summary>Where a dragged layer would land in an auto-layout frame, in screen pixels.</summary>
    public (Pt A, Pt B)? DropLine;

    public bool Panning => _g == Gesture.Pan;
    public bool SpaceHeld => _space;
    public bool Dragging => _g != Gesture.None && _moved;

    /// <summary>Layers are being moved: their handles hide until the move ends.</summary>
    public bool Moving => _g == Gesture.Move && _moved;

    public string Cursor { get; private set; } = "default";

    private double Tol => 4 / Zoom;

    // ── picking ───────────────────────────────────────────────────────────

    /// <summary>The layers under a canvas point, outermost first, down to the one actually hit.</summary>
    public List<Node>? PathAt(Pt w)
    {
        var path = new List<Node>();
        for (var i = Doc.Root.Children.Count - 1; i >= 0; i--)
            if (Find(Doc.Root.Children[i], w, path)) return path;
        return null;
    }

    private bool Find(Node n, Pt w, List<Node> path)
    {
        if (n.Hidden) return false;
        if (n.IsContainer)
        {
            var insideBox = G.HitShape(n, w, 0);
            if (n.Kind == NodeKind.Frame && n.Clip && !insideBox) return false;
            if (!n.Locked)
                for (var i = n.Children.Count - 1; i >= 0; i--)
                    if (Find(n.Children[i], w, path))
                    {
                        path.Insert(0, n);
                        return true;
                    }
            if (n.Kind == NodeKind.Frame && insideBox && !n.Locked)
            {
                path.Insert(0, n);
                return true;
            }
            return false;
        }
        if (n.Locked || !G.HitShape(n, w, Tol)) return false;
        path.Insert(0, n);
        return true;
    }

    /// <summary>What a click at a canvas point selects, the way Figma decides it.</summary>
    public Node? Pick(Pt w, bool deep)
    {
        var path = PathAt(w);
        if (path is null || path.Count == 0) return null;
        if (deep) return path[^1];
        var ctx = Context;
        if (ctx is not null)
        {
            var i = path.IndexOf(ctx);
            if (i >= 0 && i + 1 < path.Count) return path[i + 1];
        }
        // Children of top-level frames are picked directly; the frame itself only on its background.
        if (Doc.IsTop(path[0]) && path[0].Kind == NodeKind.Frame && path.Count > 1) return path[1];
        return path[0];
    }

    /// <summary>The innermost frame under a point that new layers should go into.</summary>
    public Node FrameAt(Pt w)
    {
        var path = PathAt(w);
        Node target = Doc.Root;
        if (path is null) return target;
        foreach (var n in path)
        {
            if (n.Kind == NodeKind.Instance) break;
            if (n.Kind == NodeKind.Frame) target = n;
        }
        return target;
    }

    /// <summary>A top-level frame's name label above it, in screen pixels.</summary>
    public Box LabelBox(Node frame)
    {
        var p = ToScreen(new Pt(frame.X, frame.Y));
        return new Box(p.X, p.Y - 22, Math.Max(40, Math.Min(frame.W * Zoom, 240)), 20);
    }

    private Node? LabelAt(double sx, double sy)
    {
        for (var i = Doc.Root.Children.Count - 1; i >= 0; i--)
        {
            var n = Doc.Root.Children[i];
            if (n.Kind is NodeKind.Frame && !n.Hidden && LabelBox(n).Contains(new Pt(sx, sy))) return n.Locked ? null : n;
        }
        return null;
    }

    // ── handles ───────────────────────────────────────────────────────────

    /// <summary>Where the selection's handles are: a layer's own (turned) box, or the bounds of several.</summary>
    public (Affine M, double W, double H)? HandleFrame()
    {
        if (Selection.Count == 0) return null;
        if (Selection.Count == 1)
        {
            var n = Selection[0];
            return (G.World(n), n.W, n.H);
        }
        var b = SelectionBounds()!.Value;
        return (Affine.Translate(b.X, b.Y), b.W, b.H);
    }

    public static Pt HandlePoint(Handle h, double w, double hgt) => h switch
    {
        Handle.NW => new Pt(0, 0),
        Handle.N => new Pt(w / 2, 0),
        Handle.NE => new Pt(w, 0),
        Handle.E => new Pt(w, hgt / 2),
        Handle.SE => new Pt(w, hgt),
        Handle.S => new Pt(w / 2, hgt),
        Handle.SW => new Pt(0, hgt),
        Handle.W => new Pt(0, hgt / 2),
        _ => new Pt(w / 2, hgt / 2),
    };

    public static readonly Handle[] Corners = { Handle.NW, Handle.NE, Handle.SE, Handle.SW };
    public static readonly Handle[] AllHandles = { Handle.NW, Handle.N, Handle.NE, Handle.E, Handle.SE, Handle.S, Handle.SW, Handle.W };

    public bool IsLineSelection => Single is { Kind: NodeKind.Line };

    /// <summary>Which handles the selection shows: lines have two ends, instances only turn and move.</summary>
    public Handle[] VisibleHandles()
    {
        if (Single is { Kind: NodeKind.Line }) return new[] { Handle.W, Handle.E };
        if (Single is { Kind: NodeKind.Instance }) return Array.Empty<Handle>();
        if (Single is { Kind: NodeKind.Text }) return Corners;
        return AllHandles;
    }

    private Handle HandleAt(double sx, double sy)
    {
        if (HandleFrame() is not { } f || Selection.Exists(n => n.Locked)) return Handle.None;
        var m = f.M;
        var sp = new Pt(sx, sy);
        foreach (var h in VisibleHandles())
        {
            var p = ToScreen(m.Apply(HandlePoint(h, f.W, f.H)));
            if (Math.Abs(p.X - sx) <= 6 && Math.Abs(p.Y - sy) <= 6) return h;
        }
        if (Selection.Count == 1 && Single!.Kind != NodeKind.Line)
        {
            // Just outside a corner turns the layer.
            var local = m.Inverse().Apply(ToWorld(sx, sy));
            var outside = local.X < 0 || local.Y < 0 || local.X > f.W || local.Y > f.H;
            if (outside)
                foreach (var h in Corners)
                {
                    var p = ToScreen(m.Apply(HandlePoint(h, f.W, f.H)));
                    var d = Math.Sqrt((p.X - sp.X) * (p.X - sp.X) + (p.Y - sp.Y) * (p.Y - sp.Y));
                    if (d <= 20) return Handle.Rotate;
                }
        }
        return Handle.None;
    }

    private static string ResizeCursor(Handle h, double rotation)
    {
        var angle = h switch
        {
            Handle.N or Handle.S => 90,
            Handle.E or Handle.W => 0,
            Handle.NE or Handle.SW => 45,
            _ => 135,
        };
        var a = ((angle + rotation) % 180 + 180) % 180;
        return a < 22.5 || a >= 157.5 ? "ew-resize" : a < 67.5 ? "nesw-resize" : a < 112.5 ? "ns-resize" : "nwse-resize";
    }

    // ── pointer events (screen pixels relative to the canvas) ─────────────

    public void PointerDown(double sx, double sy, int button, bool shift, bool ctrl, bool alt, int clicks)
    {
        _downScreen = new Pt(sx, sy);
        _downWorld = ToWorld(sx, sy);
        _moved = false;
        _checkpointed = false;
        _cands = null;
        Guides.Clear();
        DropLine = null;
        if (EditingTextId is not null) EndTextEdit();

        if (button == 1 || button == 0 && (_space || Tool == Tool.Hand))
        {
            _g = Gesture.Pan;
            Cursor = "grabbing";
            return;
        }
        if (button != 0) return;

        if (clicks >= 2 && Tool == Tool.Move)
        {
            DoubleClick(_downWorld);
            _g = Gesture.None;
            return;
        }

        switch (Tool)
        {
            case Tool.Pen:
                PenClick(_downWorld, clicks);
                return;
            case Tool.Frame:
            case Tool.Rect:
            case Tool.Ellipse:
            case Tool.Text:
            case Tool.Line:
                StartCreate(shift);
                return;
        }

        var handle = HandleAt(sx, sy);
        if (handle == Handle.Rotate)
        {
            StartRotate();
            return;
        }
        if (handle != Handle.None)
        {
            StartResize(handle);
            return;
        }

        if (LabelAt(sx, sy) is { } labelled)
        {
            if (!Selection.Contains(labelled)) Select(labelled, shift);
            StartMove();
            return;
        }

        var target = Pick(_downWorld, ctrl);
        if (target is not null && Doc.IsTop(target) && target.Kind == NodeKind.Frame && !Selection.Contains(target) && !ctrl)
        {
            // A top-level frame's background: a click selects the frame, a drag selects inside it.
            _clickTarget = target;
            StartMarquee(target, shift);
            return;
        }
        if (target is not null)
        {
            if (shift) Select(target, add: true);
            else if (!Selection.Contains(target)) Select(target);
            if (Selection.Contains(target)) StartMove();
            return;
        }
        _clickTarget = null;
        StartMarquee(Context ?? Doc.Root, shift);
    }

    public void PointerMove(double sx, double sy, bool pressed, bool shift, bool alt)
    {
        var w = ToWorld(sx, sy);
        if (!pressed || _g == Gesture.None)
        {
            if (_g != Gesture.None && !pressed) PointerUp(sx, sy, shift);
            UpdateHover(sx, sy, w);
            return;
        }
        if (!_moved && Math.Abs(sx - _downScreen.X) + Math.Abs(sy - _downScreen.Y) < 3) return;
        _moved = true;
        switch (_g)
        {
            case Gesture.Pan:
                PanX += sx - _downScreen.X;
                PanY += sy - _downScreen.Y;
                _downScreen = new Pt(sx, sy);
                break;
            case Gesture.Marquee:
                DoMarquee(sx, sy);
                break;
            case Gesture.Move:
                DoMove(w, shift);
                break;
            case Gesture.Resize:
                DoResize(w, shift, alt);
                break;
            case Gesture.Rotate:
                DoRotate(w, shift);
                break;
            case Gesture.Create:
                DoCreate(w, shift);
                break;
        }
    }

    public void PointerUp(double sx, double sy, bool shift)
    {
        var g = _g;
        _g = Gesture.None;
        Marquee = null;
        Guides.Clear();
        var line = DropLine;
        DropLine = null;
        switch (g)
        {
            case Gesture.Pan:
                Cursor = _space || Tool == Tool.Hand ? "grab" : "default";
                Notify();
                return;
            case Gesture.Marquee:
                if (!_moved)
                {
                    if (_clickTarget is not null) Select(_clickTarget, shift);
                    else if (!shift) Select(null);
                }
                _clickTarget = null;
                Notify();
                return;
            case Gesture.Move:
                if (_moved) EndMove(ToWorld(sx, sy), line is not null);
                else if (!shift && Selection.Count > 1 && Pick(_downWorld, false) is { } only)
                    Select(only);
                break;
            case Gesture.Create:
                EndCreate();
                break;
            case Gesture.Resize:
            case Gesture.Rotate:
                break;
        }
        _orig.Clear();
        if (_moved || g == Gesture.Create) Changes(structure: g == Gesture.Create || g == Gesture.Move);
        else Notify();
    }

    private void UpdateHover(double sx, double sy, Pt w)
    {
        if (Tool == Tool.Pen)
        {
            PenCursor = w;
            Cursor = "crosshair";
            return;
        }
        if (Tool != Tool.Move)
        {
            Hover = null;
            Cursor = Tool == Tool.Hand || _space ? "grab" : Tool == Tool.Text ? "text" : "crosshair";
            return;
        }
        if (_space)
        {
            Cursor = "grab";
            return;
        }
        var h = HandleAt(sx, sy);
        if (h == Handle.Rotate) Cursor = "alias";
        else if (h != Handle.None) Cursor = ResizeCursor(h, Single?.Rotation ?? 0);
        else Cursor = "default";
        Hover = h == Handle.None ? LabelAt(sx, sy) ?? Pick(w, false) : null;
    }

    private void EnsureCheckpoint()
    {
        if (_checkpointed) return;
        _checkpointed = true;
        Checkpoint();
    }

    // ── marquee ───────────────────────────────────────────────────────────

    private void StartMarquee(Node within, bool add)
    {
        _g = Gesture.Marquee;
        _marqueeIn = within;
        _marqueeAdd = add;
        _marqueeBase.Clear();
        if (add) _marqueeBase.AddRange(Selection);
    }

    private void DoMarquee(double sx, double sy)
    {
        var box = Box.Around(_downScreen, new Pt(sx, sy));
        Marquee = box;
        var a = ToWorld(box.X, box.Y);
        var b = ToWorld(box.Right, box.Bottom);
        var world = Box.Around(a, b);
        Selection.Clear();
        Selection.AddRange(_marqueeBase);
        var within = _marqueeIn ?? Doc.Root;
        foreach (var c in within.Children)
        {
            if (c.Hidden || c.Locked || Selection.Contains(c)) continue;
            if (Selection.Count > 0 && Selection[0].Parent != within) continue;
            var cb = G.WorldBounds(c);
            // Top-level frames are picked only when the band covers them; everything else when it touches.
            var hit = within == Doc.Root && c.Kind == NodeKind.Frame
                ? world.X <= cb.X && world.Y <= cb.Y && world.Right >= cb.Right && world.Bottom >= cb.Bottom
                : world.Intersects(cb);
            if (hit) Selection.Add(c);
        }
        if (_moved) _clickTarget = null;
        LayersRev++;
    }

    // ── move ──────────────────────────────────────────────────────────────

    /// <summary>Raised when layers start moving, with their ids: the page shifts them itself while they move.</summary>
    public Action<List<string>>? DragBegin;

    /// <summary>Raised on every move with the offset in the parent's space.</summary>
    public Action<double, double>? DragOffset;

    /// <summary>Raised when the move ends; true when it was cancelled and the layers go back.</summary>
    public Action<bool>? DragEnd;

    private bool _dragLive;
    private Box _moveBox;

    private void StartMove()
    {
        _g = Gesture.Move;
        _orig.Clear();
        _dragLive = false;
        foreach (var n in Selection)
            if (!n.Locked) _orig.Add(new Orig { N = n, X = n.X, Y = n.Y });
        if (_orig.Count == 0)
        {
            _g = Gesture.None;
            return;
        }
        // The selection's box at the start; it moves by the same offset as its layers.
        _moveBox = G.BoundsInParent(_orig[0].N);
        foreach (var o in _orig) _moveBox = Box.Union(_moveBox, G.BoundsInParent(o.N));
    }

    private void DoMove(Pt w, bool shift)
    {
        EnsureCheckpoint();
        var parent = _orig[0].N.Parent ?? Doc.Root;
        var inv = G.World(parent).Inverse();
        var a = inv.Apply(_downWorld);
        var b = inv.Apply(w);
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (shift)
        {
            if (Math.Abs(dx) > Math.Abs(dy)) dy = 0;
            else dx = 0;
        }
        var inLayout = parent.Kind == NodeKind.Frame && parent.Layout != LayoutMode.None;
        Guides.Clear();
        if (!inLayout)
        {
            var (sxSnap, sySnap) = Snap(parent, Shifted(dx, dy), true, true, true, true);
            if (!shift || dy == 0) dx += sxSnap;
            if (!shift || dx == 0) dy += sySnap;
        }
        dx = Math.Round(dx, 2);
        dy = Math.Round(dy, 2);
        // The model follows the pointer; the views are drawn again once, on release.
        foreach (var o in _orig)
        {
            o.N.X = Math.Round(o.X + dx, 2);
            o.N.Y = Math.Round(o.Y + dy, 2);
        }
        if (!_dragLive)
        {
            _dragLive = true;
            DragBegin?.Invoke(_orig.ConvertAll(o => o.N.Id));
        }
        _dragDx = dx;
        _dragDy = dy;
        DragOffset?.Invoke(dx, dy);
        if (!inLayout) AddGuides(parent, Shifted(dx, dy));
        else ShowDropLine(parent);
    }

    private Box Shifted(double dx, double dy) => new(_moveBox.X + dx, _moveBox.Y + dy, _moveBox.W, _moveBox.H);

    private double _dragDx, _dragDy;

    /// <summary>While layers are dragged, their bounds on the canvas without walking every layer.</summary>
    private Box? DragBounds()
    {
        if (!_dragLive || _orig.Count == 0) return null;
        var parent = _orig[0].N.Parent ?? Doc.Root;
        var b = Shifted(_dragDx, _dragDy);
        return parent == Doc.Root ? b : Box.OfCorners(G.World(parent).Then(Affine.Translate(b.X, b.Y)), b.W, b.H);
    }

    /// <summary>The moved layers' views draw their new places, and the page stops shifting them.</summary>
    private void SettleDrag(bool cancel)
    {
        if (!_dragLive) return;
        _dragLive = false;
        foreach (var o in _orig)
        {
            if (cancel)
            {
                o.N.X = o.X;
                o.N.Y = o.Y;
            }
            Doc.Touch(o.N);
            if (o.N.Parent is { Kind: NodeKind.Group } g) Reflow(g);
        }
        DragEnd?.Invoke(cancel);
    }

    private void ShowDropLine(Node frame)
    {
        var moved = _orig[0].N;
        var row = frame.Layout == LayoutMode.Row;
        var index = DropIndex(frame, moved);
        var others = frame.Children.FindAll(c => !c.Hidden && !_orig.Exists(o => o.N == c));
        double pos;
        if (others.Count == 0) pos = frame.Padding;
        else if (index >= others.Count) pos = (row ? others[^1].X + others[^1].W : others[^1].Y + others[^1].H) + frame.Gap / 2;
        else pos = (row ? others[index].X : others[index].Y) - frame.Gap / 2;
        var m = G.World(frame);
        var a = row ? new Pt(pos, frame.Padding) : new Pt(frame.Padding, pos);
        var b = row ? new Pt(pos, frame.H - frame.Padding) : new Pt(frame.W - frame.Padding, pos);
        DropLine = (ToScreen(m.Apply(a)), ToScreen(m.Apply(b)));
    }

    private int DropIndex(Node frame, Node moved)
    {
        var row = frame.Layout == LayoutMode.Row;
        var centre = row ? moved.X + moved.W / 2 : moved.Y + moved.H / 2;
        var index = 0;
        foreach (var c in frame.Children)
        {
            if (c.Hidden || _orig.Exists(o => o.N == c)) continue;
            var cc = row ? c.X + c.W / 2 : c.Y + c.H / 2;
            if (cc < centre) index++;
        }
        return index;
    }

    private void EndMove(Pt w, bool reorder)
    {
        SettleDrag(cancel: false);
        var parent = _orig[0].N.Parent ?? Doc.Root;
        if (reorder && parent.Layout != LayoutMode.None)
        {
            var moved = _orig.ConvertAll(o => o.N);
            var index = DropIndex(parent, moved[0]);
            foreach (var n in moved) parent.Children.Remove(n);
            var others = parent.Children.FindAll(c => !c.Hidden);
            var at = index >= others.Count ? parent.Children.Count : parent.Children.IndexOf(others[index]);
            parent.Children.InsertRange(at, moved);
            Doc.TouchChildren(parent);
            LayOut(parent);
            Reflow(parent);
            return;
        }
        // Dropped into a top-level frame, or dragged out of one onto the page.
        if (parent != Doc.Root && !Doc.IsTop(parent)) return;
        Node target = Doc.Root;
        for (var i = Doc.Root.Children.Count - 1; i >= 0; i--)
        {
            var f = Doc.Root.Children[i];
            if (f.Kind != NodeKind.Frame || f.Hidden || f.Locked || _orig.Exists(o => o.N == f)) continue;
            if (G.HitShape(f, w, 0))
            {
                target = f;
                break;
            }
        }
        if (target == parent) return;
        if (_orig.Exists(o => o.N.Kind == NodeKind.Frame && Doc.IsTop(o.N) && o.N.Children.Count > 0 && target != Doc.Root && o.N.W * o.N.H > target.W * target.H)) return;
        if (parent != Doc.Root && G.HitShape(parent, w, 0)) return;
        var inv = G.World(target).Inverse();
        foreach (var o in _orig)
        {
            var n = o.N;
            var c = inv.Apply(G.World(n).Apply(n.W / 2, n.H / 2));
            Doc.Remove(n);
            n.X = Math.Round(c.X - n.W / 2, 2);
            n.Y = Math.Round(c.Y - n.H / 2, 2);
            Doc.Add(target, n);
        }
        Reflow(target);
        Reflow(parent);
    }

    // ── snapping ──────────────────────────────────────────────────────────

    // What a gesture can snap to, worked out once when it starts (neighbours do
    // not move while one layer is dragged): the boxes, and every left, centre
    // and right edge (top, middle, bottom) sorted, so a snap is a binary search.
    private List<Box>? _cands;
    private Node? _candParent;
    private double[] _xs = Array.Empty<double>(), _ys = Array.Empty<double>();
    private int[] _xb = Array.Empty<int>(), _yb = Array.Empty<int>();
    // Each edge alone, sorted, for finding the nearest neighbour on a side.
    private double[] _lv = Array.Empty<double>(), _rv = Array.Empty<double>(), _tv = Array.Empty<double>(), _bv = Array.Empty<double>();
    private int[] _li = Array.Empty<int>(), _ri = Array.Empty<int>(), _ti = Array.Empty<int>(), _bi = Array.Empty<int>();

    private static void Sorted(List<Box> list, Func<Box, double> edge, out double[] values, out int[] index)
    {
        var order = new int[list.Count];
        var keys = new double[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            order[i] = i;
            keys[i] = edge(list[i]);
        }
        Array.Sort(keys, order);
        values = keys;
        index = order;
    }

    /// <summary>
    /// How far to shift a moving box (in its parent's space) so its edges or
    /// centre line up with a neighbour's or the frame's.
    /// </summary>
    private (double Dx, double Dy) Snap(Node parent, Box moving, bool left, bool right, bool top, bool bottom)
    {
        Candidates(parent);
        var thr = 5 / Zoom;
        double bestX = thr + 1, bestY = thr + 1;
        if (left) Nearest(_xs, moving.X, ref bestX);
        if (left && right) Nearest(_xs, moving.Cx, ref bestX);
        if (right) Nearest(_xs, moving.Right, ref bestX);
        if (top) Nearest(_ys, moving.Y, ref bestY);
        if (top && bottom) Nearest(_ys, moving.Cy, ref bestY);
        if (bottom) Nearest(_ys, moving.Bottom, ref bestY);
        return (Math.Abs(bestX) <= thr ? bestX : 0, Math.Abs(bestY) <= thr ? bestY : 0);
    }

    /// <summary>The first index whose value is at least <paramref name="v"/>.</summary>
    private static int Lower(double[] a, double v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (a[mid] < v) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static void Nearest(double[] a, double v, ref double best)
    {
        if (a.Length == 0) return;
        var i = Lower(a, v);
        if (i < a.Length && Math.Abs(a[i] - v) < Math.Abs(best)) best = a[i] - v;
        if (i > 0 && Math.Abs(a[i - 1] - v) < Math.Abs(best)) best = a[i - 1] - v;
    }

    private List<Box> Candidates(Node parent)
    {
        if (_cands is not null && _candParent == parent) return _cands;
        var moving = new HashSet<Node>(Selection);
        foreach (var o in _orig) moving.Add(o.N);
        if (_creating is not null) moving.Add(_creating);
        var list = new List<Box>();
        if (parent != Doc.Root) list.Add(new Box(0, 0, parent.W, parent.H));
        foreach (var c in parent.Children)
        {
            if (c.Hidden || moving.Contains(c)) continue;
            list.Add(G.BoundsInParent(c));
        }
        var xs = new List<(double, int)>(list.Count * 3);
        var ys = new List<(double, int)>(list.Count * 3);
        for (var i = 0; i < list.Count; i++)
        {
            var b = list[i];
            xs.Add((b.X, i));
            xs.Add((b.Cx, i));
            xs.Add((b.Right, i));
            ys.Add((b.Y, i));
            ys.Add((b.Cy, i));
            ys.Add((b.Bottom, i));
        }
        xs.Sort((p, q) => p.Item1.CompareTo(q.Item1));
        ys.Sort((p, q) => p.Item1.CompareTo(q.Item1));
        _xs = new double[xs.Count];
        _xb = new int[xs.Count];
        for (var i = 0; i < xs.Count; i++)
        {
            _xs[i] = xs[i].Item1;
            _xb[i] = xs[i].Item2;
        }
        _ys = new double[ys.Count];
        _yb = new int[ys.Count];
        for (var i = 0; i < ys.Count; i++)
        {
            _ys[i] = ys[i].Item1;
            _yb[i] = ys[i].Item2;
        }
        Sorted(list, b => b.X, out _lv, out _li);
        Sorted(list, b => b.Right, out _rv, out _ri);
        Sorted(list, b => b.Y, out _tv, out _ti);
        Sorted(list, b => b.Bottom, out _bv, out _bi);
        _cands = list;
        _candParent = parent;
        return list;
    }

    private void AddGuides(Node parent, Box m)
    {
        var cands = Candidates(parent);
        var pw = G.World(parent);
        Pt S(double x, double y) => ToScreen(pw.Apply(x, y));
        // One line through each edge the moving box lines up with, across every neighbour on it.
        void Lines(double[] values, int[] boxes, double v, bool vertical)
        {
            double lo = vertical ? m.Y : m.X, hi = vertical ? m.Bottom : m.Right;
            var any = false;
            for (var k = Lower(values, v - 0.01); k < values.Length && values[k] <= v + 0.01; k++)
            {
                var cb = cands[boxes[k]];
                lo = Math.Min(lo, vertical ? cb.Y : cb.X);
                hi = Math.Max(hi, vertical ? cb.Bottom : cb.Right);
                any = true;
            }
            if (!any) return;
            var a = vertical ? S(v, lo) : S(lo, v);
            var b = vertical ? S(v, hi) : S(hi, v);
            Guides.Add(new Guide(a.X, a.Y, b.X, b.Y, null));
        }
        Lines(_xs, _xb, m.X, true);
        Lines(_xs, _xb, m.Cx, true);
        Lines(_xs, _xb, m.Right, true);
        Lines(_ys, _yb, m.Y, false);
        Lines(_ys, _yb, m.Cy, false);
        Lines(_ys, _yb, m.Bottom, false);

        // Spacing to the nearest neighbour on each side that overlaps across.
        double l = -1, r = -1, t2 = -1, bt = -1;
        Box lb = default, rb = default, tb = default, bb = default;
        var skip = parent != Doc.Root ? 0 : -1;
        // Walk outwards from the moving box along each side until a neighbour overlaps across.
        for (var k = Lower(_rv, m.X + 1e-9) - 1; k >= 0; k--)
        {
            var cb = cands[_ri[k]];
            if (_ri[k] == skip || !(cb.Y < m.Bottom && cb.Bottom > m.Y)) continue;
            l = m.X - cb.Right;
            lb = cb;
            break;
        }
        for (var k = Lower(_lv, m.Right - 1e-9); k < _lv.Length; k++)
        {
            var cb = cands[_li[k]];
            if (_li[k] == skip || !(cb.Y < m.Bottom && cb.Bottom > m.Y)) continue;
            r = cb.X - m.Right;
            rb = cb;
            break;
        }
        for (var k = Lower(_bv, m.Y + 1e-9) - 1; k >= 0; k--)
        {
            var cb = cands[_bi[k]];
            if (_bi[k] == skip || !(cb.X < m.Right && cb.Right > m.X)) continue;
            t2 = m.Y - cb.Bottom;
            tb = cb;
            break;
        }
        for (var k = Lower(_tv, m.Bottom - 1e-9); k < _tv.Length; k++)
        {
            var cb = cands[_ti[k]];
            if (_ti[k] == skip || !(cb.X < m.Right && cb.Right > m.X)) continue;
            bt = cb.Y - m.Bottom;
            bb = cb;
            break;
        }
        if (parent != Doc.Root)
        {
            // Inside a frame, distances to its edges count when nothing is nearer.
            if (l < 0) { l = m.X; lb = new Box(0, m.Y, 0, m.H); }
            if (r < 0) { r = parent.W - m.Right; rb = new Box(parent.W, m.Y, 0, m.H); }
            if (t2 < 0) { t2 = m.Y; tb = new Box(m.X, 0, m.W, 0); }
            if (bt < 0) { bt = parent.H - m.Bottom; bb = new Box(m.X, parent.H, m.W, 0); }
        }
        void Gap(double x1, double y1, double x2, double y2, double v)
        {
            if (v <= 0.5) return;
            var a = S(x1, y1);
            var b = S(x2, y2);
            Guides.Add(new Guide(a.X, a.Y, b.X, b.Y, G.N(v, 0)));
        }
        if (l >= 0) { var y = Mid(m.Y, m.Bottom, lb.Y, lb.Bottom); Gap(lb.Right, y, m.X, y, l); }
        if (r >= 0) { var y = Mid(m.Y, m.Bottom, rb.Y, rb.Bottom); Gap(m.Right, y, rb.X, y, r); }
        if (t2 >= 0) { var x = Mid(m.X, m.Right, tb.X, tb.Right); Gap(x, tb.Bottom, x, m.Y, t2); }
        if (bt >= 0) { var x = Mid(m.X, m.Right, bb.X, bb.Right); Gap(x, m.Bottom, x, bb.Y, bt); }
    }

    private static double Mid(double a0, double a1, double b0, double b1)
    {
        var lo = Math.Max(a0, b0);
        var hi = Math.Min(a1, b1);
        return hi > lo ? (lo + hi) / 2 : (a0 + a1) / 2;
    }

    // ── resize ────────────────────────────────────────────────────────────

    private void StartResize(Handle h)
    {
        _g = Gesture.Resize;
        _handle = h;
        _orig.Clear();
        foreach (var n in Selection) _orig.Add(Capture(n));
        _origBox = Selection.Count == 1 ? new Box(Selection[0].X, Selection[0].Y, Selection[0].W, Selection[0].H) : BoundsInParentOfSelection();
    }

    private Box BoundsInParentOfSelection()
    {
        var b = G.BoundsInParent(Selection[0]);
        foreach (var n in Selection) b = Box.Union(b, G.BoundsInParent(n));
        return b;
    }

    private static Orig Capture(Node n)
    {
        var o = new Orig { N = n, X = n.X, Y = n.Y, W = n.W, H = n.H, Rot = n.Rotation, FontSize = n.FontSize };
        if (n.Kind == NodeKind.Path) o.Points = new List<Pt>(n.Points);
        if (n.Kind == NodeKind.Group)
        {
            o.Kids = new List<Orig>();
            foreach (var c in n.Children) o.Kids.Add(Capture(c));
        }
        return o;
    }

    private void DoResize(Pt w, bool shift, bool alt)
    {
        EnsureCheckpoint();
        var first = _orig[0].N;
        var parent = first.Parent ?? Doc.Root;
        var p = G.World(parent).Inverse().Apply(w);
        Guides.Clear();
        if (_orig.Count == 1 && first.Kind == NodeKind.Line)
        {
            ResizeLine(_orig[0], p, shift);
            return;
        }
        var o0 = _origBox;
        var rot = _orig.Count == 1 ? _orig[0].Rot : 0;
        var affectsX = _handle is not Handle.N and not Handle.S;
        var affectsY = _handle is not Handle.E and not Handle.W;
        // The opposite handle stays put.
        var anchorLocal = HandlePoint(Opposite(_handle), o0.W, o0.H);
        if (alt) anchorLocal = new Pt(o0.W / 2, o0.H / 2);
        var local0 = Affine.Translate(o0.X, o0.Y).Then(Affine.Rotate(rot, o0.W / 2, o0.H / 2));
        var anchor = local0.Apply(anchorLocal);
        var v = Affine.Rotate(-rot, 0, 0).Apply(p.X - anchor.X, p.Y - anchor.Y);
        var hp = HandlePoint(_handle, o0.W, o0.H);
        var dirX = Math.Sign(hp.X - anchorLocal.X);
        var dirY = Math.Sign(hp.Y - anchorLocal.Y);
        var nw = affectsX ? Math.Max(1, v.X * dirX * (alt ? 2 : 1)) : o0.W;
        var nh = affectsY ? Math.Max(1, v.Y * dirY * (alt ? 2 : 1)) : o0.H;
        var keepAspect = shift || _orig.Count == 1 && first.Kind == NodeKind.Text;
        if (keepAspect && o0.W > 0 && o0.H > 0)
        {
            var s = affectsX && affectsY ? Math.Max(nw / o0.W, nh / o0.H) : affectsX ? nw / o0.W : nh / o0.H;
            nw = Math.Max(1, o0.W * s);
            nh = Math.Max(1, o0.H * s);
        }
        // Snap the moving edges of unturned layers to their neighbours.
        if (rot == 0 && !alt && !keepAspect)
        {
            var ax = anchor.X;
            var ay = anchor.Y;
            var bx = dirX >= 0 ? ax : ax - nw;
            var by = dirY >= 0 ? ay : ay - nh;
            var box = new Box(bx, by, nw, nh);
            var (sdx, sdy) = Snap(parent, box, affectsX && dirX < 0, affectsX && dirX > 0, affectsY && dirY < 0, affectsY && dirY > 0);
            if (affectsX && sdx != 0) nw = Math.Max(1, nw + sdx * dirX);
            if (affectsY && sdy != 0) nh = Math.Max(1, nh + sdy * dirY);
        }
        nw = Math.Round(nw, 2);
        nh = Math.Round(nh, 2);
        // Where the box goes so the anchor stays at the same point.
        var aNew = alt ? new Pt(nw / 2, nh / 2) : new Pt(anchorLocal.X / Math.Max(o0.W, 1e-9) * nw, anchorLocal.Y / Math.Max(o0.H, 1e-9) * nh);
        if (o0.W == 0) aNew.X = 0;
        if (o0.H == 0) aNew.Y = 0;
        var cNew = new Pt(nw / 2, nh / 2);
        var r = Affine.Rotate(rot, 0, 0).Apply(aNew.X - cNew.X, aNew.Y - cNew.Y);
        var nx = anchor.X - cNew.X - r.X;
        var ny = anchor.Y - cNew.Y - r.Y;
        if (_orig.Count == 1)
        {
            Apply(_orig[0], nx, ny, nw, nh);
        }
        else
        {
            var sx = o0.W > 0 ? nw / o0.W : 1;
            var sy = o0.H > 0 ? nh / o0.H : 1;
            foreach (var o in _orig)
                Apply(o, nx + (o.X - o0.X) * sx, ny + (o.Y - o0.Y) * sy, o.W * sx, o.H * sy);
        }
        Reflow(parent);
    }

    /// <summary>Sets a layer's size from the properties panel, scaling what it holds like a resize does.</summary>
    public void SetSize(Node n, double w, double h)
    {
        if (n.Kind == NodeKind.Line)
        {
            var c = new Pt(n.X + n.W / 2, n.Y);
            n.W = Math.Max(1, w);
            n.X = c.X - n.W / 2;
            Doc.Touch(n);
            return;
        }
        // Keep the centre where it was, as Figma does for typed sizes of turned layers.
        var o = Capture(n);
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        var x = n.Rotation == 0 ? n.X : n.X + (n.W - w) / 2;
        var y = n.Rotation == 0 ? n.Y : n.Y + (n.H - h) / 2;
        Apply(o, x, y, w, h);
        if (n.Kind == NodeKind.Text) Fit(n);
        Reflow(n.Parent ?? Doc.Root);
    }

    private static Handle Opposite(Handle h) => h switch
    {
        Handle.NW => Handle.SE,
        Handle.N => Handle.S,
        Handle.NE => Handle.SW,
        Handle.E => Handle.W,
        Handle.SE => Handle.NW,
        Handle.S => Handle.N,
        Handle.SW => Handle.NE,
        _ => Handle.E,
    };

    /// <summary>Puts a layer in a new box, scaling what it holds.</summary>
    private void Apply(Orig o, double x, double y, double w, double h)
    {
        var n = o.N;
        var sx = o.W > 0 ? w / o.W : 1;
        var sy = o.H > 0 ? h / o.H : 1;
        n.X = Math.Round(x, 2);
        n.Y = Math.Round(y, 2);
        switch (n.Kind)
        {
            case NodeKind.Text:
                n.FontSize = Math.Max(1, Math.Round(o.FontSize * sy, 1));
                n.W = Math.Round(w, 2);
                n.H = Math.Round(h, 2);
                break;
            case NodeKind.Path:
                n.W = w;
                n.H = h;
                n.Points.Clear();
                foreach (var p in o.Points!) n.Points.Add(new Pt(p.X * sx, p.Y * sy));
                break;
            case NodeKind.Group:
                n.W = w;
                n.H = h;
                foreach (var k in o.Kids!) Apply(k, k.X * sx, k.Y * sy, k.W * sx, k.H * sy);
                break;
            case NodeKind.Instance:
                break;
            default:
                n.W = w;
                n.H = h;
                break;
        }
        Doc.Touch(n);
        if (n.Kind == NodeKind.Frame && n.Layout != LayoutMode.None) LayOut(n);
    }

    private void ResizeLine(Orig o, Pt p, bool shift)
    {
        var n = o.N;
        var m0 = Affine.Translate(o.X, o.Y).Then(Affine.Rotate(o.Rot, o.W / 2, o.H / 2));
        // The far end stays where it is.
        var fixedEnd = m0.Apply(_handle == Handle.E ? new Pt(0, 0) : new Pt(o.W, 0));
        var a = _handle == Handle.E ? fixedEnd : p;
        var b = _handle == Handle.E ? p : fixedEnd;
        SetLine(n, a, b, shift);
        Doc.Touch(n);
        Reflow(n.Parent ?? Doc.Root);
    }

    /// <summary>A line from a to b (parent space): its length is its width and its angle its rotation.</summary>
    private static void SetLine(Node n, Pt a, Pt b, bool snap45)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (snap45) angle = Math.Round(angle / 45) * 45;
        var r = angle * Math.PI / 180;
        var cx = a.X + Math.Cos(r) * len / 2;
        var cy = a.Y + Math.Sin(r) * len / 2;
        n.W = Math.Round(Math.Max(len, 1), 2);
        n.H = 0;
        n.Rotation = G.Normalize(angle);
        n.X = Math.Round(cx - n.W / 2, 2);
        n.Y = Math.Round(cy, 2);
    }

    // ── rotate ────────────────────────────────────────────────────────────

    private void StartRotate()
    {
        _g = Gesture.Rotate;
        _orig.Clear();
        _orig.Add(Capture(Selection[0]));
        var n = Selection[0];
        var c = G.World(n).Apply(n.W / 2, n.H / 2);
        _rotStart = G.Angle(c, _downWorld);
    }

    private void DoRotate(Pt w, bool shift)
    {
        EnsureCheckpoint();
        var o = _orig[0];
        var n = o.N;
        var c = G.World(n).Apply(n.W / 2, n.H / 2);
        var r = o.Rot + G.Angle(c, w) - _rotStart;
        if (shift) r = Math.Round(r / 15) * 15;
        n.Rotation = G.Normalize(r);
        Doc.Touch(n);
        Reflow(n.Parent ?? Doc.Root);
    }

    // ── drawing ───────────────────────────────────────────────────────────

    private void StartCreate(bool shift)
    {
        _g = Gesture.Create;
        var parent = FrameAt(_downWorld);
        var kind = Tool switch
        {
            Tool.Frame => NodeKind.Frame,
            Tool.Rect => NodeKind.Rect,
            Tool.Ellipse => NodeKind.Ellipse,
            Tool.Line => NodeKind.Line,
            _ => NodeKind.Text,
        };
        var p = G.World(parent).Inverse().Apply(_downWorld);
        var n = new Node
        {
            Id = Doc.NewId(),
            Kind = kind,
            X = Math.Round(p.X),
            Y = Math.Round(p.Y),
            W = 0,
            H = 0,
        };
        n.Name = Node.KindLabel(kind) + " " + n.Id.Substring(1);
        switch (kind)
        {
            case NodeKind.Frame:
                n.Fill = "#ffffff";
                break;
            case NodeKind.Rect:
            case NodeKind.Ellipse:
                n.Fill = "#c9d3ce";
                break;
            case NodeKind.Line:
                n.Stroke = "#1f2328";
                n.StrokeWidth = 2;
                break;
            case NodeKind.Text:
                n.Fill = "#1f2328";
                n.Text = "";
                n.FontSize = 16;
                n.Weight = 500;
                break;
        }
        Checkpoint();
        _checkpointed = true;
        Doc.Add(parent, n);
        _creating = n;
        Selection.Clear();
        Selection.Add(n);
    }

    private void DoCreate(Pt w, bool shift)
    {
        var n = _creating;
        if (n is null) return;
        var parent = n.Parent ?? Doc.Root;
        var inv = G.World(parent).Inverse();
        var a = inv.Apply(_downWorld);
        var b = inv.Apply(w);
        if (n.Kind == NodeKind.Line)
        {
            SetLine(n, a, b, shift);
            Doc.Touch(n);
            return;
        }
        if (n.Kind == NodeKind.Text) return;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (shift)
        {
            var s = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = Math.Sign(dx == 0 ? 1 : dx) * s;
            dy = Math.Sign(dy == 0 ? 1 : dy) * s;
        }
        var box = Box.Around(a, new Pt(a.X + dx, a.Y + dy));
        n.X = Math.Round(box.X, 2);
        n.Y = Math.Round(box.Y, 2);
        n.W = Math.Round(box.W, 2);
        n.H = Math.Round(box.H, 2);
        Guides.Clear();
        AddGuides(parent, new Box(n.X, n.Y, n.W, n.H));
        Doc.Touch(n);
        Reflow(parent);
    }

    private void EndCreate()
    {
        var n = _creating;
        _creating = null;
        Tool = Tool.Move;
        if (n is null) return;
        if (n.Kind == NodeKind.Text)
        {
            Fit(n);
            BeginTextEdit(n, checkpoint: false);
            return;
        }
        if (!_moved || n.W < 2 && n.H < 2)
        {
            if (n.Kind == NodeKind.Line)
            {
                n.W = 120;
                n.Rotation = 0;
            }
            else
            {
                n.W = n.Kind == NodeKind.Frame ? 375 : 100;
                n.H = n.Kind == NodeKind.Frame ? 812 : 100;
            }
            Doc.Touch(n);
        }
        Reflow(n.Parent ?? Doc.Root);
    }

    // ── pen ───────────────────────────────────────────────────────────────

    private void PenClick(Pt w, int clicks)
    {
        _g = Gesture.None;
        if (Pen.Count >= 2)
        {
            var first = ToScreen(Pen[0]);
            var here = ToScreen(w);
            if (Math.Abs(first.X - here.X) < 8 && Math.Abs(first.Y - here.Y) < 8)
            {
                FinishPen(closed: true);
                return;
            }
        }
        if (clicks >= 2)
        {
            FinishPen(closed: false);
            return;
        }
        Pen.Add(w);
        PenCursor = w;
    }

    public void FinishPen(bool closed)
    {
        if (Pen.Count < 2)
        {
            Pen.Clear();
            Tool = Tool.Move;
            Notify();
            return;
        }
        var parent = FrameAt(Pen[0]);
        var inv = G.World(parent).Inverse();
        var pts = Pen.ConvertAll(p => inv.Apply(p));
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in pts)
        {
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X);
            y1 = Math.Max(y1, p.Y);
        }
        Checkpoint();
        var n = new Node
        {
            Id = Doc.NewId(),
            Kind = NodeKind.Path,
            X = Math.Round(x0, 2),
            Y = Math.Round(y0, 2),
            W = Math.Max(1, Math.Round(x1 - x0, 2)),
            H = Math.Max(1, Math.Round(y1 - y0, 2)),
            Stroke = "#1f2328",
            StrokeWidth = 2,
            Fill = closed ? "#c9d3ce" : "none",
            Closed = closed,
        };
        n.Name = "Vector " + n.Id.Substring(1);
        foreach (var p in pts) n.Points.Add(new Pt(Math.Round(p.X - x0, 2), Math.Round(p.Y - y0, 2)));
        Doc.Add(parent, n);
        Pen.Clear();
        Tool = Tool.Move;
        Selection.Clear();
        Selection.Add(n);
        Reflow(parent);
        Changes(structure: true);
    }

    // ── double click ──────────────────────────────────────────────────────

    private void DoubleClick(Pt w)
    {
        var s = Single;
        if (s is { Kind: NodeKind.Text } && G.HitShape(s, w, Tol) && !s.Locked)
        {
            BeginTextEdit(s);
            Notify();
            return;
        }
        // Into a group or nested frame: select the child under the pointer.
        if (s is not null && s.IsContainer && s.Children.Count > 0)
        {
            var path = PathAt(w);
            var i = path?.IndexOf(s) ?? -1;
            if (path is not null && i >= 0 && i + 1 < path.Count)
            {
                Select(path[i + 1]);
                Notify();
                return;
            }
        }
        var t = Pick(w, false);
        if (t is { Kind: NodeKind.Text } && !t.Locked)
        {
            BeginTextEdit(t);
        }
        else if (t is not null) Select(t);
        Notify();
    }

    // ── wheel, pinch and keys ─────────────────────────────────────────────

    public void Wheel(double dx, double dy, double sx, double sy, bool ctrl)
    {
        if (ctrl) ZoomAt(Math.Exp(-dy * 0.01), sx, sy);
        else
        {
            PanX -= dx;
            PanY -= dy;
        }
    }

    public void Pinch(double scale, double cx, double cy, double dx, double dy)
    {
        PanX += dx;
        PanY += dy;
        ZoomAt(scale, cx, cy);
    }

    public void SetSpace(bool down)
    {
        _space = down;
        if (_g == Gesture.None) Cursor = down ? "grab" : "default";
    }

    public void CancelGesture()
    {
        if (_g == Gesture.Move) SettleDrag(cancel: true);
        if (_g == Gesture.Create && _creating is not null)
        {
            Doc.Remove(_creating);
            _creating = null;
            Selection.Clear();
        }
        _g = Gesture.None;
        Marquee = null;
        Guides.Clear();
        DropLine = null;
    }
}
