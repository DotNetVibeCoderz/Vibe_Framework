namespace KamarGelap.Model;

/// <summary>Keyboard shortcuts, the ones Photoshop users already have in their hands.</summary>
public sealed partial class Editor
{
    /// <summary>Shortcuts the page handles itself: files, dialogs.</summary>
    public event Action<string>? Command;

    public void KeyDown(string key, string code, bool ctrl, bool shift, bool alt)
    {
        if (ctrl)
        {
            switch (code)
            {
                case "KeyZ" when shift: Redo(); return;
                case "KeyZ": Undo(); return;
                case "KeyY": Redo(); return;
                case "KeyA": SelectAll(); return;
                case "KeyD": Deselect(); return;
                case "KeyI" when shift: InvertSelection(); return;
                case "KeyJ": Duplicate(); return;
                case "KeyN" when shift: NewLayer(); return;
                case "KeyS" when shift: Command?.Invoke("export"); return;
                case "KeyS": Command?.Invoke("save"); return;
                case "KeyO": Command?.Invoke("open"); return;
                case "BracketRight": MoveLayer(1); return;
                case "BracketLeft": MoveLayer(-1); return;
                case "Backspace": Fill("bg"); return;
            }
            return;
        }
        if (alt && (key == "Backspace" || key == "Delete"))
        {
            Fill("fg");
            return;
        }
        switch (key)
        {
            case "Backspace":
            case "Delete":
                Fill("clear");
                return;
            case "Enter":
                if (Crop is not null) ApplyCrop();
                return;
            case "Escape":
                if (Crop is not null) CancelCrop();
                return;
            case "[":
                Size = Math.Max(1, Math.Round(Size / 1.15));
                SyncTool();
                Changed?.Invoke();
                return;
            case "]":
                Size = Math.Min(800, Math.Round(Size * 1.15 + 1));
                SyncTool();
                Changed?.Invoke();
                return;
        }
        if (key.Length == 1 && key[0] >= '0' && key[0] <= '9' && Tool is Tool.Brush or Tool.Eraser or Tool.Clone or Tool.Gradient or Tool.Bucket)
        {
            // 1 is 10%, 0 is 100%, as in Photoshop.
            Opacity = key[0] == '0' ? 100 : (key[0] - '0') * 10;
            SyncTool();
            Changed?.Invoke();
            return;
        }
        switch (code)
        {
            case "KeyV": SetTool(Tool.Move); break;
            case "KeyM": SetTool(shift || Tool == Tool.Marquee ? (Tool == Tool.Marquee ? Tool.Ellipse : Tool.Marquee) : Tool.Marquee); break;
            case "KeyL": SetTool(Tool.Lasso); break;
            case "KeyC": SetTool(Tool.Crop); break;
            case "KeyB": SetTool(Tool.Brush); break;
            case "KeyE": SetTool(Tool.Eraser); break;
            case "KeyS": SetTool(Tool.Clone); break;
            case "KeyG": SetTool(Tool.Gradient); break;
            case "KeyK": SetTool(Tool.Bucket); break;
            case "KeyI": SetTool(Tool.Eyedropper); break;
            case "KeyT": SetTool(Tool.Text); break;
            case "KeyH": SetTool(Tool.Hand); break;
            case "KeyX": SwapColors(); break;
            case "KeyD": DefaultColors(); break;
            case "KeyQ": SetPaintTarget(!OnMask); break;
        }
    }
}
