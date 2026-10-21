using RustNet.UI;
using RustNet.UI.Frontends;
using RustNet.UI.Skia;
using Xunit;

namespace RustNet.Frontends.Tests;

public class FrontendTests
{
    private static string Parity => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity.xml"));

    [Fact]
    public void FramebufferPrimitivesMatchRustnetGfx()
    {
        var fb = new FramebufferCanvas(16, 16);
        // Bresenham from (0,0) to (4,2): rustnet-gfx's draw_line visits these.
        fb.DrawLine(0, 0, 4, 2, 0xFFFF);
        var lit = new List<(int, int)>();
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                if (fb.GetPixel(x, y) != 0)
                {
                    lit.Add((x, y));
                }
            }
        }
        Assert.Equal(new[] { (0, 0), (1, 1), (2, 1), (3, 2), (4, 2) }, lit);

        // A clip masks drawing; Clear ignores it.
        fb.Clear(0);
        fb.SetClip(2, 2, 3, 3);
        fb.FillRect(0, 0, 16, 16, 0x1234);
        Assert.Equal(9, fb.Pixels.Count(p => p == 0x1234));
        fb.Clear(0x0F0F);
        Assert.All(fb.Pixels, p => Assert.Equal(0x0F0F, p));
        fb.ClearClip();

        // Filled circle r=2: the 13 points with x*x + y*y <= 4.
        fb.Clear(0);
        fb.FillCircle(8, 8, 2, 1);
        Assert.Equal(13, fb.Pixels.Count(p => p == 1));
    }

    [Fact]
    public void TextUsesTheDeviceFont()
    {
        var fb = new FramebufferCanvas(16, 8);
        fb.DrawText(0, 0, "A", 0xFFFF, 1);
        // font8x8 'A' row 0 is 0x0C: pixels 2 and 3.
        Assert.Equal(new ushort[] { 0, 0, 0xFFFF, 0xFFFF, 0, 0, 0, 0 }, fb.Pixels.Take(8).ToArray());
        // Characters outside the table draw as '?', as on the device.
        Assert.Equal(Font8x8.Glyph('?').ToArray(), Font8x8.Glyph('é').ToArray());
    }

    [Fact]
    public void SkiaSnapshotIsTheFramebufferScaled()
    {
        var session = UiSession.FromXml(Parity);
        foreach (int zoom in new[] { 1, 3 })
        {
            session.Zoom = zoom;
            session.Render();
            using var renderer = new SkiaUiRenderer(session);
            using var bmp = renderer.Snapshot();
            Assert.Equal(session.Width * zoom, bmp.Width);
            Assert.Equal(session.Height * zoom, bmp.Height);
            var fb = session.Canvas;
            for (int y = 0; y < fb.Height; y += 7)
            {
                for (int x = 0; x < fb.Width; x += 5)
                {
                    uint want = FramebufferCanvas.Rgb565ToArgb(fb.GetPixel(x, y));
                    // Sample the middle of the scaled cell.
                    var got = bmp.GetPixel(x * zoom + zoom / 2, y * zoom + zoom / 2);
                    Assert.Equal(want, (uint)got);
                }
            }
        }
    }

    [Fact]
    public void SessionRoutesClicksWheelAndTyping()
    {
        var session = UiSession.FromXml(
            "<window width=\"160\" height=\"128\" pad=\"4\" gap=\"4\">" +
            "<checkbox id=\"c\" text=\"On\"/>" +
            "<textbox id=\"t\" text=\"ab\" height=\"16\"/>" +
            "<scrollviewer id=\"s\" height=\"30\"><stack><label text=\"1\"/><label text=\"2\"/><label text=\"3\"/>" +
            "<label text=\"4\"/><label text=\"5\"/><label text=\"6\"/></stack></scrollviewer>" +
            "</window>");
        session.Zoom = 2;
        int frames = 0;
        session.FrameReady = () => frames++;
        session.Render();
        var check = session.Root.FindById("c");
        var hit = session.Click((check.LayoutX + 2) * 2, (check.LayoutY + 2) * 2);
        Assert.Same(check, hit);
        Assert.True(check.Checked);

        var box = session.Root.FindById("t");
        session.Type(box, "c\bdz");
        Assert.Equal("abdz", box.Text);

        var sv = session.Root.FindById("s");
        session.Wheel((sv.LayoutX + 4) * 2, (sv.LayoutY + 4) * 2, -1);
        Assert.True(sv.ScrollOffset > 0);
        Assert.True(frames >= 4);
        Assert.Null(Paint.Target); // the canvas is only installed while rendering
    }
}
