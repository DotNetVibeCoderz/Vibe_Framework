using RustNet.Community.Bus;

namespace RustNet.Community.Max1704x;

/// <summary>
/// Maxim MAX17043 (1 cell) / MAX17044 (2 cells) ModelGauge fuel gauge at
/// I2C 0x36. VCELL holds a 12-bit reading in bits 15:4: 1.25 mV/LSB on the
/// MAX17043, 2.50 mV/LSB on the MAX17044. SOC is percent in the high byte
/// plus 1/256 % in the low byte. All registers are 16-bit, big-endian.
/// </summary>
public class Max1704x
{
    public const int DefaultAddress = 0x36;

    private const int RegVcell = 0x02;
    private const int RegSoc = 0x04;
    private const int RegMode = 0x06;
    private const int RegVersion = 0x08;
    private const int RegConfig = 0x0C;
    private const int RegCommand = 0xFE;

    private readonly I2cDevice _dev;

    /// <summary>Cells in series: 1 for MAX17043, 2 for MAX17044.</summary>
    public int Cells;

    public Max1704x(int bus, int cells) : this(new I2cDevice(bus, DefaultAddress), cells)
    {
    }

    public Max1704x(I2cDevice device, int cells)
    {
        _dev = device;
        Cells = cells;
    }

    private int Read16(int register) => _dev.ReadUInt16BigEndian(register);

    private void Write16(int register, int value)
    {
        byte[] b = new byte[3];
        b[0] = (byte)register;
        b[1] = (byte)(value >> 8);
        b[2] = (byte)value;
        _dev.Write(b);
    }

    /// <summary>Production version (VERSION register).</summary>
    public int ReadVersion() => Read16(RegVersion);

    /// <summary>Cell (MAX17043) or pack (MAX17044) voltage in V.</summary>
    public double ReadVolts() => (Read16(RegVcell) >> 4) * (Cells == 2 ? 0.0025 : 0.00125);

    /// <summary>State of charge in % (can read slightly above 100 right after a full charge).</summary>
    public double ReadStateOfCharge()
    {
        int s = Read16(RegSoc);
        return (s >> 8) + (s & 0xFF) / 256.0;
    }

    /// <summary>Restart the fuel-gauge calculation as if the cell had just been inserted (MODE = 0x4000).</summary>
    public void QuickStart() => Write16(RegMode, 0x4000);

    /// <summary>Power-on reset (COMMAND = 0x5400).</summary>
    public void Reset() => Write16(RegCommand, 0x5400);

    /// <summary>CONFIG RCOMP (high byte, default 0x97): compensation for the cell chemistry/temperature.</summary>
    public void SetCompensation(int rcomp)
    {
        int c = Read16(RegConfig);
        Write16(RegConfig, ((rcomp & 0xFF) << 8) | (c & 0xFF));
    }

    /// <summary>Alert threshold 1..32 %: ATHD (bits 4:0) = 32 − percent. Clears a pending alert.</summary>
    public void SetAlertThreshold(int percent)
    {
        if (percent < 1)
        {
            percent = 1;
        }
        if (percent > 32)
        {
            percent = 32;
        }
        int c = Read16(RegConfig);
        Write16(RegConfig, (c & 0xFF00) | (c & 0x80) | (32 - percent));
    }

    /// <summary>The alert threshold in % from CONFIG.</summary>
    public int ReadAlertThreshold() => 32 - (Read16(RegConfig) & 0x1F);

    /// <summary>CONFIG.ALRT (bit 5): SOC has fallen below the threshold.</summary>
    public bool IsAlerting() => (Read16(RegConfig) & 0x20) != 0;

    /// <summary>Clear CONFIG.ALRT.</summary>
    public void ClearAlert()
    {
        int c = Read16(RegConfig);
        Write16(RegConfig, c & ~0x20);
    }

    /// <summary>Enter (CONFIG.SLEEP = 1) or leave sleep mode.</summary>
    public void Sleep(bool sleep)
    {
        int c = Read16(RegConfig);
        Write16(RegConfig, sleep ? (c | 0x80) : (c & ~0x80));
    }
}
