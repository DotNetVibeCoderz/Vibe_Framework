using RustNet.UI.Skia;

// RustNet.UI.Skia layout.xml [--zoom N] [--png out.png]
if (args.Length == 0 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: RustNet.UI.Skia <layout.xml> [--zoom N] [--png out.png]");
    return 2;
}
int zoom = 2;
int z = Array.IndexOf(args, "--zoom");
if (z >= 0 && z + 1 < args.Length)
{
    int.TryParse(args[z + 1], out zoom);
}
string xml = File.ReadAllText(args[0]);
int png = Array.IndexOf(args, "--png");
if (png >= 0 && png + 1 < args.Length)
{
    // Headless: render once to a PNG (no window, no GPU).
    var session = RustNet.UI.Frontends.UiSession.FromXml(xml);
    session.Zoom = zoom;
    session.Render();
    using var renderer = new SkiaUiRenderer(session);
    using var bmp = renderer.Snapshot();
    using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(args[png + 1], data.ToArray());
    Console.WriteLine($"{args[png + 1]}: {bmp.Width}x{bmp.Height}");
    return 0;
}
using var window = SkiaUiWindow.FromXml(xml, zoom, Path.GetFileName(args[0]) + " — RustNet.UI");
window.Run();
return 0;
