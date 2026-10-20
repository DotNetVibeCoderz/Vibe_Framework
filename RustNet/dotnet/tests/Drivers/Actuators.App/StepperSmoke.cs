using RustNet.Community.Bus;
using RustNet.Community.Stepper;

namespace DriversApp;

public static class StepperSmoke
{
    /// <summary>
    /// Trapezoid intervals computed independently (python):
    /// t_k = sqrt(2k/a), interval = t_(k+1) - t_k, never below 1/vmax.
    /// </summary>
    public static void RunProfile()
    {
        // vmax 1000 steps/s, a 50000 steps/s^2: ramp would be 10 steps, so a 10-step move is a triangle.
        int[] tri = StepProfile.IntervalsUs(10, 1000, 50000);
        Check.Equal("profile triangle length", 10, tri.Length);
        Check.Equal("profile tri 0", 6325, tri[0]);
        Check.Equal("profile tri 1", 2620, tri[1]);
        Check.Equal("profile tri 2", 2010, tri[2]);
        Check.Equal("profile tri 3", 1695, tri[3]);
        Check.Equal("profile tri 4", 1493, tri[4]);
        Check.Equal("profile tri 5", 1493, tri[5]);
        Check.Equal("profile tri 9", 6325, tri[9]);

        // vmax 400, a 40000: v^2/2a = 2 ramp steps, then cruise at 2500 us.
        Check.Equal("profile ramp steps", 2, StepProfile.AccelerationSteps(400, 40000));
        int[] trap = StepProfile.IntervalsUs(12, 400, 40000);
        Check.Equal("profile trap 0", 7071, trap[0]);
        Check.Equal("profile trap 1", 2929, trap[1]);
        Check.Equal("profile trap cruise", 2500, trap[2]);
        Check.Equal("profile trap cruise end", 2500, trap[9]);
        Check.Equal("profile trap decel", 2929, trap[10]);
        Check.Equal("profile trap last", 7071, trap[11]);

        int[] flat = StepProfile.IntervalsUs(-3, 500, 0);
        Check.True("profile constant speed", flat.Length == 3 && flat[0] == 2000 && flat[2] == 2000);
    }

    /// <summary>A4988 microstep table (MS1 MS2 MS3): full 000, half 100, quarter 010, eighth 110, sixteenth 111.</summary>
    public static void RunA4988()
    {
        SimDigitalOutput step = new SimDigitalOutput();
        SimDigitalOutput dir = new SimDigitalOutput();
        SimDigitalOutput en = new SimDigitalOutput();
        SimDigitalOutput ms1 = new SimDigitalOutput();
        SimDigitalOutput ms2 = new SimDigitalOutput();
        SimDigitalOutput ms3 = new SimDigitalOutput();
        A4988 a = new A4988(step, dir, en, ms1, ms2, ms3);

        Check.True("a4988 eighth", a.SetMicrosteps(8));
        Check.True("a4988 eighth pins 110", ms1.State && ms2.State && !ms3.State);
        a.SetMicrosteps(4);
        Check.True("a4988 quarter pins 010", !ms1.State && ms2.State && !ms3.State);
        a.SetMicrosteps(2);
        Check.True("a4988 half pins 100", ms1.State && !ms2.State && !ms3.State);
        a.SetMicrosteps(16);
        Check.True("a4988 sixteenth pins 111", ms1.State && ms2.State && ms3.State);
        Check.Equal("a4988 steps/rev at 1/16", 3200, a.StepsPerRevolution);
        a.SetMicrosteps(1);
        Check.True("a4988 full pins 000", !ms1.State && !ms2.State && !ms3.State);
        Check.True("a4988 rejects 1/3", !a.SetMicrosteps(3));

        a.Enable();
        Check.True("a4988 enable is active low", !en.State);

        a.MaxSpeed = 1000;
        a.Acceleration = 50000;
        List<int> sleeps = Expect.RecordSleeps();
        a.Move(10);
        Expect.StopRecordingSleeps();
        Check.Equal("a4988 position", 10, a.Position);
        Expect.Levels("a4988 step pulses", "10101010101010101010", step.History);
        Expect.Levels("a4988 dir forward once", "1", dir.History);
        // Sum of the triangle intervals = 28286 us -> 28 whole ms slept, 286 us carried.
        Check.Equal("a4988 profile time", 28, Expect.Sum(sleeps));
        Check.Equal("a4988 last intervals", 6325, a.LastIntervalsUs![0]);

        a.MoveTo(4);
        Check.Equal("a4988 moveto position", 4, a.Position);
        Expect.Levels("a4988 dir reversed", "10", dir.History);
        Check.Equal("a4988 reverse pulses", 32, step.History.Count);

        a.Disable();
        Check.True("a4988 disable", en.State);
    }

    /// <summary>28BYJ-48 half-step table 1000 1100 0100 0110 0010 0011 0001 1001 (IN1..IN4).</summary>
    public static void RunUln2003()
    {
        SimDigitalOutput in1 = new SimDigitalOutput();
        SimDigitalOutput in2 = new SimDigitalOutput();
        SimDigitalOutput in3 = new SimDigitalOutput();
        SimDigitalOutput in4 = new SimDigitalOutput();
        Uln2003 m = new Uln2003(in1, in2, in3, in4);
        Check.Equal("uln2003 half steps/rev", 4096, m.StepsPerRevolution);

        string seq = "";
        for (int i = 0; i < 8; i++)
        {
            m.Step(true);
            seq += Pattern(in1, in2, in3, in4) + " ";
        }
        Expect.Text("uln2003 half-step sequence", "1100 0100 0110 0010 0011 0001 1001 1000 ", seq);
        // First step writes all four pins, every later half step flips exactly one coil.
        int writes = in1.History.Count + in2.History.Count + in3.History.Count + in4.History.Count;
        Check.Equal("uln2003 only changed pins written", 11, writes);

        m.Step(false);
        Expect.Text("uln2003 step back", "1001", Pattern(in1, in2, in3, in4));
        Check.Equal("uln2003 position", 7, m.Position);

        m.SetMode(Uln2003.FullStep);
        Check.Equal("uln2003 full steps/rev", 2048, m.StepsPerRevolution);
        seq = "";
        for (int i = 0; i < 4; i++)
        {
            m.Step(true);
            seq += Pattern(in1, in2, in3, in4) + " ";
        }
        Expect.Text("uln2003 full-step sequence", "0110 0011 1001 1100 ", seq);

        m.SetMode(Uln2003.WaveDrive);
        m.Step(true);
        Expect.Text("uln2003 wave drive", "0100", Pattern(in1, in2, in3, in4));

        m.SetMode(Uln2003.HalfStep);
        m.SetPosition(0);
        List<int> sleeps = Expect.RecordSleeps();
        m.RotateDegrees(90);
        Expect.StopRecordingSleeps();
        Check.Equal("uln2003 90 degrees = 1024 half steps", 1024, m.Position);
        // 500 steps/s, no ramp: 1024 x 2000 us = 2048 ms.
        Check.Equal("uln2003 move time", 2048, Expect.Sum(sleeps));

        m.Release();
        Expect.Text("uln2003 release", "0000", Pattern(in1, in2, in3, in4));
    }

    /// <summary>Arduino Stepper 4-wire sequence 1010 0110 0101 1001.</summary>
    public static void RunGpioStepper()
    {
        SimDigitalOutput p1 = new SimDigitalOutput();
        SimDigitalOutput p2 = new SimDigitalOutput();
        SimDigitalOutput p3 = new SimDigitalOutput();
        SimDigitalOutput p4 = new SimDigitalOutput();
        GpioStepper m = new GpioStepper(p1, p2, p3, p4, 200, GpioStepper.BipolarFullStepSequence);
        m.Hold();
        Expect.Text("gpiostepper hold phase 0", "1010", Pattern(p1, p2, p3, p4));
        string seq = "";
        for (int i = 0; i < 4; i++)
        {
            m.Step(true);
            seq += Pattern(p1, p2, p3, p4) + " ";
        }
        Expect.Text("gpiostepper sequence", "0110 0101 1001 1010 ", seq);
        m.Acceleration = 0;
        m.RotateDegrees(-18);
        Check.Equal("gpiostepper -18 degrees", -6, m.Position);
        Expect.Text("gpiostepper phase after -10", "0101", Pattern(p1, p2, p3, p4));
    }

    private static string Pattern(SimDigitalOutput a, SimDigitalOutput b, SimDigitalOutput c, SimDigitalOutput d)
        => (a.State ? "1" : "0") + (b.State ? "1" : "0") + (c.State ? "1" : "0") + (d.State ? "1" : "0");
}
