using RustNet.Community.Bus;
using RustNet.Community.Temt6000;

namespace DriversApp;

public static class Temt6000Smoke
{
    public static void Run()
    {
        SimAnalogInput adc = new SimAnalogInput();
        adc.Millivolts = 500;
        adc.Raw = 1024;
        Temt6000 t = new Temt6000(adc);
        // Datasheet: 50 uA at 100 lx; 50 uA x 10 kOhm = 500 mV -> 100 lx.
        Check.Near("temt6000 lux", 100.0, t.ReadLux(), 0.0001);
        Check.Near("temt6000 percent", 25.006, t.ReadPercent(), 0.001); // 1024 / 4095
        t.LoadOhms = 1000.0;
        Check.Near("temt6000 1k load", 1000.0, t.ReadLux(), 0.0001);
    }
}
