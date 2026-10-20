using RustNet.Community.Bus;
using Xunit;

namespace RustNet.Drivers.Tests;

/// <summary>
/// Runs the same smoke scenarios the on-device app runs, on the desktop CLR,
/// so a driver bug shows up here first with a stack trace.
/// </summary>
public class SmokeTests
{
    static SmokeTests()
    {
        Timing.SleepOverride = _ => { };
    }

    private static void Smoke(string name, Action body)
    {
        int before = DriversApp.Check.Failed;
        DriversApp.Check.Run(name, body);
        Assert.Equal(before, DriversApp.Check.Failed);
    }

    [Fact]
    public void DisplayCore() => Smoke("display-core", DriversApp.DisplayCoreSmoke.Run);

    [Fact]
    public void Segments() => Smoke("segments", DriversApp.SegmentsSmoke.Run);

    [Fact]
    public void Tm1637() => Smoke("tm1637", DriversApp.Tm1637Smoke.Run);

    [Fact]
    public void Hd44780() => Smoke("hd44780", DriversApp.Hd44780Smoke.Run);

    [Fact]
    public void Ssd1306() => Smoke("ssd1306", DriversApp.Ssd1306Smoke.Run);

    [Fact]
    public void Ssd1309() => Smoke("ssd1309", DriversApp.Ssd1309Smoke.Run);

    [Fact]
    public void Sh1106() => Smoke("sh1106", DriversApp.Sh1106Smoke.Run);

    [Fact]
    public void Sh1107() => Smoke("sh1107", DriversApp.Sh1107Smoke.Run);

    [Fact]
    public void Ch1115() => Smoke("ch1115", DriversApp.Ch1115Smoke.Run);

    [Fact]
    public void Uc1609() => Smoke("uc1609c", DriversApp.Uc1609Smoke.Run);

    [Fact]
    public void St7565() => Smoke("st7565", DriversApp.St7565Smoke.Run);

    [Fact]
    public void Ili9341() => Smoke("ili9341", DriversApp.Ili9341Smoke.Run);

    [Fact]
    public void St7735() => Smoke("st7735", DriversApp.St7735Smoke.Run);

    [Fact]
    public void St7789() => Smoke("st7789", DriversApp.St7789Smoke.Run);

    [Fact]
    public void Ili9163() => Smoke("ili9163", DriversApp.Ili9163Smoke.Run);

    [Fact]
    public void Ili9481() => Smoke("ili9481", DriversApp.Ili9481Smoke.Run);

    [Fact]
    public void Ili9486() => Smoke("ili9486", DriversApp.Ili9486Smoke.Run);

    [Fact]
    public void Ili9488() => Smoke("ili9488", DriversApp.Ili9488Smoke.Run);

    [Fact]
    public void St7796() => Smoke("st7796s", DriversApp.St7796Smoke.Run);

    [Fact]
    public void Gc9a01() => Smoke("gc9a01", DriversApp.Gc9a01Smoke.Run);

    [Fact]
    public void Hx8357() => Smoke("hx8357", DriversApp.Hx8357Smoke.Run);

    [Fact]
    public void Ssd1331() => Smoke("ssd1331", DriversApp.Ssd1331Smoke.Run);

    [Fact]
    public void Ssd1351() => Smoke("ssd1351", DriversApp.Ssd1351Smoke.Run);

    [Fact]
    public void Ili9225() => Smoke("ili9225", DriversApp.Ili9225Smoke.Run);

    [Fact]
    public void Ssd1681() => Smoke("ssd1681", DriversApp.Ssd1681Smoke.Run);

    [Fact]
    public void Ssd1680() => Smoke("ssd1680", DriversApp.Ssd1680Smoke.Run);

    [Fact]
    public void Ssd1608() => Smoke("ssd1608", DriversApp.Ssd1608Smoke.Run);

    [Fact]
    public void Il3897() => Smoke("il3897", DriversApp.Il3897Smoke.Run);

    [Fact]
    public void Il0373() => Smoke("il0373", DriversApp.Il0373Smoke.Run);

    [Fact]
    public void Il0376f() => Smoke("il0376f", DriversApp.Il0376fSmoke.Run);

    [Fact]
    public void Il0398() => Smoke("il0398", DriversApp.Il0398Smoke.Run);

    [Fact]
    public void Il91874() => Smoke("il91874", DriversApp.Il91874Smoke.Run);

    [Fact]
    public void Uc8151c() => Smoke("uc8151c", DriversApp.Uc8151cSmoke.Run);

    [Fact]
    public void Uc8179() => Smoke("uc8179", DriversApp.Uc8179Smoke.Run);

    [Fact]
    public void Acep565() => Smoke("acep565", DriversApp.Acep565Smoke.Run);
}
