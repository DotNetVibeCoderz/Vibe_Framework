using RustNet.Community.Bh1750;
using RustNet.Community.Bus;

namespace DriversApp;

public static class Bh1750Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x23);
        Bh1750 bh = new Bh1750(sim);
        Check.True("bh1750 begin", bh.Begin());
        Check.True("bh1750 power on", Hex.Wrote(sim, "01"));
        // Datasheet example: 1000_0011 1001_0000 -> 33680 / 1.2 = 28067 lx.
        sim.Responses.Enqueue(Hex.Bytes("83 90"));
        Check.Near("bh1750 lux", 28066.667, bh.ReadLux(), 0.001);
        Check.True("bh1750 one-time h-res", Hex.Wrote(sim, "20"));
        // MTreg 138 (2x default) halves the lux per count; sent as 0x44 then 0x6A.
        bh.SetMeasurementTime(138);
        Check.True("bh1750 mtreg high", Hex.Wrote(sim, "44"));
        Check.True("bh1750 mtreg low", Hex.Wrote(sim, "6A"));
        sim.Responses.Enqueue(Hex.Bytes("83 90"));
        Check.Near("bh1750 lux mt138", 14033.333, bh.ReadLux(), 0.001);
        Check.Near("bh1750 hres2", 14033.333, Bh1750.ToLux(0x8390, Bh1750.OneTimeHighRes2, 69), 0.001);
        Check.Equal("bh1750 time h-res mt138", 360, bh.MeasurementTimeMs());
    }
}
