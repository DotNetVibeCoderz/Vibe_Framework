using RustNet.Community.Bus;
using RustNet.Community.Ky038;

namespace DriversApp;

/// <summary>An ADC that cycles through a fixed list of samples.</summary>
public class SequenceAnalogInput : SimAnalogInput
{
    public int[] Sequence = new int[1];
    public int Index;

    public override int ReadRaw()
    {
        int v = Sequence[Index % Sequence.Length];
        Index++;
        return v;
    }
}

public static class Ky038Smoke
{
    public static void Run()
    {
        // Waveform around a 2048 bias: +100, -100, +50, -50.
        SequenceAnalogInput adc = new SequenceAnalogInput();
        adc.Sequence = new int[4];
        adc.Sequence[0] = 2148;
        adc.Sequence[1] = 1948;
        adc.Sequence[2] = 2098;
        adc.Sequence[3] = 1998;
        SimDigitalInput dout = new SimDigitalInput();
        Ky038 mic = new Ky038(adc, dout);
        mic.Samples = 8;
        mic.Measure();
        Check.Equal("ky038 min", 1948, mic.Minimum);
        Check.Equal("ky038 max", 2148, mic.Maximum);
        Check.Equal("ky038 peak-to-peak", 200, mic.PeakToPeak);
        Check.Near("ky038 mean", 2048.0, mic.Mean, 0.0001);
        // RMS = sqrt((100^2 + 100^2 + 50^2 + 50^2) / 4) = sqrt(6250) = 79.0569.
        Check.Near("ky038 rms", 79.056942, mic.Rms, 0.0001);
        Check.Near("ky038 level ratio", 200.0 / 4095.0, mic.LevelRatio(), 0.000001);
        // 20 log10(79.0569 / 7.90569) = 20 dB.
        Check.Near("ky038 relative dB", 20.0, mic.RelativeDecibels(7.9056942), 0.0001);
        dout.Level = true;
        Check.True("ky038 loud", mic.IsLoud());
        mic.DigitalActiveHigh = false;
        Check.True("ky038 inverted board quiet", !mic.IsLoud());
    }
}
