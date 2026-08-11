using RustNet.Graphics;
using RustNet.Sys;
using RustNet.Threading;

namespace Core2Showcase;

/// <summary>
/// A graphics demo for the M5Stack Core2's 320x240 panel, written in C# and
/// interpreted on the ESP32.
///
/// <para>
/// It is deliberately not the Maix Go showcase again. That one proved a panel
/// could be driven at all; this board's panel already works, so the question
/// here is a different one — <em>what can the interpreter actually sustain?</em>
/// So the three scenes are built around cost, and the last one measures
/// itself: a live frame-time chart drawn from timings the demo takes as it
/// runs. A demo that reports its own frame rate cannot flatter itself.
/// </para>
///
/// <para><b>The cost model this is written against.</b> A host call crosses
/// from interpreted IL into Rust and costs far more than the drawing it
/// requests, so the budget that matters is <em>calls per frame</em>, not
/// pixels. Every scene here stays near 150. That is also why there is not a
/// single <c>SetPixel</c>: a starfield of 200 points would spend its whole
/// frame in call overhead, while the same 200 points as filled circles cost
/// the same and look better.</para>
/// </summary>
internal static class Program
{
    private const int W = 320;
    private const int H = 240;

    /// <summary>How many frames of history the closing chart keeps.</summary>
    private const int HistorySize = 64;

    private static readonly int[] FrameMs = new int[HistorySize];
    private static int _frames;

    /// <summary>sin(x)*1000 at ten-degree steps, 0..90. Filled in <c>Main</c>.</summary>
    /// <remarks>
    /// A field rather than a local, because <see cref="Quarter"/> runs eight
    /// times per point per frame and rebuilding the table inside it would
    /// allocate on every call.
    /// </remarks>
    private static readonly int[] SinTable = new int[10];

    private static void Main()
    {
        Display.Init(W, H);
        Console.WriteLine("[core2] showcase starting on a 320x240 ILI9342C");

        // Written out one by one rather than as `{ 0, 174, ... }`: a braced
        // array initialiser compiles to `ldtoken` of a data field, which this
        // runtime does not support and the MetadataProcessor refuses by name.
        SinTable[0] = 0;
        SinTable[1] = 174;
        SinTable[2] = 342;
        SinTable[3] = 500;
        SinTable[4] = 643;
        SinTable[5] = 766;
        SinTable[6] = 866;
        SinTable[7] = 940;
        SinTable[8] = 985;
        SinTable[9] = 1000;

        // Loops rather than ending: a display demo that stops leaves a board
        // showing a dead frame, and there is no way to tell that from a board
        // that crashed. `rustnet apps stop` is how it ends.
        while (true)
        {
            Aurora(140);
            Compound(160);
            Report();
        }
    }

    // ---------------------------------------------------------------- scenes

    /// <summary>
    /// Scene one: horizontal bands whose hue drifts, with a sine ribbon riding
    /// over them.
    /// </summary>
    /// <remarks>
    /// Twenty gradient bands cover the whole screen for twenty calls. Painting
    /// the same area as rectangles would take hundreds and look flatter — the
    /// gradient is interpolated on the Rust side, where a per-pixel loop is
    /// free.
    /// </remarks>
    private static void Aurora(int frames)
    {
        const int Bands = 20;
        int bandH = H / Bands;

        for (int f = 0; f < frames; f++)
        {
            long t0 = DeviceInfo.UptimeMs();

            for (int b = 0; b < Bands; b++)
            {
                int phase = f * 3 + b * 11;
                int top = Wave(phase, 40, 90);
                int bottom = Wave(phase + 60, 40, 90);
                Display.FillGradient(
                    0, b * bandH, W, bandH,
                    Color.FromRgb(top / 3, top, 120 + top / 2),
                    Color.FromRgb(bottom / 4, bottom / 2, 100 + bottom),
                    true);
            }

            // The ribbon: 32 circles on a Lissajous path, each one a call. The
            // trailing ones are dimmer, which reads as motion without keeping
            // any history — the colour is a function of the index alone.
            for (int i = 0; i < 32; i++)
            {
                int p = f * 4 - i * 5;
                int x = W / 2 + (Sin(p) * 130) / 1000;
                int y = H / 2 + (Sin(p * 2 + 250) * 80) / 1000;
                int fade = 255 - i * 7;
                Display.FillCircle(x, y, 7 - i / 6, Color.FromRgb(fade, fade, 200));
            }

            Display.DrawText(8, 8, "AURORA", Color.FromRgb(255, 255, 255), 2);
            Display.DrawText(8, 28, "gradients + ribbon", Color.FromRgb(150, 200, 255), 1);
            Present(t0);
        }
    }

    /// <summary>
    /// Scene two: a wireframe cube and octahedron rotating inside each other.
    /// </summary>
    /// <remarks>
    /// The compound is the improvisation. One rotating solid shows that the
    /// maths works; two interlocked ones, turning on different axes at
    /// different rates, show it holds up when the edges cross — which is where
    /// a projection bug would be visible and a single solid hides it.
    /// </remarks>
    private static void Compound(int frames)
    {
        // Both solids are derived rather than typed out. Partly because a
        // braced initialiser needs `ldtoken`, which this runtime does not
        // support — but mostly because the derivation says what the shape *is*
        // where a list of numbers only says where its corners ended up.
        //
        // Units of 1000, integer throughout: the interpreter has doubles, but
        // integer arithmetic is markedly cheaper and nothing here needs the
        // precision.
        int[] cx = new int[8];
        int[] cy = new int[8];
        int[] cz = new int[8];
        for (int i = 0; i < 8; i++)
        {
            // Two square rings, front and back, each walked in order.
            int r = i % 4;
            cx[i] = (r == 0 || r == 3) ? -600 : 600;
            cy[i] = r < 2 ? -600 : 600;
            cz[i] = i < 4 ? -600 : 600;
        }

        int[] ea = new int[12];
        int[] eb = new int[12];
        for (int i = 0; i < 4; i++)
        {
            ea[i] = i;                  // front ring
            eb[i] = (i + 1) % 4;
            ea[4 + i] = 4 + i;          // back ring
            eb[4 + i] = 4 + ((i + 1) % 4);
            ea[8 + i] = i;              // the struts between them
            eb[8 + i] = 4 + i;
        }

        // Octahedron: two apexes on Y, four equator points on X and Z.
        int[] ox = new int[6];
        int[] oy = new int[6];
        int[] oz = new int[6];
        oy[0] = -900;
        oy[1] = 900;
        ox[2] = 900;
        ox[3] = -900;
        oz[4] = 900;
        oz[5] = -900;

        int[] oa = new int[8];
        int[] ob = new int[8];
        for (int i = 0; i < 4; i++)
        {
            oa[i] = 0;                  // top apex to each equator point
            ob[i] = 2 + i;
            oa[4 + i] = 1;              // bottom apex to the same four
            ob[4 + i] = 2 + i;
        }

        int[] px = new int[8];
        int[] py = new int[8];

        for (int f = 0; f < frames; f++)
        {
            long t0 = DeviceInfo.UptimeMs();

            // A dark sky rather than a flat clear: one call either way.
            Display.FillGradient(0, 0, W, H,
                Color.FromRgb(4, 6, 20), Color.FromRgb(20, 8, 40), true);

            Project(cx, cy, cz, px, py, f * 6, f * 4, 8);
            for (int e = 0; e < ea.Length; e++)
            {
                Display.DrawLine(px[ea[e]], py[ea[e]], px[eb[e]], py[eb[e]],
                    Color.FromRgb(90, 220, 255));
            }

            Project(ox, oy, oz, px, py, -f * 5, f * 7, 6);
            for (int e = 0; e < oa.Length; e++)
            {
                Display.DrawLine(px[oa[e]], py[oa[e]], px[ob[e]], py[ob[e]],
                    Color.FromRgb(255, 140, 60));
            }

            Display.DrawText(8, 8, "COMPOUND", Color.FromRgb(255, 255, 255), 2);
            Display.DrawText(8, 28, "cube + octahedron, two axes",
                Color.FromRgb(200, 160, 120), 1);
            Present(t0);
        }
    }

    /// <summary>
    /// Scene three: what the first two cost, as a chart of their own frame
    /// times.
    /// </summary>
    private static void Report()
    {
        int slowest = 1;
        for (int i = 0; i < HistorySize; i++)
        {
            if (FrameMs[i] > slowest)
            {
                slowest = FrameMs[i];
            }
        }

        int total = 0;
        int counted = 0;
        for (int i = 0; i < HistorySize; i++)
        {
            if (FrameMs[i] > 0)
            {
                total = total + FrameMs[i];
                counted = counted + 1;
            }
        }
        int mean = counted > 0 ? total / counted : 0;
        int fps = mean > 0 ? 1000 / mean : 0;

        Console.WriteLine("[core2] " + _frames + " frames, mean " + mean + " ms (" + fps + " fps), worst " + slowest + " ms");

        // Held on screen rather than animated: this is the one thing worth
        // photographing, and a chart that redraws is a chart you cannot read.
        for (int hold = 0; hold < 30; hold++)
        {
            Display.FillGradient(0, 0, W, H,
                Color.FromRgb(10, 12, 24), Color.FromRgb(4, 4, 10), true);

            Display.DrawText(8, 10, "FRAME TIMES", Color.FromRgb(255, 255, 255), 2);
            Display.DrawText(8, 32, "measured on this board, by this app",
                Color.FromRgb(140, 160, 200), 1);

            int baseY = 200;
            int barW = W / HistorySize;
            for (int i = 0; i < HistorySize; i++)
            {
                int ms = FrameMs[i];
                int h = (ms * 130) / slowest;
                if (h < 1)
                {
                    h = 1;
                }
                // Green while the frame is quicker than the average, amber
                // once it is slower — the eye finds the stalls without a
                // legend.
                int color = ms <= mean
                    ? Color.FromRgb(60, 220, 120)
                    : Color.FromRgb(255, 170, 60);
                Display.FillRect(i * barW, baseY - h, barW - 1, h, color);
            }

            Display.DrawLine(0, baseY, W, baseY, Color.FromRgb(90, 100, 130));
            Display.DrawText(8, baseY + 8,
                "mean " + mean + " ms   " + fps + " fps   worst " + slowest + " ms",
                Color.FromRgb(220, 230, 255), 1);
            Display.DrawText(8, baseY + 24, "C# on ESP32, RustNet",
                Color.FromRgb(120, 200, 255), 1);

            Display.Present();
            Sleep.Ms(100);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Push the frame out, record what it cost, and stand aside.</summary>
    /// <remarks>
    /// The pause is not padding, and it is not small. Presenting a 320x240
    /// frame holds the board while it streams to the panel, and the firmware's
    /// RNDP service needs the same board to answer anything at all. Drawing
    /// flat out left this demo looking perfect and the device unreachable —
    /// `info` timed out, `logs` timed out, and only `apps stop`, squeezed into
    /// the moment after a reset, got it back. The board had never crashed: its
    /// uptime was seven minutes.
    ///
    /// A single tick was not enough. Twenty-five milliseconds caps this at
    /// roughly 30 fps, which no eye will miss, and leaves the service loop
    /// room it visibly needs. **A device you cannot talk to is a device you
    /// cannot fix**, and that is worth more than frames.
    /// </remarks>
    private static void Present(long startedMs)
    {
        Display.Present();
        int ms = (int)(DeviceInfo.UptimeMs() - startedMs);
        FrameMs[_frames % HistorySize] = ms;
        _frames = _frames + 1;
        Sleep.Ms(25);
    }

    /// <summary>
    /// Rotate a set of points about Y then X, project, and centre on screen.
    /// </summary>
    /// <remarks>
    /// Inlined into one method on purpose. Splitting out a `RotateY` helper
    /// would read better and cost more than the arithmetic it factors out: a
    /// managed call is one of the more expensive things in this loop.
    /// </remarks>
    private static void Project(int[] xs, int[] ys, int[] zs, int[] px, int[] py,
                                int angY, int angX, int scale)
    {
        int cosY = Cos(angY);
        int sinY = Sin(angY);
        int cosX = Cos(angX);
        int sinX = Sin(angX);

        for (int i = 0; i < xs.Length; i++)
        {
            int x = xs[i];
            int y = ys[i];
            int z = zs[i];

            int x1 = (x * cosY + z * sinY) / 1000;
            int z1 = (z * cosY - x * sinY) / 1000;
            int y1 = (y * cosX - z1 * sinX) / 1000;
            int z2 = (z1 * cosX + y * sinX) / 1000;

            // Perspective: nearer points spread further from the centre.
            int d = 2600 + z2;
            px[i] = W / 2 + (x1 * scale * 300) / d;
            py[i] = H / 2 + (y1 * scale * 300) / d;
        }
    }

    /// <summary>Sine of <paramref name="deg"/> degrees, scaled by 1000.</summary>
    /// <remarks>
    /// A 91-entry quarter table, mirrored. `Math.Sin` exists in the
    /// interpreter, but it is a host call and this is used eight times per
    /// point per frame — the table is a lookup and an add.
    /// </remarks>
    private static int Sin(int deg)
    {
        int a = deg % 360;
        if (a < 0)
        {
            a = a + 360;
        }
        if (a <= 90)
        {
            return Quarter(a);
        }
        if (a <= 180)
        {
            return Quarter(180 - a);
        }
        if (a <= 270)
        {
            return -Quarter(a - 180);
        }
        return -Quarter(360 - a);
    }

    private static int Cos(int deg) => Sin(deg + 90);

    /// <summary>sin(x) * 1000 for x in 0..90, by linear interpolation.</summary>
    /// <remarks>
    /// Ten anchors is enough: the largest interpolation error is under one
    /// part in a hundred, which across 320 pixels is well under one of them.
    /// </remarks>
    private static int Quarter(int deg)
    {
        int slot = deg / 10;
        if (slot >= 9)
        {
            return 1000;
        }
        int lo = SinTable[slot];
        int hi = SinTable[slot + 1];
        return lo + ((hi - lo) * (deg - slot * 10)) / 10;
    }

    /// <summary>A value swinging between <c>lo</c> and <c>lo+span</c>.</summary>
    private static int Wave(int phase, int lo, int span)
        => lo + ((Sin(phase) + 1000) * span) / 2000;
}
