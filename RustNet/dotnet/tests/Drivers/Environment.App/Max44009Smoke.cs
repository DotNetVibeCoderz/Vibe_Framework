using RustNet.Community.Bus;
using RustNet.Community.Max44009;

namespace DriversApp;

public static class Max44009Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x4A);
        sim.SetRegister(0x02, 0x03);
        Max44009 max = new Max44009(sim);
        Check.True("max44009 begin", max.Begin());
        // Exponent 5, mantissa 0xA7 = 167: 32 * 167 * 0.045 = 240.48 lx.
        sim.SetRegister(0x03, 0x5A);
        sim.SetRegister(0x04, 0x07);
        Check.Near("max44009 lux", 240.48, max.ReadLux(), 0.0001);
        Check.Near("max44009 min", 0.045, Max44009.ToLux(0x00, 0x01), 0.00001);
        Check.Near("max44009 max", 188006.4, Max44009.ToLux(0xEF, 0x0F), 0.001);
        Check.Near("max44009 overrange", -1.0, Max44009.ToLux(0xF0, 0x00), 0.0001);
        max.Configure(true, false, false, 0);
        Check.True("max44009 continuous", Hex.Wrote(sim, "02 80"));
    }
}
