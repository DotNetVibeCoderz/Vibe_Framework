using RustNet.Community.Bus;
using RustNet.Community.DisplayCore;
using RustNet.Community.EPaper;

namespace DriversApp;

/// <summary>A BUSY input that serves scripted levels, then a default, counting reads.</summary>
public class ScriptInput : DigitalInput
{
    public Queue<bool> Levels = new Queue<bool>();
    public bool Default;
    public int Reads;

    public ScriptInput(bool idleLevel)
    {
        Default = idleLevel;
    }

    public override bool Read()
    {
        Reads++;
        if (Levels.Count > 0)
        {
            return Levels.Dequeue();
        }
        return Default;
    }
}

/// <summary>A fake millisecond clock that advances 50 ms per reading.</summary>
public class FakeClock
{
    public long Ms;

    public long Now()
    {
        Ms += 50;
        return Ms;
    }
}

public static class EpdCheck
{
    public static void Plane(string what, TraceSpi spi, int mark, string spec) => Expect.Trace(what, spec, spi, mark);
}

public static class Ssd1681Smoke
{
    public static void Run()
    {
        Timing.ClockOverride = new FakeClock().Now; // busy waits read the clock
        SpiRig rig = new SpiRig();
        ScriptInput busy = new ScriptInput(false); // SSD16xx: BUSY high = busy
        busy.Levels.Enqueue(true);                 // busy once right after reset
        Ssd1681 epd = new Ssd1681(rig.Bus, rig.Reset, busy, 16, 4, false);
        epd.Init();
        Expect.Trace("ssd1681 init",
            "C 12 C 01 D 03 00 00 C 3C D 05 C 18 D 80 C 11 D 03 C 44 D 00 01 C 45 D 00 00 03 00 C 4E D 00 C 4F D 00 00",
            rig.Spi, 0);
        Check.True("ssd1681 waited on busy", busy.Reads >= 4 && !epd.TimedOut);
        epd.Black.SetPixel(0, 0, MonoColor.On);
        epd.Black.SetPixel(9, 1, MonoColor.On);
        int m = rig.Mark;
        Check.True("ssd1681 display ok", epd.Display());
        // B/W RAM: 0 = black -> (0,0) clears bit 7 of byte 0, (9,1) bit 6 of byte 3.
        Expect.Trace("ssd1681 display",
            "C 11 D 03 C 44 D 00 01 C 45 D 00 00 03 00 C 4E D 00 C 4F D 00 00"
            + " C 24 D 7F FF FF BF FF FF FF FF C 22 D F7 C 20", rig.Spi, m);
        m = rig.Mark;
        epd.DeepSleep();
        Expect.Trace("ssd1681 deep sleep", "C 10 D 01", rig.Spi, m);

        // Waveshare 1.54" V2 preset: 200x200 -> MUX 199, X bytes 0..24, Y 0..199.
        SpiRig pr = new SpiRig();
        Ssd1681 p = WaveshareEpd.Epd1in54(pr.Bus, null, new ScriptInput(false));
        p.Init();
        Expect.TracePrefix("ws 1.54 window", "C 12 C 01 D C7 00 00 C 3C D 05 C 18 D 80 C 11 D 03 C 44 D 00 18 C 45 D 00 00 C7 00", pr.Spi, 0);

        // A BUSY stuck high times out on a fake clock instead of hanging.
        FakeClock clock = new FakeClock();
        Timing.ClockOverride = clock.Now;
        SpiRig sr = new SpiRig();
        ScriptInput stuck = new ScriptInput(true);
        Ssd1681 hung = new Ssd1681(sr.Bus, null, stuck, 16, 4, false);
        hung.Busy.TimeoutMs = 1000;
        bool ok = hung.Display();
        Timing.ClockOverride = null;
        Check.True("ssd1681 busy timeout", !ok && hung.TimedOut && hung.Busy.Timeouts == 1);
    }
}

public static class Ssd1680Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ssd1680 epd = new Ssd1680(rig.Bus, rig.Reset, new ScriptInput(false), 16, 4, true);
        epd.Init();
        Expect.Trace("ssd1680 init",
            "C 12 C 01 D 03 00 00 C 11 D 03 C 44 D 00 01 C 45 D 00 00 03 00 C 4E D 00 C 4F D 00 00"
            + " C 3C D 05 C 21 D 00 80 C 18 D 80", rig.Spi, 0);
        epd.Color.SetPixel(15, 3, MonoColor.On);
        int m = rig.Mark;
        epd.Display();
        // Red RAM 0x26: 1 = red, sent as-is; the B/W RAM stays all white.
        string win = "C 11 D 03 C 44 D 00 01 C 45 D 00 00 03 00 C 4E D 00 C 4F D 00 00";
        Expect.Trace("ssd1680 tri-colour display",
            win + " C 24 D FF FF FF FF FF FF FF FF " + win + " C 26 D 00 00 00 00 00 00 00 01 C 22 D F7 C 20", rig.Spi, m);

        // Waveshare 2.13" V3/V4: 122x250 -> 16 X bytes (0..15), MUX 249.
        SpiRig a = new SpiRig();
        WaveshareEpd.Epd2in13(a.Bus, null, new ScriptInput(false)).Init();
        Expect.TracePrefix("ws 2.13", "C 12 C 01 D F9 00 00 C 11 D 03 C 44 D 00 0F C 45 D 00 00 F9 00", a.Spi, 0);
        // Waveshare 2.9" V2: 128x296 -> MUX 295 = 0x127.
        SpiRig b = new SpiRig();
        WaveshareEpd.Epd2in9(b.Bus, null, new ScriptInput(false)).Init();
        Expect.TracePrefix("ws 2.9", "C 12 C 01 D 27 01 00 C 11 D 03 C 44 D 00 0F C 45 D 00 00 27 01", b.Spi, 0);
    }
}

public static class Ssd1608Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ssd1608 epd = new Ssd1608(rig.Bus, rig.Reset, new ScriptInput(false), 16, 4);
        epd.Init();
        Expect.Trace("ssd1608 init",
            "C 01 D 03 00 00 C 0C D D7 D6 9D C 2C D A8 C 3A D 1A C 3B D 08 C 11 D 03"
            + " C 32 D 02 02 01 11 12 12 22 22 66 69 69 59 58 99 99 88 00 00 00 00 F8 B4 13 51 35 51 51 19 01 00",
            rig.Spi, 0);
        int m = rig.Mark;
        epd.Display();
        Expect.TracePrefix("ssd1608 refresh", "C 11 D 03 C 44 D 00 01", rig.Spi, m);
        Expect.Trace("ssd1608 refresh tail", "C 22 D C4 C 20 C FF", rig.Spi, rig.Mark - 4);
    }
}

public static class Il3897Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Il3897 epd = new Il3897(rig.Bus, rig.Reset, new ScriptInput(false), 16, 4);
        epd.Init();
        Expect.Trace("il3897 init",
            "C 12 C 74 D 54 C 7E D 3B C 01 D 03 00 00 C 11 D 03 C 44 D 00 01 C 45 D 00 00 03 00 C 4E D 00 C 4F D 00 00"
            + " C 3C D 03 C 2C D 55", rig.Spi, 0);
        int m = rig.Mark;
        epd.Display();
        Expect.Trace("il3897 OTP full refresh", "C 22 D F7 C 20", rig.Spi, rig.Mark - 3);
        Check.True("il3897 sent plane", rig.Mark - m > 8);
    }
}

public static class Il0373Smoke
{
    public static void Run()
    {
        Timing.ClockOverride = new FakeClock().Now;
        SpiRig rig = new SpiRig();
        ScriptInput busy = new ScriptInput(true); // UC81xx: BUSY_N low = busy
        busy.Levels.Enqueue(false);
        busy.Levels.Enqueue(false);
        Il0373 epd = new Il0373(rig.Bus, rig.Reset, busy, 16, 4, true);
        epd.Init();
        Expect.Trace("il0373 init",
            "C 01 D 03 00 2B 2B 09 C 06 D 17 17 17 C 04 C 00 D CF C 50 D 37 C 30 D 29 C 61 D 10 00 04 C 82 D 0A",
            rig.Spi, 0);
        Check.Equal("il0373 polled busy through power-on", 3, busy.Reads);
        epd.Black.SetPixel(0, 0, MonoColor.On);
        epd.Color.SetPixel(8, 0, MonoColor.On);
        int m = rig.Mark;
        epd.Display();
        Expect.Trace("il0373 display",
            "C 10 D 7F FF FF FF FF FF FF FF C 13 D FF 7F FF FF FF FF FF FF C 12", rig.Spi, m);
        m = rig.Mark;
        epd.DeepSleep();
        Expect.Trace("il0373 sleep", "C 50 D 17 C 02 C 07 D A5", rig.Spi, m);
        Timing.ClockOverride = null;
    }
}

public static class Il0376fSmoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Il0376f epd = new Il0376f(rig.Bus, rig.Reset, new ScriptInput(true), 8, 2);
        epd.Init();
        Expect.Trace("il0376f init",
            "C 01 D 07 00 08 00 C 06 D 07 07 07 C 04 C 00 D CF C 50 D 17 C 30 D 39 C 61 D 08 00 02 C 82 D 0E",
            rig.Spi, 0);
        // Black at (0,0) and (5,1): white bits 0111 1111 / 1111 1011, each bit -> 2 bits (1 -> 11).
        epd.Black.SetPixel(0, 0, MonoColor.On);
        epd.Black.SetPixel(5, 1, MonoColor.On);
        epd.Color.SetPixel(1, 1, MonoColor.On);
        int m = rig.Mark;
        epd.Display();
        Expect.Trace("il0376f 2bpp black + 1bpp red", "C 10 D 3F FF FF CF C 13 D FF BF C 12", rig.Spi, m);
        Check.Equal("il0376f expand 0xA0", 0xCC, Il0376f.ExpandHigh(0xA0));
        m = rig.Mark;
        epd.DeepSleep();
        Expect.Trace("il0376f sleep", "C 50 D 17 C 82 D 00 C 01 D 02 00 00 00 C 02", rig.Spi, m);
    }
}

public static class Il0398Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Il0398 epd = new Il0398(rig.Bus, rig.Reset, new ScriptInput(true), 16, 2, false);
        epd.Init();
        Expect.Trace("il0398 init", "C 06 D 17 17 17 C 04 C 00 D 1F C 61 D 00 10 00 02 C 50 D 97", rig.Spi, 0);
        epd.Black.SetPixel(15, 1, MonoColor.On);
        int m = rig.Mark;
        epd.Display();
        Expect.Trace("il0398 b/w display (old frame white, new frame)", "C 10 D FF FF FF FF C 13 D FF FF FF FE C 12", rig.Spi, m);
        SpiRig pr = new SpiRig();
        WaveshareEpd.Epd4in2(pr.Bus, null, new ScriptInput(true)).Init();
        Expect.TracePrefix("ws 4.2 = 400x300", "C 06 D 17 17 17 C 04 C 00 D 1F C 61 D 01 90 01 2C", pr.Spi, 0);
    }
}

public static class Il91874Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Il91874 epd = new Il91874(rig.Bus, rig.Reset, new ScriptInput(true), 16, 2, true);
        epd.Init();
        Expect.Trace("il91874 init",
            "C 04 C 00 D 8F C 30 D 3A C 01 D 03 00 2B 2B 09 C 06 D 07 07 17"
            + " C F8 D 60 A5 C F8 D 89 A5 C F8 D 90 00 C F8 D 93 2A C F8 D 73 41 C 82 D 12 C 50 D 87 C 61 D 00 10 00 02",
            rig.Spi, 0);
        epd.Black.SetPixel(0, 0, MonoColor.On);
        epd.Color.SetPixel(7, 1, MonoColor.On);
        int m = rig.Mark;
        epd.Display();
        Expect.Trace("il91874 display (1 = ink)", "C 10 D 80 00 00 00 C 13 D 00 00 01 00 C 12", rig.Spi, m);
        SpiRig pr = new SpiRig();
        Il91874 p = WaveshareEpd.Epd2in7(pr.Bus, null, new ScriptInput(true));
        p.Init();
        Check.True("ws 2.7 = 176x264", p.Width == 176 && p.Height == 264);
        Expect.Trace("ws 2.7 resolution", "C 61 D 00 B0 01 08", pr.Spi, pr.Mark - 5);
    }
}

public static class Uc8151cSmoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Uc8151c epd = new Uc8151c(rig.Bus, rig.Reset, new ScriptInput(true), 16, 2, true);
        epd.Init();
        Expect.Trace("uc8151c init", "C 04 C 00 D 0F 89 C 61 D 10 00 02 C 50 D 77", rig.Spi, 0);
        epd.Color.SetPixel(0, 1, MonoColor.On);
        int m = rig.Mark;
        epd.Display();
        Expect.Trace("uc8151c display", "C 10 D FF FF FF FF C 13 D FF FF 7F FF C 12", rig.Spi, m);
        m = rig.Mark;
        epd.DeepSleep();
        Expect.Trace("uc8151c sleep", "C 02 C 07 D A5", rig.Spi, m);
    }
}

public static class Uc8179Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Uc8179 epd = new Uc8179(rig.Bus, rig.Reset, new ScriptInput(true), 16, 2);
        epd.Init();
        Expect.Trace("uc8179 init",
            "C 01 D 07 07 3F 3F C 04 C 00 D 1F C 61 D 00 10 00 02 C 15 D 00 C 50 D 10 07 C 60 D 22", rig.Spi, 0);
        epd.Black.SetPixel(0, 0, MonoColor.On);
        int m = rig.Mark;
        epd.Display();
        Expect.Trace("uc8179 display (1 = black)", "C 13 D 80 00 00 00 C 12", rig.Spi, m);
        SpiRig pr = new SpiRig();
        WaveshareEpd.Epd7in5(pr.Bus, null, new ScriptInput(true)).Init();
        Expect.TracePrefix("ws 7.5 = 800x480", "C 01 D 07 07 3F 3F C 04 C 00 D 1F C 61 D 03 20 01 E0", pr.Spi, 0);
    }
}

public static class Acep565Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        ScriptInput busy = new ScriptInput(true);
        Acep565 epd = new Acep565(rig.Bus, rig.Reset, busy, 4, 2);
        epd.Init();
        Expect.Trace("acep init",
            "C 00 D EF 08 C 01 D 37 00 23 23 C 03 D 00 C 06 D C7 C7 1D C 30 D 3C C 41 D 00 C 50 D 37 C 60 D 22"
            + " C 61 D 00 04 00 02 C E3 D AA C 50 D 37", rig.Spi, 0);
        // 4bpp, even x in the high nibble, white = 1: red (0,0) and blue (3,1).
        epd.Canvas.SetPixel(0, 0, AcepColor.Red);
        epd.Canvas.SetPixel(3, 1, AcepColor.Blue);
        busy.Levels.Enqueue(true);  // after power on: idle (high)
        busy.Levels.Enqueue(true);  // after refresh: idle (high)
        busy.Levels.Enqueue(false); // after power off: low
        int m = rig.Mark;
        Check.True("acep display ok", epd.Display());
        Expect.Trace("acep display", "C 61 D 00 04 00 02 C 10 D 41 11 11 13 C 04 C 12 C 02", rig.Spi, m);
        m = rig.Mark;
        epd.DeepSleep();
        Expect.Trace("acep sleep", "C 07 D A5", rig.Spi, m);
        SpiRig pr = new SpiRig();
        Acep565 p = WaveshareEpd.Epd5in65f(pr.Bus, null, new ScriptInput(true));
        Check.True("ws 5.65 = 600x448, 134400 bytes", p.Width == 600 && p.Height == 448 && p.Canvas.Buffer.Length == 134400);
        Check.Equal("ws 5.65 starts white", 0x11, p.Canvas.Buffer[134399]);
    }
}
