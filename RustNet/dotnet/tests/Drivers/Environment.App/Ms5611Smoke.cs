using RustNet.Community.Bus;
using RustNet.Community.Ms5611;

namespace DriversApp;

public static class Ms5611Smoke
{
    public static void Run()
    {
        // AN520 example: PROM 0x3132..0x4500 has CRC-4 0xB.
        int[] an = new int[8];
        for (int i = 0; i < 7; i++)
        {
            an[i] = ((0x31 + i * 2) << 8) | (0x32 + i * 2);
        }
        an[4] = 0x3940;
        an[5] = 0x4142;
        an[6] = 0x4344;
        an[7] = 0x4500;
        Check.Equal("ms5611 an520 crc", 0xB, Ms5611.Crc4(an));

        // Datasheet typical example: C1..C6, D1 = 9085466, D2 = 8569150 -> 20.07 C, 1000.09 mbar.
        // Word 0 = 0x0A3C makes the PROM CRC-4 equal 2 (independently computed).
        SimI2cDevice sim = new SimI2cDevice(0x77);
        int[] prom = new int[8];
        prom[0] = 0x0A3C;
        prom[1] = 40127;
        prom[2] = 36924;
        prom[3] = 23317;
        prom[4] = 23282;
        prom[5] = 33464;
        prom[6] = 28312;
        prom[7] = 0x0002;
        for (int i = 0; i < 8; i++)
        {
            sim.SetRegister16(0xA0 + i * 2, prom[i]);
        }
        Ms5611 ms = new Ms5611(sim);
        Check.True("ms5611 begin crc ok", ms.Begin());
        Check.True("ms5611 reset", Hex.Wrote(sim, "1E"));
        sim.Responses.Enqueue(Hex.Bytes("8A A2 1A"));  // D1 = 9085466
        sim.Responses.Enqueue(Hex.Bytes("82 C1 3E"));  // D2 = 8569150
        ms.Measure();
        Check.True("ms5611 convert d1 osr4096", Hex.Wrote(sim, "48"));
        Check.True("ms5611 convert d2 osr4096", Hex.Wrote(sim, "58"));
        Check.Equal("ms5611 temperature", 2007, ms.TemperatureCentiCelsius);
        Check.Equal("ms5611 pressure", 100009, ms.PressurePascal);

        // Second-order compensation (independently computed): D2 = 8000000 -> -0.61 C, 959.89 mbar;
        // D2 = 7000000 -> below -15 C branch, -44.30 C, 856.96 mbar.
        ms.Calculate(9085466, 8000000);
        Check.Equal("ms5611 cold temperature", -61, ms.TemperatureCentiCelsius);
        Check.Equal("ms5611 cold pressure", 95989, ms.PressurePascal);
        ms.Calculate(9085466, 7000000);
        Check.Equal("ms5611 very cold temperature", -4430, ms.TemperatureCentiCelsius);
        Check.Equal("ms5611 very cold pressure", 85696, ms.PressurePascal);

        sim.SetRegister16(0xAE, 0x0003);
        Check.True("ms5611 bad crc", !new Ms5611(sim).Begin());
    }
}
