using RustNet.Community.Bus;
using RustNet.Community.Mpl3115a2;

namespace DriversApp;

public static class Mpl3115a2Smoke
{
    public static void Run()
    {
        SimI2cDevice sim = new SimI2cDevice(0x60);
        sim.SetRegister(0x0C, 0xC4);
        sim.SetRegister(0x00, 0x0E); // STATUS: PTDR | PDR | TDR
        // 101325 Pa = 405300 in Q18.2, left-justified: 0x62F340; 25.5 C = 408 in Q8.4 -> 0x1980.
        sim.SetRegister(0x01, 0x62);
        sim.SetRegister(0x02, 0xF3);
        sim.SetRegister(0x03, 0x40);
        sim.SetRegister(0x04, 0x19);
        sim.SetRegister(0x05, 0x80);
        Mpl3115a2 mpl = new Mpl3115a2(sim);
        Check.True("mpl3115a2 begin", mpl.Begin());
        Check.True("mpl3115a2 data flags", Hex.Wrote(sim, "13 07"));
        mpl.MeasurePressure();
        Check.True("mpl3115a2 one-shot baro os128", Hex.Wrote(sim, "26 3A"));
        Check.Near("mpl3115a2 pressure", 101325.0, mpl.PressurePascal, 0.001);
        Check.Near("mpl3115a2 temperature", 25.5, mpl.TemperatureCelsius, 0.001);

        // Altimeter: -12.5 m = -200 in Q16.4 -> 20-bit 0xFFF38, left-justified 0xFFF380.
        sim.SetRegister(0x01, 0xFF);
        sim.SetRegister(0x02, 0xF3);
        sim.SetRegister(0x03, 0x80);
        // -10.25 C = -164 in Q8.4 -> 0xF5C0.
        sim.SetRegister(0x04, 0xF5);
        sim.SetRegister(0x05, 0xC0);
        mpl.MeasureAltitude();
        Check.True("mpl3115a2 one-shot alt", Hex.Wrote(sim, "26 BA"));
        Check.Near("mpl3115a2 altitude", -12.5, mpl.AltitudeMeters, 0.001);
        Check.Near("mpl3115a2 negative temperature", -10.25, mpl.TemperatureCelsius, 0.001);
        Check.Near("mpl3115a2 pressure lsb", 0.25, Mpl3115a2.DecodePressure(0x000010), 0.0001);

        // BAR_IN reset value 0xC5E7 corresponds to 101326 Pa (2 Pa per LSB).
        mpl.SetSeaLevelPressure(101326.0);
        Check.True("mpl3115a2 bar_in", Hex.Wrote(sim, "14 C5 E7"));

        SimI2cDevice other = new SimI2cDevice(0x60);
        Check.True("mpl3115a2 wrong id", !new Mpl3115a2(other).Begin());
    }
}
