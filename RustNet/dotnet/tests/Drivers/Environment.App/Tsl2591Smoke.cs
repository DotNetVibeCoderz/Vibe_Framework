using RustNet.Community.Bus;
using RustNet.Community.Tsl2591;

namespace DriversApp;

public static class Tsl2591Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x29);
        sim.SetRegister(0xB2, 0x50); // ID via command 0xA0 | 0x12
        sim.SetRegister(0xB3, 0x01); // AVALID
        sim.SetRegister16Le(0xB4, 1000);
        sim.SetRegister16Le(0xB6, 200);
        Tsl2591 tsl = new Tsl2591(sim);
        Check.True("tsl2591 begin", tsl.Begin());
        Check.True("tsl2591 control medium/100ms", Hex.Wrote(sim, "A1 10"));
        Check.True("tsl2591 enable", Hex.Wrote(sim, "A0 03"));
        tsl.Read();
        Check.Equal("tsl2591 ch0", 1000, tsl.FullSpectrum);
        Check.Equal("tsl2591 ch1", 200, tsl.Infrared);
        // (1000-200)*(1-0.2) / (100*25/408) = 104.448 lx.
        Check.Near("tsl2591 lux", 104.448, tsl.Lux(), 0.001);
        Check.Near("tsl2591 saturated", -1.0, Tsl2591.CalculateLux(0xFFFF, 10, 100, 25), 0.0001);
    }
}
