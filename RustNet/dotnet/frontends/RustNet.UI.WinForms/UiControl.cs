using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RustNet.UI.Frontends;

namespace RustNet.UI.WinForms;

/// <summary>
/// A Windows Forms control showing a RustNet.UI tree exactly as the device
/// draws it, scaled by an integer <see cref="Zoom"/> with nearest-neighbour
/// sampling (so pixels stay crisp). Clicks become <c>Ui.Tap</c>, the mouse
/// wheel scrolls scroll viewers, and typing goes to the last clicked textbox.
/// </summary>
public class UiControl : Control
{
    private UiSession? _session;
    private Bitmap? _frame;
    private UiElement? _focus;

    public UiControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    /// <summary>Raised after a click or key changed an element.</summary>
    public event Action<UiElement>? ElementChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public UiSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            if (_session != null)
            {
                _session.FrameReady = OnFrame;
                _session.Changed = e => ElementChanged?.Invoke(e);
                _session.Zoom = Zoom;
                _session.Render();
            }
        }
    }

    private int _zoom = 2;

    /// <summary>Screen pixels per device pixel.</summary>
    [DefaultValue(2)]
    public int Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Max(1, value);
            if (_session != null)
            {
                _session.Zoom = _zoom;
                OnFrame();
            }
        }
    }

    /// <summary>Show a layout from XML.</summary>
    public void LoadXml(string xml) => Session = UiSession.FromXml(xml);

    /// <summary>The preferred client size for the current layout and zoom.</summary>
    public Size FrameSize => _session == null ? Size.Empty : new Size(_session.Width * _zoom, _session.Height * _zoom);

    /// <summary>The current frame at device resolution (32bpp).</summary>
    public Bitmap? Frame => _frame;

    private void OnFrame()
    {
        if (_session == null)
        {
            return;
        }
        _frame?.Dispose();
        _frame = ToBitmap(_session.Canvas);
        Invalidate();
    }

    /// <summary>Copy a framebuffer into a 32bpp bitmap.</summary>
    public static Bitmap ToBitmap(FramebufferCanvas canvas)
    {
        var bmp = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, canvas.Width, canvas.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            uint[] argb = canvas.ToBgra32();
            for (int y = 0; y < canvas.Height; y++)
            {
                Marshal.Copy((int[])(object)argb, y * canvas.Width, data.Scan0 + y * data.Stride, canvas.Width);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_frame == null)
        {
            return;
        }
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_frame, new Rectangle(0, 0, _frame.Width * _zoom, _frame.Height * _zoom));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _focus = _session?.Click(e.X, e.Y);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _session?.Wheel(e.X, e.Y, e.Delta / 120);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_session != null && _focus is { Kind: "textbox" })
        {
            _session.Type(_focus, e.KeyChar.ToString());
            e.Handled = true;
        }
    }
}

/// <summary>A window holding one <see cref="UiControl"/>, sized to the layout.</summary>
public class UiForm : Form
{
    public UiControl View { get; } = new() { Dock = DockStyle.Fill };

    public UiForm(string xml, int zoom = 2)
    {
        Text = "RustNet.UI";
        View.Zoom = zoom;
        View.LoadXml(xml);
        ClientSize = View.FrameSize;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Controls.Add(View);
    }
}
