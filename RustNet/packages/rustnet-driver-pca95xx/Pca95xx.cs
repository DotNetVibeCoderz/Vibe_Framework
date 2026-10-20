using RustNet.Community.Bus;

namespace RustNet.Community.Pca95xx;

/// <summary>
/// NXP/TI PCA9554 (8-bit) and PCA9555 / TCA9535 / TCA9555 (16-bit) I2C GPIO
/// expanders. Four register kinds, one register per port:
/// <code>
///                 PCA9554   PCA9555/TCA9535/TCA9555
/// input port       0x00       0x00, 0x01
/// output port      0x01       0x02, 0x03   (power-on 0xFF)
/// polarity inv.    0x02       0x04, 0x05   (power-on 0x00)
/// configuration    0x03       0x06, 0x07   (power-on 0xFF: all inputs)
/// </code>
/// The 16-bit parts auto-increment within a register pair, so both ports
/// travel in one transfer. Config bit 1 = input. Pins 0..7 = port 0, 8..15 = port 1.
/// Addresses 0x20–0x27 (PCA9554A: 0x38–0x3F).
/// </summary>
public class Pca95xx
{
    public const int KindInput = 0;
    public const int KindOutput = 1;
    public const int KindPolarity = 2;
    public const int KindConfig = 3;

    public const int DefaultAddress = 0x20;

    private readonly I2cDevice _dev;

    /// <summary>1 or 2 ports.</summary>
    public int Ports;

    private int _output;
    private int _config;
    private int _polarity;

    protected Pca95xx(I2cDevice device, int ports)
    {
        _dev = device;
        Ports = ports;
        _output = ports == 2 ? 0xFFFF : 0xFF;
        _config = _output;
    }

    /// <summary>Register address of a register kind for a port: kind × ports + port.</summary>
    public int RegisterAddress(int kind, int port) => kind * Ports + port;

    private void WriteKind(int kind, int value)
    {
        byte[] v = new byte[Ports];
        v[0] = (byte)value;
        if (Ports == 2)
        {
            v[1] = (byte)(value >> 8);
        }
        _dev.WriteRegisters(RegisterAddress(kind, 0), v);
    }

    private void WritePortOf(int kind, int pin, int image)
    {
        int port = pin >> 3;
        _dev.WriteRegister(RegisterAddress(kind, port), image >> (port * 8));
    }

    private int ReadKind(int kind)
    {
        byte[] d = _dev.ReadRegisters(RegisterAddress(kind, 0), Ports);
        return Ports == 2 ? d[0] | (d[1] << 8) : d[0];
    }

    /// <summary>Load the cached output/config/polarity images from the chip.</summary>
    public void Begin()
    {
        _output = ReadKind(KindOutput);
        _config = ReadKind(KindConfig);
        _polarity = ReadKind(KindPolarity);
    }

    public void SetPinMode(int pin, bool output)
    {
        _config = output ? _config & ~(1 << pin) : _config | (1 << pin);
        WritePortOf(KindConfig, pin, _config);
    }

    public void WritePin(int pin, bool high)
    {
        _output = high ? _output | (1 << pin) : _output & ~(1 << pin);
        WritePortOf(KindOutput, pin, _output);
    }

    public void SetInputInverted(int pin, bool inverted)
    {
        _polarity = inverted ? _polarity | (1 << pin) : _polarity & ~(1 << pin);
        WritePortOf(KindPolarity, pin, _polarity);
    }

    public bool ReadPin(int pin) => (ReadKind(KindInput) & (1 << pin)) != 0;

    /// <summary>All pin directions at once (bit = 1: input).</summary>
    public void SetDirections(int inputMask)
    {
        _config = inputMask;
        WriteKind(KindConfig, _config);
    }

    public void WriteOutputs(int value)
    {
        _output = value;
        WriteKind(KindOutput, _output);
    }

    public int ReadInputs() => ReadKind(KindInput);
}

/// <summary>PCA9554 / PCA9554A / TCA9554: 8 pins.</summary>
public class Pca9554 : Pca95xx
{
    public Pca9554(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Pca9554(I2cDevice device) : base(device, 1)
    {
    }
}

/// <summary>PCA9555: 16 pins with 100 kΩ pull-ups.</summary>
public class Pca9555 : Pca95xx
{
    public Pca9555(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Pca9555(I2cDevice device) : base(device, 2)
    {
    }
}

/// <summary>TCA9535: 16 pins, no internal pull-ups, 1.65–5.5 V.</summary>
public class Tca9535 : Pca95xx
{
    public Tca9535(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Tca9535(I2cDevice device) : base(device, 2)
    {
    }
}

/// <summary>TCA9555: 16 pins with 100 kΩ pull-ups, 1.65–5.5 V.</summary>
public class Tca9555 : Pca95xx
{
    public Tca9555(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Tca9555(I2cDevice device) : base(device, 2)
    {
    }
}
