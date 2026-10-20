using RustNet.Community.Bus;

namespace RustNet.Community.SoilMoisture;

/// <summary>
/// Analog soil moisture probe read through an ADC, calibrated with two
/// points: the raw reading in dry air/soil and fully wet (in water). Both
/// common probe types read *lower* when wetter, but the mapping works either
/// way round. Readings are averaged; an optional power pin switches the
/// probe on only while sampling (slows electrode corrosion on resistive probes).
/// </summary>
public class SoilMoistureSensor
{
    protected readonly AnalogInput Adc;
    private readonly DigitalOutput? _power;

    /// <summary>Raw ADC reading with the probe dry.</summary>
    public int DryRaw;

    /// <summary>Raw ADC reading with the probe in water.</summary>
    public int WetRaw;

    /// <summary>Readings averaged per call.</summary>
    public int Samples = 8;

    /// <summary>Settling time after switching the probe on, in ms.</summary>
    public int PowerSettleMs = 10;

    /// <summary>Raw average of the last read.</summary>
    public double LastRaw;

    public SoilMoistureSensor(AnalogInput adc, int dryRaw, int wetRaw) : this(adc, null, dryRaw, wetRaw)
    {
    }

    public SoilMoistureSensor(AnalogInput adc, DigitalOutput? power, int dryRaw, int wetRaw)
    {
        Adc = adc;
        _power = power;
        DryRaw = dryRaw;
        WetRaw = wetRaw;
    }

    /// <summary>Averaged raw reading (powering the probe around it when a power pin is given).</summary>
    public double ReadRaw()
    {
        if (_power != null)
        {
            _power.Write(true);
            Timing.SleepMs(PowerSettleMs);
        }
        int n = Samples < 1 ? 1 : Samples;
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += Adc.ReadRaw();
        }
        if (_power != null)
        {
            _power.Write(false);
        }
        LastRaw = (double)sum / n;
        return LastRaw;
    }

    /// <summary>Volumetric-ish moisture 0 (dry) .. 100 (wet) %, linear between the calibration points and clamped.</summary>
    public double ReadPercent() => ToPercent(ReadRaw());

    public double ToPercent(double raw)
    {
        if (DryRaw == WetRaw)
        {
            return 0.0;
        }
        double p = (DryRaw - raw) * 100.0 / (DryRaw - WetRaw);
        if (p < 0)
        {
            p = 0;
        }
        if (p > 100)
        {
            p = 100;
        }
        return p;
    }

    /// <summary>Record the current reading as the dry calibration point.</summary>
    public void CalibrateDry() => DryRaw = (int)(ReadRaw() + 0.5);

    /// <summary>Record the current reading as the wet calibration point.</summary>
    public void CalibrateWet() => WetRaw = (int)(ReadRaw() + 0.5);
}

/// <summary>
/// Capacitive soil moisture sensor (v1.2 / v2.0, TLC555 based). Its analog
/// output falls as moisture rises. Default calibration for a 12-bit ADC at
/// 3.3 V: about 2800 dry and 1200 in water — calibrate each probe.
/// </summary>
public class CapacitiveSoilSensor : SoilMoistureSensor
{
    public CapacitiveSoilSensor(int adcChannel) : base(new AnalogInput(adcChannel), 2800, 1200)
    {
    }

    public CapacitiveSoilSensor(AnalogInput adc) : base(adc, 2800, 1200)
    {
    }

    public CapacitiveSoilSensor(AnalogInput adc, int dryRaw, int wetRaw) : base(adc, dryRaw, wetRaw)
    {
    }
}

/// <summary>
/// Resistive FC-28 / YL-69 soil probe with the LM393 comparator board. AO
/// falls as the soil gets wetter (default 4095 dry, 1500 wet on 12-bit); DO
/// goes LOW once moisture exceeds the threshold set by the potentiometer.
/// Power it from a GPIO through <see cref="SoilMoistureSensor"/>'s power pin
/// to limit electrolysis.
/// </summary>
public class Fc28SoilSensor : SoilMoistureSensor
{
    private readonly DigitalInput? _digital;

    public Fc28SoilSensor(int adcChannel, int digitalPin, int powerPin)
        : base(new AnalogInput(adcChannel), powerPin >= 0 ? new DigitalOutput(powerPin, false) : null, 4095, 1500)
    {
        _digital = digitalPin >= 0 ? new DigitalInput(digitalPin, RustNet.Hal.PinMode.Input) : null;
    }

    public Fc28SoilSensor(AnalogInput adc, DigitalInput? digital, DigitalOutput? power)
        : base(adc, power, 4095, 1500)
    {
        _digital = digital;
    }

    /// <summary>Comparator output: true when DO is LOW, i.e. wetter than the potentiometer threshold.</summary>
    public bool IsWet()
    {
        if (_digital == null)
        {
            throw new InvalidOperationException("fc28 digital output not wired");
        }
        return !_digital.Read();
    }
}
