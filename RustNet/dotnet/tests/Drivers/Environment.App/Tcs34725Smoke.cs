using RustNet.Community.Bus;
using RustNet.Community.Tcs34725;

namespace DriversApp;

public static class Tcs34725Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x29);
        sim.SetRegister(0x92, 0x44); // ID via command 0x80 | 0x12
        sim.SetRegister(0x93, 0x01); // AVALID
        // Block read through 0x80 | 0x20 | 0x14: C, R, G, B little-endian.
        sim.SetRegister16Le(0xB4, 2800);
        sim.SetRegister16Le(0xB6, 1000);
        sim.SetRegister16Le(0xB8, 1200);
        sim.SetRegister16Le(0xBA, 800);
        Tcs34725 tcs = new Tcs34725(sim);
        Check.True("tcs34725 begin", tcs.Begin());
        Check.True("tcs34725 atime 64 cycles", Hex.Wrote(sim, "81 C0"));
        Check.True("tcs34725 gain 4x", Hex.Wrote(sim, "8F 01"));
        Check.True("tcs34725 enable", Hex.Wrote(sim, "80 03"));
        tcs.Read();
        Check.Equal("tcs34725 clear", 2800, tcs.Clear);
        Check.Equal("tcs34725 red", 1000, tcs.Red);
        Check.Equal("tcs34725 green", 1200, tcs.Green);
        Check.Equal("tcs34725 blue", 800, tcs.Blue);
        // DN40: IR = 100; CPL = 153.6 * 4 / 310; lux = (0.136*900 + 1100 - 0.444*700) / CPL = 459.954;
        // CCT = 3810 * 700 / 900 + 1391 = 4354.33 K.
        Check.Near("tcs34725 ir", 100.0, tcs.InfraredComponent, 0.0001);
        Check.Near("tcs34725 lux", 459.954427, tcs.Lux(), 0.001);
        Check.Near("tcs34725 cct", 4354.3333, tcs.ColorTemperatureKelvin(), 0.001);
        Check.True("tcs34725 not saturated", !tcs.Saturated());

        SimI2cDevice other = new SimI2cDevice(0x29);
        other.SetRegister(0x92, 0x50);
        Check.True("tcs34725 wrong id", !new Tcs34725(other).Begin());
    }
}
