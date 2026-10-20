using RustNet.Community.Bus;

namespace RustNet.Community.Stepper;

/// <summary>
/// Trapezoidal motion profile for steppers. A move of N steps accelerates at
/// a constant rate from standstill, cruises at the maximum speed and
/// decelerates symmetrically; a move too short to reach the maximum speed
/// becomes a triangle. Each step's interval is the exact kinematic time
/// between steps: during acceleration step k (0-based) ends at
/// t = sqrt(2(k+1)/a), so its interval is sqrt(2(k+1)/a) - sqrt(2k/a),
/// clamped to never be shorter than 1/vmax.
/// </summary>
public static class StepProfile
{
    /// <summary>Number of steps needed to reach <paramref name="maxSpeed"/> at <paramref name="acceleration"/>: v^2 / 2a.</summary>
    public static int AccelerationSteps(double maxSpeed, double acceleration)
    {
        if (acceleration <= 0)
        {
            return 0;
        }
        return (int)(maxSpeed * maxSpeed / (2.0 * acceleration));
    }

    /// <summary>
    /// Step intervals in microseconds for a move of <paramref name="steps"/>
    /// steps with peak speed <paramref name="maxSpeed"/> (steps/s) and
    /// <paramref name="acceleration"/> (steps/s^2; 0 or less = constant speed).
    /// </summary>
    public static int[] IntervalsUs(int steps, double maxSpeed, double acceleration)
    {
        if (steps < 0)
        {
            steps = -steps;
        }
        int[] r = new int[steps];
        double cruise = 1.0e6 / maxSpeed;
        int ramp = AccelerationSteps(maxSpeed, acceleration);
        if (ramp * 2 > steps)
        {
            ramp = steps / 2;
        }
        for (int k = 0; k < steps; k++)
        {
            int j = -1;
            if (k < ramp)
            {
                j = k;
            }
            else if (k >= steps - ramp)
            {
                j = steps - 1 - k;
            }
            double us = cruise;
            if (j >= 0)
            {
                us = (Math.Sqrt(2.0 * (j + 1) / acceleration) - Math.Sqrt(2.0 * j / acceleration)) * 1.0e6;
                if (us < cruise)
                {
                    us = cruise;
                }
            }
            r[k] = (int)(us + 0.5);
        }
        return r;
    }
}

/// <summary>
/// Common positioning logic for every stepper driver: an absolute step
/// position, a speed limit and acceleration, and moves that follow
/// <see cref="StepProfile"/>. Subclasses only know how to take one step.
/// Waits are accumulated in microseconds and slept in whole milliseconds
/// through <see cref="Timing.SleepMs"/>, so long moves keep the right average
/// rate even though a single step interval is shorter than 1 ms.
/// </summary>
public abstract class StepperMotor
{
    /// <summary>Absolute position in (micro)steps.</summary>
    public long Position;

    /// <summary>Peak speed, steps per second.</summary>
    public double MaxSpeed = 500;

    /// <summary>Acceleration, steps per second squared (0 = start and stop at full speed).</summary>
    public double Acceleration = 1000;

    /// <summary>Steps per output-shaft revolution, used by <see cref="RotateDegrees"/>.</summary>
    public int StepsPerRevolution = 200;

    /// <summary>Intervals (µs) of the most recent move, for diagnostics.</summary>
    public int[]? LastIntervalsUs = null;

    private int _pendingUs;

    /// <summary>Take one step; <paramref name="forward"/> increases <see cref="Position"/>.</summary>
    protected abstract void StepOnce(bool forward);

    /// <summary>Single step without profile timing.</summary>
    public void Step(bool forward)
    {
        StepOnce(forward);
        Position += forward ? 1 : -1;
    }

    /// <summary>Move by <paramref name="steps"/> (signed) along the trapezoidal profile.</summary>
    public void Move(int steps)
    {
        bool forward = steps > 0;
        int[] intervals = StepProfile.IntervalsUs(steps, MaxSpeed, Acceleration);
        LastIntervalsUs = intervals;
        for (int i = 0; i < intervals.Length; i++)
        {
            Step(forward);
            Wait(intervals[i]);
        }
    }

    /// <summary>Move to an absolute position.</summary>
    public void MoveTo(long target) => Move((int)(target - Position));

    /// <summary>Rotate the shaft by an angle, using <see cref="StepsPerRevolution"/>.</summary>
    public void RotateDegrees(double degrees)
    {
        double s = degrees * StepsPerRevolution / 360.0;
        Move((int)(s < 0 ? s - 0.5 : s + 0.5));
    }

    /// <summary>Declare the current position to be <paramref name="position"/> (homing).</summary>
    public void SetPosition(long position) => Position = position;

    private void Wait(int us)
    {
        _pendingUs += us;
        if (_pendingUs >= 1000)
        {
            Timing.SleepMs(_pendingUs / 1000);
            _pendingUs = _pendingUs % 1000;
        }
    }
}

/// <summary>
/// Allegro A4988 microstepping driver: STEP (rising edge, ≥1 µs high), DIR,
/// ENABLE (active low) and MS1/MS2/MS3 microstep select. Datasheet table 1:
/// <code>
/// MS1 MS2 MS3   resolution
///  L   L   L    full step
///  H   L   L    half step
///  L   H   L    quarter step
///  H   H   L    eighth step
///  H   H   H    sixteenth step
/// </code>
/// <see cref="StepperMotor.Position"/> counts microsteps.
/// </summary>
public class A4988 : StepperMotor
{
    private readonly DigitalOutput _step;
    private readonly DigitalOutput _dir;
    private readonly DigitalOutput? _enable;
    private readonly DigitalOutput? _ms1;
    private readonly DigitalOutput? _ms2;
    private readonly DigitalOutput? _ms3;

    /// <summary>Current microstep divisor (1, 2, 4, 8 or 16).</summary>
    public int Microsteps = 1;

    /// <summary>Full steps per revolution of the motor (1.8° motors: 200).</summary>
    public int FullStepsPerRevolution = 200;

    /// <summary>Swap the DIR level for "forward".</summary>
    public bool InvertDirection;

    private bool _dirState;
    private bool _dirKnown;

    public A4988(int stepPin, int dirPin) : this(new DigitalOutput(stepPin, false), new DigitalOutput(dirPin, false), null, null, null, null)
    {
    }

    public A4988(int stepPin, int dirPin, int enablePin, int ms1Pin, int ms2Pin, int ms3Pin)
        : this(new DigitalOutput(stepPin, false), new DigitalOutput(dirPin, false), new DigitalOutput(enablePin, true),
               new DigitalOutput(ms1Pin, false), new DigitalOutput(ms2Pin, false), new DigitalOutput(ms3Pin, false))
    {
    }

    /// <param name="enable">ENABLE pin (active low), or null when strapped low.</param>
    /// <param name="ms1">MS1, or null when strapped (then also pass null for MS2/MS3).</param>
    public A4988(DigitalOutput step, DigitalOutput dir, DigitalOutput? enable, DigitalOutput? ms1, DigitalOutput? ms2, DigitalOutput? ms3)
    {
        _step = step;
        _dir = dir;
        _enable = enable;
        _ms1 = ms1;
        _ms2 = ms2;
        _ms3 = ms3;
        StepsPerRevolution = FullStepsPerRevolution;
    }

    /// <summary>Select the microstep resolution: 1, 2, 4, 8 or 16. Returns false for other values.</summary>
    public bool SetMicrosteps(int divisor)
    {
        bool m1;
        bool m2;
        bool m3;
        if (divisor == 1)
        {
            m1 = false; m2 = false; m3 = false;
        }
        else if (divisor == 2)
        {
            m1 = true; m2 = false; m3 = false;
        }
        else if (divisor == 4)
        {
            m1 = false; m2 = true; m3 = false;
        }
        else if (divisor == 8)
        {
            m1 = true; m2 = true; m3 = false;
        }
        else if (divisor == 16)
        {
            m1 = true; m2 = true; m3 = true;
        }
        else
        {
            return false;
        }
        _ms1?.Write(m1);
        _ms2?.Write(m2);
        _ms3?.Write(m3);
        Microsteps = divisor;
        StepsPerRevolution = FullStepsPerRevolution * divisor;
        return true;
    }

    /// <summary>Energise the outputs (ENABLE low).</summary>
    public void Enable() => _enable?.Write(false);

    /// <summary>Disable the outputs (ENABLE high); the motor freewheels.</summary>
    public void Disable() => _enable?.Write(true);

    protected override void StepOnce(bool forward)
    {
        bool level = forward != InvertDirection;
        if (!_dirKnown || level != _dirState)
        {
            _dir.Write(level);
            _dirState = level;
            _dirKnown = true;
        }
        _step.Write(true);
        _step.Write(false);
    }
}

/// <summary>
/// A unipolar or bipolar stepper driven directly from four GPIOs through a
/// transistor array or dual bridge. The coil pattern for each phase comes from
/// a sequence string of hex nibbles (bit 3 = IN1 … bit 0 = IN4); only pins
/// that change are written, keeping host calls down.
/// </summary>
public class FourWireStepper : StepperMotor
{
    private readonly DigitalOutput _in1;
    private readonly DigitalOutput _in2;
    private readonly DigitalOutput _in3;
    private readonly DigitalOutput _in4;

    /// <summary>Coil patterns, one hex nibble per phase.</summary>
    public string Sequence;

    /// <summary>Index into <see cref="Sequence"/> of the pattern currently applied.</summary>
    public int Phase;

    private int _applied = -1;

    public FourWireStepper(DigitalOutput in1, DigitalOutput in2, DigitalOutput in3, DigitalOutput in4, string sequence)
    {
        _in1 = in1;
        _in2 = in2;
        _in3 = in3;
        _in4 = in4;
        Sequence = sequence;
    }

    /// <summary>Coil bits of the pattern at <paramref name="phase"/> (bit 3 = IN1).</summary>
    public int PatternAt(int phase)
    {
        char c = Sequence[phase];
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        return c - 'A' + 10;
    }

    /// <summary>The coil bits currently applied (bit 3 = IN1).</summary>
    public int CurrentPattern() => _applied < 0 ? 0 : _applied;

    protected override void StepOnce(bool forward)
    {
        int n = Sequence.Length;
        Phase = forward ? (Phase + 1) % n : (Phase + n - 1) % n;
        Apply(PatternAt(Phase));
    }

    /// <summary>Energise the coils for the current phase without moving.</summary>
    public void Hold() => Apply(PatternAt(Phase));

    /// <summary>De-energise every coil (no holding torque, no heat).</summary>
    public void Release() => Apply(0);

    private void Apply(int bits)
    {
        int changed = _applied < 0 ? 0xF : (bits ^ _applied);
        if ((changed & 8) != 0)
        {
            _in1.Write((bits & 8) != 0);
        }
        if ((changed & 4) != 0)
        {
            _in2.Write((bits & 4) != 0);
        }
        if ((changed & 2) != 0)
        {
            _in3.Write((bits & 2) != 0);
        }
        if ((changed & 1) != 0)
        {
            _in4.Write((bits & 1) != 0);
        }
        _applied = bits;
    }
}

/// <summary>
/// 28BYJ-48 unipolar geared stepper on a ULN2003 board (IN1..IN4 = coils
/// blue, pink, yellow, orange). Half stepping (default) uses the 8-phase
/// sequence 1000 1100 0100 0110 0010 0011 0001 1001; full stepping the
/// two-phase-on 1100 0110 0011 1001; wave drive 1000 0100 0010 0001.
/// The 64-step motor with its 1:64 gearbox gives 4096 half steps
/// (2048 full steps) per output revolution.
/// </summary>
public class Uln2003 : FourWireStepper
{
    public const int HalfStep = 0;
    public const int FullStep = 1;
    public const int WaveDrive = 2;

    public const string HalfStepSequence = "8C462319";
    public const string FullStepSequence = "C639";
    public const string WaveDriveSequence = "8421";

    public int Mode = HalfStep;

    public Uln2003(int in1Pin, int in2Pin, int in3Pin, int in4Pin)
        : this(new DigitalOutput(in1Pin, false), new DigitalOutput(in2Pin, false), new DigitalOutput(in3Pin, false), new DigitalOutput(in4Pin, false))
    {
    }

    public Uln2003(DigitalOutput in1, DigitalOutput in2, DigitalOutput in3, DigitalOutput in4)
        : base(in1, in2, in3, in4, HalfStepSequence)
    {
        StepsPerRevolution = 4096;
        MaxSpeed = 500;
        Acceleration = 0;
    }

    /// <summary>Select <see cref="HalfStep"/>, <see cref="FullStep"/> or <see cref="WaveDrive"/>.</summary>
    public void SetMode(int mode)
    {
        Mode = mode;
        if (mode == FullStep)
        {
            Sequence = FullStepSequence;
            StepsPerRevolution = 2048;
        }
        else if (mode == WaveDrive)
        {
            Sequence = WaveDriveSequence;
            StepsPerRevolution = 2048;
        }
        else
        {
            Sequence = HalfStepSequence;
            StepsPerRevolution = 4096;
        }
        Phase = 0;
    }
}

/// <summary>
/// Generic 4-wire stepper on four GPIOs (bipolar motor through a dual
/// H-bridge such as the L298N, or any driver with four logic inputs), using
/// the classic Arduino Stepper library full-step sequence for pins 1..4:
/// 1010, 0110, 0101, 1001. Pass a custom sequence string for other wiring.
/// </summary>
public class GpioStepper : FourWireStepper
{
    public const string BipolarFullStepSequence = "A659";

    public GpioStepper(int pin1, int pin2, int pin3, int pin4, int stepsPerRevolution)
        : this(new DigitalOutput(pin1, false), new DigitalOutput(pin2, false), new DigitalOutput(pin3, false), new DigitalOutput(pin4, false), stepsPerRevolution, BipolarFullStepSequence)
    {
    }

    public GpioStepper(DigitalOutput pin1, DigitalOutput pin2, DigitalOutput pin3, DigitalOutput pin4, int stepsPerRevolution, string sequence)
        : base(pin1, pin2, pin3, pin4, sequence)
    {
        StepsPerRevolution = stepsPerRevolution;
    }
}
