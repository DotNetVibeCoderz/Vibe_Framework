using RustNet.UI.Frontends;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;

namespace RustNet.UI.Skia;

/// <summary>
/// A Silk.NET window that shows a RustNet.UI layout through SkiaSharp on an
/// OpenGL surface. Clicks become <c>Ui.Tap</c>, the wheel scrolls scroll
/// viewers, typing goes to the last clicked textbox, Escape closes.
/// </summary>
public sealed class SkiaUiWindow : IDisposable
{
    private readonly UiSession _session;
    private readonly IWindow _window;
    private SkiaUiRenderer? _renderer;
    private GRContext? _gr;
    private GRBackendRenderTarget? _target;
    private SKSurface? _surface;
    private UiElement? _focus;
    private bool _dirty = true;

    /// <summary>Raised after a click or key changed an element.</summary>
    public Action<UiElement>? ElementChanged;

    public SkiaUiWindow(UiSession session, string title = "RustNet.UI")
    {
        _session = session;
        var options = WindowOptions.Default with
        {
            Title = title,
            Size = new Vector2D<int>(session.Width * session.Zoom, session.Height * session.Zoom),
            WindowBorder = WindowBorder.Fixed,
            VSync = true,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
        };
        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += _ => ResetSurface();
        _session.FrameReady = () => _dirty = true;
        _session.Changed = e => ElementChanged?.Invoke(e);
    }

    public static SkiaUiWindow FromXml(string xml, int zoom = 2, string title = "RustNet.UI")
    {
        var session = UiSession.FromXml(xml);
        session.Zoom = zoom;
        return new SkiaUiWindow(session, title);
    }

    /// <summary>Open the window and run its loop until it is closed.</summary>
    public void Run() => _window.Run();

    private void OnLoad()
    {
        var gl = GL.GetApi(_window);
        _gr = GRContext.CreateGl(GRGlInterface.Create(name => _window.GLContext!.TryGetProcAddress(name, out var p) ? p : 0));
        _renderer = new SkiaUiRenderer(_session);
        _session.Render();
        ResetSurface();
        _ = gl;

        var input = _window.CreateInput();
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (m, button) =>
            {
                if (button == MouseButton.Left)
                {
                    _focus = _session.Click((int)m.Position.X, (int)m.Position.Y);
                }
            };
            mouse.Scroll += (m, wheel) => _session.Wheel((int)m.Position.X, (int)m.Position.Y, (int)Math.Round(wheel.Y));
        }
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyChar += (_, c) =>
            {
                if (_focus is { Kind: "textbox" })
                {
                    _session.Type(_focus, c.ToString());
                }
            };
            keyboard.KeyDown += (_, key, _) =>
            {
                if (key == Key.Escape)
                {
                    _window.Close();
                }
                else if (key == Key.Backspace && _focus is { Kind: "textbox" })
                {
                    _session.Type(_focus, "\b");
                }
            };
        }
    }

    private void ResetSurface()
    {
        if (_gr == null)
        {
            return;
        }
        _surface?.Dispose();
        _target?.Dispose();
        var size = _window.FramebufferSize;
        _target = new GRBackendRenderTarget(size.X, size.Y, 0, 8, new GRGlFramebufferInfo(0, 0x8058 /* GL_RGBA8 */));
        _surface = SKSurface.Create(_gr, _target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        _dirty = true;
    }

    private void OnRender(double _)
    {
        if (_surface == null || _renderer == null || !_dirty)
        {
            return;
        }
        _renderer.Update();
        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Black);
        _renderer.Draw(canvas);
        canvas.Flush();
        _gr!.Flush();
        _dirty = false;
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _target?.Dispose();
        _gr?.Dispose();
        _renderer?.Dispose();
        _window.Dispose();
    }
}
