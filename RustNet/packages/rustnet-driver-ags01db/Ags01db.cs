using RustNet.Community.Bus;

namespace RustNet.Community.Ags01db;

/// <summary>
/// Aosong AGS01DB MEMS VOC gas sensor at I2C address 0x11 (max 100 kHz).
/// Readings are 16-bit values in 0.1 ppm followed by a CRC-8 (poly 0x31,
/// init 0xFF). Needs about 120 s of preheat after power-up, and must not be
/// read more often than every 2 s.
/// </summary>
public class Ags01db
{
    public const int DefaultAddress = 0x11;

    private readonly I2cDevice _dev;

    /// <summary>Firmware version byte read by <see cref="Begin"/>.</summary>
    public int Version;

    /// <summary>Raw VOC reading in 0.1 ppm from the last read.</summary>
    public int RawVoc;

    public Ags01db(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Ags01db(I2cDevice device)
    {
        _dev = device;
    }

    /// <summary>VOC concentration in ppm (ethanol equivalent) from the last read.</summary>
    public double VocPpm => RawVoc / 10.0;

    /// <summary>Read the firmware version (command 0x0A 0x01); false when nothing CRC-valid answers.</summary>
    public bool Begin()
    {
        try
        {
            byte[] c = new byte[2];
            c[0] = 0x0A;
            c[1] = 0x01;
            _dev.Write(c);
            Timing.SleepMs(30);
            byte[] r = _dev.Read(2);
            if (BitOps.Crc8Sensirion(r, 0, 1) != r[1])
            {
                return false;
            }
            Version = r[0];
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Read the VOC value (command 0x00 0x02); returns ppm and throws on a CRC mismatch.</summary>
    public double ReadVocPpm()
    {
        byte[] c = new byte[2];
        c[0] = 0x00;
        c[1] = 0x02;
        _dev.Write(c);
        Timing.SleepMs(30);
        byte[] r = _dev.Read(3);
        if (BitOps.Crc8Sensirion(r, 0, 2) != r[2])
        {
            throw new InvalidOperationException("ags01db crc mismatch");
        }
        RawVoc = (r[0] << 8) | r[1];
        return VocPpm;
    }
}
