using RustNet.Community.Bus;

namespace RustNet.Community.Bh1745;

/// <summary>
/// ROHM BH1745NUC digital colour sensor (red, green, blue, clear) over I2C.
/// Address 0x38 (ADDR low) or 0x39 (ADDR high). Channel data is 16-bit
/// little-endian. The datasheet gives no lux formula; counts are raw and
/// <see cref="Normalized"/> scales them to 1x gain and 160 ms.
/// </summary>
public class Bh1745
{
    public const int DefaultAddress = 0x38;
    public const int AlternateAddress = 0x39;
    public const int PartIdValue = 0x0B;
    public const int ManufacturerIdValue = 0xE0;

    public const int RegSystemControl = 0x40;
    public const int RegModeControl1 = 0x41;
    public const int RegModeControl2 = 0x42;
    public const int RegModeControl3 = 0x44;
    public const int RegRedData = 0x50;
    public const int RegManufacturerId = 0x92;

    /// <summary>Measurement time codes (MODE_CONTROL1 bits 2:0).</summary>
    public const int Time160ms = 0;
    public const int Time320ms = 1;
    public const int Time640ms = 2;
    public const int Time1280ms = 3;
    public const int Time2560ms = 4;
    public const int Time5120ms = 5;

    /// <summary>Gain codes (MODE_CONTROL2 bits 1:0).</summary>
    public const int Gain1x = 0;
    public const int Gain2x = 1;
    public const int Gain16x = 2;

    private readonly I2cDevice _dev;

    public int MeasurementTime = Time160ms;
    public int Gain = Gain1x;

    /// <summary>Raw counts of the last read.</summary>
    public int Red, Green, Blue, Clear;

    public Bh1745(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Bh1745(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Bh1745(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Check part ID 0x0B and manufacturer ID 0xE0, reset, and start RGBC measurement.</summary>
    public bool Begin()
    {
        try
        {
            if ((_dev.ReadRegister(RegSystemControl) & 0x3F) != PartIdValue
                || _dev.ReadRegister(RegManufacturerId) != ManufacturerIdValue)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
        _dev.WriteRegister(RegSystemControl, 0xC0); // SW reset + INT reset
        Timing.SleepMs(2);
        Configure(MeasurementTime, Gain);
        _dev.WriteRegister(RegModeControl3, 0x02); // datasheet: write 0x02
        return true;
    }

    /// <summary>Set measurement time and gain codes and enable RGBC measurement.</summary>
    public void Configure(int measurementTime, int gain)
    {
        MeasurementTime = measurementTime;
        Gain = gain;
        _dev.WriteRegister(RegModeControl1, measurementTime & 0x07);
        _dev.WriteRegister(RegModeControl2, 0x10 | (gain & 0x03)); // RGBC_EN
    }

    /// <summary>True when MODE_CONTROL2.VALID reports a fresh result.</summary>
    public bool DataValid() => (_dev.ReadRegister(RegModeControl2) & 0x80) != 0;

    /// <summary>Read all four channels in one 8-byte transfer.</summary>
    public void Read()
    {
        byte[] d = _dev.ReadRegisters(RegRedData, 8);
        Red = d[0] | (d[1] << 8);
        Green = d[2] | (d[3] << 8);
        Blue = d[4] | (d[5] << 8);
        Clear = d[6] | (d[7] << 8);
    }

    /// <summary>Gain as a factor (1, 2 or 16).</summary>
    public int GainFactor => Gain == Gain16x ? 16 : (Gain == Gain2x ? 2 : 1);

    /// <summary>Measurement time in ms (160 · 2^code).</summary>
    public int MeasurementTimeMs => 160 << MeasurementTime;

    /// <summary>A raw count scaled to 1x gain and 160 ms.</summary>
    public double Normalized(int count) => count * 160.0 / (GainFactor * MeasurementTimeMs);
}
