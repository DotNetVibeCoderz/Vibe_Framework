using RustNet.Community.Bus;

namespace RustNet.Community.Ky038;

/// <summary>
/// KY-038 (and the more sensitive KY-037) electret microphone module with an
/// LM393 comparator. AO carries the audio waveform around a DC bias, so the
/// sound level is the spread of a burst of samples (peak-to-peak or RMS
/// about the mean), not one reading. DO switches when the level crosses the
/// threshold set by the potentiometer (HIGH on most boards).
/// It is not a calibrated SPL meter; levels are relative ADC counts.
/// </summary>
public class Ky038
{
    private readonly AnalogInput? _analog;
    private readonly DigitalInput? _digital;

    /// <summary>Samples taken per level measurement.</summary>
    public int Samples = 64;

    /// <summary>True when DO reads HIGH for "loud" (set false for boards that invert it).</summary>
    public bool DigitalActiveHigh = true;

    /// <summary>Results of the last <see cref="Measure"/>, in ADC counts.</summary>
    public int Minimum, Maximum;
    public double Mean, Rms;

    public Ky038(int adcChannel, int digitalPin)
        : this(adcChannel >= 0 ? new AnalogInput(adcChannel) : null,
               digitalPin >= 0 ? new DigitalInput(digitalPin, RustNet.Hal.PinMode.Input) : null)
    {
    }

    public Ky038(AnalogInput? analog, DigitalInput? digital)
    {
        _analog = analog;
        _digital = digital;
    }

    /// <summary>Peak-to-peak amplitude of the last measurement in counts.</summary>
    public int PeakToPeak => Maximum - Minimum;

    /// <summary>Take <see cref="Samples"/> readings back to back and compute min/max/mean/RMS (AC part).</summary>
    public void Measure()
    {
        if (_analog == null)
        {
            throw new InvalidOperationException("ky038 analog output not wired");
        }
        int n = Samples < 2 ? 2 : Samples;
        int[] v = new int[n];
        long sum = 0;
        int min = int.MaxValue;
        int max = int.MinValue;
        for (int i = 0; i < n; i++)
        {
            int s = _analog.ReadRaw();
            v[i] = s;
            sum += s;
            if (s < min)
            {
                min = s;
            }
            if (s > max)
            {
                max = s;
            }
        }
        Minimum = min;
        Maximum = max;
        Mean = (double)sum / n;
        double acc = 0;
        for (int i = 0; i < n; i++)
        {
            double d = v[i] - Mean;
            acc += d * d;
        }
        Rms = Math.Sqrt(acc / n);
    }

    /// <summary>Peak-to-peak level as a fraction 0..1 of the ADC full scale.</summary>
    public double LevelRatio() => _analog == null ? 0.0 : (double)PeakToPeak / _analog.MaxRaw;

    /// <summary>Relative level in dB of RMS counts against <paramref name="referenceRms"/> (e.g. a quiet-room RMS).</summary>
    public double RelativeDecibels(double referenceRms) =>
        Rms <= 0 || referenceRms <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(Rms / referenceRms);

    /// <summary>Comparator output: true when the sound is above the potentiometer threshold.</summary>
    public bool IsLoud()
    {
        if (_digital == null)
        {
            throw new InvalidOperationException("ky038 digital output not wired");
        }
        bool level = _digital.Read();
        return DigitalActiveHigh ? level : !level;
    }
}
