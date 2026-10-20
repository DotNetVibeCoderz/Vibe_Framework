using RustNet.Community.Bus;

namespace RustNet.Community.Relay;

/// <summary>
/// A relay (or relay module, solid-state relay, contactor driver) on a GPIO.
/// Many opto-isolated relay boards energise the coil when the input is
/// pulled low — construct those as active-low. The relay starts off.
/// </summary>
public class Relay
{
    private readonly DigitalOutput _pin;

    public bool ActiveLow;

    /// <summary>True when the coil is energised (NO contact closed).</summary>
    public bool IsOn;

    public Relay(int pin, bool activeLow = false) : this(new DigitalOutput(pin, activeLow), activeLow)
    {
    }

    public Relay(DigitalOutput pin, bool activeLow)
    {
        _pin = pin;
        ActiveLow = activeLow;
        IsOn = false;
    }

    public void Set(bool on)
    {
        IsOn = on;
        _pin.Write(on != ActiveLow);
    }

    public void On() => Set(true);

    public void Off() => Set(false);

    public void Toggle() => Set(!IsOn);

    /// <summary>Energise for <paramref name="ms"/> milliseconds, then release (door strikes, bells).</summary>
    public void Pulse(int ms)
    {
        Set(true);
        Timing.SleepMs(ms);
        Set(false);
    }
}
