namespace RustNet.UI.AsciiConsole;

/// <summary>
/// Runs a RustNet.UI layout interactively in a terminal. Tab / Shift+Tab move
/// focus between controls, Enter or Space activates the focused one (routed
/// through <see cref="Ui.Tap"/>, so behaviour matches a tap on the device),
/// the arrow keys move sliders and list selections, printable keys type into
/// a focused textbox, and Escape quits.
/// </summary>
public sealed class AsciiConsoleHost
{
    private readonly UiElement _root;
    private readonly AsciiRenderer _renderer;
    private readonly List<UiElement> _focusable = new();
    private int _focus;

    /// <summary>Called after a control was activated or changed by the keyboard.</summary>
    public Action<UiElement>? Changed;

    public AsciiConsoleHost(UiElement root, AsciiRenderer? renderer = null)
    {
        _root = root;
        _renderer = renderer ?? new AsciiRenderer();
        Collect(root);
        _renderer.Focused = _focusable.Count > 0 ? _focusable[0] : null;
    }

    /// <summary>Controls that take keyboard focus, in document order.</summary>
    public IReadOnlyList<UiElement> Focusable => _focusable;

    public UiElement? Focused => _renderer.Focused;

    private void Collect(UiElement e)
    {
        if (e.Kind is "button" or "checkbox" or "radio" or "slider" or "listbox" or "combobox"
            or "textbox" or "expander" or "tabcontrol" or "datagrid")
        {
            _focusable.Add(e);
        }
        foreach (var c in e.Children)
        {
            Collect(c);
        }
    }

    /// <summary>The current frame as text.</summary>
    public string Frame() => _renderer.Render(_root);

    /// <summary>Apply one key; returns false when the key asks to quit.</summary>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            return false;
        }
        if (_focusable.Count == 0)
        {
            return true;
        }
        if (key.Key == ConsoleKey.Tab)
        {
            int step = key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1;
            _focus = (_focus + step + _focusable.Count) % _focusable.Count;
            _renderer.Focused = _focusable[_focus];
            return true;
        }
        var e = _focusable[_focus];
        bool changed = false;
        switch (key.Key)
        {
            case ConsoleKey.Enter:
            case ConsoleKey.Spacebar when e.Kind != "textbox":
                Tap(e);
                changed = true;
                break;
            case ConsoleKey.LeftArrow when e.Kind == "slider":
            case ConsoleKey.RightArrow when e.Kind == "slider":
            {
                int span = Math.Max(1, e.Max - e.Min);
                int step = Math.Max(1, span / 20) * (key.Key == ConsoleKey.LeftArrow ? -1 : 1);
                e.Value = Math.Clamp(e.Value + step, e.Min, e.Max);
                changed = true;
                break;
            }
            case ConsoleKey.UpArrow when e.Items.Count > 0:
            case ConsoleKey.DownArrow when e.Items.Count > 0:
            {
                int dir = key.Key == ConsoleKey.UpArrow ? -1 : 1;
                int min = e.Kind == "datagrid" ? 1 : 0; // a datagrid's first row is its header
                e.Selected = Math.Clamp((e.Selected < min ? min - dir : e.Selected) + dir, min, e.Items.Count - 1);
                changed = true;
                break;
            }
            case ConsoleKey.LeftArrow when e.Kind == "tabcontrol":
            case ConsoleKey.RightArrow when e.Kind == "tabcontrol":
            {
                int dir = key.Key == ConsoleKey.LeftArrow ? -1 : 1;
                int n = Math.Max(1, e.Children.Count);
                e.Selected = ((e.Selected < 0 ? 0 : e.Selected) + dir + n) % n;
                changed = true;
                break;
            }
            case ConsoleKey.Backspace when e.Kind == "textbox":
                if (e.Text.Length > 0)
                {
                    e.Text = e.Text[..^1];
                    changed = true;
                }
                break;
            default:
                if (e.Kind == "textbox" && key.KeyChar >= ' ')
                {
                    e.Text += key.KeyChar;
                    changed = true;
                }
                break;
        }
        if (changed)
        {
            Changed?.Invoke(e);
        }
        return true;
    }

    /// <summary>Activate a control the way a tap on its centre would.</summary>
    public void Tap(UiElement e)
    {
        _root.Arrange(0, 0, _root.Width > 0 ? _root.Width : 160, _root.Height > 0 ? _root.Height : 128);
        int x = e.LayoutX + Math.Min(e.LayoutW - 1, 4);
        int y = e.LayoutY + Math.Min(e.LayoutH - 1, 4);
        if (e.Kind == "slider")
        {
            // Activating a slider leaves it where it is; arrows move it.
            return;
        }
        Ui.Tap(_root, x, y);
    }

    /// <summary>Interactive loop on the real console until Escape.</summary>
    public void Run()
    {
        bool cursor = true;
        try
        {
            cursor = Console.CursorVisible;
            Console.CursorVisible = false;
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
        try
        {
            while (true)
            {
                Console.Clear();
                Console.Write(Frame());
                Console.Write("Tab: focus  Enter/Space: activate  arrows: adjust  Esc: quit");
                if (!HandleKey(Console.ReadKey(intercept: true)))
                {
                    return;
                }
            }
        }
        finally
        {
            try
            {
                Console.CursorVisible = cursor;
            }
            catch (IOException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
    }
}
