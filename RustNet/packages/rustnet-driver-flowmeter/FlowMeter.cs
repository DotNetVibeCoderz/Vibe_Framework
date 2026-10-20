using RustNet.Community.Bus;

namespace RustNet.Community.FlowMeter;

/// <summary>
/// A source of pulse counts. The default counts rising edges of a GPIO in
/// software each time <see cref="Poll"/> is called — adequate only when
/// <see cref="Poll"/> runs faster than twice the pulse frequency (a YF-S201
/// at 30 L/min pulses at 225 Hz). Override <see cref="Count"/> to use a
/// hardware counter (PCNT, timer capture) or to simulate.
/// </summary>
public class PulseCounter
{
    private readonly DigitalInput? _input;
    private bool _last;
    private long _count;

    protected PulseCounter()
    {
    }

    public PulseCounter(DigitalInput input)
    {
        _input = input;
        _last = input.Read();
    }

    public PulseCounter(int pin) : this(new DigitalInput(pin, RustNet.Hal.PinMode.InputPullUp))
    {
    }

    /// <summary>Sample the pin once, counting a rising edge.</summary>
    public virtual void Poll()
    {
        if (_input == null)
        {
            return;
        }
        bool v = _input.Read();
        if (v && !_last)
        {
            _count++;
        }
        _last = v;
    }

    /// <summary>Total pulses since start (monotonic).</summary>
    public virtual long Count() => _count;
}

/// <summary>A counter whose total the test sets.</summary>
public class SimPulseCounter : PulseCounter
{
    public long Total;

    public SimPulseCounter()
    {
    }

    public override void Poll()
    {
    }

    public override long Count() => Total;
}

/// <summary>
/// Hall-effect turbine flow sensor. Each model's datasheet gives
/// F (Hz) = K × Q (L/min), so a litre is 60·K pulses. Known K factors:
/// YF-S201 7.5, YF-S401 98, YF-B1 11, YF-B5 6.6, YF-B6 6.6, YF-B7 11.
/// Other sensors (Gredia GR-105/GR-201/GR-216 and generic clones) publish
/// inconsistent figures; measure K with <see cref="CalibrateK"/>.
/// </summary>
public class FlowMeter
{
    public const double KYfS201 = 7.5;
    public const double KYfS401 = 98.0;
    public const double KYfB1 = 11.0;
    public const double KYfB5 = 6.6;
    public const double KYfB6 = 6.6;
    public const double KYfB7 = 11.0;

    private readonly PulseCounter _counter;
    private long _lastCount;
    private long _lastMs;
    private long _startCount;

    /// <summary>Pulse frequency per L/min (F = K·Q).</summary>
    public double K;

    /// <summary>Flow from the last <see cref="Update"/>, in L/min.</summary>
    public double LitersPerMinute;

    public FlowMeter(PulseCounter counter, double k)
    {
        _counter = counter;
        K = k;
        _startCount = counter.Count();
        _lastCount = _startCount;
    }

    public FlowMeter(int pin, double k) : this(new PulseCounter(pin), k)
    {
    }

    /// <summary>Pulses per litre (60·K).</summary>
    public double PulsesPerLiter => 60.0 * K;

    /// <summary>
    /// Recompute the flow from the pulses since the previous call over
    /// <paramref name="elapsedMs"/> (pass the real interval, e.g. from
    /// Timing.NowMs). Returns L/min.
    /// </summary>
    public double Update(long elapsedMs)
    {
        long c = _counter.Count();
        long pulses = c - _lastCount;
        _lastCount = c;
        if (elapsedMs <= 0)
        {
            return LitersPerMinute;
        }
        double hz = pulses * 1000.0 / elapsedMs;
        LitersPerMinute = hz / K;
        return LitersPerMinute;
    }

    /// <summary>Update using <see cref="Timing.NowMs"/> for the interval since the previous call.</summary>
    public double UpdateNow()
    {
        long now = Timing.NowMs();
        long dt = _lastMs == 0 ? 0 : now - _lastMs;
        _lastMs = now;
        return Update(dt);
    }

    /// <summary>Total volume since construction or <see cref="ResetTotal"/>, in litres.</summary>
    public double TotalLiters => (_counter.Count() - _startCount) / PulsesPerLiter;

    public void ResetTotal() => _startCount = _counter.Count();

    /// <summary>Derive K from a measured run: <paramref name="pulses"/> counted while <paramref name="liters"/> flowed.</summary>
    public void CalibrateK(long pulses, double liters) => K = pulses / liters / 60.0;
}
