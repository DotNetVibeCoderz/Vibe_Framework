using Cairo;
using Gtk;
using RustNet.UI.Frontends;

namespace RustNet.UI.Gtk;

/// <summary>
/// A GTK 3 widget showing a RustNet.UI tree exactly as the device draws it,
/// scaled by the session's integer zoom with nearest-neighbour filtering.
/// Clicks become <c>Ui.Tap</c>, the wheel scrolls scroll viewers, typing goes
/// to the last clicked textbox.
/// </summary>
public class UiView : DrawingArea
{
    private readonly UiSession _session;
    private UiElement? _focus;

    /// <summary>Raised after a click or key changed an element.</summary>
    public event Action<UiElement>? ElementChanged;

    public UiView(UiSession session)
    {
        _session = session;
        _session.FrameReady = QueueDraw;
        _session.Changed = e => ElementChanged?.Invoke(e);
        CanFocus = true;
        AddEvents((int)(Gdk.EventMask.ButtonPressMask | Gdk.EventMask.ScrollMask | Gdk.EventMask.KeyPressMask));
        SetSizeRequest(session.Width * session.Zoom, session.Height * session.Zoom);
        _session.Render();
    }

    public UiSession Session => _session;

    /// <summary>The framebuffer as a Cairo image surface (device resolution).</summary>
    public static ImageSurface ToSurface(FramebufferCanvas canvas)
    {
        var surface = new ImageSurface(Format.Rgb24, canvas.Width, canvas.Height);
        uint[] argb = canvas.ToBgra32();
        surface.Flush();
        unsafe
        {
            byte* dst = (byte*)surface.DataPtr;
            for (int y = 0; y < canvas.Height; y++)
            {
                uint* row = (uint*)(dst + y * surface.Stride);
                for (int x = 0; x < canvas.Width; x++)
                {
                    row[x] = argb[y * canvas.Width + x];
                }
            }
        }
        surface.MarkDirty();
        return surface;
    }

    protected override bool OnDrawn(Context cr)
    {
        using var surface = ToSurface(_session.Canvas);
        int z = Math.Max(1, _session.Zoom);
        cr.Scale(z, z);
        cr.SetSourceSurface(surface, 0, 0);
        using (var pattern = (SurfacePattern)cr.GetSource())
        {
            pattern.Filter = Filter.Nearest;
        }
        cr.Paint();
        return true;
    }

    protected override bool OnButtonPressEvent(Gdk.EventButton evnt)
    {
        GrabFocus();
        if (evnt.Button == 1)
        {
            _focus = _session.Click((int)evnt.X, (int)evnt.Y);
        }
        return true;
    }

    protected override bool OnScrollEvent(Gdk.EventScroll evnt)
    {
        int notches = evnt.Direction switch
        {
            Gdk.ScrollDirection.Up => 1,
            Gdk.ScrollDirection.Down => -1,
            _ => (int)Math.Round(-evnt.DeltaY),
        };
        _session.Wheel((int)evnt.X, (int)evnt.Y, notches);
        return true;
    }

    protected override bool OnKeyPressEvent(Gdk.EventKey evnt)
    {
        if (_focus is not { Kind: "textbox" })
        {
            return base.OnKeyPressEvent(evnt);
        }
        if (evnt.Key == Gdk.Key.BackSpace)
        {
            _session.Type(_focus, "\b");
            return true;
        }
        uint unicode = Gdk.Keyval.ToUnicode(evnt.KeyValue);
        if (unicode >= 32)
        {
            _session.Type(_focus, char.ConvertFromUtf32((int)unicode));
            return true;
        }
        return base.OnKeyPressEvent(evnt);
    }
}

/// <summary>A GTK window holding one <see cref="UiView"/>.</summary>
public class UiWindow : Window
{
    public UiView View { get; }

    public UiWindow(string xml, int zoom = 2, string title = "RustNet.UI") : base(title)
    {
        var session = UiSession.FromXml(xml);
        session.Zoom = zoom;
        View = new UiView(session);
        Resizable = false;
        Add(View);
        DeleteEvent += (_, _) => Application.Quit();
        KeyPressEvent += (_, e) =>
        {
            if (e.Event.Key == Gdk.Key.Escape)
            {
                Application.Quit();
            }
        };
    }
}
