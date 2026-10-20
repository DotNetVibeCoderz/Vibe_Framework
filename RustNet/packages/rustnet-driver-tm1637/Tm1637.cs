using RustNet.Community.Bus;
using RustNet.Community.Segments;
using RustNet.Hal;

namespace RustNet.Community.Tm1637;

/// <summary>
/// A GPIO used as an open-drain line: writing high releases it (the external
/// pull-up raises it), writing low drives it low. The TM1637's CLK and DIO
/// lines are open-drain with pull-ups on every common module.
/// </summary>
public class OpenDrainOutput : DigitalOutput
{
    public OpenDrainOutput(int pin)
    {
        Pin = pin;
        Gpio.SetMode(pin, PinMode.OutputOpenDrain);
        Write(true);
    }
}

/// <summary>Reads a pin without reconfiguring it (for sampling an open-drain line).</summary>
public class PinReader : DigitalInput
{
    public PinReader(int pin)
    {
        Pin = pin;
    }
}

/// <summary>
/// Titan Micro TM1637 LED driver (the 4/6-digit 7-segment "clock" modules).
/// Two-wire, I2C-like but not I2C: bytes go LSB first, there is no address,
/// and the chip pulls DIO low during the ninth clock as an ACK. The protocol
/// (start, byte, ack, stop) follows the datasheet's timing diagram and the
/// reference Arduino TM1637Display driver bit for bit; with no block
/// operation in the HAL every edge is one GPIO call (~29 per byte), so keep
/// updates to when the value changes.
/// </summary>
public class Tm1637
{
    /// <summary>Data command: write to display registers, auto-increment address.</summary>
    public const int CmdDataAutoIncrement = 0x40;

    /// <summary>Data command: write to a fixed address.</summary>
    public const int CmdDataFixed = 0x44;

    /// <summary>Address command: 0xC0 | digit (0..5).</summary>
    public const int CmdAddress = 0xC0;

    /// <summary>Display control: 0x80 | 0x08 (on) | pulse width 0..7.</summary>
    public const int CmdDisplay = 0x80;

    public DigitalOutput Clk;
    public DigitalOutput Dio;
    public DigitalInput? DioIn;

    public int Digits;
    public int Brightness = 7;
    public bool DisplayOn = true;

    /// <summary>Whether the chip acknowledged the last byte (true when there is no input to sample).</summary>
    public bool LastAck;

    /// <summary>Count of bytes the chip did not acknowledge.</summary>
    public int Nacks;

    /// <summary>Convenience: CLK and DIO GPIO numbers (open-drain, external pull-ups), 4 digits.</summary>
    public Tm1637(int clkPin, int dioPin)
        : this(new OpenDrainOutput(clkPin), new OpenDrainOutput(dioPin), new PinReader(dioPin), 4)
    {
    }

    /// <summary>Injectable: the two lines plus an optional reader of DIO for the ACK bit.</summary>
    public Tm1637(DigitalOutput clk, DigitalOutput dio, DigitalInput? dioIn, int digits)
    {
        Clk = clk;
        Dio = dio;
        DioIn = dioIn;
        Digits = digits;
    }

    /// <summary>Start condition: DIO falls while CLK is high.</summary>
    public void Start()
    {
        Dio.Write(false);
    }

    /// <summary>Stop condition: DIO rises while CLK is high.</summary>
    public void Stop()
    {
        Dio.Write(false);
        Clk.Write(true);
        Dio.Write(true);
    }

    /// <summary>Clock one byte out LSB first, then the ACK clock. Returns true if acknowledged.</summary>
    public bool WriteByte(int value)
    {
        for (int i = 0; i < 8; i++)
        {
            Clk.Write(false);
            Dio.Write(((value >> i) & 1) != 0);
            Clk.Write(true);
        }
        // Ninth clock: release DIO; the chip pulls it low as ACK.
        Clk.Write(false);
        Dio.Write(true);
        Clk.Write(true);
        bool ack = DioIn != null ? !DioIn.Read() : true;
        if (ack)
        {
            Dio.Write(false);
        }
        else
        {
            Nacks++;
        }
        Clk.Write(false);
        LastAck = ack;
        return ack;
    }

    /// <summary>One framed command: start, the bytes, stop.</summary>
    public void Frame(byte[] bytes)
    {
        Start();
        for (int i = 0; i < bytes.Length; i++)
        {
            WriteByte(bytes[i]);
        }
        Stop();
    }

    private void Frame1(int b)
    {
        Start();
        WriteByte(b);
        Stop();
    }

    /// <summary>Brightness 0..7 (pulse width 1/16 .. 14/16) and on/off; sent immediately.</summary>
    public void SetBrightness(int level, bool on)
    {
        Brightness = level < 0 ? 0 : (level > 7 ? 7 : level);
        DisplayOn = on;
        Frame1(DisplayControl());
    }

    public int DisplayControl() => CmdDisplay | (DisplayOn ? 0x08 : 0x00) | Brightness;

    /// <summary>Write raw segment bytes (bit 0 = a .. bit 6 = g, bit 7 = dp/colon) starting at <paramref name="position"/>.</summary>
    public void SetSegments(byte[] segments, int position)
    {
        Frame1(CmdDataAutoIncrement);
        byte[] f = new byte[segments.Length + 1];
        f[0] = (byte)(CmdAddress | (position & 0x07));
        for (int i = 0; i < segments.Length; i++)
        {
            f[i + 1] = segments[i];
        }
        Frame(f);
        Frame1(DisplayControl());
    }

    public void Clear() => SetSegments(new byte[Digits], 0);

    /// <summary>Show text ('.' lights the previous digit's point).</summary>
    public void ShowText(string text) => SetSegments(SevenSegment.EncodeText(text, Digits), 0);

    /// <summary>Show a right-aligned decimal number; <paramref name="colon"/> lights the clock colon (dp of digit 1).</summary>
    public void ShowNumber(int value, bool leadingZeros, bool colon)
    {
        byte[] s = SevenSegment.EncodeNumber(value, Digits, leadingZeros);
        if (colon && Digits > 1)
        {
            s[1] = (byte)(s[1] | SevenSegment.Dp);
        }
        SetSegments(s, 0);
    }

    /// <summary>Show hh:mm style time with the colon.</summary>
    public void ShowTime(int hours, int minutes, bool colon)
    {
        ShowNumber(hours * 100 + minutes, true, colon);
    }
}
