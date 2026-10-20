using RustNet.Community.Bus;

namespace RustNet.Community.Bld510b;

/// <summary>
/// StepperOnline BLD-510B brushless DC motor driver (hall-sensor BLDC,
/// 12–48 VDC). Control terminals, each switched against COM:
/// <list type="bullet">
/// <item>SV — speed: 0–5 V analog or a PWM signal (used here: PWM duty = speed).</item>
/// <item>F/R — direction: connected to COM (low) reverses the motor.</item>
/// <item>EN — enable: connected to COM (low) runs, open (high) stops/freewheels.</item>
/// <item>BRK — brake: connected to COM (low) runs, open (high) brakes.</item>
/// <item>SPEED — open-collector pulse output: 3 pulses per electrical
/// revolution, i.e. 3 × pole-pairs per shaft revolution, so
/// RPM = 60·F / (3·P) = 20·F / P (F in Hz, P = motor pole pairs).</item>
/// </list>
/// The polarity fields let a board with inverting optocouplers flip any input.
/// </summary>
public class Bld510b
{
    private readonly PwmOutput _speed;
    private readonly DigitalOutput _direction;
    private readonly DigitalOutput _enable;
    private readonly DigitalOutput _brake;
    private readonly DigitalInput? _speedPulse;

    /// <summary>Motor pole pairs (57BLF/57BLY series: 2).</summary>
    public int PolePairs = 2;

    /// <summary>Rated speed reached at 100 % SV, for <see cref="SetRpm"/>.</summary>
    public int MaxRpm = 3000;

    /// <summary>EN level that runs the motor (true = high). Default false: EN to COM runs.</summary>
    public bool EnableRunLevel = false;

    /// <summary>BRK level that brakes (true = high). Default true: BRK open brakes.</summary>
    public bool BrakeActiveLevel = true;

    /// <summary>F/R level for forward rotation. Default true: F/R open = forward.</summary>
    public bool ForwardLevel = true;

    public bool Enabled;
    public bool Braking;
    public bool Forward = true;

    /// <summary>Last commanded speed fraction, 0..1.</summary>
    public double SpeedFraction;

    public Bld510b(int svPwmChannel, int directionPin, int enablePin, int brakePin, int speedPulsePin)
        : this(new PwmOutput(svPwmChannel, 20000), new DigitalOutput(directionPin, true), new DigitalOutput(enablePin, true),
               new DigitalOutput(brakePin, true), speedPulsePin >= 0 ? new DigitalInput(speedPulsePin, RustNet.Hal.PinMode.InputPullUp) : null)
    {
    }

    /// <param name="speedPulse">SPEED output (open collector, needs a pull-up), or null when not wired.</param>
    public Bld510b(PwmOutput sv, DigitalOutput direction, DigitalOutput enable, DigitalOutput brake, DigitalInput? speedPulse)
    {
        _speed = sv;
        _direction = direction;
        _enable = enable;
        _brake = brake;
        _speedPulse = speedPulse;
    }

    /// <summary>Put the driver in a safe state: disabled, braked, zero speed, forward.</summary>
    public void Begin()
    {
        SetSpeed(0);
        SetDirection(true);
        SetBrake(true);
        SetEnabled(false);
    }

    public void SetEnabled(bool on)
    {
        Enabled = on;
        _enable.Write(on ? EnableRunLevel : !EnableRunLevel);
    }

    public void SetBrake(bool on)
    {
        Braking = on;
        _brake.Write(on ? BrakeActiveLevel : !BrakeActiveLevel);
    }

    public void SetDirection(bool forward)
    {
        Forward = forward;
        _direction.Write(forward ? ForwardLevel : !ForwardLevel);
    }

    /// <summary>Speed as a fraction of full scale, 0..1 (SV PWM duty).</summary>
    public void SetSpeed(double fraction)
    {
        if (fraction < 0)
        {
            fraction = 0;
        }
        if (fraction > 1)
        {
            fraction = 1;
        }
        SpeedFraction = fraction;
        _speed.SetDuty((int)(fraction * 10000.0 + 0.5));
    }

    /// <summary>Speed in RPM, scaled against <see cref="MaxRpm"/>.</summary>
    public void SetRpm(int rpm) => SetSpeed((double)rpm / MaxRpm);

    /// <summary>Release the brake, enable and run at <paramref name="fraction"/> in the given direction.</summary>
    public void Run(bool forward, double fraction)
    {
        SetDirection(forward);
        SetSpeed(fraction);
        SetBrake(false);
        SetEnabled(true);
    }

    /// <summary>Zero speed and brake (stays enabled so the brake holds).</summary>
    public void Stop()
    {
        SetSpeed(0);
        SetBrake(true);
    }

    /// <summary>Shaft RPM for a SPEED pulse frequency: 20·F / pole-pairs.</summary>
    public double RpmFromPulseFrequency(double hz) => 20.0 * hz / PolePairs;

    /// <summary>
    /// Count SPEED rising edges for <paramref name="windowMs"/> by polling and
    /// return the shaft RPM. Polling resolves pulses up to a few kHz on the
    /// interpreter; for faster motors use a hardware capture (RustNet.Hal.Signal).
    /// Returns -1 when no SPEED input is wired.
    /// </summary>
    public double MeasureRpm(int windowMs)
    {
        if (_speedPulse == null)
        {
            return -1;
        }
        long start = Timing.NowMs();
        long end = start + windowMs;
        bool last = _speedPulse.Read();
        int edges = 0;
        while (Timing.NowMs() < end)
        {
            bool now = _speedPulse.Read();
            if (now && !last)
            {
                edges++;
            }
            last = now;
        }
        return RpmFromPulseFrequency(edges * 1000.0 / windowMs);
    }
}
