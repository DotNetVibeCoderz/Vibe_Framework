using RustNet.Community.Bus;
using RustNet.Community.DisplayCore;

namespace RustNet.Community.MonoDisplay;

/// <summary>
/// Base of the 1-bit page-addressed controllers (SSD1306 family, SH110x,
/// CH1115, UC1609, ST7565). All of them store the picture as pages of eight
/// vertical pixels, so the driver keeps a <see cref="PageMonoCanvas"/> and
/// <see cref="Flush"/> streams it page by page. Draw on <see cref="Canvas"/>,
/// then call <see cref="Flush"/>.
/// </summary>
public abstract class MonoDisplay
{
    public DisplayBus Bus;
    public DigitalOutput? Reset;
    public PageMonoCanvas Canvas;
    public int Width;
    public int Height;

    /// <summary>First RAM column of the visible area (e.g. 2 on SH1106, which has 132 columns).</summary>
    public int ColumnOffset;

    protected MonoDisplay(DisplayBus bus, DigitalOutput? reset, int width, int height)
    {
        Bus = bus;
        Reset = reset;
        Width = width;
        Height = height;
        Canvas = new PageMonoCanvas(width, height);
    }

    /// <summary>The power-up command sequence (all bytes are commands).</summary>
    public abstract byte[] InitSequence();

    /// <summary>Hardware reset (if wired), then the init sequence. The panel is left on and the buffer cleared.</summary>
    public virtual void Begin()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        Bus.WriteCommands(InitSequence());
        Canvas.Clear(MonoColor.Off);
    }

    /// <summary>Send the whole canvas to the panel.</summary>
    public virtual void Flush()
    {
        for (int p = 0; p < Canvas.Pages; p++)
        {
            Bus.WriteCommands(PageAddress(p, ColumnOffset));
            Bus.WriteData(Canvas.Buffer, p * Width, Width);
        }
    }

    /// <summary>Commands that point the RAM write address at a page and column (page addressing mode).</summary>
    public virtual byte[] PageAddress(int page, int column)
    {
        byte[] b = new byte[3];
        b[0] = (byte)(0xB0 | page);
        b[1] = (byte)(0x10 | ((column >> 4) & 0x0F));
        b[2] = (byte)(column & 0x0F);
        return b;
    }

    public virtual void SetContrast(int value) => Bus.Command(0x81, value & 0xFF);

    public virtual void Invert(bool on) => Bus.Command(on ? 0xA7 : 0xA6);

    public virtual void DisplayOn(bool on) => Bus.Command(on ? 0xAF : 0xAE);

    // Drawing shortcuts on the canvas.
    public void Clear() => Canvas.Clear(MonoColor.Off);

    public void SetPixel(int x, int y, bool on) => Canvas.SetPixel(x, y, on ? MonoColor.On : MonoColor.Off);

    public void Text(int x, int y, string text) => Canvas.Text(x, y, text, MonoColor.On, 1);
}

/// <summary>
/// Solomon Systech SSD1306 OLED (128×64, 128×32, 96×16, 64×48, 72×40) over
/// I2C (address 0x3C/0x3D) or 4-wire SPI with D/C. Init sequence as in the
/// datasheet's application note and Adafruit_SSD1306: horizontal addressing
/// mode, segment remap + COM scan reversed (A1/C8), charge pump on (8D 14)
/// for internal VCC. Flush sets the column (0x21) and page (0x22) window and
/// streams the buffer in one burst.
/// </summary>
public class Ssd1306 : MonoDisplay
{
    /// <summary>VCC from an external supply instead of the charge pump.</summary>
    public bool ExternalVcc;

    /// <summary>Convenience: I2C bus and 7-bit address.</summary>
    public Ssd1306(int i2cBus, int address, int width, int height)
        : this(new I2cDisplayBus(i2cBus, address), null, width, height)
    {
    }

    /// <summary>Convenience: SPI bus, CS, D/C and RST pins.</summary>
    public Ssd1306(int spiBus, int csPin, int dcPin, int resetPin, int width, int height)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 8000000), new DigitalOutput(resetPin, true), width, height)
    {
    }

    public Ssd1306(DisplayBus bus, DigitalOutput? reset, int width, int height) : base(bus, reset, width, height)
    {
        if (width == 64 && height == 48)
        {
            ColumnOffset = 32;
        }
        else if (width == 72 && height == 40)
        {
            ColumnOffset = 28;
        }
    }

    public virtual int ComPins() => (Height == 64 || Height == 48 || Height == 40) && Width != 96 ? 0x12 : 0x02;

    public virtual int DefaultContrast()
    {
        if (Height == 32)
        {
            return 0x8F;
        }
        if (Width == 96 && Height == 16)
        {
            return ExternalVcc ? 0x10 : 0xAF;
        }
        return ExternalVcc ? 0x9F : 0xCF;
    }

    public override byte[] InitSequence()
    {
        ByteBuilder b = new ByteBuilder();
        b.Add(0xAE);                          // display off
        b.Add(0xD5, 0x80);                    // clock divide / oscillator (reset value)
        b.Add(0xA8, Height - 1);              // multiplex ratio
        b.Add(0xD3, 0x00);                    // display offset
        b.Add(0x40);                          // start line 0
        b.Add(0x8D, ExternalVcc ? 0x10 : 0x14); // charge pump
        b.Add(0x20, 0x00);                    // horizontal addressing mode
        b.Add(0xA1);                          // segment remap (column 127 -> SEG0)
        b.Add(0xC8);                          // COM scan descending
        b.Add(0xDA, ComPins());               // COM pins hardware configuration
        b.Add(0x81, DefaultContrast());       // contrast
        b.Add(0xD9, ExternalVcc ? 0x22 : 0xF1); // pre-charge period
        b.Add(0xDB, 0x40);                    // VCOMH deselect level
        b.Add(0xA4);                          // resume to RAM content
        b.Add(0xA6);                          // normal (not inverted)
        b.Add(0x2E);                          // scrolling off
        b.Add(0xAF);                          // display on
        return b.ToArray();
    }

    public override void Flush()
    {
        ByteBuilder b = new ByteBuilder();
        b.Add(0x22).Add(0x00).Add(Canvas.Pages - 1);                       // page start/end
        b.Add(0x21).Add(ColumnOffset).Add(ColumnOffset + Width - 1);       // column start/end
        Bus.WriteCommands(b.ToArray());
        Bus.WriteData(Canvas.Buffer);
    }

    /// <summary>Continuous horizontal scroll of pages <paramref name="startPage"/>..<paramref name="endPage"/> (0x26/0x27, then 0x2F).</summary>
    public void StartScroll(bool right, int startPage, int endPage, int interval)
    {
        ByteBuilder b = new ByteBuilder();
        b.Add(right ? 0x26 : 0x27).Add(0x00).Add(startPage).Add(interval & 7).Add(endPage).Add(0x00).Add(0xFF).Add(0x2F);
        Bus.WriteCommands(b.ToArray());
    }

    public void StopScroll() => Bus.Command(0x2E);
}

/// <summary>
/// SSD1309 OLED (128×64, typically 2.42"/1.54" modules). Same command set
/// and RAM layout as the SSD1306 but no charge pump — VCC is generated
/// externally (12–15 V) on the module — so there is no 0x8D command. Init
/// values follow the SSD1309 datasheet (VCOMH 0x34 = 0.78·VCC, contrast 0x7F
/// reset) with u8g2's oscillator setting (D5 A0).
/// </summary>
public class Ssd1309 : Ssd1306
{
    public Ssd1309(int spiBus, int csPin, int dcPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 8000000), new DigitalOutput(resetPin, true))
    {
    }

    public Ssd1309(DisplayBus bus, DigitalOutput? reset) : base(bus, reset, 128, 64)
    {
        ExternalVcc = true;
    }

    public override byte[] InitSequence()
    {
        ByteBuilder b = new ByteBuilder();
        b.Add(0xAE);
        b.Add(0xD5, 0xA0);
        b.Add(0xA8, Height - 1);
        b.Add(0xD3, 0x00);
        b.Add(0x40);
        b.Add(0x20, 0x00);
        b.Add(0xA1);
        b.Add(0xC8);
        b.Add(0xDA, 0x12);
        b.Add(0x81, 0x7F);
        b.Add(0xD9, 0xF1);
        b.Add(0xDB, 0x34);
        b.Add(0xA4);
        b.Add(0xA6);
        b.Add(0x2E);
        b.Add(0xAF);
        return b.ToArray();
    }
}

/// <summary>
/// Sino Wealth SH1106 OLED controller (the common 1.3" 128×64 modules). Its
/// RAM is 132 columns wide and the 128-pixel glass sits at columns 2..129,
/// and it only has page addressing — no 0x20/0x21/0x22 — so a flush is one
/// "page, column high, column low" command triple plus 128 data bytes per
/// page. DC-DC is switched on with the two-byte AD 8B command. Sequence as
/// in Adafruit_SH110X (SH1106G).
/// </summary>
public class Sh1106 : MonoDisplay
{
    public Sh1106(int i2cBus, int address)
        : this(new I2cDisplayBus(i2cBus, address), null, 128, 64)
    {
    }

    public Sh1106(DisplayBus bus, DigitalOutput? reset, int width, int height) : base(bus, reset, width, height)
    {
        ColumnOffset = 2;
    }

    public override byte[] InitSequence()
    {
        ByteBuilder b = new ByteBuilder();
        b.Add(0xAE);
        b.Add(0xD5, 0x80);
        b.Add(0xA8, Height - 1);
        b.Add(0xD3, 0x00);
        b.Add(0x40);
        b.Add(0xAD, 0x8B);      // DC-DC control: on
        b.Add(0xA1);
        b.Add(0xC8);
        b.Add(0xDA, 0x12);
        b.Add(0x81, 0xFF);
        b.Add(0xD9, 0x1F);
        b.Add(0xDB, 0x40);
        b.Add(0x33);            // pump voltage 9.0 V (0x30..0x33)
        b.Add(0xA6);
        b.Add(0xA4);
        b.Add(0xAF);
        return b.ToArray();
    }
}

/// <summary>
/// Sino Wealth SH1107 OLED (128×128, or 64×128 as on the Adafruit 128×64
/// FeatherWing). Page addressing only, 16 pages of 8 rows; pages run across
/// <see cref="MonoDisplay.Height"/>/8 and each page holds Width columns.
/// Sequence as in Adafruit_SH110X (SH1107): display offset 0x60 and
/// multiplex 0x3F for 64-wide, 0x00 / 0x7F for 128×128.
/// </summary>
public class Sh1107 : MonoDisplay
{
    public Sh1107(int i2cBus, int address, int width, int height)
        : this(new I2cDisplayBus(i2cBus, address), null, width, height)
    {
    }

    public Sh1107(DisplayBus bus, DigitalOutput? reset, int width, int height) : base(bus, reset, width, height)
    {
    }

    public override byte[] InitSequence()
    {
        bool square = Width == 128 && Height == 128;
        ByteBuilder b = new ByteBuilder();
        b.Add(0xAE);
        b.Add(0xD5, 0x51);      // clock divide / oscillator
        b.Add(0x20);            // page addressing mode
        b.Add(0x81, 0x4F);      // contrast
        b.Add(0xAD, 0x8A);      // DC-DC control
        b.Add(0xA0);            // segment remap normal
        b.Add(0xC0);            // COM scan ascending
        b.Add(0xDC, 0x00);      // display start line
        b.Add(0xD3, square ? 0x00 : 0x60);
        b.Add(0xD9, 0x22);      // pre/dis-charge period
        b.Add(0xDB, 0x35);      // VCOM deselect level
        b.Add(0xA8, square ? 0x7F : 0x3F);
        b.Add(0xA4);
        b.Add(0xA6);
        b.Add(0xAF);
        return b.ToArray();
    }
}

/// <summary>
/// Chipwealth CH1115 OLED controller (88×48 0.5" modules such as the
/// ER-OLEDM0.5). SH1106-style command set: page addressing (B0+page, column
/// low/high nibbles), contrast 81, DC-DC AD 8B, oscillator D5, pre-charge
/// D9, VCOM DB. Multiplex is set to the panel height.
/// </summary>
public class Ch1115 : MonoDisplay
{
    public Ch1115(int spiBus, int csPin, int dcPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, dcPin, 8000000), new DigitalOutput(resetPin, true), 88, 48)
    {
    }

    public Ch1115(DisplayBus bus, DigitalOutput? reset, int width, int height) : base(bus, reset, width, height)
    {
    }

    public override byte[] InitSequence()
    {
        ByteBuilder b = new ByteBuilder();
        b.Add(0xAE);
        b.Add(0x00).Add(0x10);  // column 0
        b.Add(0xB0);            // page 0
        b.Add(0x40);            // start line 0
        b.Add(0x81, 0x80);      // contrast
        b.Add(0xA0);            // segment remap normal
        b.Add(0xA4);
        b.Add(0xA6);
        b.Add(0xA8, Height - 1); // multiplex
        b.Add(0xAD, 0x8B);      // DC-DC on
        b.Add(0xC8);            // COM scan reversed
        b.Add(0xD3, 0x00);      // display offset
        b.Add(0xD5, 0xA0);      // oscillator
        b.Add(0xD9, 0x22);      // pre-charge
        b.Add(0xDB, 0x35);      // VCOM level
        b.Add(0xAF);
        return b.ToArray();
    }

    public override byte[] PageAddress(int page, int column)
    {
        byte[] b = new byte[3];
        b[0] = (byte)(0xB0 | page);
        b[1] = (byte)(column & 0x0F);
        b[2] = (byte)(0x10 | ((column >> 4) & 0x0F));
        return b;
    }
}

/// <summary>
/// UltraChip UC1609(C) 192×64 STN LCD controller (ERM19264 modules), 4-wire
/// SPI with CD pin. Command bytes carry their parameter in the low bits
/// (system reset E2, temperature compensation 0x24|TC, address control
/// 0x88|AC, frame rate 0xA0|FR, bias 0xE8|BR, power control 0x28|PC, LCD
/// mapping 0xC0|MY/MX); gain+potentiometer is the two-byte 0x81, PM. The
/// values follow the ERM19264_UC1609 reference driver. Page address is
/// 0xB0|page and the column is set by its two nibbles.
/// </summary>
public class Uc1609 : MonoDisplay
{
    public int Contrast = 0x49;

    public Uc1609(int spiBus, int csPin, int cdPin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, cdPin, 8000000), new DigitalOutput(resetPin, true))
    {
    }

    public Uc1609(DisplayBus bus, DigitalOutput? reset) : base(bus, reset, 192, 64)
    {
    }

    public override void Begin()
    {
        DisplayPins.ResetPulse(Reset, 10, 10);
        Bus.Command(0xE2);          // system reset
        Timing.SleepMs(5);
        Bus.WriteCommands(InitSequence());
        Canvas.Clear(MonoColor.Off);
    }

    public override byte[] InitSequence()
    {
        ByteBuilder b = new ByteBuilder();
        b.Add(0x24 | 0x03);         // temperature compensation -0.20 %/°C
        b.Add(0x88 | 0x02);         // address control: auto-increment column, wrap to next page
        b.Add(0xA0 | 0x01);         // frame rate
        b.Add(0xE8 | 0x03);         // bias ratio 1/12
        b.Add(0x28 | 0x06);         // power control: internal pump, 15 nF panel
        b.Add(0x81, Contrast);      // gain + potentiometer
        b.Add(0xC0 | 0x04);         // LCD mapping: MY (rows mirrored, panel orientation)
        b.Add(0xAF);                // display enable
        return b.ToArray();
    }

    public override byte[] PageAddress(int page, int column)
    {
        byte[] b = new byte[3];
        b[0] = (byte)(column & 0x0F);
        b[1] = (byte)(0x10 | ((column >> 4) & 0x0F));
        b[2] = (byte)(0xB0 | page);
        return b;
    }
}

/// <summary>
/// Sitronix ST7565 / ST7565R 128×64 LCD (also ST7565P, NT7534, many "12864"
/// SPI modules), 4-wire SPI with A0 (= D/C). Power-up as in the datasheet
/// and Adafruit's ST7565 driver: bias 1/7, ADC normal, COM normal, start
/// line 0, then the internal power circuits switched on in three steps
/// (0x2C booster, 0x2E regulator, 0x2F follower) with waits, resistor ratio,
/// display on, then the electronic volume (0x81 + 6-bit value).
/// </summary>
public class St7565 : MonoDisplay
{
    public int Contrast = 0x18;

    public St7565(int spiBus, int csPin, int a0Pin, int resetPin)
        : this(new SpiDisplayBus(spiBus, csPin, a0Pin, 8000000), new DigitalOutput(resetPin, true))
    {
    }

    public St7565(DisplayBus bus, DigitalOutput? reset) : base(bus, reset, 128, 64)
    {
    }

    public override byte[] InitSequence() => HexTable.Decode("A3 A0 C0 40 2C 2E 2F 26");

    public override void Begin()
    {
        DisplayPins.ResetPulse(Reset, 1, 1);
        byte[] seq = InitSequence();
        byte[] one = new byte[1];
        for (int i = 0; i < seq.Length; i++)
        {
            one[0] = seq[i];
            Bus.WriteCommands(one);
            if (seq[i] == 0x2C || seq[i] == 0x2E)
            {
                Timing.SleepMs(50);
            }
            else if (seq[i] == 0x2F)
            {
                Timing.SleepMs(10);
            }
        }
        Bus.Command(0xAF);
        Bus.Command(0xA4);
        SetContrast(Contrast);
        Canvas.Clear(MonoColor.Off);
    }

    public override void SetContrast(int value)
    {
        Contrast = value & 0x3F;
        Bus.Command(0x81, Contrast);
    }
}
