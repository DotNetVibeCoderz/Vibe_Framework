using RustNet.Community.Bus;

namespace RustNet.Community.Mcp9808;

/// <summary>
/// Microchip MCP9808 ±0.25 °C digital temperature sensor over I2C. Address
/// 0x18..0x1F (A2..A0). Ambient temperature is a 13-bit two's complement
/// value in 1/16 °C with three alert flags in bits 15:13.
/// </summary>
public class Mcp9808
{
    public const int DefaultAddress = 0x18;
    public const int ManufacturerIdValue = 0x0054;
    public const int DeviceIdValue = 0x04;

    public const int RegConfig = 0x01;
    public const int RegUpper = 0x02;
    public const int RegLower = 0x03;
    public const int RegCritical = 0x04;
    public const int RegAmbient = 0x05;
    public const int RegManufacturerId = 0x06;
    public const int RegDeviceId = 0x07;
    public const int RegResolution = 0x08;

    /// <summary>Resolution codes: 0.5, 0.25, 0.125, 0.0625 (default) °C; 30/65/130/250 ms.</summary>
    public const int Resolution0_5 = 0;
    public const int Resolution0_25 = 1;
    public const int Resolution0_125 = 2;
    public const int Resolution0_0625 = 3;

    private readonly I2cDevice _dev;

    /// <summary>Device revision (low byte of the device ID register).</summary>
    public int Revision;

    /// <summary>Alert flags of the last read: bit 2 = T ≥ T_CRIT, bit 1 = T &gt; T_UPPER, bit 0 = T &lt; T_LOWER.</summary>
    public int Flags;

    public Mcp9808(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Mcp9808(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Mcp9808(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check manufacturer ID 0x0054 and device ID 0x04. False when no MCP9808 answers.</summary>
    public bool Begin()
    {
        try
        {
            if (_dev.ReadUInt16BigEndian(RegManufacturerId) != ManufacturerIdValue)
            {
                return false;
            }
            int id = _dev.ReadUInt16BigEndian(RegDeviceId);
            Revision = id & 0xFF;
            return (id >> 8) == DeviceIdValue;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Ambient temperature in °C; also updates <see cref="Flags"/>.</summary>
    public double ReadTemperatureCelsius()
    {
        int raw = _dev.ReadUInt16BigEndian(RegAmbient);
        Flags = (raw >> 13) & 0x07;
        return DecodeAmbient(raw);
    }

    /// <summary>T_A: bits 12:0 two's complement in 1/16 °C (flags in 15:13 ignored).</summary>
    public static double DecodeAmbient(int raw) => BitOps.SignExtend(raw & 0x1FFF, 13) / 16.0;

    /// <summary>Encode a limit (T_UPPER/T_LOWER/T_CRIT): 0.25 °C steps in bits 12:2, sign in bit 12.</summary>
    public static int EncodeLimit(double celsius)
    {
        int quarters = (int)Math.Floor(celsius * 4.0 + 0.5);
        return (quarters << 2) & 0x1FFC;
    }

    public void SetResolution(int code) => _dev.WriteRegister(RegResolution, code & 0x03);

    public int ReadResolution() => _dev.ReadRegister(RegResolution) & 0x03;

    /// <summary>Shutdown (SHDN, config bit 8) to ~0.1 µA; wake takes one conversion time.</summary>
    public void SetShutdown(bool shutdown)
    {
        int c = _dev.ReadUInt16BigEndian(RegConfig);
        WriteRegister16(RegConfig, shutdown ? (c | 0x0100) : (c & ~0x0100));
    }

    /// <summary>Set alert limits in °C and enable the alert output (comparator mode, active low).</summary>
    public void SetLimits(double lower, double upper, double critical)
    {
        WriteRegister16(RegLower, EncodeLimit(lower));
        WriteRegister16(RegUpper, EncodeLimit(upper));
        WriteRegister16(RegCritical, EncodeLimit(critical));
        int c = _dev.ReadUInt16BigEndian(RegConfig);
        WriteRegister16(RegConfig, (c & ~0x000F) | 0x0008); // Alert Cnt. enabled
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
