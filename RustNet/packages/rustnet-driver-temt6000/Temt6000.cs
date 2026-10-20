using RustNet.Community.Bus;

namespace RustNet.Community.Temt6000;

/// <summary>
/// Vishay TEMT6000 ambient light phototransistor, read through an ADC
/// channel. The emitter drives a load resistor (10 kΩ on the common
/// breakouts); the datasheet's typical photocurrent is 50 µA at 100 lx
/// (0.5 µA/lx), so lux = V / (R_load × 0.5 µA/lx), e.g. 5 mV per lux at 10 kΩ.
/// The output saturates near the supply voltage (about 660 lx at 3.3 V / 10 kΩ).
/// </summary>
public class Temt6000
{
    private readonly AnalogInput _adc;

    /// <summary>Load resistor between emitter and ground, in Ω.</summary>
    public double LoadOhms = 10000.0;

    /// <summary>Photocurrent per lux in µA (datasheet typical: 50 µA at 100 lx).</summary>
    public double MicroampsPerLux = 0.5;

    /// <summary>Readings averaged per call (the output follows mains flicker under artificial light).</summary>
    public int Samples = 8;

    public Temt6000(int adcChannel) : this(new AnalogInput(adcChannel))
    {
    }

    public Temt6000(AnalogInput adc)
    {
        _adc = adc;
    }

    /// <summary>Averaged emitter voltage in mV.</summary>
    public double ReadMillivolts()
    {
        int n = Samples < 1 ? 1 : Samples;
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += _adc.ReadMillivolts();
        }
        return (double)sum / n;
    }

    /// <summary>Illuminance in lux.</summary>
    public double ReadLux() => MillivoltsToLux(ReadMillivolts());

    public double MillivoltsToLux(double millivolts)
    {
        double microamps = millivolts / 1000.0 / LoadOhms * 1000000.0;
        return microamps / MicroampsPerLux;
    }

    /// <summary>Relative brightness 0..100 % of the ADC full scale.</summary>
    public double ReadPercent() => _adc.ReadRatio() * 100.0;
}
