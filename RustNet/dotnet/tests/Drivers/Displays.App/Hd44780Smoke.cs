using RustNet.Community.Bus;
using RustNet.Community.CharLcd;

namespace DriversApp;

/// <summary>
/// The E pin of a simulated HD44780: on each falling edge it samples RS and
/// D4..D7 — exactly what the controller latches — and records (RS &lt;&lt; 4) | nibble.
/// </summary>
public class LatchPin : DigitalOutput
{
    public List<int> Latched = new List<int>();
    public DigitalOutput Rs;
    public DigitalOutput D4;
    public DigitalOutput D5;
    public DigitalOutput D6;
    public DigitalOutput D7;

    public LatchPin(DigitalOutput rs, DigitalOutput d4, DigitalOutput d5, DigitalOutput d6, DigitalOutput d7)
    {
        Rs = rs;
        D4 = d4;
        D5 = d5;
        D6 = d6;
        D7 = d7;
    }

    public override void Write(bool high)
    {
        if (State && !high)
        {
            int n = (D4.State ? 1 : 0) | (D5.State ? 2 : 0) | (D6.State ? 4 : 0) | (D7.State ? 8 : 0);
            Latched.Add((Rs.State ? 0x10 : 0) | n);
        }
        State = high;
    }
}

public static class Hd44780Smoke
{
    public static void Run()
    {
        // PCF8574 backpack: P0 RS, P1 RW, P2 E, P3 backlight, P4..P7 D4..D7.
        // Each nibble n is three expander writes: (n<<4)|rs|BL, the same | E (0x04), then without E.
        SimI2cDevice exp = new SimI2cDevice(0x27);
        exp.OnWrite = null;
        Hd44780 lcd = new Hd44780(new Hd44780Pcf8574Bus(exp), 16, 2);
        lcd.Begin();
        Expect.I2c("hd44780 pcf8574 init",
            "| 08"                       // backlight on, everything else low
            + "| 38 3C 38 | 38 3C 38 | 38 3C 38" // 0x3 three times (8-bit mode, datasheet fig. 24)
            + "| 28 2C 28"                 // 0x2: switch to 4-bit
            + "| 28 2C 28 88 8C 88"        // function set 0x28: 4-bit, 2 lines, 5x8
            + "| 08 0C 08 C8 CC C8"        // display control 0x0C: display on
            + "| 08 0C 08 18 1C 18"        // clear 0x01
            + "| 08 0C 08 68 6C 68",       // entry mode 0x06: increment, no shift
            exp, 0);
        int w = exp.Writes.Count;
        lcd.SetCursor(3, 1);
        lcd.Write("Hi");
        // DDRAM 0x40 + 3 -> command 0xC3; 'H' 0x48 and 'i' 0x69 with RS = 1.
        Expect.I2c("hd44780 cursor + text", "| C8 CC C8 38 3C 38 | 49 4D 49 89 8D 89 69 6D 69 99 9D 99", exp, w);
        Check.Equal("hd44780 20x4 row 2", 0x14, new Hd44780(new Hd44780Pcf8574Bus(exp), 20, 4).Address(0, 2));
        Check.Equal("hd44780 20x4 row 3", 0x54 + 5, new Hd44780(new Hd44780Pcf8574Bus(exp), 20, 4).Address(5, 3));
        w = exp.Writes.Count;
        lcd.Backlight(false);
        Expect.I2c("hd44780 backlight off", "| 00", exp, w);

        // Direct GPIO: decode what the controller latches on E falling edges.
        SimDigitalOutput rs = new SimDigitalOutput();
        SimDigitalOutput d4 = new SimDigitalOutput();
        SimDigitalOutput d5 = new SimDigitalOutput();
        SimDigitalOutput d6 = new SimDigitalOutput();
        SimDigitalOutput d7 = new SimDigitalOutput();
        LatchPin e = new LatchPin(rs, d4, d5, d6, d7);
        Hd44780 g = new Hd44780(new Hd44780GpioBus(rs, e, d4, d5, d6, d7, null), 16, 2);
        g.Begin();
        g.Write("A");
        byte[] cg = new byte[8];
        cg[0] = 0x1F;
        cg[7] = 0x11;
        g.CreateChar(2, cg);
        Expect.Compare("hd44780 gpio nibbles",
            Expect.Parse("03 03 03 02  02 08  00 0C  00 01  00 06  14 11"
                + " 05 00"                                  // CGRAM address 0x40 | 2<<3 = 0x50
                + " 11 1F 10 10 10 10 10 10 10 10 10 10 10 10 11 11"),
            e.Latched, 0, true);
        Check.True("hd44780 gpio rs high for data", rs.State);
    }
}
