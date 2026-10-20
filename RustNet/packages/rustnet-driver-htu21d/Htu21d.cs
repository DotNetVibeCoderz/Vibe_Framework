using RustNet.Community.Bus;

namespace RustNet.Community.Htu21d;

/// <summary>
/// TE Connectivity HTU21D(F) temperature and humidity sensor over I2C
/// (fixed address 0x40). Uses the "no hold master" commands, so no clock
/// stretching is needed, and checks the CRC-8 (x^8+x^5+x^4+1, init 0x00)
/// the chip appends to every result. The Silicon Labs Si70xx parts speak
/// the same protocol; see <see cref="Si70xx"/>.
/// </summary>
public class Htu21d
{
    public const int DefaultAddress = 0x40;

    public const int CmdTempHold = 0xE3;
    public const int CmdHumidityHold = 0xE5;
    public const int CmdTempNoHold = 0xF3;
    public const int CmdHumidityNoHold = 0xF5;
    public const int CmdWriteUser = 0xE6;
    public const int CmdReadUser = 0xE7;
    public const int CmdSoftReset = 0xFE;

    /// <summary>User register resolution codes (bits 7 and 0): RH 12 / T 14 bit (default).</summary>
    public const int ResolutionRh12T14 = 0x00;
    /// <summary>RH 8 / T 12 bit.</summary>
    public const int ResolutionRh8T12 = 0x01;
    /// <summary>RH 10 / T 13 bit.</summary>
    public const int ResolutionRh10T13 = 0x80;
    /// <summary>RH 11 / T 11 bit.</summary>
    public const int ResolutionRh11T11 = 0x81;

    protected readonly I2cDevice Dev;

    /// <summary>Raw 16-bit temperature word (status bits cleared) of the last reading.</summary>
    public int RawTemperature;

    /// <summary>Raw 16-bit humidity word (status bits cleared) of the last reading.</summary>
    public int RawHumidity;

    public Htu21d(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Htu21d(I2cDevice device)
    {
        Dev = device;
    }

    /// <summary>Soft-reset and read the user register; false when no device answers or it holds a value a reset cannot leave.</summary>
    public virtual bool Begin()
    {
        try
        {
            Dev.WriteByte(CmdSoftReset);
            Timing.SleepMs(15);
            int user = ReadUserRegister();
            // After reset: resolution 12/14 bit, heater off. Bits 5:3 are reserved.
            return (user & 0x81) == 0 && (user & 0x04) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public int ReadUserRegister()
    {
        byte[] c = new byte[1];
        c[0] = CmdReadUser;
        return Dev.WriteRead(c, 1)[0];
    }

    public void WriteUserRegister(int value) => Dev.WriteRegister(CmdWriteUser, value);

    /// <summary>Select measurement resolution (a Resolution* constant); other bits are preserved.</summary>
    public void SetResolution(int resolution)
    {
        int user = ReadUserRegister();
        WriteUserRegister((user & 0x7E) | (resolution & 0x81));
    }

    /// <summary>Enable or disable the on-chip heater (user register bit 2).</summary>
    public void SetHeater(bool on)
    {
        int user = ReadUserRegister();
        WriteUserRegister(on ? (user | 0x04) : (user & ~0x04));
    }

    /// <summary>True when the supply voltage dropped below 2.25 V (user register bit 6).</summary>
    public bool IsEndOfBattery() => (ReadUserRegister() & 0x40) != 0;

    /// <summary>Measure temperature, in °C (datasheet: T = -46.85 + 175.72 · S_T / 2^16).</summary>
    public double ReadTemperatureCelsius()
    {
        RawTemperature = ReadMeasurement(CmdTempNoHold, 50);
        return ConvertTemperature(RawTemperature);
    }

    /// <summary>Measure relative humidity, in % (datasheet: RH = -6 + 125 · S_RH / 2^16), clipped to 0..100.</summary>
    public double ReadHumidityPercent()
    {
        RawHumidity = ReadMeasurement(CmdHumidityNoHold, 16);
        return ConvertHumidity(RawHumidity);
    }

    /// <summary>Temperature-compensated humidity (HTU21D datasheet: RH + (25 - T) · -0.15 %RH/°C).</summary>
    public double CompensatedHumidity(double humidityPercent, double temperatureCelsius) =>
        humidityPercent + (25.0 - temperatureCelsius) * -0.15;

    protected int ReadMeasurement(int command, int waitMs)
    {
        Dev.WriteByte(command);
        Timing.SleepMs(waitMs);
        byte[] r = Dev.Read(3);
        if (Crc(r, 0, 2) != r[2])
        {
            throw new InvalidOperationException("htu21d crc mismatch");
        }
        // The two LSBs are status bits (bit 1 = humidity), not data.
        return ((r[0] << 8) | r[1]) & 0xFFFC;
    }

    public static double ConvertTemperature(int raw) => -46.85 + 175.72 * (raw & 0xFFFC) / 65536.0;

    public static double ConvertHumidity(int raw)
    {
        double rh = -6.0 + 125.0 * (raw & 0xFFFC) / 65536.0;
        if (rh < 0)
        {
            rh = 0;
        }
        if (rh > 100)
        {
            rh = 100;
        }
        return rh;
    }

    /// <summary>CRC-8, polynomial 0x31 (x^8+x^5+x^4+1), init 0x00. Datasheet examples: 0xDC → 0x79, 0x683A → 0x7C, 0x4E85 → 0x6B.</summary>
    public static int Crc(byte[] data, int offset, int length)
    {
        int crc = 0;
        for (int i = 0; i < length; i++)
        {
            crc ^= data[offset + i];
            for (int b = 0; b < 8; b++)
            {
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x31) & 0xFF : (crc << 1) & 0xFF;
            }
        }
        return crc;
    }
}

/// <summary>
/// Silicon Labs Si7006 / Si7013 / Si7020 / Si7021 (Si70xx) humidity and
/// temperature sensor at 0x40. Shares the HTU21D command set and adds the
/// electronic serial number, firmware revision, heater current control and
/// the free temperature read taken during every humidity conversion.
/// </summary>
public class Si70xx : Htu21d
{
    public const int CmdReadTempFromPreviousRh = 0xE0;
    public const int CmdWriteHeater = 0x51;
    public const int CmdReadHeater = 0x11;

    /// <summary>Device ID byte (SNB_3) values.</summary>
    public const int IdSi7006 = 0x06;
    public const int IdSi7013 = 0x0D;
    public const int IdSi7020 = 0x14;
    public const int IdSi7021 = 0x15;

    /// <summary>64-bit electronic serial number (SNA_3..SNA_0, SNB_3..SNB_0).</summary>
    public long SerialNumber;

    /// <summary>Device ID byte (SNB_3): 0x15 = Si7021, 0x14 = Si7020, 0x0D = Si7013, 0x06 = Si7006.</summary>
    public int DeviceId;

    /// <summary>Firmware revision byte: 0xFF = 1.0, 0x20 = 2.0.</summary>
    public int FirmwareRevision;

    public Si70xx(int bus) : base(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Si70xx(I2cDevice device) : base(device)
    {
    }

    /// <summary>Reset, read the electronic ID and firmware revision. False when no Si70xx answers.</summary>
    public override bool Begin()
    {
        try
        {
            Dev.WriteByte(CmdSoftReset);
            Timing.SleepMs(15);
            ReadElectronicId();
            FirmwareRevision = ReadFirmwareRevision();
            return DeviceId == IdSi7006 || DeviceId == IdSi7013 || DeviceId == IdSi7020 || DeviceId == IdSi7021
                || DeviceId == 0x00 || DeviceId == 0xFF;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Datasheet 5.6: two accesses (0xFA 0x0F, then 0xFC 0xC9) return the 8 ID bytes, each group CRC-protected.</summary>
    public long ReadElectronicId()
    {
        byte[] c1 = new byte[2];
        c1[0] = 0xFA;
        c1[1] = 0x0F;
        byte[] a = Dev.WriteRead(c1, 8);
        byte[] c2 = new byte[2];
        c2[0] = 0xFC;
        c2[1] = 0xC9;
        byte[] b = Dev.WriteRead(c2, 6);
        long id = 0;
        for (int i = 0; i < 8; i += 2)
        {
            id = (id << 8) | a[i];
        }
        id = (id << 8) | b[0];
        id = (id << 8) | b[1];
        id = (id << 8) | b[3];
        id = (id << 8) | b[4];
        SerialNumber = id;
        DeviceId = b[0];
        return id;
    }

    public int ReadFirmwareRevision()
    {
        byte[] c = new byte[2];
        c[0] = 0x84;
        c[1] = 0xB8;
        return Dev.WriteRead(c, 1)[0];
    }

    /// <summary>The temperature measured during the last humidity conversion, in °C — no extra conversion needed.</summary>
    public double ReadTemperatureFromPreviousHumidity()
    {
        byte[] c = new byte[1];
        c[0] = CmdReadTempFromPreviousRh;
        byte[] r = Dev.WriteRead(c, 2);
        RawTemperature = (r[0] << 8) | r[1];
        return ConvertTemperature(RawTemperature);
    }

    /// <summary>Heater current level 0..15 (3.09 mA .. 94.20 mA); the heater still has to be enabled with <see cref="Htu21d.SetHeater"/>.</summary>
    public void SetHeaterLevel(int level) => Dev.WriteRegister(CmdWriteHeater, level & 0x0F);

    public int ReadHeaterLevel()
    {
        byte[] c = new byte[1];
        c[0] = CmdReadHeater;
        return Dev.WriteRead(c, 1)[0] & 0x0F;
    }
}
