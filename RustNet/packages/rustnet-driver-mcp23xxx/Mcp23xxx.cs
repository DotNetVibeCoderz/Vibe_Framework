using RustNet.Community.Bus;

namespace RustNet.Community.Mcp23xxx;

/// <summary>
/// Microchip MCP23008/MCP23017 (I2C) and MCP23S08/MCP23S17 (SPI) GPIO
/// expanders. 8-port register map (MCP23x08):
/// <code>
/// IODIR 0x00  IPOL 0x01  GPINTEN 0x02  DEFVAL 0x03  INTCON 0x04  IOCON 0x05
/// GPPU 0x06   INTF 0x07  INTCAP 0x08   GPIO 0x09    OLAT 0x0A
/// </code>
/// The 16-port parts with IOCON.BANK = 0 (power-on default) interleave the
/// ports: register R of port A sits at 2R and of port B at 2R+1
/// (IODIRA 0x00, IODIRB 0x01, … GPPUA 0x0C, GPIOA 0x12, OLATA 0x14, OLATB 0x15),
/// so both ports of a register are written in one sequential transfer.
/// SPI parts take an opcode first: 0100·A2·A1·A0·R/W (0x40 | addr&lt;&lt;1 | 1 for read),
/// and only decode A2..A0 once IOCON.HAEN is set.
/// Pins are numbered 0..7 (port A / GP) and 8..15 (port B). IODIR bit 1 = input.
/// </summary>
public class Mcp23xxx
{
    public const int RegIodir = 0x00;
    public const int RegIpol = 0x01;
    public const int RegGpinten = 0x02;
    public const int RegDefval = 0x03;
    public const int RegIntcon = 0x04;
    public const int RegIocon = 0x05;
    public const int RegGppu = 0x06;
    public const int RegIntf = 0x07;
    public const int RegIntcap = 0x08;
    public const int RegGpio = 0x09;
    public const int RegOlat = 0x0A;

    /// <summary>IOCON.HAEN: hardware address enable (SPI parts).</summary>
    public const int IoconHaen = 0x08;

    private readonly I2cDevice? _i2c;
    private readonly SpiDevice? _spi;

    /// <summary>SPI hardware address A2..A0 (0..7).</summary>
    public int HardwareAddress;

    /// <summary>1 (x08) or 2 (x17) ports of 8 pins.</summary>
    public int Ports;

    // Cached register images, bit n = pin n.
    private int _iodir;
    private int _gppu;
    private int _olat;
    private int _ipol;

    protected Mcp23xxx(I2cDevice device, int ports)
    {
        // Assign both transports explicitly: on the interpreter a reference
        // field that was never stored cannot be compared with null.
        _i2c = device;
        _spi = null;
        Ports = ports;
        _iodir = ports == 2 ? 0xFFFF : 0xFF;
    }

    protected Mcp23xxx(SpiDevice device, int hardwareAddress, int ports)
    {
        _i2c = null;
        _spi = device;
        HardwareAddress = hardwareAddress & 7;
        Ports = ports;
        _iodir = ports == 2 ? 0xFFFF : 0xFF;
    }

    public int PinCount => Ports * 8;

    /// <summary>Address of an 8-port register for a port, in the BANK=0 map.</summary>
    public int RegisterAddress(int register, int port) => Ports == 1 ? register : register * 2 + port;

    /// <summary>SPI opcode byte: 0x40 | A2A1A0 &lt;&lt; 1 | R/W.</summary>
    public int Opcode(bool read) => 0x40 | (HardwareAddress << 1) | (read ? 1 : 0);

    /// <summary>Write consecutive registers starting at the absolute address <paramref name="address"/>.</summary>
    public void WriteRegisters(int address, byte[] values)
    {
        if (_i2c != null)
        {
            _i2c.WriteRegisters(address, values);
            return;
        }
        WriteSpi(Opcode(false), address, values);
    }

    private void WriteSpi(int opcode, int address, byte[] values)
    {
        byte[] b = new byte[values.Length + 2];
        b[0] = (byte)opcode;
        b[1] = (byte)address;
        for (int i = 0; i < values.Length; i++)
        {
            b[i + 2] = values[i];
        }
        _spi!.Write(b);
    }

    public byte[] ReadRegisters(int address, int count)
    {
        if (_i2c != null)
        {
            return _i2c.ReadRegisters(address, count);
        }
        byte[] tx = new byte[count + 2];
        tx[0] = (byte)Opcode(true);
        tx[1] = (byte)address;
        byte[] rx = _spi!.Transfer(tx);
        byte[] r = new byte[count];
        for (int i = 0; i < count; i++)
        {
            r[i] = rx[i + 2];
        }
        return r;
    }

    /// <summary>Write an 8-port register for every port (one transfer).</summary>
    private void WriteAll(int register, int value)
    {
        byte[] v = new byte[Ports];
        v[0] = (byte)value;
        if (Ports == 2)
        {
            v[1] = (byte)(value >> 8);
        }
        WriteRegisters(RegisterAddress(register, 0), v);
    }

    private void WritePortOf(int register, int pin, int image)
    {
        int port = pin >> 3;
        byte[] v = new byte[1];
        v[0] = (byte)(image >> (port * 8));
        WriteRegisters(RegisterAddress(register, port), v);
    }

    private int ReadAll(int register)
    {
        byte[] d = ReadRegisters(RegisterAddress(register, 0), Ports);
        return Ports == 2 ? d[0] | (d[1] << 8) : d[0];
    }

    /// <summary>
    /// Configure IOCON (BANK=0, sequential addressing; HAEN on SPI parts) and
    /// restore the power-on state: all pins inputs, no pull-ups, OLAT = 0.
    /// For SPI the IOCON write goes to address 0, which every MCP23Sxx on the
    /// bus answers before HAEN is set.
    /// </summary>
    public void Begin()
    {
        int iocon = _spi != null ? IoconHaen : 0x00;
        byte[] v = new byte[1];
        v[0] = (byte)iocon;
        if (_spi != null)
        {
            WriteSpi(0x40, RegisterAddress(RegIocon, 0), v);
        }
        else
        {
            WriteRegisters(RegisterAddress(RegIocon, 0), v);
        }
        _iodir = Ports == 2 ? 0xFFFF : 0xFF;
        _gppu = 0;
        _olat = 0;
        WriteAll(RegIodir, _iodir);
        WriteAll(RegGppu, _gppu);
        WriteAll(RegOlat, _olat);
    }

    /// <summary>Direction of one pin.</summary>
    public void SetPinMode(int pin, bool output)
    {
        _iodir = output ? _iodir & ~(1 << pin) : _iodir | (1 << pin);
        WritePortOf(RegIodir, pin, _iodir);
    }

    /// <summary>100 kΩ pull-up on an input pin.</summary>
    public void SetPullUp(int pin, bool on)
    {
        _gppu = on ? _gppu | (1 << pin) : _gppu & ~(1 << pin);
        WritePortOf(RegGppu, pin, _gppu);
    }

    /// <summary>Invert the GPIO reading of an input pin (IPOL).</summary>
    public void SetInputInverted(int pin, bool inverted)
    {
        _ipol = inverted ? _ipol | (1 << pin) : _ipol & ~(1 << pin);
        WritePortOf(RegIpol, pin, _ipol);
    }

    /// <summary>Drive an output pin (writes the output latch of its port).</summary>
    public void WritePin(int pin, bool high)
    {
        _olat = high ? _olat | (1 << pin) : _olat & ~(1 << pin);
        WritePortOf(RegOlat, pin, _olat);
    }

    public bool ReadPin(int pin) => (ReadAll(RegGpio) & (1 << pin)) != 0;

    /// <summary>Direction of every pin at once (bit = 1: input).</summary>
    public void SetDirections(int inputMask)
    {
        _iodir = inputMask;
        WriteAll(RegIodir, _iodir);
    }

    /// <summary>Write every output latch at once (bit n = pin n).</summary>
    public void WriteOutputs(int value)
    {
        _olat = value;
        WriteAll(RegOlat, _olat);
    }

    /// <summary>Read every pin level at once (bit n = pin n).</summary>
    public int ReadInputs() => ReadAll(RegGpio);

    /// <summary>Pins that raised an interrupt (INTF).</summary>
    public int ReadInterruptFlags() => ReadAll(RegIntf);

    /// <summary>Interrupt-on-change for a pin (against its previous value).</summary>
    public void EnableInterruptOnChange(int mask)
    {
        WriteAll(RegIntcon, 0);
        WriteAll(RegGpinten, mask);
    }
}

/// <summary>MCP23008: 8 GPIOs on I2C, address 0x20..0x27.</summary>
public class Mcp23008 : Mcp23xxx
{
    public const int DefaultAddress = 0x20;

    public Mcp23008(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Mcp23008(I2cDevice device) : base(device, 1)
    {
    }
}

/// <summary>MCP23017: 16 GPIOs on I2C, address 0x20..0x27.</summary>
public class Mcp23017 : Mcp23xxx
{
    public const int DefaultAddress = 0x20;

    public Mcp23017(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Mcp23017(I2cDevice device) : base(device, 2)
    {
    }
}

/// <summary>MCP23S08: 8 GPIOs on SPI (up to 10 MHz), hardware address 0..3 (A1, A0).</summary>
public class Mcp23s08 : Mcp23xxx
{
    public Mcp23s08(int bus, int chipSelectPin, int hardwareAddress) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0), hardwareAddress)
    {
    }

    public Mcp23s08(SpiDevice device, int hardwareAddress) : base(device, hardwareAddress, 1)
    {
    }
}

/// <summary>MCP23S17: 16 GPIOs on SPI (up to 10 MHz), hardware address 0..7.</summary>
public class Mcp23s17 : Mcp23xxx
{
    public Mcp23s17(int bus, int chipSelectPin, int hardwareAddress) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0), hardwareAddress)
    {
    }

    public Mcp23s17(SpiDevice device, int hardwareAddress) : base(device, hardwareAddress, 2)
    {
    }
}
