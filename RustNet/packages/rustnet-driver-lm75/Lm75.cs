using RustNet.Community.Bus;

namespace RustNet.Community.Lm75;

/// <summary>
/// LM75-family digital temperature sensor and thermal watchdog over I2C
/// (National/TI LM75, NXP LM75A/LM75B and compatibles). Address 0x48..0x4F
/// set by A2..A0. The temperature register is a left-justified two's
/// complement value: 9 bits (0.5 °C) on the classic LM75, 11 bits (0.125 °C)
/// on the LM75A/B. TOS and THYST are always 9-bit.
/// </summary>
public class Lm75
{
    public const int DefaultAddress = 0x48;

    public const int RegTemperature = 0x00;
    public const int RegConfiguration = 0x01;
    public const int RegHysteresis = 0x02;
    public const int RegOvertemperature = 0x03;

    private readonly I2cDevice _dev;

    /// <summary>Temperature register resolution: 9 (LM75) or 11 (LM75A/LM75B).</summary>
    public int ResolutionBits = 9;

    public Lm75(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Lm75(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Lm75(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>
    /// True when the device answers and its set-points look like an LM75's (THYST below TOS, both in range).
    /// The power-up defaults are TOS = 80 °C and THYST = 75 °C.
    /// </summary>
    public bool Begin()
    {
        try
        {
            double tos = ReadOvertemperature();
            double hyst = ReadHysteresis();
            return hyst <= tos && tos <= 125.0 && hyst >= -55.0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Temperature in °C.</summary>
    public double ReadTemperatureCelsius() => Decode(_dev.ReadUInt16BigEndian(RegTemperature), ResolutionBits);

    /// <summary>Decode a left-justified register: the top <paramref name="bits"/> bits, LSB = 2^-(bits-8) °C.</summary>
    public static double Decode(int register16, int bits)
    {
        int v = BitOps.ToInt16(register16) >> (16 - bits);
        return v / (double)(1 << (bits - 8));
    }

    /// <summary>Encode °C as a 9-bit left-justified set-point (0.5 °C steps).</summary>
    public static int EncodeSetPoint(double celsius)
    {
        int halfDegrees = (int)Math.Floor(celsius * 2.0 + 0.5);
        return (halfDegrees << 7) & 0xFF80;
    }

    public double ReadOvertemperature() => Decode(_dev.ReadUInt16BigEndian(RegOvertemperature), 9);

    public double ReadHysteresis() => Decode(_dev.ReadUInt16BigEndian(RegHysteresis), 9);

    /// <summary>Set TOS (O.S. asserts above it) and THYST (released below it), in °C.</summary>
    public void SetLimits(double overtemperature, double hysteresis)
    {
        WriteRegister16(RegOvertemperature, EncodeSetPoint(overtemperature));
        WriteRegister16(RegHysteresis, EncodeSetPoint(hysteresis));
    }

    /// <summary>
    /// Configuration: shutdown (bit 0), interrupt instead of comparator mode (bit 1),
    /// O.S. active high (bit 2), fault queue 1/2/4/6 (code 0..3 in bits 4:3).
    /// </summary>
    public void Configure(bool shutdown, bool interruptMode, bool activeHigh, int faultQueue)
    {
        _dev.WriteRegister(RegConfiguration,
            (shutdown ? 0x01 : 0) | (interruptMode ? 0x02 : 0) | (activeHigh ? 0x04 : 0) | ((faultQueue & 3) << 3));
    }

    private void WriteRegister16(int register, int value)
    {
        byte[] b = new byte[3];
        b[0] = (byte)register;
        b[1] = (byte)(value >> 8);
        b[2] = (byte)value;
        _dev.Write(b);
    }
}
