using RustNet.Community.Bh1745;
using RustNet.Community.Bus;

namespace DriversApp;

public static class Bh1745Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x38);
        sim.SetRegister(0x40, 0x0B);
        sim.SetRegister(0x92, 0xE0);
        Bh1745 bh = new Bh1745(sim);
        Check.True("bh1745 begin", bh.Begin());
        Check.True("bh1745 reset", Hex.Wrote(sim, "40 C0"));
        Check.True("bh1745 rgbc enable", Hex.Wrote(sim, "42 10"));
        Check.True("bh1745 mode3", Hex.Wrote(sim, "44 02"));
        sim.SetRegister16Le(0x50, 1000);
        sim.SetRegister16Le(0x52, 2000);
        sim.SetRegister16Le(0x54, 3000);
        sim.SetRegister16Le(0x56, 4000);
        bh.Read();
        Check.Equal("bh1745 red", 1000, bh.Red);
        Check.Equal("bh1745 green", 2000, bh.Green);
        Check.Equal("bh1745 blue", 3000, bh.Blue);
        Check.Equal("bh1745 clear", 4000, bh.Clear);
        bh.Configure(Bh1745.Time320ms, Bh1745.Gain16x);
        Check.True("bh1745 gain 16x", Hex.Wrote(sim, "42 12"));
        Check.Near("bh1745 normalized", 50.0, bh.Normalized(1600), 0.0001); // 1600 * 160 / (16 * 320)

        SimI2cDevice other = new SimI2cDevice(0x38);
        Check.True("bh1745 wrong id", !new Bh1745(other).Begin());
    }
}
