using RustNet.UI.Frontends;
using SkiaSharp;

namespace RustNet.UI.Skia;

/// <summary>
/// Draws a <see cref="UiSession"/>'s device-exact frame onto any Skia canvas —
/// a GPU surface in <see cref="SkiaUiWindow"/>, or a raster surface for
/// screenshots and tests — scaled by the session's zoom with nearest-neighbour
/// sampling so device pixels stay square and sharp.
/// </summary>
public sealed class SkiaUiRenderer : IDisposable
{
    private readonly UiSession _session;
    private SKBitmap? _frame;

    public SkiaUiRenderer(UiSession session)
    {
        _session = session;
    }

    /// <summary>Refresh the cached bitmap from the session's framebuffer (call after a render).</summary>
    public void Update()
    {
        var fb = _session.Canvas;
        if (_frame == null || _frame.Width != fb.Width || _frame.Height != fb.Height)
        {
            _frame?.Dispose();
            _frame = new SKBitmap(new SKImageInfo(fb.Width, fb.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        }
        uint[] argb = fb.ToBgra32();
        unsafe
        {
            fixed (uint* src = argb)
            {
                Buffer.MemoryCopy(src, (void*)_frame.GetPixels(), (long)_frame.ByteCount, (long)argb.Length * 4);
            }
        }
        _frame.NotifyPixelsChanged();
    }

    /// <summary>Paint the frame at the session's zoom onto <paramref name="canvas"/>.</summary>
    public void Draw(SKCanvas canvas)
    {
        if (_frame == null)
        {
            Update();
        }
        int z = Math.Max(1, _session.Zoom);
        var dest = SKRect.Create(0, 0, _frame!.Width * z, _frame.Height * z);
        using var image = SKImage.FromBitmap(_frame);
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
    }

    /// <summary>Render the frame at the session's zoom into a new raster image (screenshots, tests).</summary>
    public SKBitmap Snapshot()
    {
        Update();
        int z = Math.Max(1, _session.Zoom);
        var bmp = new SKBitmap(new SKImageInfo(_session.Width * z, _session.Height * z, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);
        Draw(canvas);
        canvas.Flush();
        return bmp;
    }

    public void Dispose() => _frame?.Dispose();
}
