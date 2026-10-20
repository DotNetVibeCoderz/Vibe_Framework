using RustNet.Community.Bus;
using RustNet.Community.DisplayCore;
using RustNet.Community.MonoDisplay;

namespace DriversApp;

/// <summary>Helpers for the page-addressed mono controllers.</summary>
public static class MonoCheck
{
    /// <summary>The traced entry at <paramref name="index"/> past <paramref name="mark"/> must be a data byte of <paramref name="value"/>.</summary>
    public static void DataAt(string what, TraceSpi spi, int mark, int index, int value)
    {
        int at = mark + index;
        int got = at < spi.Trace.Count ? spi.Trace[at] : -1;
        Check.Equal(what, 0x100 | value, got);
    }
}

public static class Ssd1306Smoke
{
    public static void Run()
    {
        // I2C, 128x64: Adafruit_SSD1306 begin() with SSD1306_SWITCHCAPVCC.
        SimI2cDevice i2c = new SimI2cDevice(0x3C);
        i2c.OnWrite = null;
        Ssd1306 oled = new Ssd1306(new I2cDisplayBus(i2c), null, 128, 64);
        oled.Begin();
        Expect.I2c("ssd1306 i2c init",
            "| 00 AE D5 80 A8 3F D3 00 40 8D 14 20 00 A1 C8 DA 12 81 CF D9 F1 DB 40 A4 A6 2E AF", i2c, 0);
        oled.SetPixel(5, 9, true);
        oled.SetPixel(127, 63, true);
        int w = i2c.Writes.Count;
        oled.Flush();
        Check.Equal("ssd1306 i2c flush writes", 2, i2c.Writes.Count - w);
        Expect.Bytes("ssd1306 i2c window", "00 22 00 07 21 00 7F", i2c.Writes[w]);
        byte[] data = i2c.Writes[w + 1];
        Check.Equal("ssd1306 i2c data length", 1025, data.Length);
        Check.Equal("ssd1306 i2c data control byte", 0x40, data[0]);
        Check.Equal("ssd1306 pixel (5,9) -> page 1 col 5 bit 1", 0x02, data[1 + 128 + 5]);
        Check.Equal("ssd1306 pixel (127,63) -> page 7 col 127 bit 7", 0x80, data[1024]);

        // SPI, 128x32: every byte of the init sequence is a command (D/C low), args included.
        SpiRig rig = new SpiRig();
        Ssd1306 spiOled = new Ssd1306(rig.Bus, rig.Reset, 128, 32);
        spiOled.Begin();
        Expect.Trace("ssd1306 spi 128x32 init",
            "C AE D5 80 A8 1F D3 00 40 8D 14 20 00 A1 C8 DA 02 81 8F D9 F1 DB 40 A4 A6 2E AF", rig.Spi, 0);
        Check.True("ssd1306 reset pulsed", rig.Reset.History.Count == 3 && !rig.Reset.History[1]);
        spiOled.SetPixel(0, 31, true);
        int m = rig.Mark;
        spiOled.Flush();
        Expect.TracePrefix("ssd1306 spi window", "C 22 00 03 21 00 7F D 00", rig.Spi, m);
        Check.Equal("ssd1306 spi flush length", 6 + 512, rig.Spi.Trace.Count - m);
        MonoCheck.DataAt("ssd1306 pixel (0,31) -> page 3 col 0 bit 7", rig.Spi, m, 6 + 3 * 128, 0x80);
        m = rig.Mark;
        spiOled.SetContrast(0x40);
        spiOled.Invert(true);
        spiOled.StartScroll(true, 0, 7, 0);
        Expect.Trace("ssd1306 contrast/invert/scroll", "C 81 40 A7 26 00 00 00 07 00 FF 2F", rig.Spi, m);

        // 64x48 glass sits at columns 32..95.
        SpiRig small = new SpiRig();
        Ssd1306 s64 = new Ssd1306(small.Bus, null, 64, 48);
        s64.Flush();
        Expect.TracePrefix("ssd1306 64x48 window", "C 22 00 05 21 20 5F", small.Spi, 0);
    }
}

public static class Ssd1309Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ssd1309 oled = new Ssd1309(rig.Bus, rig.Reset);
        oled.Begin();
        // No 8D charge-pump command; VCOMH 0x34 (0.78 x VCC), contrast 0x7F reset value.
        Expect.Trace("ssd1309 init",
            "C AE D5 A0 A8 3F D3 00 40 20 00 A1 C8 DA 12 81 7F D9 F1 DB 34 A4 A6 2E AF", rig.Spi, 0);
        oled.SetPixel(10, 20, true);
        int m = rig.Mark;
        oled.Flush();
        Expect.TracePrefix("ssd1309 window", "C 22 00 07 21 00 7F", rig.Spi, m);
        MonoCheck.DataAt("ssd1309 pixel (10,20) -> page 2 bit 4", rig.Spi, m, 6 + 2 * 128 + 10, 0x10);
    }
}

public static class Sh1106Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Sh1106 oled = new Sh1106(rig.Bus, rig.Reset, 128, 64);
        oled.Begin();
        Expect.Trace("sh1106 init",
            "C AE D5 80 A8 3F D3 00 40 AD 8B A1 C8 DA 12 81 FF D9 1F DB 40 33 A6 A4 AF", rig.Spi, 0);
        oled.SetPixel(0, 8, true);
        int m = rig.Mark;
        oled.Flush();
        // Page addressing only, glass at RAM columns 2..129: page, column high 0, column low 2.
        Check.Equal("sh1106 flush length", 8 * (3 + 128), rig.Spi.Trace.Count - m);
        Expect.TracePrefix("sh1106 page 0", "C B0 10 02 D 00", rig.Spi, m);
        Expect.TracePrefix("sh1106 page 1", "C B1 10 02 D 01", rig.Spi, m + 131);
        Expect.TracePrefix("sh1106 page 7", "C B7 10 02", rig.Spi, m + 7 * 131);
    }
}

public static class Sh1107Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Sh1107 sq = new Sh1107(rig.Bus, null, 128, 128);
        sq.Begin();
        Expect.Trace("sh1107 128x128 init",
            "C AE D5 51 20 81 4F AD 8A A0 C0 DC 00 D3 00 D9 22 DB 35 A8 7F A4 A6 AF", rig.Spi, 0);

        SpiRig wing = new SpiRig();
        Sh1107 fw = new Sh1107(wing.Bus, null, 64, 128);
        fw.Begin();
        Expect.Trace("sh1107 64x128 init",
            "C AE D5 51 20 81 4F AD 8A A0 C0 DC 00 D3 60 D9 22 DB 35 A8 3F A4 A6 AF", wing.Spi, 0);
        fw.SetPixel(63, 127, true);
        int m = wing.Mark;
        fw.Flush();
        Check.Equal("sh1107 16 pages of 64", 16 * (3 + 64), wing.Spi.Trace.Count - m);
        Expect.TracePrefix("sh1107 page 15", "C BF 10 00", wing.Spi, m + 15 * 67);
        MonoCheck.DataAt("sh1107 pixel (63,127)", wing.Spi, m, 16 * 67 - 1, 0x80);
    }
}

public static class Ch1115Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Ch1115 oled = new Ch1115(rig.Bus, rig.Reset, 88, 48);
        oled.Begin();
        Expect.Trace("ch1115 init",
            "C AE 00 10 B0 40 81 80 A0 A4 A6 A8 2F AD 8B C8 D3 00 D5 A0 D9 22 DB 35 AF", rig.Spi, 0);
        oled.SetPixel(87, 47, true);
        int m = rig.Mark;
        oled.Flush();
        Check.Equal("ch1115 6 pages of 88", 6 * (3 + 88), rig.Spi.Trace.Count - m);
        Expect.TracePrefix("ch1115 page 5", "C B5 00 10 D", rig.Spi, m + 5 * 91);
        MonoCheck.DataAt("ch1115 pixel (87,47)", rig.Spi, m, 6 * 91 - 1, 0x80);
    }
}

public static class Uc1609Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        Uc1609 lcd = new Uc1609(rig.Bus, rig.Reset);
        lcd.Begin();
        // E2 reset; TC 0x24|3; AC 0x88|2; FR 0xA0|1; BR 0xE8|3; PC 0x28|6; 81 PM; mapping 0xC0|4; enable 0xAF.
        Expect.Trace("uc1609 init", "C E2 27 8A A1 EB 2E 81 49 C4 AF", rig.Spi, 0);
        lcd.SetPixel(191, 63, true);
        lcd.SetPixel(17, 0, true);
        int m = rig.Mark;
        lcd.Flush();
        Check.Equal("uc1609 8 pages of 192", 8 * (3 + 192), rig.Spi.Trace.Count - m);
        Expect.TracePrefix("uc1609 page 0", "C 00 10 B0 D", rig.Spi, m);
        Expect.TracePrefix("uc1609 page 7", "C 00 10 B7 D", rig.Spi, m + 7 * 195);
        MonoCheck.DataAt("uc1609 pixel (17,0)", rig.Spi, m, 3 + 17, 0x01);
        MonoCheck.DataAt("uc1609 pixel (191,63)", rig.Spi, m, 8 * 195 - 1, 0x80);
        m = rig.Mark;
        lcd.SetContrast(0x30);
        lcd.Invert(true);
        Expect.Trace("uc1609 contrast/invert", "C 81 30 A7", rig.Spi, m);
    }
}

public static class St7565Smoke
{
    public static void Run()
    {
        SpiRig rig = new SpiRig();
        St7565 lcd = new St7565(rig.Bus, rig.Reset);
        SleepCounter counter = new SleepCounter();
        Timing.SleepOverride = counter.Sleep;
        lcd.Begin();
        Timing.SleepOverride = counter.Ignore;
        // Bias 1/7, ADC normal, COM normal, line 0, power 0x2C/0x2E/0x2F stepwise, Rb/Ra 0x26, on, all-points normal, volume.
        Expect.Trace("st7565 init", "C A3 A0 C0 40 2C 2E 2F 26 AF A4 81 18", rig.Spi, 0);
        Check.True("st7565 power-up waits >= 110 ms", counter.Total >= 110);
        lcd.SetPixel(3, 62, true);
        int m = rig.Mark;
        lcd.Flush();
        Expect.TracePrefix("st7565 page 7", "C B7 10 00 D", rig.Spi, m + 7 * 131);
        MonoCheck.DataAt("st7565 pixel (3,62)", rig.Spi, m, 7 * 131 + 3 + 3, 0x40);
        m = rig.Mark;
        lcd.SetContrast(0x50);
        Expect.Trace("st7565 6-bit volume", "C 81 10", rig.Spi, m);
    }
}
