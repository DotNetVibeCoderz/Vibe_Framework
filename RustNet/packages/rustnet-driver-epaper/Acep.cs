using RustNet.Community.Bus;
using RustNet.Community.DisplayCore;

namespace RustNet.Community.EPaper;

/// <summary>The seven ACeP inks (plus "clean") as 4-bit codes.</summary>
public static class AcepColor
{
    public const int Black = 0;
    public const int White = 1;
    public const int Green = 2;
    public const int Blue = 3;
    public const int Red = 4;
    public const int Yellow = 5;
    public const int Orange = 6;
    public const int Clean = 7;
}

/// <summary>
/// 4 bits per pixel, two pixels per byte, even x in the high nibble — the
/// frame layout of 7-colour ACeP panels. Starts all white.
/// </summary>
public class Nibble4Canvas : Canvas
{
    public byte[] Buffer;

    public Nibble4Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        Buffer = new byte[(width * height + 1) / 2];
        Clear(AcepColor.White);
    }

    public override void SetPixel(int x, int y, int color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }
        int p = y * Width + x;
        int i = p >> 1;
        if ((p & 1) == 0)
        {
            Buffer[i] = (byte)((Buffer[i] & 0x0F) | ((color & 0x0F) << 4));
        }
        else
        {
            Buffer[i] = (byte)((Buffer[i] & 0xF0) | (color & 0x0F));
        }
    }

    public override int GetPixel(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return 0;
        }
        int p = y * Width + x;
        int b = Buffer[p >> 1];
        return (p & 1) == 0 ? b >> 4 : b & 0x0F;
    }

    public override void Clear(int color)
    {
        byte v = (byte)(((color & 0x0F) << 4) | (color & 0x0F));
        for (int i = 0; i < Buffer.Length; i++)
        {
            Buffer[i] = v;
        }
    }
}

/// <summary>
/// 7-colour ACeP panel, 600×448 (Waveshare 5.65" F). Command set is UC81xx
/// style; the sequence is Waveshare's: wait for BUSY high, PSR EF 08, power
/// setting 37 00 23 23, 0x03 00, booster C7 C7 1D, PLL 3C, TSE 00, CDI 37,
/// TCON 22, resolution 02 58 01 C0, power saving E3 AA, then CDI 37 again.
/// A frame: resolution, DTM1 with 4-bit pixels, power on (wait high),
/// refresh (wait high), power off (wait low).
/// </summary>
public class Acep565 : EPaperDisplay
{
    public Nibble4Canvas Canvas;

    /// <summary>Second view of BUSY for the one wait that waits for the pin to go low.</summary>
    public BusyPin BusyLow;

    public Acep565(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height)
        : base(bus, reset, busy, false, 8, 1, false)
    {
        Width = width;
        Height = height;
        Canvas = new Nibble4Canvas(width, height);
        BusyLow = new BusyPin(busy, true);
    }

    private void Resolution()
    {
        byte[] b = new byte[4];
        b[0] = (byte)(Width >> 8);
        b[1] = (byte)Width;
        b[2] = (byte)(Height >> 8);
        b[3] = (byte)Height;
        Command(0x61, b);
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 1, 200);
        WaitIdle();
        Command(0x00, "EF 08");
        Command(0x01, "37 00 23 23");
        Command(0x03, "00");
        Command(0x06, "C7 C7 1D");
        Command(0x30, "3C");
        Command(0x41, "00");
        Command(0x50, "37");
        Command(0x60, "22");
        Resolution();
        Command(0xE3, "AA");
        Timing.SleepMs(100);
        Command(0x50, "37");
    }

    public override bool Display()
    {
        TimedOut = false;
        Resolution();
        Command(0x10);
        Bus.WriteData(Canvas.Buffer);
        Command(0x04);
        bool ok = WaitIdle();
        Command(0x12);
        ok = WaitIdle() && ok;
        Command(0x02);
        if (!BusyLow.WaitIdle(200))
        {
            TimedOut = true;
            ok = false;
        }
        return ok;
    }

    public override void DeepSleep()
    {
        Timing.SleepMs(100);
        Command(0x07, "A5");
    }
}

/// <summary>
/// Waveshare e-paper modules mapped to their controller and resolution.
/// Each takes the shared SPI bus objects plus RST and BUSY.
/// </summary>
public static class WaveshareEpd
{
    /// <summary>1.54" V2, 200×200, SSD1681.</summary>
    public static Ssd1681 Epd1in54(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy)
        => new Ssd1681(bus, reset, busy, 200, 200, false);

    /// <summary>2.13" V3/V4, 122×250, SSD1680.</summary>
    public static Ssd1680 Epd2in13(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy)
        => new Ssd1680(bus, reset, busy, 122, 250, false);

    /// <summary>2.7" (V1), 176×264, IL91874.</summary>
    public static Il91874 Epd2in7(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy)
        => new Il91874(bus, reset, busy, 176, 264, false);

    /// <summary>2.9" V2, 128×296, SSD1680.</summary>
    public static Ssd1680 Epd2in9(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy)
        => new Ssd1680(bus, reset, busy, 128, 296, false);

    /// <summary>4.2", 400×300, IL0398 (UC8176).</summary>
    public static Il0398 Epd4in2(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy)
        => new Il0398(bus, reset, busy, 400, 300, false);

    /// <summary>5.65" F, 600×448, 7-colour ACeP.</summary>
    public static Acep565 Epd5in65f(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy)
        => new Acep565(bus, reset, busy, 600, 448);

    /// <summary>7.5" V2, 800×480, UC8179.</summary>
    public static Uc8179 Epd7in5(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy)
        => new Uc8179(bus, reset, busy, 800, 480);

    /// <summary>Convenience: build the bus and pins from GPIO numbers (BUSY as a plain input).</summary>
    public static DisplayBus SpiBus(int spiBus, int csPin, int dcPin) => new SpiDisplayBus(spiBus, csPin, dcPin, 4000000);

    public static DigitalOutput ResetPin(int pin) => new DigitalOutput(pin, true);

    public static DigitalInput BusyInput(int pin) => new DigitalInput(pin, RustNet.Hal.PinMode.Input);
}
