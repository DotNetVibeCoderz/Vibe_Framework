using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.Browser.OffscreenCanvas;

/// <summary>
/// Canvases that are not on the page: render off screen and export, or hand a
/// visible canvas to a worker so drawing never blocks the page.
/// </summary>
public static class Offscreen
{
    public static bool IsSupported => Js.Call("off.supported").AsBool();

    /// <summary>
    /// Moves drawing of a page canvas to a worker running <paramref name="workerScript"/>.
    /// The worker receives <c>{ canvas }</c> in its first message; later
    /// messages from C# arrive through <c>onmessage</c>.
    /// </summary>
    public static OffscreenWorker TransferToWorker(ElementReference canvas, string workerScript) =>
        new(Js.Call("off.transfer", Js.Args("el", canvas.Id, "url", workerScript)).AsInt());

    /// <summary>
    /// Renders SVG or a drawing script off screen and returns a PNG data URL:
    /// <paramref name="draw"/> is JavaScript run with <c>ctx</c>, <c>w</c> and <c>h</c>.
    /// </summary>
    public static async Task<string> RenderAsync(int width, int height, string drawScript, string type = "image/png") =>
        (await Js.CallAsync("off.render", Js.Args("w", width, "h", height, "draw", drawScript, "type", type))).AsString();

    /// <summary>Rasterises an SVG string to a PNG data URL (thumbnails, exports).</summary>
    public static async Task<string> SvgToPngAsync(string svg, int width, int height) =>
        (await Js.CallAsync("off.svg", Js.Args("svg", svg, "w", width, "h", height))).AsString();
}

public sealed class OffscreenWorker : IDisposable
{
    private readonly int _handle;

    internal OffscreenWorker(int handle) => _handle = handle;

    public void Post(object? message) => Js.Send("off.post", Js.Args("h", _handle, "data", message));

    public void Dispose() => Js.Send("off.stop", Js.Args("h", _handle));
}
