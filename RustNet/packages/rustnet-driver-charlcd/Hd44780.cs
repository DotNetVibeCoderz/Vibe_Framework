using RustNet.Community.Bus;

namespace RustNet.Community.CharLcd;

/// <summary>
/// The 4-bit interface of an HD44780 (or compatible: ST7066, SPLC780,
/// KS0066). A byte goes out as two nibbles, high first; the controller
/// latches D4..D7 and RS on the falling edge of E.
/// </summary>
public abstract class Hd44780Bus
{
    /// <summary>Send one nibble with RS low (used only by the power-on initialisation).</summary>
    public abstract void WriteInitNibble(int nibble);

    /// <summary>Send bytes, each as high then low nibble, with RS = <paramref name="rs"/> (false = instruction, true = data).</summary>
    public abstract void WriteBytes(byte[] values, bool rs);

    public virtual void SetBacklight(bool on)
    {
    }
}

/// <summary>HD44780 wired directly to six GPIOs (RS, E, D4..D7; R/W tied low).</summary>
public class Hd44780GpioBus : Hd44780Bus
{
    public DigitalOutput Rs;
    public DigitalOutput En;
    public DigitalOutput D4;
    public DigitalOutput D5;
    public DigitalOutput D6;
    public DigitalOutput D7;
    public DigitalOutput? Backlight;

    public Hd44780GpioBus(int rs, int en, int d4, int d5, int d6, int d7)
        : this(new DigitalOutput(rs, false), new DigitalOutput(en, false), new DigitalOutput(d4, false),
               new DigitalOutput(d5, false), new DigitalOutput(d6, false), new DigitalOutput(d7, false), null)
    {
    }

    public Hd44780GpioBus(DigitalOutput rs, DigitalOutput en, DigitalOutput d4, DigitalOutput d5,
        DigitalOutput d6, DigitalOutput d7, DigitalOutput? backlight)
    {
        Rs = rs;
        En = en;
        D4 = d4;
        D5 = d5;
        D6 = d6;
        D7 = d7;
        Backlight = backlight;
    }

    private void Nibble(int n, bool rs)
    {
        Rs.Write(rs);
        D4.Write((n & 1) != 0);
        D5.Write((n & 2) != 0);
        D6.Write((n & 4) != 0);
        D7.Write((n & 8) != 0);
        // Each GPIO call takes far longer than the 450 ns E pulse / 40 ns setup.
        En.Write(true);
        En.Write(false);
    }

    public override void WriteInitNibble(int nibble) => Nibble(nibble, false);

    public override void WriteBytes(byte[] values, bool rs)
    {
        for (int i = 0; i < values.Length; i++)
        {
            Nibble(values[i] >> 4, rs);
            Nibble(values[i] & 0x0F, rs);
        }
    }

    public override void SetBacklight(bool on)
    {
        if (Backlight != null)
        {
            Backlight.Write(on);
        }
    }
}

/// <summary>
/// HD44780 behind a PCF8574/PCF8574A I2C expander (the ubiquitous "I2C LCD
/// backpack"). Default wiring: P0 = RS, P1 = R/W, P2 = E, P3 = backlight,
/// P4..P7 = D4..D7; addresses 0x20..0x27 (PCF8574) or 0x38..0x3F
/// (PCF8574A), 0x27 most common. Each nibble is three expander states —
/// data set up, E high, E low — like LiquidCrystal_I2C, and a whole command
/// or string goes out in one I2C write.
/// </summary>
public class Hd44780Pcf8574Bus : Hd44780Bus
{
    public I2cDevice Device;
    public int RsBit = 0x01;
    public int RwBit = 0x02;
    public int EnBit = 0x04;
    public int BacklightBit = 0x08;
    public int DataShift = 4;
    public bool BacklightOn = true;

    public Hd44780Pcf8574Bus(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Hd44780Pcf8574Bus(I2cDevice device)
    {
        Device = device;
    }

    private int Base(bool rs) => (rs ? RsBit : 0) | (BacklightOn ? BacklightBit : 0);

    private void Put(byte[] b, int at, int nibble, bool rs)
    {
        int v = ((nibble & 0x0F) << DataShift) | Base(rs);
        b[at] = (byte)v;
        b[at + 1] = (byte)(v | EnBit);
        b[at + 2] = (byte)v;
    }

    public override void WriteInitNibble(int nibble)
    {
        byte[] b = new byte[3];
        Put(b, 0, nibble, false);
        Device.Write(b);
    }

    public override void WriteBytes(byte[] values, bool rs)
    {
        byte[] b = new byte[values.Length * 6];
        for (int i = 0; i < values.Length; i++)
        {
            Put(b, i * 6, values[i] >> 4, rs);
            Put(b, i * 6 + 3, values[i] & 0x0F, rs);
        }
        Device.Write(b);
    }

    public override void SetBacklight(bool on)
    {
        BacklightOn = on;
        Device.WriteByte(Base(false));
    }
}

/// <summary>
/// HD44780 character LCD (16×2, 20×4, …) in 4-bit mode. <see cref="Begin"/>
/// runs the datasheet's "initialisation by instruction" (figure 24: three
/// 0x3 nibbles with &gt;4.1 ms / &gt;100 µs waits, then 0x2 for 4-bit), then
/// function set, display on, clear and entry mode — the same sequence as
/// Arduino's LiquidCrystal.
/// </summary>
public class Hd44780
{
    public const int CmdClear = 0x01;
    public const int CmdHome = 0x02;
    public const int CmdEntryMode = 0x04;     // | 0x02 increment | 0x01 shift
    public const int CmdDisplayControl = 0x08; // | 0x04 display | 0x02 cursor | 0x01 blink
    public const int CmdShift = 0x10;          // | 0x08 display (vs cursor) | 0x04 right
    public const int CmdFunctionSet = 0x20;    // | 0x10 8-bit | 0x08 2 lines | 0x04 5x10 font
    public const int CmdSetCgramAddress = 0x40;
    public const int CmdSetDdramAddress = 0x80;

    public Hd44780Bus Bus;
    public int Columns;
    public int Rows;
    public bool LargeFont;

    private int _displayControl = 0x04;
    private int _entryMode = 0x02;

    /// <summary>Convenience: PCF8574 backpack on an I2C bus.</summary>
    public Hd44780(int i2cBus, int address, int columns, int rows)
        : this(new Hd44780Pcf8574Bus(i2cBus, address), columns, rows)
    {
    }

    public Hd44780(Hd44780Bus bus, int columns, int rows)
    {
        Bus = bus;
        Columns = columns;
        Rows = rows;
    }

    public void Begin()
    {
        Bus.SetBacklight(true);
        Timing.SleepMs(50);
        Bus.WriteInitNibble(0x03);
        Timing.SleepMs(5);
        Bus.WriteInitNibble(0x03);
        Timing.SleepMs(1);
        Bus.WriteInitNibble(0x03);
        Timing.SleepMs(1);
        Bus.WriteInitNibble(0x02);
        int function = CmdFunctionSet | (Rows > 1 ? 0x08 : 0x00) | (LargeFont && Rows == 1 ? 0x04 : 0x00);
        Command(function);
        _displayControl = 0x04;
        Command(CmdDisplayControl | _displayControl);
        Clear();
        _entryMode = 0x02;
        Command(CmdEntryMode | _entryMode);
    }

    public void Command(int command)
    {
        byte[] b = new byte[1];
        b[0] = (byte)command;
        Bus.WriteBytes(b, false);
    }

    /// <summary>Clear and home (the controller needs 1.52 ms).</summary>
    public void Clear()
    {
        Command(CmdClear);
        Timing.SleepMs(2);
    }

    public void Home()
    {
        Command(CmdHome);
        Timing.SleepMs(2);
    }

    /// <summary>DDRAM address of a position: rows start at 0x00, 0x40, 0x00+cols, 0x40+cols.</summary>
    public int Address(int column, int row)
    {
        if (row < 0)
        {
            row = 0;
        }
        if (row >= Rows)
        {
            row = Rows - 1;
        }
        int start = (row & 1) != 0 ? 0x40 : 0x00;
        if (row >= 2)
        {
            start += Columns;
        }
        return start + column;
    }

    public void SetCursor(int column, int row) => Command(CmdSetDdramAddress | (Address(column, row) & 0x7F));

    /// <summary>Write text at the cursor (characters above 0xFF become '?'); one bus burst.</summary>
    public void Write(string text)
    {
        byte[] b = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            int c = text[i];
            b[i] = (byte)(c > 0xFF ? '?' : c);
        }
        Bus.WriteBytes(b, true);
    }

    public void WriteAt(int column, int row, string text)
    {
        SetCursor(column, row);
        Write(text);
    }

    /// <summary>Define custom character 0..7 from eight 5-bit rows; print it with (char)location.</summary>
    public void CreateChar(int location, byte[] rows)
    {
        Command(CmdSetCgramAddress | ((location & 0x07) << 3));
        byte[] b = new byte[8];
        for (int i = 0; i < 8 && i < rows.Length; i++)
        {
            b[i] = (byte)(rows[i] & 0x1F);
        }
        Bus.WriteBytes(b, true);
    }

    private void SetControl(int bit, bool on)
    {
        _displayControl = on ? (_displayControl | bit) : (_displayControl & ~bit);
        Command(CmdDisplayControl | _displayControl);
    }

    public void Display(bool on) => SetControl(0x04, on);

    public void Cursor(bool on) => SetControl(0x02, on);

    public void Blink(bool on) => SetControl(0x01, on);

    public void ScrollLeft() => Command(CmdShift | 0x08);

    public void ScrollRight() => Command(CmdShift | 0x08 | 0x04);

    public void Backlight(bool on) => Bus.SetBacklight(on);
}
