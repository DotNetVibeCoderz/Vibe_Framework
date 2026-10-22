using Rustzor.Interop;
using Rustzor.UI;

namespace KamarGelap.Model;

/// <summary>Layer, mask, adjustment, selection and filter commands.</summary>
public sealed partial class Editor
{
    /// <summary>Puts a layer above the active one and makes it active.</summary>
    private void Insert(Layer l)
    {
        var at = Doc.Layers.FindIndex(x => x.Id == Doc.Active);
        Doc.Layers.Insert(at < 0 ? Doc.Layers.Count : at + 1, l);
        Doc.Active = l.Id;
        OnMask = l.Kind == LayerKind.Adjust;
    }

    public void Select(string id)
    {
        if (Doc.Active == id) return;
        Doc.Active = id;
        var l = Doc.ActiveLayer;
        OnMask = l is { Kind: LayerKind.Adjust, HasMask: true };
        SyncMeta();
        SyncTool();
        Changed?.Invoke();
    }

    public void NewLayer()
    {
        var l = new Layer { Id = Doc.NewId(), Name = "Layer " + (Doc.Layers.Count(x => x.Kind == LayerKind.Pixel) + 1), Kind = LayerKind.Pixel };
        Insert(l);
        Js.Send("kg.layer", Js.Args("id", l.Id));
        SyncTool();
        Commit("New layer");
    }

    public void Duplicate()
    {
        var src = Doc.ActiveLayer;
        if (src is null) return;
        var l = src.Copy();
        l.Id = Doc.NewId();
        l.Name = src.Name + " copy";
        Insert(l);
        Js.Send("kg.layer", Js.Args("id", l.Id, "from", src.Id));
        SyncTool();
        Commit("Duplicate layer");
    }

    public void Delete()
    {
        var l = Doc.ActiveLayer;
        if (l is null) return;
        if (Doc.Layers.Count == 1)
        {
            Toasts.Warning("A picture needs at least one layer.");
            return;
        }
        var i = Doc.Layers.IndexOf(l);
        Doc.Layers.RemoveAt(i);
        Doc.Active = Doc.Layers[Math.Max(0, i - 1)].Id;
        OnMask = false;
        // The pixels stay with the history, so undo brings the layer back.
        SyncTool();
        Commit("Delete layer");
    }

    public void MoveLayer(int by)
    {
        var l = Doc.ActiveLayer;
        if (l is null) return;
        var i = Doc.Layers.IndexOf(l);
        var j = Math.Clamp(i + by, 0, Doc.Layers.Count - 1);
        if (i == j) return;
        Doc.Layers.RemoveAt(i);
        Doc.Layers.Insert(j, l);
        Commit(by > 0 ? "Bring forward" : "Send backward");
    }

    /// <summary>Drops a dragged layer onto another layer's place in the stack.</summary>
    public void Reorder(string id, string targetId)
    {
        var l = Doc.Layers.Find(x => x.Id == id);
        var t = Doc.Layers.Find(x => x.Id == targetId);
        if (l is null || t is null || l == t) return;
        var to = Doc.Layers.IndexOf(t);
        Doc.Layers.Remove(l);
        Doc.Layers.Insert(to, l);
        Commit("Reorder layers");
    }

    public void ToggleVisible(string id)
    {
        var l = Doc.Layers.Find(x => x.Id == id);
        if (l is null) return;
        l.Visible = !l.Visible;
        Commit(l.Visible ? "Show layer" : "Hide layer");
    }

    public void ToggleLock(string id)
    {
        var l = Doc.Layers.Find(x => x.Id == id);
        if (l is null) return;
        l.Locked = !l.Locked;
        Commit(l.Locked ? "Lock layer" : "Unlock layer");
    }

    public void Rename(string id, string name)
    {
        var l = Doc.Layers.Find(x => x.Id == id);
        name = name.Trim();
        if (l is null || name.Length == 0 || name == l.Name) return;
        l.Name = name;
        Commit("Rename layer");
    }

    /// <summary>Live while a slider moves; <see cref="Commit"/> when it is let go.</summary>
    public void SetOpacityLive(double v)
    {
        if (Doc.ActiveLayer is not { } l) return;
        l.Opacity = Math.Clamp(Math.Round(v), 0, 100);
        SyncMeta();
        Changed?.Invoke();
    }

    public void SetBlend(string mode)
    {
        if (Doc.ActiveLayer is not { } l || l.Blend == mode) return;
        l.Blend = mode;
        Commit("Blend: " + Layer.BlendLabel(mode));
    }

    // ── masks ─────────────────────────────────────────────────────────────

    public void AddMask(bool fromSelection)
    {
        if (Doc.ActiveLayer is not { } l) return;
        if (l.HasMask)
        {
            Toasts.Info("This layer already has a mask.");
            return;
        }
        var mode = fromSelection && Doc.Sel is not null ? "selection" : "reveal";
        Js.Send("kg.mask", Js.Args("id", l.Id, "mode", mode));
        l.HasMask = true;
        l.MaskOn = true;
        OnMask = true;
        SyncTool();
        Commit(mode == "selection" ? "Mask from selection" : "Add layer mask");
    }

    public void ToggleMask()
    {
        if (Doc.ActiveLayer is not { HasMask: true } l) return;
        l.MaskOn = !l.MaskOn;
        Commit(l.MaskOn ? "Enable mask" : "Disable mask");
    }

    public void RemoveMask(bool apply)
    {
        if (Doc.ActiveLayer is not { HasMask: true } l) return;
        if (apply && l.Kind != LayerKind.Pixel)
        {
            Toasts.Warning("Only a pixel layer's mask can be applied to it.");
            return;
        }
        Js.Send("kg.mask", Js.Args("id", l.Id, "mode", apply ? "apply" : "remove"));
        l.HasMask = false;
        OnMask = false;
        SyncTool();
        Commit(apply ? "Apply mask" : "Delete mask");
    }

    // ── adjustment layers ─────────────────────────────────────────────────

    public void AddAdjustment(AdjustKind kind)
    {
        var n = Doc.Layers.Count(x => x.Kind == LayerKind.Adjust && x.Adjust == kind) + 1;
        var l = new Layer { Id = Doc.NewId(), Name = Layer.AdjustLabel(kind) + " " + n, Kind = LayerKind.Adjust, Adjust = kind, P = Layer.AdjustDefaults(kind), HasMask = true };
        Insert(l);
        Js.Send("kg.layer", Js.Args("id", l.Id));
        Js.Send("kg.mask", Js.Args("id", l.Id, "mode", Doc.Sel is not null ? "selection" : "reveal"));
        SyncTool();
        Commit("New " + Layer.AdjustLabel(kind));
    }

    public void SetParamLive(int i, double v)
    {
        if (Doc.ActiveLayer is not { Kind: LayerKind.Adjust } l) return;
        l.P[i] = v;
        SyncMeta();
        Changed?.Invoke();
    }

    public void ResetAdjustment()
    {
        if (Doc.ActiveLayer is not { Kind: LayerKind.Adjust } l) return;
        l.P = Layer.AdjustDefaults(l.Adjust);
        Commit("Reset " + Layer.AdjustLabel(l.Adjust));
    }

    // ── text ──────────────────────────────────────────────────────────────

    public async Task AddText(double x, double y)
    {
        var l = new Layer
        {
            Id = Doc.NewId(), Name = "Caption", Kind = LayerKind.Text, Text = "Kaliurang, 05.42", Font = "DM Serif Display", Italic = true,
            Size = Math.Max(24, Math.Round(Doc.H / 18.0)), Color = Fg, TX = Math.Round(x), TY = Math.Round(y),
        };
        Insert(l);
        Js.Send("kg.layer", Js.Args("id", l.Id));
        OnMask = false;
        await RenderText(l);
        SyncTool();
        Commit("New text layer");
    }

    public async Task RenderText(Layer l)
    {
        var box = await Js.CallAsync("kg.text", w =>
        {
            w.BeginObject().Prop("id", l.Id).Name("t").BeginObject().Prop("text", l.Text).Prop("font", l.Font).Prop("size", l.Size)
                .Prop("weight", l.Weight).Prop("italic", l.Italic).Prop("color", l.Color).Prop("x", l.TX).Prop("y", l.TY).Prop("align", l.Align)
                .EndObject().EndObject();
        });
        l.BW = box["w"].AsDouble();
        l.BH = box["h"].AsDouble();
        SyncMeta();
        Changed?.Invoke();
    }

    public async Task EditText(Action<Layer> change, string label)
    {
        if (Doc.ActiveLayer is not { Kind: LayerKind.Text } l) return;
        change(l);
        await RenderText(l);
        Commit(label);
    }

    public void Rasterize()
    {
        if (Doc.ActiveLayer is not { Kind: LayerKind.Text } l) return;
        l.Kind = LayerKind.Pixel;
        Commit("Rasterize type");
    }

    // ── selection ─────────────────────────────────────────────────────────

    public void SelectAll()
    {
        Doc.Sel = new Sel { Kind = "rect", X = 0, Y = 0, W = Doc.W, H = Doc.H };
        Commit("Select all");
    }

    public void Deselect()
    {
        if (Doc.Sel is null) return;
        Doc.Sel = null;
        Commit("Deselect");
    }

    public void InvertSelection()
    {
        if (Doc.Sel is null) return;
        Doc.Sel.Inverted = !Doc.Sel.Inverted;
        Commit("Inverse");
    }

    /// <summary>Fills the selection (or the whole layer) with the foreground or background colour, or clears it.</summary>
    public void Fill(string what)
    {
        if (!PixelTarget(what == "clear" ? "Clear" : "Fill")) return;
        Js.Send("kg.fill", Js.Args("id", Doc.Active, "what", what));
        Commit(what == "clear" ? "Clear" : what == "bg" ? "Fill with background" : "Fill with foreground");
    }

    /// <summary>Whether the active layer takes pixel edits, saying why not when it does not.</summary>
    private bool PixelTarget(string action)
    {
        var l = Doc.ActiveLayer;
        if (l is null) return false;
        if (l.Kind != LayerKind.Pixel)
        {
            Toasts.Warning(action + " works on a pixel layer. " + (l.Kind == LayerKind.Text ? "Rasterize the text first." : "Pick a pixel layer."));
            return false;
        }
        if (l.Locked)
        {
            Toasts.Warning("This layer is locked.");
            return false;
        }
        if (!l.Visible)
        {
            Toasts.Warning("This layer is hidden.");
            return false;
        }
        return true;
    }

    // ── filters, with a live preview over the current step ────────────────

    public bool CanFilter => Doc.ActiveLayer is { Kind: LayerKind.Pixel, Locked: false, Visible: true };

    public JsonValue PreviewFilter(string kind, double radius, double amount, bool mono)
    {
        Js.Send("kg.restore", Js.Args("i", Index));
        return Js.Call("kg.filter", w => w.BeginObject().Prop("id", Doc.Active).Prop("kind", kind)
            .Name("p").BeginObject().Prop("radius", radius).Prop("amount", amount).Prop("mono", mono).EndObject().EndObject());
    }

    public void CancelPreview() => Js.Send("kg.restore", Js.Args("i", Index));

    public void ApplyFilter(string label) => Commit(label);

    // ── crop ──────────────────────────────────────────────────────────────

    public void CropToSelection()
    {
        if (Doc.Sel is null)
        {
            Toasts.Info("Make a selection first, or use the crop tool.");
            return;
        }
        var b = Doc.Sel.Bounds();
        CropTo(b.X, b.Y, b.W, b.H);
    }

    public void ApplyCrop()
    {
        if (Crop is not { } c) return;
        Crop = null;
        CropTo(c.X, c.Y, c.W, c.H);
    }

    public void CancelCrop()
    {
        Crop = null;
        Changed?.Invoke();
    }

    private void CropTo(double x, double y, double w, double h)
    {
        var x0 = (int)Math.Max(0, Math.Floor(x));
        var y0 = (int)Math.Max(0, Math.Floor(y));
        var x1 = (int)Math.Min(Doc.W, Math.Ceiling(x + w));
        var y1 = (int)Math.Min(Doc.H, Math.Ceiling(y + h));
        if (x1 - x0 < 2 || y1 - y0 < 2) return;
        Js.Send("kg.crop", wr =>
        {
            wr.BeginObject().Prop("x", x0).Prop("y", y0).Prop("w", x1 - x0).Prop("h", y1 - y0).Name("offsets").BeginObject();
            foreach (var l in Doc.Layers) wr.Name(l.Id).BeginObject().Prop("x", l.X).Prop("y", l.Y).EndObject();
            wr.EndObject().EndObject();
        });
        foreach (var l in Doc.Layers)
        {
            if (l.Kind == LayerKind.Text)
            {
                l.TX += l.X - x0;
                l.TY += l.Y - y0;
            }
            l.X = 0;
            l.Y = 0;
        }
        Doc.W = x1 - x0;
        Doc.H = y1 - y0;
        Doc.Sel = null;
        Commit("Crop to " + Doc.W + " × " + Doc.H);
    }
}
