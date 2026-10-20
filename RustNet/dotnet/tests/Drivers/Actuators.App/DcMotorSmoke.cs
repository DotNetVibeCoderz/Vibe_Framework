using RustNet.Community.Bus;
using RustNet.Community.DcMotor;

namespace DriversApp;

public static class DcMotorSmoke
{
    /// <summary>L298N truth table: forward IN1=H IN2=L, reverse IN1=L IN2=H, brake IN1=IN2=H with EN, coast EN=L.</summary>
    public static void RunHBridge()
    {
        SimDigitalOutput in1 = new SimDigitalOutput();
        SimDigitalOutput in2 = new SimDigitalOutput();
        SimPwmOutput en = new SimPwmOutput(1000);
        HBridgeMotor m = new HBridgeMotor(in1, in2, en);

        m.SetSpeed(0.75);
        Check.True("hbridge fwd in1", in1.State);
        Check.True("hbridge fwd in2", !in2.State);
        Check.Equal("hbridge fwd duty", 7500, en.Duty);

        m.SetSpeed(-0.3);
        Check.True("hbridge rev in1", !in1.State);
        Check.True("hbridge rev in2", in2.State);
        Check.Equal("hbridge rev duty", 3000, en.Duty);

        m.Brake();
        Check.True("hbridge brake pins", in1.State && in2.State);
        Check.Equal("hbridge brake enable", 10000, en.Duty);

        m.Coast();
        Check.Equal("hbridge coast enable", 0, en.Duty);
        Check.True("hbridge coast pins", !in1.State && !in2.State);

        m.SetSpeed(2.0);
        Check.Equal("hbridge clamp", 10000, en.Duty);

        m.Inverted = true;
        m.SetSpeed(0.5);
        Check.True("hbridge inverted", !in1.State && in2.State);
        Check.Equal("hbridge inverted duty", 5000, en.Duty);
    }

    /// <summary>DRV8833 table: forward fast decay IN1=PWM IN2=0; slow decay IN1=1 IN2=PWM inverted.</summary>
    public static void RunBidirectional()
    {
        SimPwmOutput a = new SimPwmOutput(20000);
        SimPwmOutput b = new SimPwmOutput(20000);
        BidirectionalDcMotor m = new BidirectionalDcMotor(a, b);

        m.SetSpeed(0.6);
        Check.Equal("bidir fast fwd in1", 6000, a.Duty);
        Check.Equal("bidir fast fwd in2", 0, b.Duty);

        m.SetSpeed(-0.25);
        Check.Equal("bidir fast rev in1", 0, a.Duty);
        Check.Equal("bidir fast rev in2", 2500, b.Duty);

        m.DecayMode = BidirectionalDcMotor.SlowDecay;
        m.SetSpeed(0.6);
        Check.Equal("bidir slow fwd in1", 10000, a.Duty);
        Check.Equal("bidir slow fwd in2", 4000, b.Duty);
        m.SetSpeed(-0.9);
        Check.Equal("bidir slow rev in1", 1000, a.Duty);
        Check.Equal("bidir slow rev in2", 10000, b.Duty);

        m.Brake();
        Check.True("bidir brake", a.Duty == 10000 && b.Duty == 10000);
        m.Coast();
        Check.True("bidir coast", a.Duty == 0 && b.Duty == 0);
    }

    public static void RunTb67h420ftg()
    {
        SimDigitalOutput ina1 = new SimDigitalOutput();
        SimDigitalOutput ina2 = new SimDigitalOutput();
        SimPwmOutput pwma = new SimPwmOutput(20000);
        SimDigitalOutput inb1 = new SimDigitalOutput();
        SimDigitalOutput inb2 = new SimDigitalOutput();
        SimPwmOutput pwmb = new SimPwmOutput(20000);
        SimDigitalOutput hb = new SimDigitalOutput();
        SimDigitalInput lo1 = new SimDigitalInput();
        SimDigitalInput lo2 = new SimDigitalInput();
        lo1.Level = true;
        lo2.Level = true;
        Tb67h420ftg d = new Tb67h420ftg(ina1, ina2, pwma, inb1, inb2, pwmb, hb, lo1, lo2);

        d.SetSpeed(Tb67h420ftg.ChannelA, 0.5);
        Check.True("tb67 A forward IN1=H IN2=L", ina1.State && !ina2.State);
        Check.Equal("tb67 A pwm", 5000, pwma.Duty);
        d.SetSpeed(Tb67h420ftg.ChannelB, -1.0);
        Check.True("tb67 B reverse IN1=L IN2=H", !inb1.State && inb2.State);
        Check.Equal("tb67 B pwm", 10000, pwmb.Duty);

        // OFF needs PWM high with both inputs low (PWM low would be a short brake).
        d.Coast(Tb67h420ftg.ChannelB);
        Check.True("tb67 B off", !inb1.State && !inb2.State && pwmb.Duty == 10000);
        d.Brake(Tb67h420ftg.ChannelA);
        Check.True("tb67 A brake", ina1.State && ina2.State && pwma.Duty == 10000);

        d.SetSingleBridgeMode(true);
        Check.True("tb67 hbmode", hb.State);

        // Full-step bipolar sequence A+B+ -> A-B+ -> A-B- -> A+B- -> A+B+.
        string seq = "";
        for (int i = 0; i < 4; i++)
        {
            d.Step(true);
            seq += (ina1.State ? "+" : "-") + (inb1.State ? "+" : "-") + " ";
        }
        Expect.Text("tb67 full-step sequence", "-+ -- +- ++ ", seq);
        Check.True("tb67 step drive complementary", ina1.State != ina2.State && inb1.State != inb2.State);
        d.Step(false);
        Check.True("tb67 step back to A+B-", ina1.State && !inb1.State);
        Check.Equal("tb67 step position", 3, d.StepPosition);

        Check.Equal("tb67 no fault", 0, d.FaultCode());
        lo1.Level = false;
        Check.Equal("tb67 LO1 fault", 1, d.FaultCode());
        lo2.Level = false;
        Check.Equal("tb67 LO1+LO2 fault", 3, d.FaultCode());
        Check.True("tb67 has fault", d.HasFault());
    }
}
