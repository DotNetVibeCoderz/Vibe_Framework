using RustNet.Community.Bus;

namespace RustNet.Community.DcMotor;

/// <summary>
/// Toshiba TB67H420FTG dual H-bridge (as on the Pololu carrier). Each bridge
/// has INx1, INx2 and PWMx inputs; datasheet function table:
/// <code>
/// INx1 INx2 PWMx   OUTx1 OUTx2
///  H    H    H      L     L     short brake
///  L    H    H      L     H     reverse (CCW)
///  H    L    H      H     L     forward (CW)
///  L    L    H      Z     Z     off (coast)
///  x    x    L      L     L     short brake
/// </code>
/// so a PWM on PWMx alternates drive with short brake (slow decay).
/// HBMODE high combines both bridges into one high-current bridge controlled
/// by the A inputs. LO1/LO2 are open-drain error outputs, low on a fault
/// (over-current, thermal shutdown, under-voltage).
/// Both bridges together can also drive one bipolar stepper (full steps).
/// </summary>
public class Tb67h420ftg
{
    public const int ChannelA = 0;
    public const int ChannelB = 1;

    private readonly DigitalOutput _ina1;
    private readonly DigitalOutput _ina2;
    private readonly PwmOutput _pwma;
    private readonly DigitalOutput _inb1;
    private readonly DigitalOutput _inb2;
    private readonly PwmOutput _pwmb;
    private readonly DigitalOutput? _hbMode;
    private readonly DigitalInput? _lo1;
    private readonly DigitalInput? _lo2;

    /// <summary>Position in the 4-step full-step sequence (0..3).</summary>
    public int StepPhase;

    /// <summary>Net full steps taken by <see cref="Step"/>.</summary>
    public long StepPosition;

    public Tb67h420ftg(int ina1Pin, int ina2Pin, int pwmaChannel, int inb1Pin, int inb2Pin, int pwmbChannel, int pwmFrequencyHz = 20000)
        : this(new DigitalOutput(ina1Pin, false), new DigitalOutput(ina2Pin, false), new PwmOutput(pwmaChannel, pwmFrequencyHz),
               new DigitalOutput(inb1Pin, false), new DigitalOutput(inb2Pin, false), new PwmOutput(pwmbChannel, pwmFrequencyHz),
               null, null, null)
    {
    }

    /// <param name="hbMode">HBMODE pin (null when strapped low: two independent bridges).</param>
    /// <param name="lo1">LO1 error output (null when not wired).</param>
    /// <param name="lo2">LO2 error output (null when not wired).</param>
    public Tb67h420ftg(DigitalOutput ina1, DigitalOutput ina2, PwmOutput pwma,
                       DigitalOutput inb1, DigitalOutput inb2, PwmOutput pwmb,
                       DigitalOutput? hbMode, DigitalInput? lo1, DigitalInput? lo2)
    {
        _ina1 = ina1;
        _ina2 = ina2;
        _pwma = pwma;
        _inb1 = inb1;
        _inb2 = inb2;
        _pwmb = pwmb;
        _hbMode = hbMode;
        _lo1 = lo1;
        _lo2 = lo2;
    }

    /// <summary>HBMODE: true = single large-current bridge driven by the A inputs.</summary>
    public void SetSingleBridgeMode(bool single)
    {
        _hbMode?.Write(single);
    }

    /// <summary>Drive one bridge at <paramref name="speed"/> (-1..1); zero coasts (OFF).</summary>
    public void SetSpeed(int channel, double speed)
    {
        if (speed > 1.0)
        {
            speed = 1.0;
        }
        if (speed < -1.0)
        {
            speed = -1.0;
        }
        if (speed == 0.0)
        {
            Coast(channel);
            return;
        }
        bool forward = speed > 0;
        double mag = forward ? speed : -speed;
        Drive(channel, forward, !forward, (int)(mag * 10000.0 + 0.5));
    }

    /// <summary>Short brake: INx1 = INx2 = H with PWM high.</summary>
    public void Brake(int channel) => Drive(channel, true, true, 10000);

    /// <summary>Off (high impedance): INx1 = INx2 = L with PWM high.</summary>
    public void Coast(int channel) => Drive(channel, false, false, 10000);

    private void Drive(int channel, bool in1, bool in2, int duty)
    {
        if (channel == ChannelA)
        {
            _ina1.Write(in1);
            _ina2.Write(in2);
            _pwma.SetDuty(duty);
        }
        else
        {
            _inb1.Write(in1);
            _inb2.Write(in2);
            _pwmb.SetDuty(duty);
        }
    }

    /// <summary>
    /// Advance a bipolar stepper wired across OUTA and OUTB one full step
    /// (two-phase-on). Sequence: A+B+, A-B+, A-B-, A+B- for forward.
    /// </summary>
    public void Step(bool forward)
    {
        StepPhase = (StepPhase + (forward ? 1 : 3)) & 3;
        StepPosition += forward ? 1 : -1;
        bool aPositive = StepPhase == 0 || StepPhase == 3;
        bool bPositive = StepPhase == 0 || StepPhase == 1;
        Drive(ChannelA, aPositive, !aPositive, 10000);
        Drive(ChannelB, bPositive, !bPositive, 10000);
    }

    /// <summary>Error code from the open-drain outputs: bit 0 = LO1 low, bit 1 = LO2 low; 0 = no fault.</summary>
    public int FaultCode()
    {
        int code = 0;
        if (_lo1 != null && !_lo1.Read())
        {
            code |= 1;
        }
        if (_lo2 != null && !_lo2.Read())
        {
            code |= 2;
        }
        return code;
    }

    public bool HasFault() => FaultCode() != 0;
}
