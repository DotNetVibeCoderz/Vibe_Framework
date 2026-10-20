using RustNet.Community.Bus;

namespace RustNet.Community.Mcp4725;

/// <summary>Decoded 5-byte MCP4725 read-back.</summary>
public class Mcp4725Status
{
    /// <summary>RDY/BSY: false while an EEPROM write is in progress.</summary>
    public bool Ready;

    /// <summary>Power-on-reset flag.</summary>
    public bool PowerOnReset;

    public int PowerDown;
    public int DacValue;
    public int EepromPowerDown;
    public int EepromValue;
}

/// <summary>
/// Microchip MCP4725 12-bit I2C DAC with EEPROM. Write commands (datasheet
/// §6.1):
/// <code>
/// fast mode        C2C1=00: [0 0 PD1 PD0 D11..D8] [D7..D0]
/// write DAC        C2C1C0=010: [0 1 0 x x PD1 PD0 x] [D11..D4] [D3..D0 x x x x]
/// write DAC+EEPROM C2C1C0=011: [0 1 1 x x PD1 PD0 x] [D11..D4] [D3..D0 x x x x]
/// </code>
/// Power-down: 0 normal, 1 = 1 kΩ, 2 = 100 kΩ, 3 = 500 kΩ to ground.
/// Vout = VDD × D / 4096. Addresses 0x60..0x67 (part A0/A1/A2 + A0 pin).
/// </summary>
public class Mcp4725
{
    public const int DefaultAddress = 0x60;

    public const int PowerDownNone = 0;
    public const int PowerDown1k = 1;
    public const int PowerDown100k = 2;
    public const int PowerDown500k = 3;

    private readonly I2cDevice _dev;

    /// <summary>Supply (= reference) in millivolts.</summary>
    public int SupplyMillivolts = 3300;

    public Mcp4725(int bus, int address = DefaultAddress) : this(new I2cDevice(bus, address))
    {
    }

    public Mcp4725(I2cDevice device)
    {
        _dev = device;
    }

    private static int Clamp12(int v) => v < 0 ? 0 : (v > 4095 ? 4095 : v);

    /// <summary>Fast-mode write (2 bytes): DAC register only, nothing persisted.</summary>
    public void SetValueFast(int value, int powerDown = PowerDownNone)
    {
        value = Clamp12(value);
        byte[] b = new byte[2];
        b[0] = (byte)(((powerDown & 3) << 4) | (value >> 8));
        b[1] = (byte)value;
        _dev.Write(b);
    }

    /// <summary>Write-DAC-register command (3 bytes).</summary>
    public void SetValue(int value, int powerDown = PowerDownNone) => WriteCommand(0x40, value, powerDown);

    /// <summary>Write DAC register and EEPROM (power-up value), then wait for RDY. Returns false on time-out.</summary>
    public bool SetValueAndPersist(int value, int powerDown = PowerDownNone)
    {
        WriteCommand(0x60, value, powerDown);
        // EEPROM write takes 25..50 ms; RDY/BSY goes high when done.
        for (int i = 0; i < 10; i++)
        {
            Timing.SleepMs(10);
            if (ReadStatus().Ready)
            {
                return true;
            }
        }
        return false;
    }

    private void WriteCommand(int command, int value, int powerDown)
    {
        value = Clamp12(value);
        byte[] b = new byte[3];
        b[0] = (byte)(command | ((powerDown & 3) << 1));
        b[1] = (byte)(value >> 4);
        b[2] = (byte)((value & 0x0F) << 4);
        _dev.Write(b);
    }

    /// <summary>Output voltage in millivolts: D = round(mV × 4096 / VDD).</summary>
    public void SetMillivolts(double millivolts) => SetValueFast((int)(millivolts * 4096.0 / SupplyMillivolts + 0.5));

    /// <summary>Read the 5 status bytes: settings, DAC register, EEPROM.</summary>
    public Mcp4725Status ReadStatus()
    {
        byte[] d = _dev.Read(5);
        Mcp4725Status s = new Mcp4725Status();
        s.Ready = (d[0] & 0x80) != 0;
        s.PowerOnReset = (d[0] & 0x40) != 0;
        s.PowerDown = (d[0] >> 1) & 3;
        s.DacValue = (d[1] << 4) | (d[2] >> 4);
        s.EepromPowerDown = (d[3] >> 5) & 3;
        s.EepromValue = ((d[3] & 0x0F) << 8) | d[4];
        return s;
    }
}
