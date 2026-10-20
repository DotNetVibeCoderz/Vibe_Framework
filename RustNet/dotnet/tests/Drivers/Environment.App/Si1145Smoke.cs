using RustNet.Community.Bus;
using RustNet.Community.Si1145;

namespace DriversApp;

public static class Si1145Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x60);
        sim.SetRegister(0x00, 0x45);
        Si1145 si = new Si1145(sim);
        Check.True("si1145 begin", si.Begin());
        Check.True("si1145 reset cmd", Hex.Wrote(sim, "18 01"));
        Check.True("si1145 hw key", Hex.Wrote(sim, "07 17"));
        Check.True("si1145 ucoef", Hex.Wrote(sim, "13 7B 6B 01 00"));
        Check.True("si1145 chlist value", Hex.Wrote(sim, "17 B1"));
        Check.True("si1145 chlist set", Hex.Wrote(sim, "18 A1"));
        Check.True("si1145 auto", Hex.Wrote(sim, "18 0F"));
        sim.SetRegister16Le(0x22, 258);
        sim.SetRegister16Le(0x24, 772);
        sim.SetRegister16Le(0x26, 5);
        sim.SetRegister16Le(0x2C, 165);
        si.Read();
        Check.Equal("si1145 visible", 258, si.Visible);
        Check.Equal("si1145 ir", 772, si.Infrared);
        Check.Equal("si1145 ps1", 5, si.Proximity);
        Check.Near("si1145 uv index", 1.65, si.UvIndex, 0.0001);

        SimI2cDevice other = new SimI2cDevice(0x60);
        other.SetRegister(0x00, 0xC4);
        Check.True("si1145 wrong id", !new Si1145(other).Begin());
    }
}
