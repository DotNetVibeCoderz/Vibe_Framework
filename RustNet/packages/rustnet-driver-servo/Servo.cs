using RustNet.Community.Bus;

namespace RustNet.Community.Servo;

/// <summary>Pulse-width helpers shared by servos and ESCs.</summary>
public static class PulseWidth
{
    /// <summary>
    /// Duty (permyriad) of a <paramref name="pulseUs"/> pulse repeated at
    /// <paramref name="frequencyHz"/>: pulse / period × 10000 = pulse × f / 100.
    /// </summary>
    public static int ToPermyriad(int pulseUs, int frequencyHz) => (int)(((long)pulseUs * frequencyHz + 50) / 100);
}

/// <summary>
/// Hobby RC servo: a 50 Hz frame whose high time encodes the angle
/// (typically 1000 µs = 0°, 1500 µs = centre, 2000 µs = full travel; many
/// micro servos such as the SG90 use 500..2400 µs). Configure the endpoints to
/// match the servo.
/// </summary>
public class Servo
{
    public const int DefaultFrequencyHz = 50;

    private readonly PwmOutput _pwm;

    public int MinPulseUs;
    public int MaxPulseUs;

    /// <summary>Angle reached at <see cref="MaxPulseUs"/> (usually 180°).</summary>
    public double MaxAngle;

    /// <summary>Last commanded angle, degrees.</summary>
    public double Angle;

    /// <summary>Last commanded pulse width, µs.</summary>
    public int PulseUs;

    public Servo(int pwmChannel) : this(new PwmOutput(pwmChannel, DefaultFrequencyHz), 1000, 2000, 180)
    {
    }

    public Servo(int pwmChannel, int minPulseUs, int maxPulseUs, double maxAngle)
        : this(new PwmOutput(pwmChannel, DefaultFrequencyHz), minPulseUs, maxPulseUs, maxAngle)
    {
    }

    public Servo(PwmOutput pwm, int minPulseUs, int maxPulseUs, double maxAngle)
    {
        _pwm = pwm;
        MinPulseUs = minPulseUs;
        MaxPulseUs = maxPulseUs;
        MaxAngle = maxAngle;
    }

    /// <summary>Output a raw pulse width (clamped to the configured endpoints).</summary>
    public void SetPulseUs(int us)
    {
        if (us < MinPulseUs)
        {
            us = MinPulseUs;
        }
        if (us > MaxPulseUs)
        {
            us = MaxPulseUs;
        }
        PulseUs = us;
        _pwm.SetDuty(PulseWidth.ToPermyriad(us, _pwm.FrequencyHz));
    }

    /// <summary>Move to <paramref name="degrees"/> (0..MaxAngle), linearly between the pulse endpoints.</summary>
    public void SetAngle(double degrees)
    {
        if (degrees < 0)
        {
            degrees = 0;
        }
        if (degrees > MaxAngle)
        {
            degrees = MaxAngle;
        }
        Angle = degrees;
        double us = MinPulseUs + (MaxPulseUs - MinPulseUs) * degrees / MaxAngle;
        SetPulseUs((int)(us + 0.5));
    }

    /// <summary>Stop sending pulses; most analog servos then go limp.</summary>
    public void Release() => _pwm.SetDuty(0);
}

/// <summary>
/// Electronic speed controller for brushless (drone / RC) motors: the same
/// 50 Hz servo signal, 1000 µs = zero throttle and 2000 µs = full throttle.
/// An ESC only starts after it has seen minimum throttle for a moment after
/// power-up (<see cref="Arm"/>); throttle commands before arming are held at
/// minimum. <see cref="Calibrate"/> teaches the ESC the throttle endpoints
/// (call it with the ESC unpowered, then power it during the high phase).
/// </summary>
public class Esc
{
    private readonly PwmOutput _pwm;

    public int MinPulseUs = 1000;
    public int MaxPulseUs = 2000;

    /// <summary>How long minimum throttle is held when arming (most ESCs need 1–3 s).</summary>
    public int ArmHoldMs = 2000;

    public bool Armed;

    /// <summary>Last applied throttle, 0..1.</summary>
    public double Throttle;

    public int PulseUs;

    public Esc(int pwmChannel) : this(new PwmOutput(pwmChannel, Servo.DefaultFrequencyHz))
    {
    }

    public Esc(PwmOutput pwm)
    {
        _pwm = pwm;
    }

    private void Output(int us)
    {
        PulseUs = us;
        _pwm.SetDuty(PulseWidth.ToPermyriad(us, _pwm.FrequencyHz));
    }

    /// <summary>Hold minimum throttle for <see cref="ArmHoldMs"/> so the ESC arms.</summary>
    public void Arm()
    {
        Throttle = 0;
        Output(MinPulseUs);
        Timing.SleepMs(ArmHoldMs);
        Armed = true;
    }

    /// <summary>Endpoint calibration: full throttle for <paramref name="highMs"/>, then minimum for <paramref name="lowMs"/>.</summary>
    public void Calibrate(int highMs, int lowMs)
    {
        Output(MaxPulseUs);
        Timing.SleepMs(highMs);
        Output(MinPulseUs);
        Timing.SleepMs(lowMs);
        Throttle = 0;
        Armed = true;
    }

    /// <summary>Set throttle 0..1 (ignored, holding minimum, until armed).</summary>
    public void SetThrottle(double throttle)
    {
        if (throttle < 0)
        {
            throttle = 0;
        }
        if (throttle > 1)
        {
            throttle = 1;
        }
        if (!Armed)
        {
            throttle = 0;
        }
        Throttle = throttle;
        double us = MinPulseUs + (MaxPulseUs - MinPulseUs) * throttle;
        Output((int)(us + 0.5));
    }

    /// <summary>Back to minimum throttle (stays armed).</summary>
    public void Stop() => SetThrottle(0);

    /// <summary>Stop the signal entirely; the ESC disarms after its signal-loss timeout.</summary>
    public void Disarm()
    {
        Armed = false;
        Throttle = 0;
        PulseUs = 0;
        _pwm.SetDuty(0);
    }
}
