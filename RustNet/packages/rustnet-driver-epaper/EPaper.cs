using RustNet.Community.Bus;
using RustNet.Community.DisplayCore;

namespace RustNet.Community.EPaper;

/// <summary>
/// Base of the e-paper drivers: 4-wire SPI (D/C), RST, and the BUSY pin a
/// controller holds while it refreshes. Draw on <see cref="Black"/> (and
/// <see cref="Color"/> on tri-colour panels) — a set bit means ink — then
/// <see cref="Display"/> sends the planes and runs a full refresh, waiting on
/// BUSY (bounded by <see cref="BusyPin.TimeoutMs"/>). Controllers differ in
/// whether a 0 or a 1 bit means ink; the driver converts while sending.
/// </summary>
public abstract class EPaperDisplay
{
    public DisplayBus Bus;
    public DigitalOutput? Reset;
    public BusyPin Busy;
    public int Width;
    public int Height;
    public bool TriColor;

    /// <summary>Black plane, 1 = black ink.</summary>
    public RowMonoCanvas Black;

    /// <summary>Red/yellow plane, 1 = coloured ink (an empty 8×1 plane on black/white panels).</summary>
    public RowMonoCanvas Color;

    /// <summary>The controller's black RAM uses 0 for black (most do).</summary>
    public bool BlackInkIsZero = true;

    /// <summary>The controller's colour RAM uses 0 for coloured ink.</summary>
    public bool ColorInkIsZero = true;

    /// <summary>Set when the last <see cref="Display"/> or init gave up waiting on BUSY.</summary>
    public bool TimedOut;

    protected EPaperDisplay(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, bool busyLevel,
        int width, int height, bool triColor)
    {
        Bus = bus;
        Reset = reset;
        Busy = new BusyPin(busy, busyLevel);
        Width = width;
        Height = height;
        TriColor = triColor;
        Black = new RowMonoCanvas(width, height);
        Color = triColor ? new RowMonoCanvas(width, height) : new RowMonoCanvas(8, 1);
    }

    /// <summary>Hardware reset and the controller's power-up sequence.</summary>
    public abstract void Init();

    /// <summary>Send the planes and run a full refresh. False if BUSY timed out.</summary>
    public abstract bool Display();

    /// <summary>Enter deep sleep (only a hardware reset + <see cref="Init"/> wakes the panel).</summary>
    public abstract void DeepSleep();

    /// <summary>Wait for BUSY to clear; records a timeout.</summary>
    protected bool WaitIdle()
    {
        bool ok = Busy.WaitIdle(200);
        if (!ok)
        {
            TimedOut = true;
        }
        return ok;
    }

    protected void Command(int command) => Bus.Command(command);

    /// <summary>A command with parameters written as hex.</summary>
    protected void Command(int command, string hexParameters) => Bus.CommandWithData(command, HexTable.Decode(hexParameters));

    protected void Command(int command, byte[] parameters) => Bus.CommandWithData(command, parameters);

    /// <summary>Command, then a plane as one data burst, inverted when the controller's ink is 0.</summary>
    protected void SendPlane(int command, byte[] plane, bool inkIsZero)
    {
        Bus.Command(command);
        if (!inkIsZero)
        {
            Bus.WriteData(plane);
            return;
        }
        byte[] inv = new byte[plane.Length];
        for (int i = 0; i < plane.Length; i++)
        {
            inv[i] = (byte)~plane[i];
        }
        Bus.WriteData(inv);
    }

    /// <summary>Command, then <paramref name="count"/> bytes that all mean "white" for that RAM.</summary>
    protected void SendWhite(int command, int count, bool inkIsZero)
    {
        Bus.Command(command);
        byte[] b = new byte[count];
        if (inkIsZero)
        {
            for (int i = 0; i < count; i++)
            {
                b[i] = 0xFF;
            }
        }
        Bus.WriteData(b);
    }

    public void Clear()
    {
        Black.Clear(MonoColor.Off);
        Color.Clear(MonoColor.Off);
    }

    protected static byte[] Bytes2(int a, int b)
    {
        byte[] r = new byte[2];
        r[0] = (byte)a;
        r[1] = (byte)b;
        return r;
    }
}

/// <summary>
/// Solomon Systech SSD16xx family (SSD1608 / IL3829, SSD1675A / IL3897,
/// SSD1680, SSD1681). BUSY is high while busy. RAM is addressed in bytes of 8
/// horizontal pixels: data entry mode 0x11 = 0x03 (X then Y increment), X
/// window 0x44 [x/8, (x+w-1)/8], Y window 0x45 [y lo, y hi, end lo, end hi],
/// address counters 0x4E/0x4F. B/W RAM 0x24 (0 = black), red RAM 0x26
/// (1 = red). A full refresh is 0x22 [sequence], 0x20, wait for BUSY.
/// </summary>
public abstract class Ssd16xxEPaper : EPaperDisplay
{
    /// <summary>Display update control 2 value for a full refresh.</summary>
    public int UpdateSequence = 0xF7;

    protected Ssd16xxEPaper(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height, bool triColor)
        : base(bus, reset, busy, true, width, height, triColor)
    {
        ColorInkIsZero = false;
    }

    /// <summary>Data entry mode X+/Y+, RAM window and address counters for a region.</summary>
    public void SetRamArea(int x, int y, int w, int h)
    {
        Command(0x11, "03");
        Command(0x44, Bytes2(x / 8, (x + w - 1) / 8));
        byte[] yw = new byte[4];
        yw[0] = (byte)y;
        yw[1] = (byte)(y >> 8);
        yw[2] = (byte)(y + h - 1);
        yw[3] = (byte)((y + h - 1) >> 8);
        Command(0x45, yw);
        byte[] xc = new byte[1];
        xc[0] = (byte)(x / 8);
        Command(0x4E, xc);
        Command(0x4F, Bytes2(y, y >> 8));
    }

    /// <summary>Driver output control 0x01: MUX = height − 1 (9 bits), gate scan 0.</summary>
    protected void DriverOutput()
    {
        byte[] b = new byte[3];
        b[0] = (byte)(Height - 1);
        b[1] = (byte)((Height - 1) >> 8);
        b[2] = 0x00;
        Command(0x01, b);
    }

    public override bool Display()
    {
        TimedOut = false;
        SetRamArea(0, 0, Width, Height);
        SendPlane(0x24, Black.Buffer, BlackInkIsZero);
        if (TriColor)
        {
            SetRamArea(0, 0, Width, Height);
            SendPlane(0x26, Color.Buffer, ColorInkIsZero);
        }
        return Refresh();
    }

    protected virtual bool Refresh()
    {
        byte[] u = new byte[1];
        u[0] = (byte)UpdateSequence;
        Command(0x22, u);
        Command(0x20);
        return WaitIdle();
    }

    public override void DeepSleep() => Command(0x10, "01");
}

/// <summary>
/// SSD1681, 200×200 (Waveshare 1.54" V2, GDEH0154D67). Init as GxEPD2 /
/// Waveshare: SWRESET 0x12, driver output (199), border 0x3C 05, internal
/// temperature sensor 0x18 80, RAM window; full refresh 0x22 F7.
/// </summary>
public class Ssd1681 : Ssd16xxEPaper
{
    public Ssd1681(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height, bool triColor)
        : base(bus, reset, busy, width, height, triColor)
    {
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        WaitIdle();
        Command(0x12);
        WaitIdle();
        DriverOutput();
        Command(0x3C, "05");
        Command(0x18, "80");
        SetRamArea(0, 0, Width, Height);
        WaitIdle();
    }
}

/// <summary>
/// SSD1680, 122×250 / 128×296 (Waveshare 2.13" V3/V4, 2.9" V2). Init as the
/// Waveshare drivers: SWRESET, driver output, data entry 03 + window, border
/// 0x3C 05, display update control 1 0x21 [00 80], temperature sensor 0x18
/// 80; full refresh 0x22 F7.
/// </summary>
public class Ssd1680 : Ssd16xxEPaper
{
    public Ssd1680(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height, bool triColor)
        : base(bus, reset, busy, width, height, triColor)
    {
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        WaitIdle();
        Command(0x12);
        WaitIdle();
        DriverOutput();
        SetRamArea(0, 0, Width, Height);
        Command(0x3C, "05");
        Command(0x21, "00 80");
        Command(0x18, "80");
        WaitIdle();
    }
}

/// <summary>
/// SSD1608 / Good Display IL3829, 200×200 (Waveshare 1.54" V1, GDEP015OC1).
/// No OTP waveform: the 30-byte full-update LUT is loaded with 0x32. Init:
/// driver output, booster soft start 0x0C [D7 D6 9D], VCOM 0x2C A8, dummy
/// line 0x3A 1A, gate time 0x3B 08, data entry 03, LUT. Full refresh is
/// 0x22 C4, 0x20, 0xFF (terminate frame), wait.
/// </summary>
public class Ssd1608 : Ssd16xxEPaper
{
    public const string FullUpdateLut =
        "02 02 01 11 12 12 22 22 66 69 69 59 58 99 99 88 00 00 00 00 F8 B4 13 51 35 51 51 19 01 00";

    public Ssd1608(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height)
        : base(bus, reset, busy, width, height, false)
    {
        UpdateSequence = 0xC4;
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        DriverOutput();
        Command(0x0C, "D7 D6 9D");
        Command(0x2C, "A8");
        Command(0x3A, "1A");
        Command(0x3B, "08");
        Command(0x11, "03");
        Command(0x32, FullUpdateLut);
    }

    protected override bool Refresh()
    {
        byte[] u = new byte[1];
        u[0] = (byte)UpdateSequence;
        Command(0x22, u);
        Command(0x20);
        Command(0xFF);
        return WaitIdle();
    }
}

/// <summary>
/// Good Display IL3897 (SSD1675A clone), 122×250 (Waveshare 2.13" V2). Init
/// as Waveshare's: SWRESET, analog block 0x74 54, digital block 0x7E 3B,
/// driver output, data entry 03 + window, border 0x3C 03, VCOM 0x2C 55.
/// The full refresh uses the waveform from OTP (0x22 F7: clock, analog,
/// load temperature + LUT, display, power down) rather than a register LUT.
/// </summary>
public class Il3897 : Ssd16xxEPaper
{
    public Il3897(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height)
        : base(bus, reset, busy, width, height, false)
    {
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        WaitIdle();
        Command(0x12);
        WaitIdle();
        Command(0x74, "54");
        Command(0x7E, "3B");
        DriverOutput();
        SetRamArea(0, 0, Width, Height);
        Command(0x3C, "03");
        Command(0x2C, "55");
    }
}

/// <summary>
/// UltraChip UC81xx / Good Display IL03xx family (IL0373, IL0376F, IL0398,
/// IL91874, UC8151C, UC8176, UC8179). BUSY_N is low while busy. Command set:
/// PSR 0x00, PWR 0x01, POF 0x02, PON 0x04, BTST 0x06, DSLP 0x07 (A5),
/// DTM1 0x10, DRF 0x12, DTM2 0x13, PLL 0x30, CDI 0x50, TCON 0x60, TRES 0x61,
/// VDCS 0x82. Tri-colour: DTM1 = black plane, DTM2 = colour plane. Black /
/// white: DTM1 = "old" frame (sent white), DTM2 = new frame.
/// </summary>
public abstract class Uc81xxEPaper : EPaperDisplay
{
    protected Uc81xxEPaper(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height, bool triColor)
        : base(bus, reset, busy, false, width, height, triColor)
    {
    }

    /// <summary>TRES with an 8-bit horizontal and 16-bit vertical size (IL0373/IL0376F/UC8151C).</summary>
    protected void Resolution8x16()
    {
        byte[] b = new byte[3];
        b[0] = (byte)Width;
        b[1] = (byte)(Height >> 8);
        b[2] = (byte)Height;
        Command(0x61, b);
    }

    /// <summary>TRES with 16-bit horizontal and vertical sizes (IL0398/IL91874/UC8179).</summary>
    protected void Resolution16x16()
    {
        byte[] b = new byte[4];
        b[0] = (byte)(Width >> 8);
        b[1] = (byte)Width;
        b[2] = (byte)(Height >> 8);
        b[3] = (byte)Height;
        Command(0x61, b);
    }

    public override bool Display()
    {
        TimedOut = false;
        if (TriColor)
        {
            SendPlane(0x10, Black.Buffer, BlackInkIsZero);
            SendPlane(0x13, Color.Buffer, ColorInkIsZero);
        }
        else
        {
            SendWhite(0x10, Black.Buffer.Length, BlackInkIsZero);
            SendPlane(0x13, Black.Buffer, BlackInkIsZero);
        }
        return Refresh();
    }

    protected bool Refresh()
    {
        Command(0x12);
        Timing.SleepMs(100);
        return WaitIdle();
    }

    /// <summary>Power off (wait), then deep sleep with the A5 check code.</summary>
    public override void DeepSleep()
    {
        Command(0x02);
        WaitIdle();
        Command(0x07, "A5");
    }
}

/// <summary>
/// IL0373, tri-colour 104×212 (2.13") / 128×296 (2.9") / 152×152 (1.54").
/// Init as Adafruit_IL0373: power setting, booster 17 17 17, power on + wait,
/// PSR CF (KWR, LUT from OTP), CDI 37, PLL 29, resolution, VCOM DC 0A.
/// Black-and-white panels use PSR DF.
/// </summary>
public class Il0373 : Uc81xxEPaper
{
    public Il0373(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height, bool triColor)
        : base(bus, reset, busy, width, height, triColor)
    {
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        Command(0x01, "03 00 2B 2B 09");
        Command(0x06, "17 17 17");
        Command(0x04);
        WaitIdle();
        Command(0x00, TriColor ? "CF" : "DF");
        Command(0x50, "37");
        Command(0x30, "29");
        Resolution8x16();
        Command(0x82, "0A");
    }

    /// <summary>Adafruit power-down: CDI 17 (floating border), power off, deep sleep.</summary>
    public override void DeepSleep()
    {
        Command(0x50, "17");
        base.DeepSleep();
    }
}

/// <summary>
/// IL0376F, 200×200 black/white/red (Waveshare 1.54" B V1). The black RAM
/// takes 2 bits per pixel (11 = white, 00 = black, so 8 pixels go out as two
/// bytes, MSB first); the red RAM 1 bit (0 = red). Init as Waveshare's
/// (PSR CF = LUT from OTP, so no register LUTs are needed).
/// </summary>
public class Il0376f : Uc81xxEPaper
{
    public Il0376f(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height)
        : base(bus, reset, busy, width, height, true)
    {
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        Command(0x01, "07 00 08 00");
        Command(0x06, "07 07 07");
        Command(0x04);
        WaitIdle();
        Command(0x00, "CF");
        Command(0x50, "17");
        Command(0x30, "39");
        Resolution8x16();
        Command(0x82, "0E");
    }

    /// <summary>Expand one byte of 1bpp "white" bits into two 2bpp bytes (bit set -> 11).</summary>
    public static int ExpandHigh(int b)
    {
        int r = 0;
        for (int bit = 0; bit < 4; bit++)
        {
            if ((b & (0x80 >> bit)) != 0)
            {
                r |= 0xC0 >> (bit * 2);
            }
        }
        return r;
    }

    public override bool Display()
    {
        TimedOut = false;
        byte[] src = Black.Buffer;
        byte[] two = new byte[src.Length * 2];
        for (int i = 0; i < src.Length; i++)
        {
            int white = ~src[i] & 0xFF;
            two[i * 2] = (byte)ExpandHigh(white);
            two[i * 2 + 1] = (byte)ExpandHigh((white << 4) & 0xFF);
        }
        Command(0x10);
        Bus.WriteData(two);
        SendPlane(0x13, Color.Buffer, true);
        return Refresh();
    }

    /// <summary>Waveshare sleep: CDI 17, VCOM 00, power setting 02 00 00 00, wait, power off.</summary>
    public override void DeepSleep()
    {
        Command(0x50, "17");
        Command(0x82, "00");
        Command(0x01, "02 00 00 00");
        WaitIdle();
        Command(0x02);
    }
}

/// <summary>
/// IL0398 (UC8176), 400×300 (Waveshare 4.2"). Booster, power on + wait, PSR
/// 1F (black/white, OTP LUT) or 0F (tri-colour), 16-bit resolution, CDI 97
/// (B/W) / 77 (tri-colour).
/// </summary>
public class Il0398 : Uc81xxEPaper
{
    public Il0398(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height, bool triColor)
        : base(bus, reset, busy, width, height, triColor)
    {
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        Command(0x06, "17 17 17");
        Command(0x04);
        WaitIdle();
        Command(0x00, TriColor ? "0F" : "1F");
        Resolution16x16();
        Command(0x50, TriColor ? "77" : "97");
    }
}

/// <summary>
/// IL91874, 176×264 (Waveshare / Adafruit 2.7"). Sequence after Waveshare's
/// 2.7" B driver: power on + wait, PSR, PLL 3A, power setting, booster
/// 07 07 17, the five F8 power-optimisation pairs, VCOM DC 12, CDI 87,
/// resolution. PSR is 8F/9F (waveform from OTP) instead of Waveshare's AF
/// with register LUTs. With CDI 87 a 1 bit is ink on both planes.
/// </summary>
public class Il91874 : Uc81xxEPaper
{
    public Il91874(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height, bool triColor)
        : base(bus, reset, busy, width, height, triColor)
    {
        BlackInkIsZero = false;
        ColorInkIsZero = false;
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        Command(0x04);
        WaitIdle();
        Command(0x00, TriColor ? "8F" : "9F");
        Command(0x30, "3A");
        Command(0x01, "03 00 2B 2B 09");
        Command(0x06, "07 07 17");
        Command(0xF8, "60 A5");
        Command(0xF8, "89 A5");
        Command(0xF8, "90 00");
        Command(0xF8, "93 2A");
        Command(0xF8, "73 41");
        Command(0x82, "12");
        Command(0x50, "87");
        Resolution16x16();
    }
}

/// <summary>
/// UC8151C, 128×296 black/white/red (Waveshare 2.9" B V3) and 152×296 /
/// 104×212 relatives. Init as Waveshare: power on + wait, PSR 0F 89 (OTP
/// LUT, KWR), resolution, CDI 77.
/// </summary>
public class Uc8151c : Uc81xxEPaper
{
    public Uc8151c(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height, bool triColor)
        : base(bus, reset, busy, width, height, triColor)
    {
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        Command(0x04);
        WaitIdle();
        Command(0x00, TriColor ? "0F 89" : "1F 89");
        Resolution8x16();
        Command(0x50, TriColor ? "77" : "97");
    }
}

/// <summary>
/// UC8179 (GD7965), 800×480 (Waveshare 7.5" V2). Init as Waveshare: power
/// setting 07 07 3F 3F, power on + wait, PSR 1F (B/W, OTP LUT), resolution
/// 03 20 01 E0, dual SPI off 0x15 00, CDI 10 07, TCON 22. With that CDI the
/// new-frame RAM takes 1 = black, so the plane is sent as-is to DTM2 only.
/// </summary>
public class Uc8179 : Uc81xxEPaper
{
    public Uc8179(DisplayBus bus, DigitalOutput? reset, DigitalInput? busy, int width, int height)
        : base(bus, reset, busy, width, height, false)
    {
        BlackInkIsZero = false;
    }

    public override void Init()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        Command(0x01, "07 07 3F 3F");
        Command(0x04);
        Timing.SleepMs(100);
        WaitIdle();
        Command(0x00, "1F");
        Resolution16x16();
        Command(0x15, "00");
        Command(0x50, "10 07");
        Command(0x60, "22");
    }

    public override bool Display()
    {
        TimedOut = false;
        SendPlane(0x13, Black.Buffer, BlackInkIsZero);
        return Refresh();
    }
}
