using RustNet.Community.Bus;

namespace RustNet.Community.Ms5611;

/// <summary>
/// TE (Measurement Specialties) MS5611-01BA barometric pressure sensor over
/// I2C. Address 0x77 (CSB low) or 0x76 (CSB high). Reads the factory PROM,
/// verifies its CRC-4 (AN520), and applies the datasheet's first- and
/// second-order temperature compensation with 64-bit integers.
/// </summary>
public class Ms5611
{
    public const int DefaultAddress = 0x77;
    public const int AlternateAddress = 0x76;

    /// <summary>Oversampling ratio codes 0..4 = OSR 256, 512, 1024, 2048, 4096.</summary>
    public const int Osr256 = 0;
    public const int Osr512 = 1;
    public const int Osr1024 = 2;
    public const int Osr2048 = 3;
    public const int Osr4096 = 4;

    public const int CmdReset = 0x1E;
    public const int CmdConvertD1 = 0x40;
    public const int CmdConvertD2 = 0x50;
    public const int CmdAdcRead = 0x00;
    public const int CmdPromRead = 0xA0;

    private readonly I2cDevice _dev;

    /// <summary>PROM words 0..7: word 0 factory data, C1..C6 calibration, word 7 serial + CRC.</summary>
    public int[] Prom = new int[8];

    /// <summary>Oversampling code 0..4 used for both conversions.</summary>
    public int Oversampling = Osr4096;

    public int D1;
    public int D2;

    /// <summary>Last temperature in 0.01 °C.</summary>
    public int TemperatureCentiCelsius;

    /// <summary>Last pressure in 0.01 mbar (= Pa).</summary>
    public int PressurePascal;

    public Ms5611(int bus) : this(new I2cDevice(bus, DefaultAddress))
    {
    }

    public Ms5611(int bus, int address) : this(new I2cDevice(bus, address))
    {
    }

    public Ms5611(I2cDevice device)
    {
        _dev = device;
    }

    public double TemperatureCelsius => TemperatureCentiCelsius / 100.0;

    public double PressureMillibar => PressurePascal / 100.0;

    /// <summary>Reset, read the PROM and check its CRC-4. False when it does not match (or nothing answers).</summary>
    public bool Begin()
    {
        try
        {
            _dev.WriteByte(CmdReset);
            Timing.SleepMs(3);
            for (int i = 0; i < 8; i++)
            {
                byte[] c = new byte[1];
                c[0] = (byte)(CmdPromRead + i * 2);
                byte[] w = _dev.WriteRead(c, 2);
                Prom[i] = (w[0] << 8) | w[1];
            }
            return Crc4(Prom) == (Prom[7] & 0x0F);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>AN520 CRC-4 over the 8 PROM words (with the CRC nibble zeroed).</summary>
    public static int Crc4(int[] prom)
    {
        int rem = 0;
        for (int cnt = 0; cnt < 16; cnt++)
        {
            int word = prom[cnt >> 1];
            if (cnt == 14 || cnt == 15)
            {
                word = word & 0xFF00;
            }
            rem ^= (cnt & 1) == 1 ? (word & 0xFF) : (word >> 8);
            for (int b = 8; b > 0; b--)
            {
                rem = (rem & 0x8000) != 0 ? ((rem << 1) ^ 0x3000) : (rem << 1);
                rem &= 0xFFFF;
            }
        }
        return (rem >> 12) & 0x0F;
    }

    private int Convert(int command)
    {
        _dev.WriteByte(command + Oversampling * 2);
        // Max conversion times: 0.60 / 1.17 / 2.28 / 4.54 / 9.04 ms.
        Timing.SleepMs(Oversampling == 4 ? 10 : (Oversampling == 3 ? 5 : (Oversampling == 2 ? 3 : (Oversampling == 1 ? 2 : 1))));
        byte[] c = new byte[1];
        c[0] = CmdAdcRead;
        byte[] r = _dev.WriteRead(c, 3);
        return (r[0] << 16) | (r[1] << 8) | r[2];
    }

    /// <summary>Convert D1 (pressure) then D2 (temperature) and compensate.</summary>
    public void Measure()
    {
        D1 = Convert(CmdConvertD1);
        D2 = Convert(CmdConvertD2);
        Calculate(D1, D2);
    }

    /// <summary>Datasheet "pressure and temperature calculation" including second-order compensation below 20 °C.</summary>
    public void Calculate(int d1, int d2)
    {
        long dT = d2 - ((long)Prom[5] << 8);
        long temp = 2000 + dT * Prom[6] / 8388608;
        long off = ((long)Prom[2] << 16) + (Prom[4] * dT) / 128;
        long sens = ((long)Prom[1] << 15) + (Prom[3] * dT) / 256;
        if (temp < 2000)
        {
            long t2 = (dT * dT) >> 31; // dT² / 2^31, never negative
            long d = temp - 2000;
            long off2 = 5 * d * d / 2;
            long sens2 = 5 * d * d / 4;
            if (temp < -1500)
            {
                long e = temp + 1500;
                off2 += 7 * e * e;
                sens2 += 11 * e * e / 2;
            }
            temp -= t2;
            off -= off2;
            sens -= sens2;
        }
        TemperatureCentiCelsius = (int)temp;
        PressurePascal = (int)(((d1 * sens) / 2097152 - off) / 32768);
    }
}
