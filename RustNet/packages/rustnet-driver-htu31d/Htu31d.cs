using RustNet.Community.Bus;

namespace RustNet.Community.Htu31d;

/// <summary>
/// TE Connectivity HTU31D temperature and humidity sensor over I2C.
/// Address 0x40 (ADDR low) or 0x41 (ADDR high). Every result word carries a
/// CRC-8 (x^8+x^5+x^4+1, init 0x00) which is checked.
/// </summary>
public class Htu31d
{
    public const int DefaultAddress = 0x40;
    public const int AlternateAddress = 0x41;

    public const int CmdConversion = 0x40;
    public const int CmdReadTempHumidity = 0x00;
    public const int CmdReadHumidity = 0x10;
    public const int CmdReset = 0x1E;
    public const int CmdHeaterOn = 0x04;
    public const int CmdHeaterOff = 0x02;
    public const int CmdReadSerial = 0x0A;
    public const int CmdReadDiagnostic = 0x08;

    private readonly I2cDevice _dev;

    /// <summary>Humidity oversampling 0..3 (0 = 0.020 %RH, 3 = 0.007 %RH resolution).</summary>
    public int HumidityOversampling = 3;

    /// <summary>Temperature oversampling 0..3 (0 = 0.040 °C, 3 = 0.012 °C resolution).</summary>
    public int TemperatureOversampling = 3;

    /// <summary>24-bit serial number read by <see cref="Begin"/>.</summary>
    public int SerialNumber;

    public int RawTemperature;
    public int RawHumidity;

    /// <summary>Temperature in °C from the last measurement.</summary>
    public double TemperatureCelsius;

    /// <summary>Relative humidity in % from the last measurement.</summary>
    public double HumidityPercent;

    public Htu31d(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Htu31d(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Htu31d(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>Reset and read the serial number; false if nothing CRC-valid answers.</summary>
    public bool Begin()
    {
        try
        {
            _dev.WriteByte(CmdReset);
            Timing.SleepMs(15);
            SerialNumber = ReadSerial();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Read the 24-bit serial number (3 bytes + CRC over all three).</summary>
    public int ReadSerial()
    {
        byte[] c = new byte[1];
        c[0] = CmdReadSerial;
        byte[] r = _dev.WriteRead(c, 4);
        if (Crc(r, 0, 3) != r[3])
        {
            throw new InvalidOperationException("htu31d crc mismatch");
        }
        return (r[0] << 16) | (r[1] << 8) | r[2];
    }

    /// <summary>Read the diagnostic byte (bit 0 heater on, bits 7..1 NVM/range errors).</summary>
    public int ReadDiagnostic()
    {
        byte[] c = new byte[1];
        c[0] = CmdReadDiagnostic;
        byte[] r = _dev.WriteRead(c, 2);
        if (Crc(r, 0, 1) != r[1])
        {
            throw new InvalidOperationException("htu31d crc mismatch");
        }
        return r[0];
    }

    public void SetHeater(bool on) => _dev.WriteByte(on ? CmdHeaterOn : CmdHeaterOff);

    /// <summary>Start a conversion with the configured oversampling, wait for it, and read T and RH.</summary>
    public void Measure()
    {
        _dev.WriteByte(CmdConversion | ((HumidityOversampling & 3) << 3) | ((TemperatureOversampling & 3) << 1));
        // Datasheet worst case at OSR 3/3: 7.8 ms (RH) + 12.1 ms (T).
        Timing.SleepMs(20);
        byte[] c = new byte[1];
        c[0] = CmdReadTempHumidity;
        byte[] r = _dev.WriteRead(c, 6);
        RawTemperature = Word(r, 0);
        RawHumidity = Word(r, 3);
        TemperatureCelsius = ConvertTemperature(RawTemperature);
        HumidityPercent = ConvertHumidity(RawHumidity);
    }

    /// <summary>Datasheet: T = -40 + 165 · S_T / (2^16 - 1), in °C.</summary>
    public static double ConvertTemperature(int raw) => -40.0 + 165.0 * raw / 65535.0;

    /// <summary>Datasheet: RH = 100 · S_RH / (2^16 - 1), in %.</summary>
    public static double ConvertHumidity(int raw) => 100.0 * raw / 65535.0;

    private static int Word(byte[] r, int offset)
    {
        if (Crc(r, offset, 2) != r[offset + 2])
        {
            throw new InvalidOperationException("htu31d crc mismatch");
        }
        return (r[offset] << 8) | r[offset + 1];
    }

    /// <summary>CRC-8, polynomial 0x31, init 0x00, MSB first.</summary>
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
