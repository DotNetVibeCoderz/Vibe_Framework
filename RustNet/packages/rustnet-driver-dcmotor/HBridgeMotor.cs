using RustNet.Community.Bus;

namespace RustNet.Community.DcMotor;

/// <summary>
/// A brushed DC motor on one channel of an L298N / L293D style H-bridge:
/// two direction inputs (IN1/IN2) and a PWM-driven enable (ENA).
/// Truth table (L298N datasheet, "Bidirectional DC motor control"):
/// EN=H IN1=H IN2=L forward; EN=H IN1=L IN2=H reverse; EN=H IN1=IN2 fast
/// motor stop (brake); EN=L free-running stop (coast).
/// </summary>
public class HBridgeMotor
{
    private readonly DigitalOutput _in1;
    private readonly DigitalOutput _in2;
    private readonly PwmOutput _enable;

    /// <summary>Last commanded speed, -1.0 (full reverse) .. +1.0 (full forward).</summary>
    public double Speed;

    /// <summary>Swap forward and reverse (motor wired the other way round).</summary>
    public bool Inverted;

    /// <param name="in1Pin">GPIO driving IN1.</param>
    /// <param name="in2Pin">GPIO driving IN2.</param>
    /// <param name="enablePwmChannel">PWM channel driving ENA.</param>
    /// <param name="pwmFrequencyHz">PWM frequency; the L298N is happy up to ~25 kHz.</param>
    public HBridgeMotor(int in1Pin, int in2Pin, int enablePwmChannel, int pwmFrequencyHz = 1000)
        : this(new DigitalOutput(in1Pin, false), new DigitalOutput(in2Pin, false), new PwmOutput(enablePwmChannel, pwmFrequencyHz))
    {
    }

    public HBridgeMotor(DigitalOutput in1, DigitalOutput in2, PwmOutput enable)
    {
        _in1 = in1;
        _in2 = in2;
        _enable = enable;
    }

    /// <summary>Run at <paramref name="speed"/> (-1..1). Zero coasts.</summary>
    public void SetSpeed(double speed)
    {
        if (speed > 1.0)
        {
            speed = 1.0;
        }
        if (speed < -1.0)
        {
            speed = -1.0;
        }
        Speed = speed;
        if (speed == 0.0)
        {
            Coast();
            return;
        }
        bool forward = speed > 0;
        if (Inverted)
        {
            forward = !forward;
        }
        double mag = speed < 0 ? -speed : speed;
        _in1.Write(forward);
        _in2.Write(!forward);
        _enable.SetDuty((int)(mag * 10000.0 + 0.5));
    }

    /// <summary>Fast motor stop: both inputs high with the bridge enabled shorts the windings.</summary>
    public void Brake()
    {
        Speed = 0;
        _in1.Write(true);
        _in2.Write(true);
        _enable.SetDuty(10000);
    }

    /// <summary>Free-running stop: bridge disabled, outputs high impedance.</summary>
    public void Coast()
    {
        Speed = 0;
        _enable.SetDuty(0);
        _in1.Write(false);
        _in2.Write(false);
    }
}

/// <summary>
/// A brushed DC motor on a bridge driven by two PWM inputs and no enable pin
/// (TI DRV8833, DRV8871, DRV8835 in IN/IN mode, Toshiba TB6612 with PWM tied
/// high, …). Datasheet truth table: IN1=0 IN2=0 coast, IN1=1 IN2=0 forward,
/// IN1=0 IN2=1 reverse, IN1=1 IN2=1 brake. Speed control PWMs one input:
/// <b>fast decay</b> drives IN1=PWM, IN2=0 (coasts during the off time);
/// <b>slow decay</b> drives IN1=1, IN2=PWM inverted (brakes during the off time).
/// </summary>
public class BidirectionalDcMotor
{
    public const int FastDecay = 0;
    public const int SlowDecay = 1;

    private readonly PwmOutput _in1;
    private readonly PwmOutput _in2;

    /// <summary>Current decay mode, <see cref="FastDecay"/> or <see cref="SlowDecay"/>.</summary>
    public int DecayMode = FastDecay;

    public double Speed;

    public bool Inverted;

    public BidirectionalDcMotor(int in1PwmChannel, int in2PwmChannel, int pwmFrequencyHz = 20000)
        : this(new PwmOutput(in1PwmChannel, pwmFrequencyHz), new PwmOutput(in2PwmChannel, pwmFrequencyHz))
    {
    }

    public BidirectionalDcMotor(PwmOutput in1, PwmOutput in2)
    {
        _in1 = in1;
        _in2 = in2;
    }

    /// <summary>Run at <paramref name="speed"/> (-1..1). Zero coasts.</summary>
    public void SetSpeed(double speed)
    {
        if (speed > 1.0)
        {
            speed = 1.0;
        }
        if (speed < -1.0)
        {
            speed = -1.0;
        }
        Speed = speed;
        if (speed == 0.0)
        {
            Coast();
            return;
        }
        bool forward = speed > 0;
        if (Inverted)
        {
            forward = !forward;
        }
        double mag = speed < 0 ? -speed : speed;
        int duty = (int)(mag * 10000.0 + 0.5);
        PwmOutput drive = forward ? _in1 : _in2;
        PwmOutput other = forward ? _in2 : _in1;
        if (DecayMode == SlowDecay)
        {
            // Hold the drive input high and PWM the other one inverted:
            // on-time = (other low), off-time = both high = brake.
            drive.SetDuty(10000);
            other.SetDuty(10000 - duty);
        }
        else
        {
            other.SetDuty(0);
            drive.SetDuty(duty);
        }
    }

    /// <summary>Both inputs high: low-side (or high-side) short brake.</summary>
    public void Brake()
    {
        Speed = 0;
        _in1.SetDuty(10000);
        _in2.SetDuty(10000);
    }

    /// <summary>Both inputs low: outputs high impedance.</summary>
    public void Coast()
    {
        Speed = 0;
        _in1.SetDuty(0);
        _in2.SetDuty(0);
    }
}
