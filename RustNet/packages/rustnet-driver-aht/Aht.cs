using RustNet.Community.Bus;

namespace RustNet.Community.Aht;

/// <summary>
/// Aosong AHT20 / AHT21 temperature and humidity sensor at I2C address 0x38.
/// A measurement returns a status byte, 20-bit humidity, 20-bit temperature
/// and a CRC-8 (poly 0x31, init 0xFF) over the six data bytes.
/// <see cref="Aht10"/> and <see cref="Dht10"/> reuse the same protocol.
/// </summary>
public class Aht20
{
    public const int DefaultAddress = 0x38;

    public const int CmdStatus = 0x71;
    public const int CmdTrigger = 0xAC;
    public const int CmdSoftReset = 0xBA;

    /// <summary>Status bit 7: a measurement is in progress.</summary>
    public const int StatusBusy = 0x80;
    /// <summary>Status bit 3: calibration coefficients are loaded.</summary>
    public const int StatusCalibrated = 0x08;

    protected readonly I2cDevice Dev;

    /// <summary>Status byte of the last measurement.</summary>
    public int Status;

    /// <summary>Raw 20-bit humidity of the last measurement.</summary>
    public int RawHumidity;

    /// <summary>Raw 20-bit temperature of the last measurement.</summary>
    public int RawTemperature;

    /// <summary>Relative humidity in % from the last measurement (RH = S_RH / 2^20 · 100).</summary>
    public double HumidityPercent;

    /// <summary>Temperature in °C from the last measurement (T = S_T / 2^20 · 200 - 50).</summary>
    public double TemperatureCelsius;

    public Aht20(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Aht20(I2cDevice device)
    {
        Dev = device;
    }

    /// <summary>Initialisation command: 0xBE 0x08 0x00 on AHT20.</summary>
    protected virtual int InitCommand => 0xBE;

    /// <summary>True when the reply carries a trailing CRC byte (AHT20/21).</summary>
    protected virtual bool HasCrc => true;

    /// <summary>Wait for power-up, check the calibration bit and send the init command if it is clear.</summary>
    public bool Begin()
    {
        try
        {
            Timing.SleepMs(40);
            if ((ReadStatus() & StatusCalibrated) == 0)
            {
                byte[] init = new byte[3];
                init[0] = (byte)InitCommand;
                init[1] = 0x08;
                init[2] = 0x00;
                Dev.Write(init);
                Timing.SleepMs(10);
            }
            return (ReadStatus() & StatusCalibrated) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public int ReadStatus()
    {
        byte[] c = new byte[1];
        c[0] = CmdStatus;
        return Dev.WriteRead(c, 1)[0];
    }

    public void SoftReset()
    {
        Dev.WriteByte(CmdSoftReset);
        Timing.SleepMs(20);
    }

    /// <summary>Trigger a measurement (0xAC 0x33 0x00), wait ~80 ms until not busy, and decode it.</summary>
    public void Measure()
    {
        byte[] t = new byte[3];
        t[0] = CmdTrigger;
        t[1] = 0x33;
        t[2] = 0x00;
        Dev.Write(t);
        Timing.SleepMs(80);
        int n = HasCrc ? 7 : 6;
        byte[] r = Dev.Read(n);
        for (int i = 0; i < 10 && (r[0] & StatusBusy) != 0; i++)
        {
            Timing.SleepMs(10);
            r = Dev.Read(n);
        }
        if ((r[0] & StatusBusy) != 0)
        {
            throw new InvalidOperationException("aht busy");
        }
        if (HasCrc && BitOps.Crc8Sensirion(r, 0, 6) != r[6])
        {
            throw new InvalidOperationException("aht crc mismatch");
        }
        Status = r[0];
        RawHumidity = (r[1] << 12) | (r[2] << 4) | (r[3] >> 4);
        RawTemperature = ((r[3] & 0x0F) << 16) | (r[4] << 8) | r[5];
        HumidityPercent = RawHumidity * 100.0 / 1048576.0;
        TemperatureCelsius = RawTemperature * 200.0 / 1048576.0 - 50.0;
    }
}

/// <summary>
/// Aosong AHT10 at 0x38 (0x39 with ADDR high). Same measurement as the
/// AHT20, but initialised with 0xE1 and without the trailing CRC byte.
/// </summary>
public class Aht10 : Aht20
{
    public const int AlternateAddress = 0x39;

    public Aht10(int bus) : base(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Aht10(int bus, int address) : base(new I2cDevice(bus, address))
    {
    }

    public Aht10(I2cDevice device) : base(device)
    {
    }

    protected override int InitCommand => 0xE1;

    protected override bool HasCrc => false;
}

/// <summary>Aosong DHT10: an AHT10 in a DHT-style housing, at address 0x38.</summary>
public class Dht10 : Aht10
{
    public Dht10(int bus) : base(bus)
    {
    }

    public Dht10(I2cDevice device) : base(device)
    {
    }
}
