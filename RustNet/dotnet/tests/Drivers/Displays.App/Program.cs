using RustNet.Community.Bus;

namespace DriversApp;

public static class Program
{
    public static void Main()
    {
        Timing.SleepOverride = ms => { };
        Console.WriteLine("drivers starting");
        Check.Run("display-core", DisplayCoreSmoke.Run);
        Check.Run("segments", SegmentsSmoke.Run);
        Check.Run("tm1637", Tm1637Smoke.Run);
        Check.Run("hd44780", Hd44780Smoke.Run);
        Check.Run("ssd1306", Ssd1306Smoke.Run);
        Check.Run("ssd1309", Ssd1309Smoke.Run);
        Check.Run("sh1106", Sh1106Smoke.Run);
        Check.Run("sh1107", Sh1107Smoke.Run);
        Check.Run("ch1115", Ch1115Smoke.Run);
        Check.Run("uc1609c", Uc1609Smoke.Run);
        Check.Run("st7565", St7565Smoke.Run);
        Check.Run("ili9341", Ili9341Smoke.Run);
        Check.Run("st7735", St7735Smoke.Run);
        Check.Run("st7789", St7789Smoke.Run);
        Check.Run("ili9163", Ili9163Smoke.Run);
        Check.Run("ili9481", Ili9481Smoke.Run);
        Check.Run("ili9486", Ili9486Smoke.Run);
        Check.Run("ili9488", Ili9488Smoke.Run);
        Check.Run("st7796s", St7796Smoke.Run);
        Check.Run("gc9a01", Gc9a01Smoke.Run);
        Check.Run("hx8357", Hx8357Smoke.Run);
        Check.Run("ssd1331", Ssd1331Smoke.Run);
        Check.Run("ssd1351", Ssd1351Smoke.Run);
        Check.Run("ili9225", Ili9225Smoke.Run);
        Check.Run("ssd1681", Ssd1681Smoke.Run);
        Check.Run("ssd1680", Ssd1680Smoke.Run);
        Check.Run("ssd1608", Ssd1608Smoke.Run);
        Check.Run("il3897", Il3897Smoke.Run);
        Check.Run("il0373", Il0373Smoke.Run);
        Check.Run("il0376f", Il0376fSmoke.Run);
        Check.Run("il0398", Il0398Smoke.Run);
        Check.Run("il91874", Il91874Smoke.Run);
        Check.Run("uc8151c", Uc8151cSmoke.Run);
        Check.Run("uc8179", Uc8179Smoke.Run);
        Check.Run("acep565", Acep565Smoke.Run);
        Console.WriteLine($"drivers finished passed={Check.Passed} failed={Check.Failed}");
    }
}
