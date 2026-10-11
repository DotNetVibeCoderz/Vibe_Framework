using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.Browser.Canvas;

/// <summary>
/// A 2D canvas context with batched drawing.
/// </summary>
/// <remarks>
/// Every drawing call is appended to a command buffer; <see cref="Flush"/> sends
/// the whole buffer to the browser in one call. A frame of a game draws
/// hundreds of shapes, and crossing from WebAssembly to JavaScript once per
/// frame instead of once per shape is the difference between 60 frames a second
/// and a slideshow. Calls that must answer — <see cref="MeasureText"/>,
/// <see cref="GetPixels"/> — flush first, so the order of drawing is kept.
/// </remarks>
public sealed class Canvas2D : IDisposable
{
    private JsonWriter _cmds = new();
    private int _count;

    private Canvas2D(int handle, string elementId)
    {
        Handle = handle;
        ElementId = elementId;
        Begin();
    }

    public int Handle { get; private set; }
    public string ElementId { get; }

    /// <summary>The 2D context of a &lt;canvas&gt; captured with <c>@ref</c>.</summary>
    /// <param name="hiDpi">Scale the backing store to the device pixel ratio, so lines are crisp.</param>
    public static Canvas2D FromElement(ElementReference canvas, bool hiDpi = true, bool alpha = true)
    {
        var r = Js.Call("canvas.ctx", Js.Args("el", canvas.Id, "hiDpi", hiDpi, "alpha", alpha));
        var ctx = new Canvas2D(r["h"].AsInt(), canvas.Id) { Width = r["w"].AsDouble(), Height = r["h2"].AsDouble() };
        return ctx;
    }

    /// <summary>The CSS size of the canvas, after any device-pixel scaling.</summary>
    public double Width { get; private set; }
    public double Height { get; private set; }

    /// <summary>Re-reads the element's size and resizes the backing store to match.</summary>
    public void Fit()
    {
        Flush();
        var r = Js.Call("canvas.fit", Js.Args("h", Handle));
        Width = r["w"].AsDouble();
        Height = r["h"].AsDouble();
    }

    private void Begin()
    {
        _cmds = new JsonWriter();
        _cmds.BeginArray();
        _count = 0;
    }

    private JsonWriter Cmd(int code)
    {
        _cmds.BeginArray().Number(code);
        _count++;
        return _cmds;
    }

    /// <summary>Sends everything drawn since the last flush.</summary>
    public void Flush()
    {
        if (_count == 0 || Handle == 0) return;
        _cmds.EndArray();
        var json = _cmds.ToString();
        Begin();
        Js.InvokeVoid("canvas.batch", "{\"h\":" + Handle + ",\"c\":" + json + "}");
    }

    // ── state ────────────────────────────────────────────────────────────

    public Canvas2D Fill(string style) { Cmd(1).String(style).EndArray(); return this; }
    public Canvas2D Stroke(string style) { Cmd(2).String(style).EndArray(); return this; }
    public Canvas2D LineWidth(double w) { Cmd(3).Number(w).EndArray(); return this; }
    public Canvas2D Font(string font) { Cmd(4).String(font).EndArray(); return this; }
    /// <summary>"left", "center", "right", "start", "end".</summary>
    public Canvas2D TextAlign(string align) { Cmd(5).String(align).EndArray(); return this; }
    /// <summary>"top", "middle", "alphabetic", "bottom".</summary>
    public Canvas2D TextBaseline(string baseline) { Cmd(6).String(baseline).EndArray(); return this; }
    public Canvas2D Alpha(double alpha) { Cmd(7).Number(alpha).EndArray(); return this; }
    public Canvas2D LineCap(string cap) { Cmd(8).String(cap).EndArray(); return this; }
    public Canvas2D LineJoin(string join) { Cmd(9).String(join).EndArray(); return this; }
    public Canvas2D Shadow(string color, double blur, double offsetX = 0, double offsetY = 0) { Cmd(10).String(color).Number(blur).Number(offsetX).Number(offsetY).EndArray(); return this; }
    public Canvas2D NoShadow() => Shadow("transparent", 0);
    public Canvas2D CompositeOperation(string op) { Cmd(11).String(op).EndArray(); return this; }
    public Canvas2D LineDash(params double[] segments)
    {
        var w = Cmd(12).BeginArray();
        foreach (var s in segments) w.Number(s);
        w.EndArray().EndArray();
        return this;
    }
    public Canvas2D ImageSmoothing(bool on) { Cmd(13).Bool(on).EndArray(); return this; }
    /// <summary>A CSS filter for what follows, e.g. "blur(4px) grayscale(1)".</summary>
    public Canvas2D Filter(string filter) { Cmd(14).String(filter).EndArray(); return this; }

    /// <summary>A linear gradient as the fill style: stops as (offset, color) pairs.</summary>
    public Canvas2D FillLinearGradient(double x0, double y0, double x1, double y1, params (double Offset, string Color)[] stops)
    {
        var w = Cmd(15).Number(x0).Number(y0).Number(x1).Number(y1).BeginArray();
        foreach (var (o, c) in stops) w.Number(o).String(c);
        w.EndArray().EndArray();
        return this;
    }

    public Canvas2D FillRadialGradient(double x0, double y0, double r0, double x1, double y1, double r1, params (double Offset, string Color)[] stops)
    {
        var w = Cmd(16).Number(x0).Number(y0).Number(r0).Number(x1).Number(y1).Number(r1).BeginArray();
        foreach (var (o, c) in stops) w.Number(o).String(c);
        w.EndArray().EndArray();
        return this;
    }

    // ── rectangles and paths ─────────────────────────────────────────────

    public Canvas2D FillRect(double x, double y, double w, double h) { Cmd(20).Number(x).Number(y).Number(w).Number(h).EndArray(); return this; }
    public Canvas2D StrokeRect(double x, double y, double w, double h) { Cmd(21).Number(x).Number(y).Number(w).Number(h).EndArray(); return this; }
    public Canvas2D ClearRect(double x, double y, double w, double h) { Cmd(22).Number(x).Number(y).Number(w).Number(h).EndArray(); return this; }
    public Canvas2D Clear() => ClearRect(0, 0, Width, Height);
    public Canvas2D BeginPath() { Cmd(23).EndArray(); return this; }
    public Canvas2D ClosePath() { Cmd(24).EndArray(); return this; }
    public Canvas2D MoveTo(double x, double y) { Cmd(25).Number(x).Number(y).EndArray(); return this; }
    public Canvas2D LineTo(double x, double y) { Cmd(26).Number(x).Number(y).EndArray(); return this; }
    public Canvas2D Arc(double x, double y, double r, double start, double end, bool ccw = false) { Cmd(27).Number(x).Number(y).Number(r).Number(start).Number(end).Bool(ccw).EndArray(); return this; }
    public Canvas2D ArcTo(double x1, double y1, double x2, double y2, double r) { Cmd(28).Number(x1).Number(y1).Number(x2).Number(y2).Number(r).EndArray(); return this; }
    public Canvas2D QuadraticCurveTo(double cx, double cy, double x, double y) { Cmd(29).Number(cx).Number(cy).Number(x).Number(y).EndArray(); return this; }
    public Canvas2D BezierCurveTo(double c1x, double c1y, double c2x, double c2y, double x, double y) { Cmd(30).Number(c1x).Number(c1y).Number(c2x).Number(c2y).Number(x).Number(y).EndArray(); return this; }
    public Canvas2D Rect(double x, double y, double w, double h) { Cmd(31).Number(x).Number(y).Number(w).Number(h).EndArray(); return this; }
    public Canvas2D RoundRect(double x, double y, double w, double h, double radius) { Cmd(32).Number(x).Number(y).Number(w).Number(h).Number(radius).EndArray(); return this; }
    public Canvas2D Ellipse(double x, double y, double rx, double ry, double rotation = 0, double start = 0, double end = Math.PI * 2) { Cmd(33).Number(x).Number(y).Number(rx).Number(ry).Number(rotation).Number(start).Number(end).EndArray(); return this; }
    public Canvas2D FillPath() { Cmd(34).EndArray(); return this; }
    public Canvas2D StrokePath() { Cmd(35).EndArray(); return this; }
    public Canvas2D Clip() { Cmd(36).EndArray(); return this; }

    /// <summary>A filled circle.</summary>
    public Canvas2D FillCircle(double x, double y, double r) => BeginPath().Arc(x, y, r, 0, Math.PI * 2).FillPath();

    public Canvas2D StrokeCircle(double x, double y, double r) => BeginPath().Arc(x, y, r, 0, Math.PI * 2).StrokePath();

    public Canvas2D Line(double x1, double y1, double x2, double y2) => BeginPath().MoveTo(x1, y1).LineTo(x2, y2).StrokePath();

    /// <summary>A polyline through the points (x0, y0, x1, y1, ...), in one command.</summary>
    public Canvas2D Polyline(double[] xy, bool close = false, bool fill = false)
    {
        var w = Cmd(37).Bool(close).Bool(fill).BeginArray();
        foreach (var v in xy) w.Number(v);
        w.EndArray().EndArray();
        return this;
    }

    // ── text and images ──────────────────────────────────────────────────

    public Canvas2D FillText(string text, double x, double y) { Cmd(40).String(text).Number(x).Number(y).EndArray(); return this; }
    public Canvas2D StrokeText(string text, double x, double y) { Cmd(41).String(text).Number(x).Number(y).EndArray(); return this; }
    public Canvas2D DrawImage(CanvasImage image, double x, double y) { Cmd(42).Number(image.Handle).Number(x).Number(y).EndArray(); return this; }
    public Canvas2D DrawImage(CanvasImage image, double x, double y, double w, double h) { Cmd(43).Number(image.Handle).Number(x).Number(y).Number(w).Number(h).EndArray(); return this; }
    /// <summary>A region of an image (a sprite from a sheet) into a region of the canvas.</summary>
    public Canvas2D DrawImage(CanvasImage image, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh)
    {
        Cmd(44).Number(image.Handle).Number(sx).Number(sy).Number(sw).Number(sh).Number(dx).Number(dy).Number(dw).Number(dh).EndArray();
        return this;
    }

    /// <summary>Draws another canvas (an offscreen layer) onto this one.</summary>
    public Canvas2D DrawCanvas(Canvas2D source, double x, double y) { Cmd(45).Number(source.Handle).Number(x).Number(y).EndArray(); return this; }

    // ── transforms ───────────────────────────────────────────────────────

    public Canvas2D Save() { Cmd(50).EndArray(); return this; }
    public Canvas2D Restore() { Cmd(51).EndArray(); return this; }
    public Canvas2D Translate(double x, double y) { Cmd(52).Number(x).Number(y).EndArray(); return this; }
    public Canvas2D Rotate(double radians) { Cmd(53).Number(radians).EndArray(); return this; }
    public Canvas2D Scale(double x, double y) { Cmd(54).Number(x).Number(y).EndArray(); return this; }
    public Canvas2D SetTransform(double a, double b, double c, double d, double e, double f) { Cmd(55).Number(a).Number(b).Number(c).Number(d).Number(e).Number(f).EndArray(); return this; }
    public Canvas2D ResetTransform() { Cmd(56).EndArray(); return this; }

    // ── queries (these flush first) ──────────────────────────────────────

    public double MeasureText(string text, string? font = null)
    {
        Flush();
        return Js.Call("canvas.measure", Js.Args("h", Handle, "text", text, "font", font)).AsDouble();
    }

    /// <summary>The pixels of a region as RGBA bytes, row by row.</summary>
    public byte[] GetPixels(int x, int y, int width, int height)
    {
        Flush();
        var b64 = Js.Call("canvas.getPixels", Js.Args("h", Handle, "x", x, "y", y, "w", width, "h2", height)).AsString();
        return Convert.FromBase64String(b64);
    }

    /// <summary>Writes RGBA bytes back to a region.</summary>
    public void PutPixels(byte[] rgba, int x, int y, int width, int height)
    {
        Flush();
        Js.Send("canvas.putPixels", Js.Args("h", Handle, "data", Convert.ToBase64String(rgba), "x", x, "y", y, "w", width, "h2", height));
    }

    /// <summary>The canvas as an image data URL ("image/png" or "image/jpeg").</summary>
    public string ToDataUrl(string type = "image/png", double quality = 0.92)
    {
        Flush();
        return Js.Call("canvas.dataUrl", Js.Args("h", Handle, "type", type, "q", quality)).AsString();
    }

    /// <summary>An offscreen canvas of the given size, for layers and caching.</summary>
    public static Canvas2D CreateLayer(int width, int height)
    {
        var r = Js.Call("canvas.layer", Js.Args("w", width, "h", height));
        return new Canvas2D(r.AsInt(), "") { Width = width, Height = height };
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        Js.Send("canvas.release", Js.Args("h", Handle));
        Handle = 0;
    }
}

/// <summary>An image loaded for drawing on a canvas.</summary>
public sealed class CanvasImage : IDisposable
{
    private CanvasImage(int handle, int width, int height)
    {
        Handle = handle;
        Width = width;
        Height = height;
    }

    public int Handle { get; private set; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Loads an image from a URL (or a data URL) and waits until it is decoded.</summary>
    public static async Task<CanvasImage> LoadAsync(string url)
    {
        var r = await Js.CallAsync("canvas.loadImage", Js.Args("url", url));
        return new CanvasImage(r["h"].AsInt(), r["w"].AsInt(), r["hh"].AsInt());
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        Js.Send("canvas.release", Js.Args("h", Handle));
        Handle = 0;
    }
}

/// <summary>Pointer input relative to a canvas, for drawing apps and games.</summary>
public static class CanvasInput
{
    /// <summary>
    /// Pointer down, move and up on the element, in canvas coordinates. The
    /// handler gets the kind ("down", "move", "up"), x, y, pressure and buttons.
    /// </summary>
    public static IDisposable OnPointer(ElementReference canvas, Action<PointerSample> handler)
    {
        var id = Callbacks.Register(e => handler(new PointerSample(
            e["kind"].AsString(), e["x"].AsDouble(), e["y"].AsDouble(), e["pressure"].AsDouble(), e["buttons"].AsInt(), e["pointerId"].AsInt())));
        var token = Js.Call("canvas.pointer", Js.Args("el", canvas.Id, "id", id)).AsInt();
        return new Subscription(id, token);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly int _id;
        private int _token;

        public Subscription(int id, int token)
        {
            _id = id;
            _token = token;
        }

        public void Dispose()
        {
            if (_token == 0) return;
            Js.Send("js.off", Js.Args("token", _token));
            Callbacks.Unregister(_id);
            _token = 0;
        }
    }
}

public readonly record struct PointerSample(string Kind, double X, double Y, double Pressure, int Buttons, int PointerId);
