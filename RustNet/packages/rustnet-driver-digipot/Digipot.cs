using RustNet.Community.Bus;

namespace RustNet.Community.Digipot;

/// <summary>
/// Microchip MCP4131/MCP4151 (single) and MCP4231/MCP4251 (dual) SPI digital
/// potentiometers: 7-bit (129 taps, 0..128) for MCP41x1/42x1 "3" parts and
/// 8-bit (257 taps, 0..256) for the "5" parts. Command byte:
/// <code>
/// AD3..AD0 (7..4) · C1C0 (3..2) · D9 D8 (1..0)
/// C = 00 write data (16-bit command, data byte follows)
///     01 increment, 10 decrement (8-bit commands)
///     11 read data (16-bit; the device answers D8 in byte 0 bit 0, D7..D0 in byte 1)
/// AD = 0x0 wiper 0, 0x1 wiper 1, 0x4 TCON, 0x5 STATUS
/// </code>
/// Resistance from B to wiper: R_WB = R_AB × N / taps-1 + R_W (≈75 Ω).
/// </summary>
public class Mcp41xx
{
    public const int AddrWiper0 = 0x0;
    public const int AddrWiper1 = 0x1;
    public const int AddrTcon = 0x4;
    public const int AddrStatus = 0x5;

    private readonly SpiDevice _spi;

    /// <summary>1 (MCP41x1) or 2 (MCP42x1) wipers.</summary>
    public int Wipers;

    /// <summary>Highest wiper value: 128 (7-bit) or 256 (8-bit).</summary>
    public int MaxValue;

    /// <summary>End-to-end resistance in ohms (5k, 10k, 50k or 100k part).</summary>
    public int ResistanceOhms;

    /// <summary>Typical wiper resistance, ohms.</summary>
    public int WiperOhms = 75;

    protected Mcp41xx(SpiDevice spi, int wipers, int maxValue, int resistanceOhms)
    {
        _spi = spi;
        Wipers = wipers;
        MaxValue = maxValue;
        ResistanceOhms = resistanceOhms;
    }

    /// <summary>The 16-bit write-data command for a wiper.</summary>
    public static byte[] WriteCommand(int address, int value)
    {
        byte[] b = new byte[2];
        b[0] = (byte)((address << 4) | ((value >> 8) & 0x03));
        b[1] = (byte)value;
        return b;
    }

    public void SetWiper(int wiper, int value)
    {
        if (value < 0)
        {
            value = 0;
        }
        if (value > MaxValue)
        {
            value = MaxValue;
        }
        _spi.Write(WriteCommand(wiper == 1 ? AddrWiper1 : AddrWiper0, value));
    }

    public void Increment(int wiper) => _spi.WriteByte(((wiper == 1 ? AddrWiper1 : AddrWiper0) << 4) | 0x04);

    public void Decrement(int wiper) => _spi.WriteByte(((wiper == 1 ? AddrWiper1 : AddrWiper0) << 4) | 0x08);

    public int ReadWiper(int wiper)
    {
        byte[] tx = new byte[2];
        tx[0] = (byte)(((wiper == 1 ? AddrWiper1 : AddrWiper0) << 4) | 0x0C | 0x03);
        tx[1] = 0xFF;
        byte[] rx = _spi.Transfer(tx);
        return ((rx[0] & 0x01) << 8) | rx[1];
    }

    /// <summary>Write the TCON register (terminal connections; 0xFF = all connected).</summary>
    public void SetTerminalControl(int tcon) => _spi.Write(WriteCommand(AddrTcon, tcon));

    /// <summary>R_WB in ohms for a wiper value.</summary>
    public double WiperToBResistance(int value) => (double)ResistanceOhms * value / MaxValue + WiperOhms;

    /// <summary>Wiper value giving the nearest R_WB to <paramref name="ohms"/>.</summary>
    public int ValueForResistance(double ohms)
    {
        int v = (int)((ohms - WiperOhms) * MaxValue / ResistanceOhms + 0.5);
        return v < 0 ? 0 : (v > MaxValue ? MaxValue : v);
    }
}

/// <summary>MCP4131: single, 7-bit (129 taps).</summary>
public class Mcp4131 : Mcp41xx
{
    public Mcp4131(int bus, int chipSelectPin, int resistanceOhms) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0), resistanceOhms)
    {
    }

    public Mcp4131(SpiDevice spi, int resistanceOhms) : base(spi, 1, 128, resistanceOhms)
    {
    }
}

/// <summary>MCP4151: single, 8-bit (257 taps).</summary>
public class Mcp4151 : Mcp41xx
{
    public Mcp4151(int bus, int chipSelectPin, int resistanceOhms) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0), resistanceOhms)
    {
    }

    public Mcp4151(SpiDevice spi, int resistanceOhms) : base(spi, 1, 256, resistanceOhms)
    {
    }
}

/// <summary>MCP4231: dual, 7-bit (129 taps).</summary>
public class Mcp4231 : Mcp41xx
{
    public Mcp4231(int bus, int chipSelectPin, int resistanceOhms) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0), resistanceOhms)
    {
    }

    public Mcp4231(SpiDevice spi, int resistanceOhms) : base(spi, 2, 128, resistanceOhms)
    {
    }
}

/// <summary>MCP4251: dual, 8-bit (257 taps).</summary>
public class Mcp4251 : Mcp41xx
{
    public Mcp4251(int bus, int chipSelectPin, int resistanceOhms) : this(new SpiDevice(bus, chipSelectPin, 1000000, 0), resistanceOhms)
    {
    }

    public Mcp4251(SpiDevice spi, int resistanceOhms) : base(spi, 2, 256, resistanceOhms)
    {
    }
}

/// <summary>
/// Microchip MCP4017 / MCP4018 / MCP4019: 7-bit (128 taps, 0..127) volatile
/// I2C digital pot/rheostat, address 0x2F. The protocol is a single data byte
/// in both directions: write [0·D6..D0], read 1 byte. MCP4018 is a
/// potentiometer (A, W, B), MCP4017 a rheostat with B grounded, MCP4019 a
/// floating rheostat. R_WB = R_AB × N / 127 + R_W.
/// </summary>
public class Mcp401x
{
    public const int DefaultAddress = 0x2F;
    public const int MaxValue = 127;

    private readonly I2cDevice _dev;

    public int ResistanceOhms;

    public int WiperOhms = 75;

    public Mcp401x(int bus, int resistanceOhms) : this(new I2cDevice(bus, DefaultAddress), resistanceOhms)
    {
    }

    public Mcp401x(I2cDevice device, int resistanceOhms)
    {
        _dev = device;
        ResistanceOhms = resistanceOhms;
    }

    public void SetWiper(int value) => _dev.WriteByte(value < 0 ? 0 : (value > MaxValue ? MaxValue : value));

    public int ReadWiper() => _dev.Read(1)[0] & 0x7F;

    public double WiperToBResistance(int value) => (double)ResistanceOhms * value / MaxValue + WiperOhms;

    public void SetResistance(double ohms)
    {
        SetWiper((int)((ohms - WiperOhms) * MaxValue / ResistanceOhms + 0.5));
    }
}

/// <summary>
/// Microchip MCP4451 (volatile) / MCP4461 (non-volatile) quad 8-bit (257
/// taps, 0..256) I2C digital potentiometers, address 0b01011·A1·A0 (0x2C..0x2F).
/// Command byte as the MCP41xx: AD3..AD0 · C1C0 · D9 D8, followed by D7..D0
/// for a write, so a wiper value takes 9 bits (D8 carries 256).
/// Memory map: volatile wipers 0,1 at 0x00,0x01 and 2,3 at 0x06,0x07;
/// non-volatile wipers (MCP4461) 0x02,0x03,0x08,0x09; TCON0 0x04, STATUS 0x05,
/// TCON1 0x0A.
/// </summary>
public class Mcp44xx
{
    public const int DefaultAddress = 0x2C;
    public const int MaxValue = 256;

    private readonly I2cDevice _dev;

    /// <summary>True for the MCP4461 (EEPROM-backed wipers).</summary>
    public bool NonVolatile;

    public int ResistanceOhms;

    public int WiperOhms = 75;

    protected Mcp44xx(I2cDevice device, bool nonVolatile, int resistanceOhms)
    {
        _dev = device;
        NonVolatile = nonVolatile;
        ResistanceOhms = resistanceOhms;
    }

    /// <summary>Memory address of wiper 0..3, volatile or non-volatile copy.</summary>
    public static int WiperAddress(int wiper, bool nonVolatile)
    {
        int a = wiper < 2 ? wiper : wiper + 4;
        return nonVolatile ? a + 2 : a;
    }

    private void WriteData(int address, int value)
    {
        byte[] b = new byte[2];
        b[0] = (byte)((address << 4) | ((value >> 8) & 0x01));
        b[1] = (byte)value;
        _dev.Write(b);
    }

    public void SetWiper(int wiper, int value)
    {
        WriteData(WiperAddress(wiper, false), value < 0 ? 0 : (value > MaxValue ? MaxValue : value));
    }

    /// <summary>Store a power-up wiper value in EEPROM (MCP4461 only; waits the 10 ms write cycle).</summary>
    public bool SetWiperPowerUp(int wiper, int value)
    {
        if (!NonVolatile)
        {
            return false;
        }
        WriteData(WiperAddress(wiper, true), value < 0 ? 0 : (value > MaxValue ? MaxValue : value));
        Timing.SleepMs(10);
        return true;
    }

    public void Increment(int wiper) => _dev.WriteByte((WiperAddress(wiper, false) << 4) | 0x04);

    public void Decrement(int wiper) => _dev.WriteByte((WiperAddress(wiper, false) << 4) | 0x08);

    public int ReadWiper(int wiper)
    {
        byte[] cmd = new byte[1];
        cmd[0] = (byte)((WiperAddress(wiper, false) << 4) | 0x0C);
        byte[] d = _dev.WriteRead(cmd, 2);
        return ((d[0] & 0x01) << 8) | d[1];
    }

    /// <summary>TCON0 (wipers 0,1) or TCON1 (wipers 2,3); 0xFF = all terminals connected.</summary>
    public void SetTerminalControl(int tconIndex, int value) => WriteData(tconIndex == 0 ? 0x04 : 0x0A, value);

    public double WiperToBResistance(int value) => (double)ResistanceOhms * value / MaxValue + WiperOhms;
}

/// <summary>MCP4451: quad 8-bit volatile pot.</summary>
public class Mcp4451 : Mcp44xx
{
    public Mcp4451(int bus, int address, int resistanceOhms) : this(new I2cDevice(bus, address), resistanceOhms)
    {
    }

    public Mcp4451(I2cDevice device, int resistanceOhms) : base(device, false, resistanceOhms)
    {
    }
}

/// <summary>MCP4461: quad 8-bit non-volatile pot.</summary>
public class Mcp4461 : Mcp44xx
{
    public Mcp4461(int bus, int address, int resistanceOhms) : this(new I2cDevice(bus, address), resistanceOhms)
    {
    }

    public Mcp4461(I2cDevice device, int resistanceOhms) : base(device, true, resistanceOhms)
    {
    }
}
