namespace RustNet.UI.Frontends;

/// <summary>
/// Everything a desktop frontend does that is not toolkit-specific: render the
/// tree through RustNet.UI's own paint code into a <see cref="FramebufferCanvas"/>,
/// map window coordinates (at any integer zoom) back to device pixels, and route
/// clicks, wheel scrolls and keys through <see cref="Ui.Tap"/> /
/// <see cref="Ui.Scroll"/> so behaviour matches the device.
/// </summary>
public sealed class UiSession
{
    public UiElement Root { get; private set; }
    public FramebufferCanvas Canvas { get; }

    /// <summary>Window pixels per device pixel.</summary>
    public int Zoom { get; set; } = 2;

    /// <summary>Called after a click or key changed something; the frame is already re-rendered.</summary>
    public Action<UiElement>? Changed;

    /// <summary>Called whenever a new frame is ready to show.</summary>
    public Action? FrameReady;

    public UiSession(UiElement root)
    {
        Root = root;
        Canvas = new FramebufferCanvas(Width, Height);
    }

    public static UiSession FromXml(string xml) => new(Ui.LoadXml(xml));

    public int Width => Root.Width > 0 ? Root.Width : 160;
    public int Height => Root.Height > 0 ? Root.Height : 128;

    /// <summary>Replace the tree (e.g. after reloading the layout file) and render it.</summary>
    public void Load(UiElement root)
    {
        Root = root;
        Render();
    }

    /// <summary>Paint the tree with the device's paint code into <see cref="Canvas"/>.</summary>
    public void Render()
    {
        // Paint.Target is process-wide; frontends run one UI thread, so set it
        // for the duration of this render only.
        var previous = Paint.Target;
        Paint.Target = Canvas;
        try
        {
            if (Canvas.Width != Width || Canvas.Height != Height)
            {
                Canvas.Init(Width, Height);
            }
            Root.Arrange(0, 0, Width, Height);
            Paint.Clear(Root.Background);
            Root.Render();
            Paint.Present();
        }
        finally
        {
            Paint.Target = previous;
        }
        FrameReady?.Invoke();
    }

    /// <summary>A click at window coordinates; returns the element it hit, if any.</summary>
    public UiElement? Click(int windowX, int windowY)
    {
        int x = windowX / Math.Max(1, Zoom);
        int y = windowY / Math.Max(1, Zoom);
        Root.Arrange(0, 0, Width, Height);
        UiElement? hit = Ui.Tap(Root, x, y);
        Render();
        if (hit != null)
        {
            Changed?.Invoke(hit);
        }
        return hit;
    }

    /// <summary>A wheel scroll over window coordinates: scrolls the scroll viewer under the pointer.</summary>
    public void Wheel(int windowX, int windowY, int notches)
    {
        int x = windowX / Math.Max(1, Zoom);
        int y = windowY / Math.Max(1, Zoom);
        Root.Arrange(0, 0, Width, Height);
        for (UiElement? e = Root.HitTest(x, y); e != null; e = Parent(Root, e))
        {
            if (e.Kind == "scrollviewer" && e.Id.Length > 0)
            {
                Ui.Scroll(Root, e.Id, -notches * 16);
                Render();
                Changed?.Invoke(e);
                return;
            }
        }
    }

    /// <summary>Text typed while a textbox is the last clicked element.</summary>
    public void Type(UiElement textbox, string text)
    {
        if (textbox.Kind != "textbox")
        {
            return;
        }
        foreach (char c in text)
        {
            if (c == '\b')
            {
                if (textbox.Text.Length > 0)
                {
                    textbox.Text = textbox.Text[..^1];
                }
            }
            else if (c >= ' ')
            {
                textbox.Text += c;
            }
        }
        Render();
        Changed?.Invoke(textbox);
    }

    private static UiElement? Parent(UiElement root, UiElement child)
    {
        foreach (var c in root.Children)
        {
            if (ReferenceEquals(c, child))
            {
                return root;
            }
            var p = Parent(c, child);
            if (p != null)
            {
                return p;
            }
        }
        return null;
    }
}
