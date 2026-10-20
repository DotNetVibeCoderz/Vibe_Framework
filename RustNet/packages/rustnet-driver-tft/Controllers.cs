using RustNet.Community.Bus;
using RustNet.Community.DisplayCore;

namespace RustNet.Community.Tft;

// MADCTL bits: MY 0x80, MX 0x40, MV 0x20, ML 0x10, BGR 0x08, MH 0x04
// (ILI9481 also uses SS 0x02 / GS 0x01 for horizontal/vertical flip).

/// <summary>
/// ILI9341, 240×320. Power-up table from Adafruit_ILI9341 (vendor
/// undocumented EF/CF/ED/E8/CB/F7/EA registers, power/VCOM, MADCTL 0x48,
/// COLMOD 0x55 = 16 bit, frame rate, gamma, sleep out, display on).
/// Rotation MADCTL: MX|BGR, MV|BGR, MY|BGR, MX|MY|MV|BGR.
/// </summary>
public class Ili9341 : TftDisplay
{
    public Ili9341(int spiBus, int csPin, int dcPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 40000000), new DigitalOutput(resetPin, true))
    {
    }

    public Ili9341(DisplayBus bus, DigitalOutput? reset) : base(bus, reset, 240, 320)
    {
        SetMadctl(0x48, 0x28, 0x88, 0xE8);
    }

    public override string InitTable() =>
        "EF 03 03 80 02  CF 03 00 C1 30  ED 04 64 03 12 81  E8 03 85 00 78"
        + " CB 05 39 2C 00 34 02  F7 01 20  EA 02 00 00"
        + " C0 01 23  C1 01 10  C5 02 3E 28  C7 01 86  36 01 48  37 01 00  3A 01 55"
        + " B1 02 00 18  B6 03 08 82 27  F2 01 00  26 01 01"
        + " E0 0F 0F 31 2B 0C 0E 08 4E F1 37 07 10 03 0E 09 00"
        + " E1 0F 00 0E 14 03 11 07 31 C1 48 08 0F 0C 31 36 0F"
        + " 11 80 96  29 80 96";
}

/// <summary>ST7735 panel variants ("tabs" on the protective film), as in Adafruit_ST7735.</summary>
public static class St7735Tab
{
    /// <summary>1.8" 128×160, green tab: RAM offset (2, 1), BGR.</summary>
    public const int Green = 0;

    /// <summary>1.8" 128×160, red tab: no offset, BGR.</summary>
    public const int Red = 1;

    /// <summary>1.8" 128×160, black tab: no offset, RGB order.</summary>
    public const int Black = 2;

    /// <summary>1.44" 128×128, green tab: RAM offset (2, 3), BGR.</summary>
    public const int Green144 = 3;

    /// <summary>0.96" 80×160 mini: RAM offset (24, 0), RGB order.</summary>
    public const int Mini160x80 = 4;
}

/// <summary>
/// Sitronix ST7735R/S. Init = Adafruit's Rcmd1 (SWRESET, SLPOUT, frame rate
/// B1–B3, inversion B4, power C0–C4, VCOM C5, INVOFF, MADCTL C8, COLMOD 05),
/// the per-tab Rcmd2 (CASET/RASET for the glass), then Rcmd3 (gamma, NORON,
/// DISPON). Rotation MADCTL: MX|MY, MY|MV, none, MX|MV, plus BGR except on
/// black-tab and mini panels.
/// </summary>
public class St7735 : TftDisplay
{
    public int Tab;

    public St7735(int spiBus, int csPin, int dcPin, int resetPin, int tab)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 16000000), new DigitalOutput(resetPin, true), tab)
    {
    }

    public St7735(DisplayBus bus, DigitalOutput? reset, int tab)
        : base(bus, reset, tab == St7735Tab.Mini160x80 ? 80 : 128, tab == St7735Tab.Green144 ? 128 : 160)
    {
        Tab = tab;
        SoftResetWithoutPin = false; // Rcmd1 starts with SWRESET
        int bgr = tab == St7735Tab.Black || tab == St7735Tab.Mini160x80 ? 0x00 : 0x08;
        SetMadctl(0xC0 | bgr, 0xA0 | bgr, 0x00 | bgr, 0x60 | bgr);
        if (tab == St7735Tab.Green)
        {
            SetOffsets(2, 1, 2, 1);
        }
        else if (tab == St7735Tab.Green144)
        {
            SetOffsets(2, 3, 2, 3);
        }
        else if (tab == St7735Tab.Mini160x80)
        {
            SetOffsets(24, 0, 24, 0);
        }
    }

    public const string Rcmd1 =
        "01 80 96  11 80 FF  B1 03 01 2C 2D  B2 03 01 2C 2D  B3 06 01 2C 2D 01 2C 2D"
        + " B4 01 07  C0 03 A2 02 84  C1 01 C5  C2 02 0A 00  C3 02 8A 2A  C4 02 8A EE"
        + " C5 01 0E  20 00  36 01 C8  3A 01 05 ";

    public const string Rcmd3 =
        " E0 10 02 1C 07 12 37 32 29 2D 29 25 2B 39 00 01 03 10"
        + " E1 10 03 1D 07 06 2E 2C 29 2D 2E 2E 37 3F 00 00 02 10"
        + " 13 80 0A  29 80 64";

    public override string InitTable()
    {
        string rcmd2;
        if (Tab == St7735Tab.Green)
        {
            rcmd2 = "2A 04 00 02 00 81  2B 04 00 01 00 A0";
        }
        else if (Tab == St7735Tab.Green144)
        {
            rcmd2 = "2A 04 00 00 00 7F  2B 04 00 00 00 7F";
        }
        else if (Tab == St7735Tab.Mini160x80)
        {
            rcmd2 = "2A 04 00 00 00 4F  2B 04 00 00 00 9F";
        }
        else
        {
            rcmd2 = "2A 04 00 00 00 7F  2B 04 00 00 00 9F";
        }
        string t = Rcmd1 + rcmd2 + Rcmd3;
        if (Tab == St7735Tab.Black)
        {
            t = t + "  36 01 C0";
        }
        return t;
    }
}

/// <summary>
/// Sitronix ST7789(V), 240×320 or 240×240 / 135×240 / 170×320 / 172×320
/// glass on the 240×320 RAM. Init = Adafruit generic_st7789 (SWRESET, SLPOUT,
/// COLMOD 55, MADCTL, full CASET/RASET, INVON — these IPS panels need
/// inversion — NORON, DISPON). Offsets as Adafruit computes them: 240×240 is
/// bottom-aligned (rows 80..319 in rotation 0), others centred.
/// </summary>
public class St7789 : TftDisplay
{
    public St7789(int spiBus, int csPin, int dcPin, int resetPin, int width, int height)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 40000000), new DigitalOutput(resetPin, true), width, height)
    {
    }

    public St7789(DisplayBus bus, DigitalOutput? reset, int width, int height) : base(bus, reset, width, height)
    {
        SoftResetWithoutPin = false;
        SetMadctl(0xC0, 0xA0, 0x00, 0x60);
        if (width == 240 && height == 240)
        {
            SetOffsets(0, 320 - height, 0, 0);
        }
        else if (width == 135 && height == 240)
        {
            SetOffsets((240 - width + 1) / 2, (320 - height) / 2, (240 - width) / 2, (320 - height) / 2);
        }
        else
        {
            SetOffsets((240 - width) / 2, (320 - height) / 2, (240 - width) / 2, (320 - height) / 2);
        }
    }

    public override string InitTable() =>
        "01 80 96  11 80 0A  3A 81 55 0A  36 01 08"
        + " 2A 04 00 00 00 F0  2B 04 00 00 01 40"
        + " 21 80 0A  13 80 0A  29 80 0A";
}

/// <summary>
/// ILI9163(C), 128×128 glass on a 132×162 RAM. Table after the TFT_ILI9163C
/// driver: SWRESET, SLPOUT, COLMOD 05, gamma curve 4 + gamma adjust enable,
/// NORON, display function, positive/negative gamma, frame rate, inversion,
/// power, VCOM, DISPON. Red-PCB 1.44" modules map the glass 32 rows down
/// when mirrored; set <see cref="TftDisplay.RowStart2"/> = 32 for those.
/// </summary>
public class Ili9163 : TftDisplay
{
    public Ili9163(int spiBus, int csPin, int dcPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 16000000), new DigitalOutput(resetPin, true), 128, 128)
    {
    }

    public Ili9163(DisplayBus bus, DigitalOutput? reset, int width, int height) : base(bus, reset, width, height)
    {
        SoftResetWithoutPin = false;
        SetMadctl(0x08, 0x68, 0xC8, 0xA8);
    }

    public override string InitTable() =>
        "01 80 FF  11 80 05  3A 01 05  26 01 04  F2 01 01  13 00  B6 02 FF 06"
        + " E0 0F 36 29 12 22 1C 15 42 B7 2F 13 12 0A 11 0B 06"
        + " E1 0F 09 16 2D 0D 13 15 40 48 53 0C 1D 25 2E 34 39"
        + " B1 02 08 02  B4 01 07  C0 02 0A 02  C1 01 02  C5 02 50 5B  C7 01 40"
        + " 29 80 0A";
}

/// <summary>
/// ILI9481, 320×480. Table from TFT_eSPI's ILI9481 init (SLPOUT, power D0–D2,
/// panel C0, frame C5, gamma C8, MADCTL, COLMOD, full window, DISPON). Over
/// SPI the ILI9481 accepts only 18-bit pixels (COLMOD 0x66, 3 bytes per
/// pixel); pass <c>rgb666: false</c> for 16-bit parallel-style bridges.
/// Rotation MADCTL: BGR|SS, MV|BGR, BGR|GS, MV|BGR|SS|GS.
/// </summary>
public class Ili9481 : TftDisplay
{
    public Ili9481(int spiBus, int csPin, int dcPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 20000000), new DigitalOutput(resetPin, true), true)
    {
    }

    public Ili9481(DisplayBus bus, DigitalOutput? reset, bool rgb666) : base(bus, reset, 320, 480)
    {
        Rgb666 = rgb666;
        SetMadctl(0x0A, 0x28, 0x09, 0x2B);
    }

    public override string InitTable() =>
        "11 80 14  D0 03 07 42 18  D1 03 00 07 10  D2 02 01 02  C0 05 10 3B 00 02 11  C5 01 03"
        + " C8 0C 00 32 36 45 06 16 37 75 77 54 0C 00  36 01 0A  3A 01 " + (Rgb666 ? "66" : "55")
        + " 2A 04 00 00 01 3F  2B 84 00 00 01 DF 78  29 80 19";
}

/// <summary>
/// ILI9486, 320×480, 16-bit (TFT_eSPI init: SWRESET, SLPOUT, COLMOD 55, power
/// C2, VCOM C5, gamma E0/E1, INVOFF, MADCTL 48, DISPON). Rotation as ILI9341.
/// </summary>
public class Ili9486 : TftDisplay
{
    public Ili9486(int spiBus, int csPin, int dcPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 20000000), new DigitalOutput(resetPin, true))
    {
    }

    public Ili9486(DisplayBus bus, DigitalOutput? reset) : base(bus, reset, 320, 480)
    {
        SoftResetWithoutPin = false;
        SetMadctl(0x48, 0x28, 0x88, 0xE8);
    }

    public override string InitTable() =>
        "01 80 78  11 80 78  3A 01 55  C2 01 44  C5 04 00 00 00 00"
        + " E0 0F 0F 1F 1C 0C 0F 08 48 98 37 0A 13 04 11 0D 00"
        + " E1 0F 0F 32 2E 0B 0D 05 47 75 37 06 10 03 24 20 00"
        + " 20 00  36 01 48  29 80 96";
}

/// <summary>
/// ILI9488, 320×480. Over SPI it only supports 18-bit colour (COLMOD 0x66):
/// every pixel goes out as three bytes R, G, B with the channel in the top 6
/// bits. Table from TFT_eSPI (gamma, power, VCOM, MADCTL 48, COLMOD 66,
/// interface/frame/inversion/function/entry/adjust-control, SLPOUT, DISPON).
/// </summary>
public class Ili9488 : TftDisplay
{
    public Ili9488(int spiBus, int csPin, int dcPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 20000000), new DigitalOutput(resetPin, true))
    {
    }

    public Ili9488(DisplayBus bus, DigitalOutput? reset) : base(bus, reset, 320, 480)
    {
        Rgb666 = true;
        SetMadctl(0x48, 0x28, 0x88, 0xE8);
    }

    public override string InitTable() =>
        "E0 0F 00 03 09 08 16 0A 3F 78 4C 09 0A 08 16 1A 0F"
        + " E1 0F 00 16 19 03 0F 05 32 45 46 04 0E 0D 35 37 0F"
        + " C0 02 17 15  C1 01 41  C5 03 00 12 80  36 01 48  3A 01 66"
        + " B0 01 00  B1 01 A0  B4 01 02  B6 03 02 02 3B  B7 01 C6  F7 04 A9 51 2C 82"
        + " 11 80 78  29 80 19";
}

/// <summary>
/// Sitronix ST7796S, 320×480 (TFT_eSPI init: SWRESET, SLPOUT, unlock command
/// set 2 with F0 C3 / F0 96, MADCTL 48, COLMOD 55, inversion, display
/// function, output adjust E8, power C1/C2, VCOM C5, gamma, lock again with
/// F0 3C / F0 69, DISPON). Rotation as ILI9341.
/// </summary>
public class St7796 : TftDisplay
{
    public St7796(int spiBus, int csPin, int dcPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 40000000), new DigitalOutput(resetPin, true))
    {
    }

    public St7796(DisplayBus bus, DigitalOutput? reset) : base(bus, reset, 320, 480)
    {
        SoftResetWithoutPin = false;
        SetMadctl(0x48, 0x28, 0x88, 0xE8);
    }

    public override string InitTable() =>
        "01 80 78  11 80 78  F0 01 C3  F0 01 96  36 01 48  3A 01 55  B4 01 01  B6 03 80 02 3B"
        + " E8 08 40 8A 00 00 29 19 A5 33  C1 01 06  C2 01 A7  C5 81 18 78"
        + " E0 0E F0 09 0B 06 04 15 2F 54 42 3C 17 14 18 1B"
        + " E1 8E E0 09 0B 06 04 03 2B 43 42 3B 16 14 17 1B 78"
        + " F0 01 3C  F0 81 69 78  29 00";
}

/// <summary>
/// GalaxyCore GC9A01(A), 240×240 round. Table from Adafruit_GC9A01A (inter
/// register enable FE/EF, the vendor 0x84–0x8F/0x62–0x74 analog settings,
/// MADCTL MX|BGR, COLMOD 05, power C3/C4/C9, gamma F0–F3, frame rate E8,
/// TEON, INVON, SLPOUT, DISPON). Rotation as ILI9341.
/// </summary>
public class Gc9a01 : TftDisplay
{
    public Gc9a01(int spiBus, int csPin, int dcPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 40000000), new DigitalOutput(resetPin, true))
    {
    }

    public Gc9a01(DisplayBus bus, DigitalOutput? reset) : base(bus, reset, 240, 240)
    {
        SetMadctl(0x48, 0x28, 0x88, 0xE8);
    }

    public override string InitTable() =>
        "EF 00  EB 01 14  FE 00  EF 00  EB 01 14  84 01 40  85 01 FF  86 01 FF  87 01 FF"
        + " 88 01 0A  89 01 21  8A 01 00  8B 01 80  8C 01 01  8D 01 01  8E 01 FF  8F 01 FF"
        + " B6 02 00 00  36 01 48  3A 01 05  90 04 08 08 08 08  BD 01 06  BC 01 00"
        + " FF 03 60 01 04  C3 01 13  C4 01 13  C9 01 22  BE 01 11  E1 02 10 0E  DF 03 21 0C 02"
        + " F0 06 45 09 08 08 26 2A  F1 06 43 70 72 36 37 6F"
        + " F2 06 45 09 08 08 26 2A  F3 06 43 70 72 36 37 6F"
        + " ED 02 1B 0B  AE 01 77  CD 01 63  70 09 07 07 04 0E 0F 09 07 08 03  E8 01 34"
        + " 62 0C 18 0D 71 ED 70 70 18 0F 71 EF 70 70"
        + " 63 0C 18 11 71 F1 70 70 18 13 71 F3 70 70"
        + " 64 07 28 29 F1 01 F1 00 07"
        + " 66 0A 3C 00 CD 67 45 45 10 00 00 00"
        + " 67 0A 00 3C 00 00 00 01 54 10 32 98"
        + " 74 07 10 85 80 00 00 4E 00  98 02 3E 07"
        + " 35 00  21 00  11 80 96  29 80 96";
}

/// <summary>
/// Himax HX8357-B and HX8357-D, 320×480 (Adafruit_HX8357 initb / initd).
/// The D needs the B9 FF 83 57 "enable extension commands" key first. Pixels
/// 16-bit; rotation MADCTL MX|MY, MV|MY, none, MX|MV (RGB).
/// </summary>
public class Hx8357 : TftDisplay
{
    public bool IsD;

    public Hx8357(int spiBus, int csPin, int dcPin, int resetPin, bool hx8357d)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 16000000), new DigitalOutput(resetPin, true), hx8357d)
    {
    }

    public Hx8357(DisplayBus bus, DigitalOutput? reset, bool hx8357d) : base(bus, reset, 320, 480)
    {
        IsD = hx8357d;
        SoftResetWithoutPin = !hx8357d; // initd starts with its own SWRESET
        SetMadctl(0xC0, 0xA0, 0x00, 0x60);
    }

    public const string InitD =
        "01 80 0A  B9 03 FF 83 57  00 FF FA  00 FF FA"
        + " B3 04 80 00 06 06  B6 01 25  B0 01 68  CC 01 05"
        + " B1 06 00 15 1C 1C 83 AA  C0 06 50 50 01 3C 1E 08  B4 07 02 40 00 2A 2A 0D 78"
        + " E0 22 02 0A 11 1D 23 35 41 4B 4B 42 3A 27 1B 08 09 03 02 0A 11 1D 23 35 41 4B 4B 42 3A 27 1B 08 09 03 00 01"
        + " 3A 01 55  36 01 C0  35 01 00  44 02 00 02  11 80 96  29 80 32";

    public const string InitB =
        "D0 03 44 41 06  D1 02 40 10  D2 02 05 12  C0 05 14 3B 00 02 11  C5 01 0C  E9 01 01"
        + " EA 03 03 00 00  EB 04 40 54 26 DB  C8 0C 00 15 00 22 00 08 77 26 66 22 04 00"
        + " 36 01 C0  3A 01 55  2B 04 00 00 01 DF  2A 04 00 00 01 3F  B4 01 00"
        + " 11 80 78  29 80 0A";

    public override string InitTable() => IsD ? InitD : InitB;
}

/// <summary>
/// Solomon Systech SSD1331, 96×64 RGB OLED. Unlike MIPI panels every
/// parameter is sent as a command byte (D/C low); the window is set with
/// 0x15 (column start, end) and 0x75 (row start, end) and pixels follow as
/// data with no RAM-write command. Table from Adafruit_SSD1331; remap 0x72 =
/// 65k colour, COM split, column remap. Rotation via remap 0x72/0x73/0x60/0x61
/// (bit 0 = vertical address increment, so x and y swap in rotations 1, 3).
/// </summary>
public class Ssd1331 : TftDisplay
{
    public Ssd1331(int spiBus, int csPin, int dcPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 8000000), new DigitalOutput(resetPin, true))
    {
    }

    public Ssd1331(DisplayBus bus, DigitalOutput? reset) : base(bus, reset, 96, 64)
    {
        SoftResetWithoutPin = false;
        SetMadctl(0x72, 0x73, 0x60, 0x61);
    }

    public override string InitTable() =>
        "AE 00  A0 01 72  A1 01 00  A2 01 00  A4 00  A8 01 3F  AD 01 8E  B0 01 0B  B1 01 31"
        + " B3 01 F0  8A 01 64  8B 01 78  8C 01 64  BB 01 3A  BE 01 3E  87 01 06"
        + " 81 01 91  82 01 50  83 01 7D  AF 00";

    public override void CommandWithArgs(int command, byte[] args, int offset, int count)
    {
        byte[] b = new byte[count + 1];
        b[0] = (byte)command;
        for (int i = 0; i < count; i++)
        {
            b[i + 1] = args[offset + i];
        }
        Bus.WriteCommands(b);
    }

    public override void SetRotation(int rotation)
    {
        Rotation = rotation & 3;
        Bus.Command(0xA0, Madctl[Rotation]);
        ApplyRotationGeometry();
    }

    public override void SetWindow(int x, int y, int w, int h)
    {
        int x0 = x;
        int y0 = y;
        int x1 = x + w - 1;
        int y1 = y + h - 1;
        if ((Rotation & 1) != 0)
        {
            int t = x0;
            x0 = y0;
            y0 = t;
            t = x1;
            x1 = y1;
            y1 = t;
        }
        byte[] b = new byte[6];
        b[0] = 0x15;
        b[1] = (byte)x0;
        b[2] = (byte)x1;
        b[3] = 0x75;
        b[4] = (byte)y0;
        b[5] = (byte)y1;
        Bus.WriteCommands(b);
    }

    public override void Invert(bool on) => Bus.Command(on ? 0xA7 : 0xA4);

    public override void DisplayOn(bool on) => Bus.Command(on ? 0xAF : 0xAE);

    public override void Sleep(bool sleep) => DisplayOn(!sleep);
}

/// <summary>
/// Solomon Systech SSD1351, 128×128 (or 128×96) RGB OLED. Parameters are data
/// bytes; window 0x15 [x0 x1], 0x75 [y0 y1], then write-RAM 0x5C. Table from
/// Adafruit_SSD1351 (unlock FD 12 / FD B1, clock, mux, offset, GPIO, function
/// select, pre-charge, VCOMH, contrast, VSL, second pre-charge, display on).
/// Rotation: remap 0x74/0x77/0x66/0x65 and start line (height or 0).
/// </summary>
public class Ssd1351 : TftDisplay
{
    public Ssd1351(int spiBus, int csPin, int dcPin, int resetPin, int height)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 16000000), new DigitalOutput(resetPin, true), height)
    {
    }

    public Ssd1351(DisplayBus bus, DigitalOutput? reset, int height) : base(bus, reset, 128, height)
    {
        SoftResetWithoutPin = false;
        SetMadctl(0x74, 0x77, 0x66, 0x65);
    }

    public override string InitTable() =>
        "FD 01 12  FD 01 B1  AE 00  B3 01 F1  CA 01 7F  A2 01 00  B5 01 00  AB 01 01"
        + " B1 01 32  BE 01 05  A6 00  C1 03 C8 80 C8  C7 01 0F  B4 03 A0 B5 55  B6 01 01  AF 00";

    public override void SetRotation(int rotation)
    {
        Rotation = rotation & 3;
        byte[] b = new byte[1];
        b[0] = (byte)Madctl[Rotation];
        CommandWithArgs(0xA0, b, 0, 1);
        b[0] = (byte)(Rotation < 2 ? NativeHeight : 0);
        CommandWithArgs(0xA1, b, 0, 1);
        ApplyRotationGeometry();
    }

    public override void SetWindow(int x, int y, int w, int h)
    {
        int x0 = x;
        int y0 = y;
        int x1 = x + w - 1;
        int y1 = y + h - 1;
        if ((Rotation & 1) != 0)
        {
            int t = x0;
            x0 = y0;
            y0 = t;
            t = x1;
            x1 = y1;
            y1 = t;
        }
        byte[] b = new byte[2];
        b[0] = (byte)x0;
        b[1] = (byte)x1;
        CommandWithArgs(0x15, b, 0, 2);
        b[0] = (byte)y0;
        b[1] = (byte)y1;
        CommandWithArgs(0x75, b, 0, 2);
        Bus.Command(0x5C);
    }

    public override void Invert(bool on) => Bus.Command(on ? 0xA7 : 0xA6);

    public override void DisplayOn(bool on) => Bus.Command(on ? 0xAF : 0xAE);

    public override void Sleep(bool sleep) => DisplayOn(!sleep);
}

/// <summary>
/// ILI9225(G), 176×220. A register interface rather than DCS: each write is
/// a 16-bit register index (D/C low) and a 16-bit value (D/C high). The
/// window is R36/R37 (horizontal end/start), R38/R39 (vertical end/start),
/// the start address R20/R21, then index R22 streams pixels. Power-up
/// sequence from the TFT_22_ILI9225 library. Rotation is done in the entry
/// mode register R03 (I/D0 horizontal, I/D1 vertical increment, AM = vertical
/// first) and by mapping the window: 0x1030, 0x1028, 0x1000, 0x1018.
/// </summary>
public class Ili9225 : TftDisplay
{
    public Ili9225(int spiBus, int csPin, int rsPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, rsPin, 20000000), new DigitalOutput(resetPin, true))
    {
    }

    public Ili9225(DisplayBus bus, DigitalOutput? reset) : base(bus, reset, 176, 220)
    {
        SetMadctl(0x1030, 0x1028, 0x1000, 0x1018);
    }

    /// <summary>Register writes as reg, value-hi, value-lo; reg 0xFE = delay of value-lo ms.</summary>
    public override string InitTable() =>
        "10 00 00  11 00 00  12 00 00  13 00 00  14 00 00  FE 00 28"
        + " 11 00 18  12 61 21  13 00 6F  14 49 5F  10 08 00  FE 00 0A  11 10 3B  FE 00 32"
        + " 01 01 1C  02 01 00  03 10 30  07 00 00  08 08 08  0B 11 00  0C 00 00  0F 0D 01  15 00 20"
        + " 20 00 00  21 00 00"
        + " 30 00 00  31 00 DB  32 00 00  33 00 00  34 00 DB  35 00 00  36 00 AF  37 00 00  38 00 DB  39 00 00"
        + " 50 00 00  51 08 08  52 08 0A  53 00 0A  54 0A 08  55 08 08  56 00 00  57 0A 00  58 07 10  59 07 10"
        + " 07 00 12  FE 00 32  07 10 17";

    public void WriteRegister(int register, int value)
    {
        byte[] idx = new byte[2];
        idx[0] = 0x00;
        idx[1] = (byte)register;
        Bus.WriteCommands(idx);
        byte[] v = new byte[2];
        v[0] = (byte)(value >> 8);
        v[1] = (byte)value;
        Bus.WriteData(v);
    }

    public override void Begin()
    {
        DisplayPins.ResetPulse(Reset, 10, 50);
        byte[] t = HexTable.Decode(InitTable());
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            if (t[i] == 0xFE)
            {
                Timing.SleepMs(t[i + 2]);
            }
            else
            {
                WriteRegister(t[i], (t[i + 1] << 8) | t[i + 2]);
            }
        }
        SetRotation(Rotation);
    }

    public override void SetRotation(int rotation)
    {
        Rotation = rotation & 3;
        WriteRegister(0x03, Madctl[Rotation]);
        ApplyRotationGeometry();
    }

    public override void SetWindow(int x, int y, int w, int h)
    {
        int lx0 = x;
        int ly0 = y;
        int lx1 = x + w - 1;
        int ly1 = y + h - 1;
        int maxX = NativeWidth - 1;
        int maxY = NativeHeight - 1;
        int px0;
        int px1;
        int py0;
        int py1;
        int sx;
        int sy;
        if (Rotation == 0)
        {
            px0 = lx0;
            px1 = lx1;
            py0 = ly0;
            py1 = ly1;
            sx = px0;
            sy = py0;
        }
        else if (Rotation == 1)
        {
            px0 = maxX - ly1;
            px1 = maxX - ly0;
            py0 = lx0;
            py1 = lx1;
            sx = px1;
            sy = py0;
        }
        else if (Rotation == 2)
        {
            px0 = maxX - lx1;
            px1 = maxX - lx0;
            py0 = maxY - ly1;
            py1 = maxY - ly0;
            sx = px1;
            sy = py1;
        }
        else
        {
            px0 = ly0;
            px1 = ly1;
            py0 = maxY - lx1;
            py1 = maxY - lx0;
            sx = px0;
            sy = py1;
        }
        WriteRegister(0x36, px1);
        WriteRegister(0x37, px0);
        WriteRegister(0x38, py1);
        WriteRegister(0x39, py0);
        WriteRegister(0x20, sx);
        WriteRegister(0x21, sy);
        byte[] idx = new byte[2];
        idx[0] = 0x00;
        idx[1] = 0x22;
        Bus.WriteCommands(idx);
    }

    /// <summary>R07 display control: 0x1017 normal, 0x1013 with REV cleared (inverted).</summary>
    public override void Invert(bool on) => WriteRegister(0x07, on ? 0x1013 : 0x1017);

    public override void DisplayOn(bool on) => WriteRegister(0x07, on ? 0x1017 : 0x0000);

    /// <summary>R10 power control 1: the init value 0x0800 with STB (bit 0, standby) set or cleared.</summary>
    public override void Sleep(bool sleep) => WriteRegister(0x10, sleep ? 0x0801 : 0x0800);
}
