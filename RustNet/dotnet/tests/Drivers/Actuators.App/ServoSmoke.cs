using RustNet.Community.Bus;
using RustNet.Community.Servo;

namespace DriversApp;

public static class ServoSmoke
{
    /// <summary>50 Hz frame = 20000 us, so duty permyriad = pulse_us / 2.</summary>
    public static void RunServo()
    {
        SimPwmOutput pwm = new SimPwmOutput(50);
        Servo s = new Servo(pwm, 1000, 2000, 180);
        s.SetAngle(90);
        Check.Equal("servo 90deg pulse", 1500, s.PulseUs);
        Check.Equal("servo 90deg duty", 750, pwm.Duty);
        s.SetAngle(45);
        Check.Equal("servo 45deg duty", 625, pwm.Duty);
        s.SetAngle(0);
        Check.Equal("servo 0deg duty", 500, pwm.Duty);
        s.SetPulseUs(5000);
        Check.Equal("servo pulse clamped", 1000, pwm.Duty);
        s.Release();
        Check.Equal("servo release", 0, pwm.Duty);

        // SG90-style endpoints 500..2400 us: 90deg -> 1450 us -> 725.
        SimPwmOutput pwm2 = new SimPwmOutput(50);
        Servo sg90 = new Servo(pwm2, 500, 2400, 180);
        sg90.SetAngle(90);
        Check.Equal("sg90 90deg duty", 725, pwm2.Duty);
        sg90.SetAngle(400);
        Check.Equal("sg90 angle clamped", 1200, pwm2.Duty);

        // Same pulse on a 330 Hz digital-servo frame: 1500 us x 330 / 100 = 4950.
        Check.Equal("pulse at 330 Hz", 4950, PulseWidth.ToPermyriad(1500, 330));
    }

    public static void RunEsc()
    {
        SimPwmOutput pwm = new SimPwmOutput(50);
        Esc esc = new Esc(pwm);
        esc.SetThrottle(0.5);
        Check.Equal("esc unarmed holds min", 500, pwm.Duty);

        List<int> sleeps = Expect.RecordSleeps();
        esc.Arm();
        Expect.StopRecordingSleeps();
        Check.True("esc armed", esc.Armed);
        Check.Equal("esc arm hold", 2000, Expect.Sum(sleeps));
        Check.Equal("esc arm pulse", 1000, esc.PulseUs);

        esc.SetThrottle(0.5);
        Check.Equal("esc half throttle duty", 750, pwm.Duty);
        esc.SetThrottle(0.25);
        Check.Equal("esc quarter throttle pulse", 1250, esc.PulseUs);
        esc.SetThrottle(1.0);
        Check.Equal("esc full throttle duty", 1000, pwm.Duty);
        esc.Stop();
        Check.Equal("esc stop duty", 500, pwm.Duty);

        SimPwmOutput pwm2 = new SimPwmOutput(50);
        Esc cal = new Esc(pwm2);
        sleeps = Expect.RecordSleeps();
        cal.Calibrate(3000, 2000);
        Expect.StopRecordingSleeps();
        Expect.Duties("esc calibration max then min", "1000,500", pwm2.History);
        Expect.Duties("esc calibration waits", "3000,2000", sleeps);

        esc.Disarm();
        Check.True("esc disarmed", !esc.Armed && pwm.Duty == 0);
    }
}
