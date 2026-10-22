namespace Rupa.Model;

/// <summary>Keyboard shortcuts, the ones Figma users already know.</summary>
public sealed partial class Editor
{
    /// <summary>Raised for shortcuts the page handles itself (opening and saving files).</summary>
    public event Action<string>? Command;

    public void KeyDown(string key, string code, bool ctrl, bool shift, bool alt)
    {
        if (key == " ")
        {
            SetSpace(true);
            return;
        }
        if (ctrl)
        {
            switch (code)
            {
                case "KeyZ" when shift: Redo(); return;
                case "KeyZ": Undo(); return;
                case "KeyY": Redo(); return;
                case "KeyG" when shift: Ungroup(); return;
                case "KeyG": Group(); return;
                case "KeyD": Duplicate(); return;
                case "KeyC": CopyToClipboard(); return;
                case "KeyX": Cut(); return;
                case "KeyV": Paste(); return;
                case "KeyA": SelectAll(); Notify(); return;
                case "KeyK" when alt: MakeComponent(); return;
                case "KeyH" when shift: foreach (var n in Selection.ToArray()) ToggleHidden(n); return;
                case "KeyL" when shift: foreach (var n in Selection.ToArray()) ToggleLocked(n); return;
                case "KeyS": Command?.Invoke("save"); return;
                case "KeyO": Command?.Invoke("open"); return;
                case "KeyE" when shift: Command?.Invoke("export"); return;
                case "BracketRight": Arrange(1, shift); return;
                case "BracketLeft": Arrange(-1, shift); return;
                case "Equal": ZoomAt(1.25, ViewW / 2, ViewH / 2); Notify(); return;
                case "Minus": ZoomAt(0.8, ViewW / 2, ViewH / 2); Notify(); return;
                case "Digit0": ZoomAt(1 / Zoom, ViewW / 2, ViewH / 2); Notify(); return;
            }
            return;
        }
        if (shift)
        {
            switch (code)
            {
                case "Digit1": ZoomToFit(); Notify(); return;
                case "Digit2": ZoomToSelection(); Notify(); return;
                case "Digit0": ZoomAt(1 / Zoom, ViewW / 2, ViewH / 2); Notify(); return;
            }
        }
        switch (key)
        {
            case "Escape":
                if (Pen.Count > 0) { FinishPen(false); return; }
                if (_g != Gesture.None) { CancelGesture(); Notify(); return; }
                if (Tool != Tool.Move) { Tool = Tool.Move; Notify(); return; }
                var parent = Single?.Parent;
                if (parent is not null && parent != Doc.Root) Select(parent);
                else Select(null);
                Notify();
                return;
            case "Enter":
                if (Pen.Count > 0) { FinishPen(false); return; }
                if (Single is { Kind: NodeKind.Text } t) { BeginTextEdit(t); Notify(); return; }
                if (Single is { IsContainer: true } c && c.Children.Count > 0)
                {
                    Selection.Clear();
                    foreach (var k in c.Children)
                        if (!k.Hidden && !k.Locked) Selection.Add(k);
                    LayersRev++;
                    Notify();
                }
                return;
            case "Delete":
            case "Backspace":
                Delete();
                return;
            case "ArrowLeft": Nudge(shift ? -10 : -1, 0); return;
            case "ArrowRight": Nudge(shift ? 10 : 1, 0); return;
            case "ArrowUp": Nudge(0, shift ? -10 : -1); return;
            case "ArrowDown": Nudge(0, shift ? 10 : 1); return;
        }
        var tool = code switch
        {
            "KeyV" => Tool.Move,
            "KeyF" or "KeyA" => Tool.Frame,
            "KeyR" => Tool.Rect,
            "KeyO" => Tool.Ellipse,
            "KeyL" => Tool.Line,
            "KeyP" => Tool.Pen,
            "KeyT" => Tool.Text,
            "KeyH" => Tool.Hand,
            _ => (Tool?)null,
        };
        if (tool is { } next && !shift && !alt)
        {
            if (Pen.Count > 0 && next != Tool.Pen) FinishPen(false);
            Tool = next;
            Hover = null;
            Cursor = next == Tool.Move ? "default" : next == Tool.Hand ? "grab" : next == Tool.Text ? "text" : "crosshair";
            Notify();
        }
    }

    public void KeyUp(string key)
    {
        if (key == " ")
        {
            SetSpace(false);
            Notify();
        }
    }
}
