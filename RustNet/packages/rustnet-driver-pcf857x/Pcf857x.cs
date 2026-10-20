using RustNet.Community.Bus;

namespace RustNet.Community.Pcf857x;

/// <summary>
/// NXP/TI PCF8574 (8 pins, 0x20–0x27; PCF8574A 0x38–0x3F) and PCF8575
/// (16 pins, 0x20–0x27) quasi-bidirectional I2C expanders. There are no
/// registers: a write sets the output latches (P0..P7, then P10..P17 on the
/// PCF8575), a read returns the pin levels in the same order. A latch at 0
/// sinks current hard; a latch at 1 is a weak (~100 µA) pull-up, so a pin is
/// used as an input by writing 1 to it and letting the outside world pull it
/// low. Power-on state: all latches high.
/// Pins are numbered 0..7 and (PCF8575) 8..15 for P10..P17.
/// </summary>
public class Pcf857x
{
    private readonly I2cDevice _dev;

    /// <summary>8 or 16.</summary>
    public int PinCount;

    /// <summary>Output latch image (bit n = pin n); 1 = high / input.</summary>
    public int Latch;

    protected Pcf857x(I2cDevice device, int pins)
    {
        _dev = device;
        PinCount = pins;
        Latch = pins == 16 ? 0xFFFF : 0xFF;
    }

    /// <summary>Write the latch image to the chip.</summary>
    public void WriteAll(int value)
    {
        Latch = value & (PinCount == 16 ? 0xFFFF : 0xFF);
        byte[] b = new byte[PinCount / 8];
        b[0] = (byte)Latch;
        if (PinCount == 16)
        {
            b[1] = (byte)(Latch >> 8);
        }
        _dev.Write(b);
    }

    /// <summary>Read every pin level (bit n = pin n).</summary>
    public int ReadAll()
    {
        byte[] d = _dev.Read(PinCount / 8);
        return PinCount == 16 ? d[0] | (d[1] << 8) : d[0];
    }

    public void WritePin(int pin, bool high) => WriteAll(high ? Latch | (1 << pin) : Latch & ~(1 << pin));

    /// <summary>Make a pin an input: release it to the weak pull-up.</summary>
    public void SetInput(int pin) => WritePin(pin, true);

    public bool ReadPin(int pin) => (ReadAll() & (1 << pin)) != 0;
}

/// <summary>PCF8574 / PCF8574A: 8 quasi-bidirectional pins.</summary>
public class Pcf8574 : Pcf857x
{
    public const int DefaultAddress = 0x20;

    public Pcf8574(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Pcf8574(I2cDevice device) : base(device, 8)
    {
    }
}

/// <summary>PCF8575: 16 quasi-bidirectional pins.</summary>
public class Pcf8575 : Pcf857x
{
    public const int DefaultAddress = 0x20;

    public Pcf8575(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Pcf8575(I2cDevice device) : base(device, 16)
    {
    }
}
