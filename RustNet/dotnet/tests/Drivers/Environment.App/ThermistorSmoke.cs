using RustNet.Community.Bus;
using RustNet.Community.Thermistor;

namespace DriversApp;

public static class ThermistorSmoke
{
    public static void Run()
    {
        SimAnalogInput adc = new SimAnalogInput();
        adc.Raw = 2048;
        Thermistor ntc = new Thermistor(adc);
        // 10 k series to Vref, 10 k NTC (B 3950) to ground: R = 10000 * 2048 / 2047 = 10004.885 Ohm.
        Check.Near("thermistor resistance", 10004.885, ntc.ReadResistance(), 0.001);
        // Beta: 1/T = 1/298.15 + ln(R/R0)/3950 -> 24.98901 C (independently computed).
        Check.Near("thermistor beta", 24.989009, ntc.ReadCelsius(), 0.0001);
        Check.Near("thermistor nominal", 25.0, ntc.CelsiusFromResistance(10000.0), 0.000001);

        // Steinhart-Hart, the widely published 10 k coefficients.
        ntc.UseSteinhartHart(1.009249522e-3, 2.378405444e-4, 2.019202697e-7);
        Check.Near("thermistor sh 10k", 24.681293, ntc.CelsiusFromResistance(10000.0), 0.0001);
        Check.Near("thermistor sh 3k", 58.292061, ntc.CelsiusFromResistance(3000.0), 0.0001);
        Check.Near("thermistor sh reading", 24.668763, ntc.ReadCelsius(), 0.0001);

        // NTC on the high side: R = 10000 * (4095 - 1000) / 1000 = 30950 Ohm -> 1.5724 C with Beta 3950.
        ntc.UseBeta(3950.0);
        ntc.NtcOnHighSide = true;
        adc.Raw = 1000;
        Check.Near("thermistor high side R", 30950.0, ntc.ReadResistance(), 0.001);
        Check.Near("thermistor high side T", 1.572355, ntc.ReadCelsius(), 0.0001);
    }
}
