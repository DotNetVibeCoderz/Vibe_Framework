using RustNet.Community.Bus;

namespace RustNet.Community.Thermistor;

/// <summary>
/// NTC thermistor read through an ADC voltage divider with a fixed series
/// resistor. By default the NTC sits on the low side (series resistor to the
/// ADC reference, NTC to ground), so R_ntc = R_series · raw / (max − raw).
/// Temperature uses the Beta model (1/T = 1/T0 + ln(R/R0)/B) unless
/// Steinhart-Hart coefficients are set (1/T = A + B·ln R + C·(ln R)^3).
/// The divider must be powered from the ADC reference for the ratio to hold.
/// </summary>
public class Thermistor
{
    public const double KelvinOffset = 273.15;

    private readonly AnalogInput _adc;

    /// <summary>Series (fixed) resistor in Ω.</summary>
    public double SeriesOhms = 10000.0;

    /// <summary>NTC resistance at <see cref="NominalCelsius"/>, in Ω.</summary>
    public double NominalOhms = 10000.0;

    /// <summary>Reference temperature for <see cref="NominalOhms"/> (usually 25 °C).</summary>
    public double NominalCelsius = 25.0;

    /// <summary>Beta coefficient in K (3950 for the common 10 kΩ probes; check the datasheet's B25/50 or B25/85).</summary>
    public double Beta = 3950.0;

    /// <summary>True when the NTC is between the reference and the ADC pin (series resistor to ground).</summary>
    public bool NtcOnHighSide;

    /// <summary>Readings averaged per call.</summary>
    public int Samples = 4;

    private bool _steinhart;
    private double _a, _b, _c;

    public Thermistor(int adcChannel) : this(new AnalogInput(adcChannel))
    {
    }

    public Thermistor(AnalogInput adc)
    {
        _adc = adc;
    }

    /// <summary>Use the Steinhart-Hart equation with coefficients A, B, C (for R in Ω, T in K).</summary>
    public void UseSteinhartHart(double a, double b, double c)
    {
        _a = a;
        _b = b;
        _c = c;
        _steinhart = true;
    }

    /// <summary>Back to the Beta model.</summary>
    public void UseBeta(double beta)
    {
        Beta = beta;
        _steinhart = false;
    }

    /// <summary>Averaged raw ADC reading.</summary>
    public double ReadRawAverage()
    {
        int n = Samples < 1 ? 1 : Samples;
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += _adc.ReadRaw();
        }
        return (double)sum / n;
    }

    /// <summary>NTC resistance in Ω from a raw reading; +∞/0 at the rails.</summary>
    public double ResistanceFromRaw(double raw)
    {
        double max = _adc.MaxRaw;
        if (NtcOnHighSide)
        {
            return raw <= 0 ? double.PositiveInfinity : SeriesOhms * (max - raw) / raw;
        }
        return raw >= max ? double.PositiveInfinity : SeriesOhms * raw / (max - raw);
    }

    /// <summary>Temperature in °C for a resistance in Ω.</summary>
    public double CelsiusFromResistance(double ohms)
    {
        double lnR = Math.Log(ohms);
        double inverseT;
        if (_steinhart)
        {
            inverseT = _a + _b * lnR + _c * lnR * lnR * lnR;
        }
        else
        {
            inverseT = 1.0 / (NominalCelsius + KelvinOffset) + Math.Log(ohms / NominalOhms) / Beta;
        }
        return 1.0 / inverseT - KelvinOffset;
    }

    public double ReadResistance() => ResistanceFromRaw(ReadRawAverage());

    /// <summary>Temperature in °C.</summary>
    public double ReadCelsius() => CelsiusFromResistance(ReadResistance());
}
