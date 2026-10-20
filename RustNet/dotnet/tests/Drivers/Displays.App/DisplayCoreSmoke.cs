using RustNet.Community.Bus;
using RustNet.Community.DisplayCore;

namespace DriversApp;

public static class DisplayCoreSmoke
{
    public static void Run()
    {
        Expect.Bytes("hex decode", "0A FF 3C", HexTable.Decode("0a FF\n3c"));

        // Glyph bitmaps of the classic 5x7 font, drawn as they look.
        PageMonoCanvas page = new PageMonoCanvas(32, 16);
        page.Text(0, 0, "A1", MonoColor.On, 1);
        Art.Matches("font A", page, 0, 0, ".###../#...#./#...#./#...#./#####./#...#./#...#./....../");
        Art.Matches("font 1", page, 6, 0, "..#.../.##.../..#.../..#.../..#.../..#.../.###../......");
        Expect.Bytes("font A columns", "7E 11 11 11 7E 00", Art.Slice(page.Buffer, 0, 6));
        page.Clear(MonoColor.Off);
        page.Char(0, 0, 'g', MonoColor.On, 1);
        Art.Matches("font g", page, 0, 0, "...../.####/#...#/#...#/.####/....#/.###./.....");
        page.Clear(MonoColor.Off);
        page.Char(0, 0, 'T', MonoColor.On, 2);
        Art.Matches("font T x2", page, 0, 0, "##########/##########/....##..../....##....");

        // Page layout: byte x + (y/8)*W, bit y%8.
        PageMonoCanvas oled = new PageMonoCanvas(128, 64);
        oled.SetPixel(0, 0, MonoColor.On);
        oled.SetPixel(5, 9, MonoColor.On);
        oled.SetPixel(127, 63, MonoColor.On);
        Check.Equal("page (0,0)", 0x01, oled.Buffer[0]);
        Check.Equal("page (5,9)", 0x02, oled.Buffer[5 + 128]);
        Check.Equal("page (127,63)", 0x80, oled.Buffer[1023]);
        oled.SetPixel(5, 9, MonoColor.Invert);
        Check.Equal("page invert", 0x00, oled.Buffer[5 + 128]);

        // Row layout: byte y*stride + x/8, MSB = leftmost.
        RowMonoCanvas row = new RowMonoCanvas(16, 4);
        row.SetPixel(0, 0, MonoColor.On);
        row.SetPixel(9, 1, MonoColor.On);
        row.SetPixel(15, 3, MonoColor.On);
        Check.Equal("row (0,0)", 0x80, row.Buffer[0]);
        Check.Equal("row (9,1)", 0x40, row.Buffer[3]);
        Check.Equal("row (15,3)", 0x01, row.Buffer[7]);

        // RGB565: 0x12,0x34,0x56 -> r 00010 g 001101 b 01010 = 0x11AA; wire order high byte first.
        Check.Equal("rgb565 red", 0xF800, Rgb565.FromRgb(255, 0, 0));
        Check.Equal("rgb565 mix", 0x11AA, Rgb565.FromRgb(0x12, 0x34, 0x56));
        Rgb565Canvas tft = new Rgb565Canvas(2, 2);
        tft.SetPixel(1, 0, 0x11AA);
        Check.Equal("rgb565 hi byte", 0x11, tft.Buffer[2]);
        Check.Equal("rgb565 lo byte", 0xAA, tft.Buffer[3]);
        Check.Equal("rgb666 r", 0xF8, Rgb565.Red8(0xF800));
        Check.Equal("rgb666 g", 0xFC, Rgb565.Green8(0x07E0));
        Check.Equal("rgb666 b", 0xF8, Rgb565.Blue8(0x001F));

        // Primitives.
        PageMonoCanvas g = new PageMonoCanvas(12, 12);
        g.Line(0, 0, 3, 1, MonoColor.On);
        Art.Matches("line", g, 0, 0, "##../..##");
        Check.Equal("line pixels", 4, Art.CountOn(g));
        g.Clear(MonoColor.Off);
        g.Circle(5, 5, 2, MonoColor.On);
        Check.Equal("circle r2 pixels", 12, Art.CountOn(g));
        Check.Equal("circle centre", 0, g.GetPixel(5, 5));
        Check.Equal("circle east", 1, g.GetPixel(7, 5));
        g.Clear(MonoColor.Off);
        g.FillCircle(5, 5, 2, MonoColor.On);
        Check.Equal("fill circle r2 lattice points", 13, Art.CountOn(g));
        g.Clear(MonoColor.Off);
        g.Rect(1, 1, 4, 3, MonoColor.On);
        Check.Equal("rect outline", 10, Art.CountOn(g));
        g.FillRect(-2, -2, 4, 4, MonoColor.On);
        Check.Equal("fill rect clipped", 13, Art.CountOn(g));

        // SPI bus: D/C written only on change.
        TraceSpi spi = new TraceSpi();
        SimDigitalOutput dc = new SimDigitalOutput();
        spi.Dc = dc;
        SpiDisplayBus bus = new SpiDisplayBus(spi, dc);
        bus.Command(0x2A);
        bus.DataByte(1);
        bus.DataByte(2);
        bus.Command(0x2C);
        Expect.Trace("spi bus", "C 2A D 01 02 C 2C", spi, 0);
        Check.Equal("dc writes", 3, dc.History.Count);
        bus.MaxTransfer = 2;
        int t = spi.Transactions.Count;
        byte[] five = HexTable.Decode("0102030405");
        bus.WriteData(five);
        Check.Equal("spi chunks", 3, spi.Transactions.Count - t);

        // I2C bus: control byte 0x00 for commands, 0x40 for data.
        SimI2cDevice i2c = new SimI2cDevice(0x3C);
        I2cDisplayBus ib = new I2cDisplayBus(i2c);
        ib.Command(0xAE);
        ib.Command(0x81, 0x7F);
        ib.WriteData(five, 1, 3);
        ib.MaxChunk = 2;
        ib.WriteData(five, 0, 3);
        Expect.I2c("i2c framing", "| 00 AE | 00 81 7F | 40 02 03 04 | 40 01 02 | 40 03", i2c, 0);

        // Busy pin: idle after three busy reads; a stuck pin times out on a fake clock.
        SimDigitalInput busyIn = new SimDigitalInput();
        int reads = 0;
        busyIn.Source = () => { reads++; return reads <= 3; };
        long clock = 0;
        Timing.ClockOverride = () => { clock += 5; return clock; };
        BusyPin busy = new BusyPin(busyIn, true);
        Check.True("busy wait ends", busy.WaitIdle(0));
        Check.Equal("busy reads", 4, reads);
        SimDigitalInput stuck = new SimDigitalInput();
        stuck.Level = false;
        stuck.Source = null;
        BusyPin busyLow = new BusyPin(stuck, false);
        busyLow.TimeoutMs = 100;
        Check.True("busy timeout", !busyLow.WaitIdle(0));
        Check.Equal("busy timeouts", 1, busyLow.Timeouts);
        Timing.ClockOverride = null;

        // Reset pulse.
        SimDigitalOutput rst = new SimDigitalOutput();
        DisplayPins.ResetPulse(rst, 10, 10);
        Check.True("reset high-low-high", rst.History.Count == 3 && rst.History[0] && !rst.History[1] && rst.History[2]);
    }
}
