using Rustzor.Interop;
using Rustzor.UI;

namespace KamarGelap.Model;

/// <summary>The tools whose work is geometry: move, selections, crop, bucket, text.</summary>
public sealed partial class Editor
{
    private double _sx, _sy;
    private int _ox, _oy;
    private bool _dragging;

    /// <summary>Raised while a selection, lasso or crop is being dragged: only the overlay draws again.</summary>
    public event Action? OverlayChanged;

    /// <summary>Raised when the text tool picks a text layer, so the page can focus its field.</summary>
    public event Action? EditTextRequested;

    private void Pointer(string phase, double x, double y, bool shift, bool alt)
    {
        if (phase == "cancel")
        {
            _dragging = false;
            Drafting = null;
            OverlayChanged?.Invoke();
            return;
        }
        switch (Tool)
        {
            case Tool.Move: MoveTool(phase, x, y, shift); break;
            case Tool.Marquee:
            case Tool.Ellipse: Marquee(phase, x, y, shift, alt); break;
            case Tool.Lasso: Lasso(phase, x, y); break;
            case Tool.Crop: CropTool(phase, x, y, shift); break;
            case Tool.Bucket when phase == "down": Bucket(x, y); break;
            case Tool.Text when phase == "down": _ = TextTool(x, y); break;
        }
    }

    private void MoveTool(string phase, double x, double y, bool shift)
    {
        var l = Doc.ActiveLayer;
        if (phase == "down")
        {
            if (l is null) return;
            if (l.Kind == LayerKind.Adjust)
            {
                Toasts.Info("An adjustment layer covers the whole picture; there is nothing to move.");
                return;
            }
            if (l.Locked)
            {
                Toasts.Warning("This layer is locked.");
                return;
            }
            _dragging = true;
            _sx = x;
            _sy = y;
            _ox = l.X;
            _oy = l.Y;
            return;
        }
        if (!_dragging || l is null) return;
        var dx = x - _sx;
        var dy = y - _sy;
        if (shift)
        {
            if (Math.Abs(dx) > Math.Abs(dy)) dy = 0;
            else dx = 0;
        }
        l.X = _ox + (int)Math.Round(dx);
        l.Y = _oy + (int)Math.Round(dy);
        SyncMeta();
        if (phase == "up")
        {
            _dragging = false;
            if (l.X != _ox || l.Y != _oy) Commit("Move");
            return;
        }
        OverlayChanged?.Invoke();
    }

    private void Marquee(string phase, double x, double y, bool shift, bool alt)
    {
        if (phase == "down")
        {
            _dragging = true;
            _sx = x;
            _sy = y;
            Drafting = new Sel { Kind = Tool == Tool.Ellipse ? "ellipse" : "rect", X = x, Y = y };
            OverlayChanged?.Invoke();
            return;
        }
        if (!_dragging || Drafting is null) return;
        var w = x - _sx;
        var h = y - _sy;
        if (shift)
        {
            var s = Math.Max(Math.Abs(w), Math.Abs(h));
            w = Math.Sign(w == 0 ? 1 : w) * s;
            h = Math.Sign(h == 0 ? 1 : h) * s;
        }
        // Alt draws from the centre, as in Photoshop.
        var x0 = alt ? _sx - Math.Abs(w) : Math.Min(_sx, _sx + w);
        var y0 = alt ? _sy - Math.Abs(h) : Math.Min(_sy, _sy + h);
        var ww = alt ? Math.Abs(w) * 2 : Math.Abs(w);
        var hh = alt ? Math.Abs(h) * 2 : Math.Abs(h);
        Drafting.X = Math.Round(x0);
        Drafting.Y = Math.Round(y0);
        Drafting.W = Math.Round(ww);
        Drafting.H = Math.Round(hh);
        if (phase == "up")
        {
            _dragging = false;
            var d = Drafting;
            Drafting = null;
            if (d.W < 2 || d.H < 2)
            {
                if (Doc.Sel is not null) Deselect();
                else OverlayChanged?.Invoke();
                return;
            }
            Doc.Sel = d;
            Commit(d.Kind == "ellipse" ? "Elliptical marquee" : "Rectangular marquee");
            return;
        }
        OverlayChanged?.Invoke();
    }

    private void Lasso(string phase, double x, double y)
    {
        if (phase == "down")
        {
            _dragging = true;
            Drafting = new Sel { Kind = "lasso" };
            Drafting.Pts.Add(Math.Round(x, 1));
            Drafting.Pts.Add(Math.Round(y, 1));
            OverlayChanged?.Invoke();
            return;
        }
        if (!_dragging || Drafting is null) return;
        var n = Drafting.Pts.Count;
        if (Math.Abs(x - Drafting.Pts[n - 2]) + Math.Abs(y - Drafting.Pts[n - 1]) >= 2.5 / Math.Max(Zoom, 0.05))
        {
            Drafting.Pts.Add(Math.Round(x, 1));
            Drafting.Pts.Add(Math.Round(y, 1));
        }
        if (phase == "up")
        {
            _dragging = false;
            var d = Drafting;
            Drafting = null;
            if (d.Pts.Count < 6)
            {
                if (Doc.Sel is not null) Deselect();
                else OverlayChanged?.Invoke();
                return;
            }
            Doc.Sel = d;
            Commit("Lasso");
            return;
        }
        OverlayChanged?.Invoke();
    }

    private void CropTool(string phase, double x, double y, bool shift)
    {
        if (phase == "down")
        {
            _dragging = true;
            _sx = Math.Clamp(x, 0, Doc.W);
            _sy = Math.Clamp(y, 0, Doc.H);
            Crop = (_sx, _sy, 0, 0);
            OverlayChanged?.Invoke();
            return;
        }
        if (!_dragging) return;
        x = Math.Clamp(x, 0, Doc.W);
        y = Math.Clamp(y, 0, Doc.H);
        var w = x - _sx;
        var h = y - _sy;
        if (shift)
        {
            // Keep the picture's proportions.
            var r = (double)Doc.W / Doc.H;
            if (Math.Abs(w) / r > Math.Abs(h)) h = Math.Sign(h == 0 ? 1 : h) * Math.Abs(w) / r;
            else w = Math.Sign(w == 0 ? 1 : w) * Math.Abs(h) * r;
        }
        Crop = (Math.Round(Math.Min(_sx, _sx + w)), Math.Round(Math.Min(_sy, _sy + h)), Math.Round(Math.Abs(w)), Math.Round(Math.Abs(h)));
        if (phase == "up")
        {
            _dragging = false;
            if (Crop is { W: < 4 } or { H: < 4 }) Crop = null;
            Changed?.Invoke();
            return;
        }
        OverlayChanged?.Invoke();
    }

    private void Bucket(double x, double y)
    {
        if (!PixelTarget("The paint bucket")) return;
        if (Js.Call("kg.bucket", Js.Args("id", Doc.Active, "x", x, "y", y)).AsBool()) Commit("Paint bucket");
    }

    private async Task TextTool(double x, double y)
    {
        // A click on a text layer edits it; anywhere else starts a new one.
        for (var i = Doc.Layers.Count - 1; i >= 0; i--)
        {
            var l = Doc.Layers[i];
            if (l.Kind != LayerKind.Text || !l.Visible) continue;
            var lx = l.TX + l.X - (l.Align == "center" ? l.BW / 2 : l.Align == "right" ? l.BW : 0);
            var ly = l.TY + l.Y;
            if (x >= lx - 6 && x <= lx + l.BW + 6 && y >= ly - 6 && y <= ly + l.BH + 6)
            {
                Select(l.Id);
                EditTextRequested?.Invoke();
                return;
            }
        }
        await AddText(x, y);
        EditTextRequested?.Invoke();
    }
}
