using RustNet.Community.Bus;
using RustNet.Community.Dht12;

namespace DriversApp;

public static class Dht12Smoke
{
    public static void Run()
    {
        // 56.8 %RH, -12.3 C (sign in bit 7 of the temperature decimal byte); checksum 56+8+12+0x83 = 207.
        SimI2cDevice sim = new SimI2cDevice(0x5C);
        sim.SetRegister(0, 56);
        sim.SetRegister(1, 8);
        sim.SetRegister(2, 12);
        sim.SetRegister(3, 0x83);
        sim.SetRegister(4, 207);
        Dht12 dht = new Dht12(sim);
        Check.True("dht12 begin", dht.Begin());
        Check.Near("dht12 humidity", 56.8, dht.HumidityPercent, 0.0001);
        Check.Near("dht12 temperature", -12.3, dht.TemperatureCelsius, 0.0001);
        sim.SetRegister(4, 208);
        bool threw1 = false;
        try
        {
            dht.Measure();
        }
        catch (Exception)
        {
            threw1 = true;
        }
        Check.True("dht12 bad checksum", threw1);
    }
}
