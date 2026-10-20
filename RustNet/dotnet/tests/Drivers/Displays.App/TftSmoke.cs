using RustNet.Community.Bus;
using RustNet.Community.DisplayCore;
using RustNet.Community.Tft;

namespace DriversApp;

/// <summary>Shared checks for the MIPI-style TFTs.</summary>
public static class TftCheck
{
    /// <summary>Begin() with a reset pin wired: the trace must be exactly the init spec.</summary>
    public static void Init(string what, TftDisplay tft, SpiRig rig, string spec)
    {
        tft.Begin();
        Expect.Trace(what + " init", spec, rig.Spi, 0);
    }

    public static void Window(string what, TftDisplay tft, SpiRig rig, int x, int y, int w, int h, string spec)
    {
        int m = rig.Mark;
        tft.SetWindow(x, y, w, h);
        Expect.Trace(what, spec, rig.Spi, m);
    }

    /// <summary>A 2x1 canvas (0x1234, 0xABCD) flushed at (5, 6) of a CASET/RASET controller with no offset.</summary>
    public static void FlushDcs(string what, TftDisplay tft, SpiRig rig)
    {
        Rgb565Canvas c = new Rgb565Canvas(2, 1);
        c.SetPixel(0, 0, 0x1234);
        c.SetPixel(1, 0, 0xABCD);
        int m = rig.Mark;
        tft.Flush(c, 5, 6);
        Expect.Trace(what + " flush", "C 2A D 00 05 00 06 C 2B D 00 06 00 06 C 2C D 12 34 AB CD", rig.Spi, m);
    }

    /// <summary>A 2x2 fill of pure red: four F8 00 pairs after the window.</summary>
    public static void FillRed(string what, TftDisplay tft, SpiRig rig, string windowSpec)
    {
        int m = rig.Mark;
        tft.FillRect(0, 0, 2, 2, Rgb565.Red);
        Expect.Trace(what + " fill", windowSpec + " D F8 00 F8 00 F8 00 F8 00", rig.Spi, m);
    }

    /// <summary>ILI9225 register write: index 00 rr as command, value as data.</summary>
    public static string Regs(string pairs)
    {
        // pairs: "rr vvvv rr vvvv ..."
        string r = "";
        int i = 0;
        int field = 0;
        string reg = "";
        while (i < pairs.Length)
        {
            if (pairs[i] == ' ')
            {
                i++;
                continue;
            }
            if (field == 0)
            {
                reg = pairs.Substring(i, 2);
                i += 2;
                field = 1;
            }
            else
            {
                string hi = pairs.Substring(i, 2);
                string lo = pairs.Substring(i + 2, 2);
                r = r + " C 00 " + reg + " D " + hi + " " + lo;
                i += 4;
                field = 0;
            }
        }
        return r;
    }
}

public static class Ili9341Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ili9341 tft = new Ili9341(rig.Bus, rig.Reset);
        TftCheck.Init("ili9341", tft, rig,
            "C EF D 03 80 02 C CF D 00 C1 30 C ED D 64 03 12 81 C E8 D 85 00 78 C CB D 39 2C 00 34 02"
            + " C F7 D 20 C EA D 00 00 C C0 D 23 C C1 D 10 C C5 D 3E 28 C C7 D 86 C 36 D 48 C 37 D 00"
            + " C 3A D 55 C B1 D 00 18 C B6 D 08 82 27 C F2 D 00 C 26 D 01"
            + " C E0 D 0F 31 2B 0C 0E 08 4E F1 37 07 10 03 0E 09 00"
            + " C E1 D 00 0E 14 03 11 07 31 C1 48 08 0F 0C 31 36 0F"
            + " C 11 C 29 C 36 D 48");
        Check.True("ili9341 reset pin pulsed", rig.Reset.History.Count == 3);
        TftCheck.Window("ili9341 window", tft, rig, 10, 20, 30, 40, "C 2A D 00 0A 00 27 C 2B D 00 14 00 3B C 2C");
        TftCheck.FillRed("ili9341", tft, rig, "C 2A D 00 00 00 01 C 2B D 00 00 00 01 C 2C");
        TftCheck.FlushDcs("ili9341", tft, rig);
        int m = rig.Mark;
        tft.SetRotation(1);
        Expect.Trace("ili9341 rotation 1 = MV|BGR", "C 36 D 28", rig.Spi, m);
        Check.True("ili9341 landscape 320x240", tft.Width == 320 && tft.Height == 240);
        m = rig.Mark;
        tft.SetRotation(2);
        tft.SetRotation(3);
        tft.Invert(true);
        Expect.Trace("ili9341 rotation 2/3 + invert", "C 36 D 88 C 36 D E8 C 21", rig.Spi, m);
        // Fill clipped to the screen: (318, 238) 5x5 on 320x240 -> 2x2.
        m = rig.Mark;
        tft.FillRect(318, 238, 5, 5, 0x0000);
        Expect.TracePrefix("ili9341 clipped fill", "C 2A D 01 3E 01 3F C 2B D 00 EE 00 EF", rig.Spi, m);
        // No reset pin: SWRESET first.
        SpiRig soft = new SpiRig();
        new Ili9341(soft.Bus, null).Begin();
        Expect.TracePrefix("ili9341 soft reset", "C 01 C EF", soft.Spi, 0);
    }
}

public static class St7735Smoke
{
    public static void Run()
    {
        string rcmd1 = "C 01 C 11 C B1 D 01 2C 2D C B2 D 01 2C 2D C B3 D 01 2C 2D 01 2C 2D C B4 D 07"
            + " C C0 D A2 02 84 C C1 D C5 C C2 D 0A 00 C C3 D 8A 2A C C4 D 8A EE C C5 D 0E C 20"
            + " C 36 D C8 C 3A D 05 ";
        string rcmd3 = " C E0 D 02 1C 07 12 37 32 29 2D 29 25 2B 39 00 01 03 10"
            + " C E1 D 03 1D 07 06 2E 2C 29 2D 2E 2E 37 3F 00 00 02 10 C 13 C 29";

        SpiRig rig = new SpiRig();
        St7735 green = new St7735(rig.Bus, rig.Reset, St7735Tab.Green);
        TftCheck.Init("st7735 green", green, rig,
            rcmd1 + "C 2A D 00 02 00 81 C 2B D 00 01 00 A0" + rcmd3 + " C 36 D C8");
        // Green tab glass starts at RAM column 2, row 1.
        TftCheck.Window("st7735 green window", green, rig, 0, 0, 128, 160, "C 2A D 00 02 00 81 C 2B D 00 01 00 A0 C 2C");
        green.SetRotation(1);
        TftCheck.Window("st7735 green rot1 window", green, rig, 0, 0, 1, 1, "C 2A D 00 01 00 01 C 2B D 00 02 00 02 C 2C");

        SpiRig rb = new SpiRig();
        St7735 black = new St7735(rb.Bus, rb.Reset, St7735Tab.Black);
        TftCheck.Init("st7735 black", black, rb,
            rcmd1 + "C 2A D 00 00 00 7F C 2B D 00 00 00 9F" + rcmd3 + " C 36 D C0 C 36 D C0");

        SpiRig rr = new SpiRig();
        St7735 red = new St7735(rr.Bus, rr.Reset, St7735Tab.Red);
        TftCheck.Init("st7735 red", red, rr,
            rcmd1 + "C 2A D 00 00 00 7F C 2B D 00 00 00 9F" + rcmd3 + " C 36 D C8");

        SpiRig r144 = new SpiRig();
        St7735 g144 = new St7735(r144.Bus, r144.Reset, St7735Tab.Green144);
        TftCheck.Init("st7735 1.44", g144, r144,
            rcmd1 + "C 2A D 00 00 00 7F C 2B D 00 00 00 7F" + rcmd3 + " C 36 D C8");
        TftCheck.Window("st7735 1.44 window", g144, r144, 0, 0, 128, 128, "C 2A D 00 02 00 81 C 2B D 00 03 00 82 C 2C");

        SpiRig rm = new SpiRig();
        St7735 mini = new St7735(rm.Bus, rm.Reset, St7735Tab.Mini160x80);
        TftCheck.Init("st7735 mini", mini, rm,
            rcmd1 + "C 2A D 00 00 00 4F C 2B D 00 00 00 9F" + rcmd3 + " C 36 D C0");
        Check.True("st7735 mini 80x160", mini.Width == 80 && mini.Height == 160);
        TftCheck.Window("st7735 mini window", mini, rm, 0, 0, 80, 160, "C 2A D 00 18 00 67 C 2B D 00 00 00 9F C 2C");
    }
}

public static class St7789Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        St7789 sq = new St7789(rig.Bus, rig.Reset, 240, 240);
        TftCheck.Init("st7789 240x240", sq, rig,
            "C 01 C 11 C 3A D 55 C 36 D 08 C 2A D 00 00 00 F0 C 2B D 00 00 01 40 C 21 C 13 C 29 C 36 D C0");
        // 240x240 glass sits at RAM rows 80..319 in rotation 0, rows 0..239 in rotation 2.
        TftCheck.Window("st7789 240 rot0", sq, rig, 10, 20, 5, 5, "C 2A D 00 0A 00 0E C 2B D 00 64 00 68 C 2C");
        sq.SetRotation(2);
        TftCheck.Window("st7789 240 rot2", sq, rig, 0, 0, 240, 240, "C 2A D 00 00 00 EF C 2B D 00 00 00 EF C 2C");
        sq.SetRotation(1);
        TftCheck.Window("st7789 240 rot1", sq, rig, 0, 0, 1, 1, "C 2A D 00 50 00 50 C 2B D 00 00 00 00 C 2C");

        SpiRig r2 = new SpiRig();
        St7789 tall = new St7789(r2.Bus, r2.Reset, 240, 320);
        tall.Begin();
        TftCheck.FlushDcs("st7789 240x320", tall, r2);

        // 1.14" 135x240: column offset 53 (odd split rounds up), row offset 40.
        SpiRig r3 = new SpiRig();
        St7789 small = new St7789(r3.Bus, r3.Reset, 135, 240);
        small.Begin();
        TftCheck.Window("st7789 135 rot0", small, r3, 0, 0, 1, 1, "C 2A D 00 35 00 35 C 2B D 00 28 00 28 C 2C");
        small.SetRotation(1);
        TftCheck.Window("st7789 135 rot1", small, r3, 0, 0, 1, 1, "C 2A D 00 28 00 28 C 2B D 00 34 00 34 C 2C");
    }
}

public static class Ili9163Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ili9163 tft = new Ili9163(rig.Bus, rig.Reset, 128, 128);
        TftCheck.Init("ili9163", tft, rig,
            "C 01 C 11 C 3A D 05 C 26 D 04 C F2 D 01 C 13 C B6 D FF 06"
            + " C E0 D 36 29 12 22 1C 15 42 B7 2F 13 12 0A 11 0B 06"
            + " C E1 D 09 16 2D 0D 13 15 40 48 53 0C 1D 25 2E 34 39"
            + " C B1 D 08 02 C B4 D 07 C C0 D 0A 02 C C1 D 02 C C5 D 50 5B C C7 D 40 C 29 C 36 D 08");
        TftCheck.FlushDcs("ili9163", tft, rig);
        tft.RowStart2 = 32;
        tft.SetRotation(2);
        TftCheck.Window("ili9163 red-pcb rot2", tft, rig, 0, 0, 1, 1, "C 2A D 00 00 00 00 C 2B D 00 20 00 20 C 2C");
    }
}

public static class Ili9481Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ili9481 tft = new Ili9481(rig.Bus, rig.Reset, true);
        TftCheck.Init("ili9481", tft, rig,
            "C 11 C D0 D 07 42 18 C D1 D 00 07 10 C D2 D 01 02 C C0 D 10 3B 00 02 11 C C5 D 03"
            + " C C8 D 00 32 36 45 06 16 37 75 77 54 0C 00 C 36 D 0A C 3A D 66"
            + " C 2A D 00 00 01 3F C 2B D 00 00 01 DF C 29 C 36 D 0A");
        // 18-bit: magenta 0xF81F -> F8 00 F8.
        int m = rig.Mark;
        tft.DrawPixel(0, 0, 0xF81F);
        Expect.Trace("ili9481 rgb666 pixel", "C 2A D 00 00 00 00 C 2B D 00 00 00 00 C 2C D F8 00 F8", rig.Spi, m);
        m = rig.Mark;
        tft.SetRotation(3);
        Expect.Trace("ili9481 rotation 3 = MV|BGR|SS|GS", "C 36 D 2B", rig.Spi, m);
        Check.Equal("ili9481 landscape width", 480, tft.Width);
    }
}

public static class Ili9486Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ili9486 tft = new Ili9486(rig.Bus, rig.Reset);
        TftCheck.Init("ili9486", tft, rig,
            "C 01 C 11 C 3A D 55 C C2 D 44 C C5 D 00 00 00 00"
            + " C E0 D 0F 1F 1C 0C 0F 08 48 98 37 0A 13 04 11 0D 00"
            + " C E1 D 0F 32 2E 0B 0D 05 47 75 37 06 10 03 24 20 00 C 20 C 36 D 48 C 29 C 36 D 48");
        TftCheck.Window("ili9486 last pixel", tft, rig, 319, 479, 1, 1, "C 2A D 01 3F 01 3F C 2B D 01 DF 01 DF C 2C");
        TftCheck.FillRed("ili9486", tft, rig, "C 2A D 00 00 00 01 C 2B D 00 00 00 01 C 2C");
    }
}

public static class Ili9488Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ili9488 tft = new Ili9488(rig.Bus, rig.Reset);
        TftCheck.Init("ili9488", tft, rig,
            "C E0 D 00 03 09 08 16 0A 3F 78 4C 09 0A 08 16 1A 0F"
            + " C E1 D 00 16 19 03 0F 05 32 45 46 04 0E 0D 35 37 0F"
            + " C C0 D 17 15 C C1 D 41 C C5 D 00 12 80 C 36 D 48 C 3A D 66"
            + " C B0 D 00 C B1 D A0 C B4 D 02 C B6 D 02 02 3B C B7 D C6 C F7 D A9 51 2C 82 C 11 C 29 C 36 D 48");
        // RGB565 0x1234 = r 00010 g 010001 b 10100 -> R8 0x10, G8 0x44, B8 0xA0; white -> F8 FC F8.
        Rgb565Canvas c = new Rgb565Canvas(2, 1);
        c.SetPixel(0, 0, 0x1234);
        c.SetPixel(1, 0, 0xFFFF);
        int m = rig.Mark;
        tft.Flush(c, 0, 0);
        Expect.Trace("ili9488 18-bit flush", "C 2A D 00 00 00 01 C 2B D 00 00 00 00 C 2C D 10 44 A0 F8 FC F8", rig.Spi, m);
        tft.ChunkPixels = 2;
        int t = rig.Spi.Transactions.Count;
        m = rig.Mark;
        tft.FillRect(0, 0, 3, 1, Rgb565.Blue);
        Expect.TracePrefix("ili9488 18-bit fill", "C 2A D 00 00 00 02 C 2B D 00 00 00 00 C 2C D 00 00 F8 00 00 F8 00 00 F8", rig.Spi, m);
        Check.Equal("ili9488 fill bursts", 3 + 2 + 2, rig.Spi.Transactions.Count - t);
    }
}

public static class St7796Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        St7796 tft = new St7796(rig.Bus, rig.Reset);
        TftCheck.Init("st7796s", tft, rig,
            "C 01 C 11 C F0 D C3 C F0 D 96 C 36 D 48 C 3A D 55 C B4 D 01 C B6 D 80 02 3B"
            + " C E8 D 40 8A 00 00 29 19 A5 33 C C1 D 06 C C2 D A7 C C5 D 18"
            + " C E0 D F0 09 0B 06 04 15 2F 54 42 3C 17 14 18 1B"
            + " C E1 D E0 09 0B 06 04 03 2B 43 42 3B 16 14 17 1B C F0 D 3C C F0 D 69 C 29 C 36 D 48");
        TftCheck.FlushDcs("st7796s", tft, rig);
    }
}

public static class Gc9a01Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Gc9a01 tft = new Gc9a01(rig.Bus, rig.Reset);
        TftCheck.Init("gc9a01", tft, rig,
            "C EF C EB D 14 C FE C EF C EB D 14 C 84 D 40 C 85 D FF C 86 D FF C 87 D FF C 88 D 0A C 89 D 21"
            + " C 8A D 00 C 8B D 80 C 8C D 01 C 8D D 01 C 8E D FF C 8F D FF C B6 D 00 00 C 36 D 48 C 3A D 05"
            + " C 90 D 08 08 08 08 C BD D 06 C BC D 00 C FF D 60 01 04 C C3 D 13 C C4 D 13 C C9 D 22"
            + " C BE D 11 C E1 D 10 0E C DF D 21 0C 02"
            + " C F0 D 45 09 08 08 26 2A C F1 D 43 70 72 36 37 6F C F2 D 45 09 08 08 26 2A C F3 D 43 70 72 36 37 6F"
            + " C ED D 1B 0B C AE D 77 C CD D 63 C 70 D 07 07 04 0E 0F 09 07 08 03 C E8 D 34"
            + " C 62 D 18 0D 71 ED 70 70 18 0F 71 EF 70 70 C 63 D 18 11 71 F1 70 70 18 13 71 F3 70 70"
            + " C 64 D 28 29 F1 01 F1 00 07 C 66 D 3C 00 CD 67 45 45 10 00 00 00"
            + " C 67 D 00 3C 00 00 00 01 54 10 32 98 C 74 D 10 85 80 00 00 4E 00 C 98 D 3E 07"
            + " C 35 C 21 C 11 C 29 C 36 D 48");
        TftCheck.Window("gc9a01 window", tft, rig, 120, 120, 1, 1, "C 2A D 00 78 00 78 C 2B D 00 78 00 78 C 2C");
        TftCheck.FlushDcs("gc9a01", tft, rig);
    }
}

public static class Hx8357Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Hx8357 d = new Hx8357(rig.Bus, rig.Reset, true);
        TftCheck.Init("hx8357d", d, rig,
            "C 01 C B9 D FF 83 57 C B3 D 80 00 06 06 C B6 D 25 C B0 D 68 C CC D 05"
            + " C B1 D 00 15 1C 1C 83 AA C C0 D 50 50 01 3C 1E 08 C B4 D 02 40 00 2A 2A 0D 78"
            + " C E0 D 02 0A 11 1D 23 35 41 4B 4B 42 3A 27 1B 08 09 03 02 0A 11 1D 23 35 41 4B 4B 42 3A 27 1B 08 09 03 00 01"
            + " C 3A D 55 C 36 D C0 C 35 D 00 C 44 D 00 02 C 11 C 29 C 36 D C0");
        TftCheck.FlushDcs("hx8357d", d, rig);

        // HX8357-B with no reset pin: SWRESET, then initb.
        SpiRig rb = new SpiRig();
        Hx8357 b = new Hx8357(rb.Bus, null, false);
        b.Begin();
        Expect.Trace("hx8357b init",
            "C 01 C D0 D 44 41 06 C D1 D 40 10 C D2 D 05 12 C C0 D 14 3B 00 02 11 C C5 D 0C C E9 D 01"
            + " C EA D 03 00 00 C EB D 40 54 26 DB C C8 D 00 15 00 22 00 08 77 26 66 22 04 00"
            + " C 36 D C0 C 3A D 55 C 2B D 00 00 01 DF C 2A D 00 00 01 3F C B4 D 00 C 11 C 29 C 36 D C0",
            rb.Spi, 0);
        int m = rb.Mark;
        b.SetRotation(1);
        Expect.Trace("hx8357 rotation 1", "C 36 D A0", rb.Spi, m);
    }
}

public static class Ssd1331Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ssd1331 oled = new Ssd1331(rig.Bus, rig.Reset);
        // Every parameter is a command byte (D/C low).
        TftCheck.Init("ssd1331", oled, rig,
            "C AE A0 72 A1 00 A2 00 A4 A8 3F AD 8E B0 0B B1 31 B3 F0 8A 64 8B 78 8C 64 BB 3A BE 3E 87 06"
            + " 81 91 82 50 83 7D AF A0 72");
        int m = rig.Mark;
        oled.FillRect(2, 3, 1, 1, Rgb565.Green);
        Expect.Trace("ssd1331 window + pixel", "C 15 02 02 75 03 03 D 07 E0", rig.Spi, m);
        TftCheck.Window("ssd1331 window", oled, rig, 2, 3, 4, 5, "C 15 02 05 75 03 07");
        oled.SetRotation(1);
        TftCheck.Window("ssd1331 rot1 window swaps axes", oled, rig, 2, 3, 4, 5, "C 15 03 07 75 02 05");
        m = rig.Mark;
        oled.Invert(true);
        Expect.Trace("ssd1331 invert", "C A7", rig.Spi, m);
    }
}

public static class Ssd1351Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ssd1351 oled = new Ssd1351(rig.Bus, rig.Reset, 128);
        TftCheck.Init("ssd1351", oled, rig,
            "C FD D 12 C FD D B1 C AE C B3 D F1 C CA D 7F C A2 D 00 C B5 D 00 C AB D 01 C B1 D 32 C BE D 05"
            + " C A6 C C1 D C8 80 C8 C C7 D 0F C B4 D A0 B5 55 C B6 D 01 C AF C A0 D 74 C A1 D 80");
        TftCheck.Window("ssd1351 window", oled, rig, 1, 2, 3, 4, "C 15 D 01 03 C 75 D 02 05 C 5C");
        int m = rig.Mark;
        oled.SetRotation(2);
        Expect.Trace("ssd1351 rotation 2", "C A0 D 66 C A1 D 00", rig.Spi, m);
        Rgb565Canvas c = new Rgb565Canvas(1, 1);
        c.SetPixel(0, 0, 0xBEEF);
        m = rig.Mark;
        oled.Flush(c, 0, 0);
        Expect.Trace("ssd1351 flush", "C 15 D 00 00 C 75 D 00 00 C 5C D BE EF", rig.Spi, m);
    }
}

public static class Ili9225Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ili9225 tft = new Ili9225(rig.Bus, rig.Reset);
        TftCheck.Init("ili9225", tft, rig, TftCheck.Regs(
            "10 0000 11 0000 12 0000 13 0000 14 0000"
            + " 11 0018 12 6121 13 006F 14 495F 10 0800 11 103B"
            + " 01 011C 02 0100 03 1030 07 0000 08 0808 0B 1100 0C 0000 0F 0D01 15 0020 20 0000 21 0000"
            + " 30 0000 31 00DB 32 0000 33 0000 34 00DB 35 0000 36 00AF 37 0000 38 00DB 39 0000"
            + " 50 0000 51 0808 52 080A 53 000A 54 0A08 55 0808 56 0000 57 0A00 58 0710 59 0710"
            + " 07 0012 07 1017 03 1030"));
        // Rotation 0, (10,20) 5x4: R36 x-end 14, R37 x-start 10, R38 y-end 23, R39 y-start 20, RAM address (10,20), R22.
        TftCheck.Window("ili9225 window", tft, rig, 10, 20, 5, 4,
            TftCheck.Regs("36 000E 37 000A 38 0017 39 0014 20 000A 21 0014") + " C 00 22");
        // Rotation 1 (landscape 220x176): logical x -> panel y, logical y -> panel x mirrored (175 - y);
        // entry mode AM=1 (vertical first), I/D1=1 (y up), I/D0=0 (x down); start at the window's max x.
        int m = rig.Mark;
        tft.SetRotation(1);
        Expect.Trace("ili9225 rotation 1 entry mode", TftCheck.Regs("03 1028"), rig.Spi, m);
        Check.True("ili9225 landscape", tft.Width == 220 && tft.Height == 176);
        TftCheck.Window("ili9225 rot1 window", tft, rig, 10, 20, 5, 4,
            TftCheck.Regs("36 009B 37 0098 38 000E 39 000A 20 009B 21 000A") + " C 00 22");
        m = rig.Mark;
        tft.FillRect(0, 0, 1, 1, 0xF800);
        Expect.TracePrefix("ili9225 pixel", TftCheck.Regs("36 00AF 37 00AF 38 0000 39 0000 20 00AF 21 0000") + " C 00 22 D F8 00", rig.Spi, m);
    }
}
