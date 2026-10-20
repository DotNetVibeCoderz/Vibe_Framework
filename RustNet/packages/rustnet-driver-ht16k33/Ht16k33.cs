using RustNet.Community.Bus;

namespace RustNet.Community.Ht16k33;

/// <summary>
/// Holtek HT16K33 RAM-mapped 16×8 LED controller with 13×3 key scan.
/// Commands (single byte):
/// <code>
/// 0x20 | S          system setup, S=1 oscillator on (0x21)
/// 0x80 | B1B0·D     display setup: D display on, B blink 00 off, 01 2 Hz, 10 1 Hz, 11 0.5 Hz
/// 0xE0 | P3..P0     dimming, 1/16 .. 16/16 duty
/// 0xA0 | ACT·ROW    ROW/INT set: 0xA0 = ROW15/INT pin is a row output
/// 0x00..0x0F        display RAM: byte 2n = COMn ROW0..7, 2n+1 = COMn ROW8..15
/// 0x40..0x45        key data RAM: KS0, KS1, KS2 as 16-bit little-endian, K1..K13
/// 0x60              INT flag (non-zero when a key is pressed)
/// </code>
/// Address 0x70..0x77.
/// </summary>
public class Ht16k33
{
    public const int DefaultAddress = 0x70;

    public const int BlinkOff = 0;
    public const int Blink2Hz = 1;
    public const int Blink1Hz = 2;
    public const int BlinkHalfHz = 3;

    private readonly I2cDevice _dev;

    /// <summary>Display RAM image, 16 bytes.</summary>
    public byte[] Buffer = new byte[16];

    private int _blink;
    private bool _on;

    public Ht16k33(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Ht16k33(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Oscillator on, ROW15/INT as row output, full brightness, display on, RAM cleared.</summary>
    public void Begin()
    {
        _dev.WriteByte(0x21);
        _dev.WriteByte(0xA0);
        SetBrightness(15);
        Clear();
        Show();
        SetDisplay(true, BlinkOff);
    }

    /// <summary>Display on/off and blink rate (<see cref="BlinkOff"/> … <see cref="BlinkHalfHz"/>).</summary>
    public void SetDisplay(bool on, int blink)
    {
        _on = on;
        _blink = blink & 3;
        _dev.WriteByte(0x80 | (_blink << 1) | (on ? 1 : 0));
    }

    public void SetBlink(int blink) => SetDisplay(_on, blink);

    /// <summary>Brightness 0..15 (duty (n+1)/16).</summary>
    public void SetBrightness(int level) => _dev.WriteByte(0xE0 | (level < 0 ? 0 : (level > 15 ? 15 : level)));

    /// <summary>Standby: oscillator off.</summary>
    public void Standby() => _dev.WriteByte(0x20);

    public void Clear()
    {
        for (int i = 0; i < 16; i++)
        {
            Buffer[i] = 0;
        }
    }

    /// <summary>Set the LED at common <paramref name="com"/> (0..7) and row <paramref name="row"/> (0..15).</summary>
    public void SetLed(int com, int row, bool on)
    {
        int index = com * 2 + (row >> 3);
        int mask = 1 << (row & 7);
        Buffer[index] = (byte)(on ? Buffer[index] | mask : Buffer[index] & ~mask);
    }

    public bool GetLed(int com, int row) => (Buffer[com * 2 + (row >> 3)] & (1 << (row & 7))) != 0;

    /// <summary>All 16 rows of one common as a 16-bit word (7-segment digits: one byte per digit position).</summary>
    public void SetRow(int com, int bits)
    {
        Buffer[com * 2] = (byte)bits;
        Buffer[com * 2 + 1] = (byte)(bits >> 8);
    }

    /// <summary>Write the RAM image (one 17-byte transfer from address 0x00).</summary>
    public void Show() => _dev.WriteRegisters(0x00, Buffer);

    /// <summary>Key data: three 13-bit masks (KS0, KS1, KS2; bit 0 = K1). Reading clears them.</summary>
    public int[] ReadKeys()
    {
        byte[] d = _dev.ReadRegisters(0x40, 6);
        int[] r = new int[3];
        for (int i = 0; i < 3; i++)
        {
            r[i] = (d[i * 2] | (d[i * 2 + 1] << 8)) & 0x1FFF;
        }
        return r;
    }

    /// <summary>INT flag register: true when key data is pending.</summary>
    public bool KeyPending() => _dev.ReadRegister(0x60) != 0;
}
