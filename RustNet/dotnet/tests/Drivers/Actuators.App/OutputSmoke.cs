using RustNet.Community.Bus;
using RustNet.Community.Piezo;
using RustNet.Community.Relay;

namespace DriversApp;

/// <summary>A PWM channel that also records the frequency of every non-zero duty (a tone).</summary>
public class TonePwm : SimPwmOutput
{
    public List<int> Tones = new List<int>();

    public TonePwm() : base(1000)
    {
    }

    public override void SetDuty(int permyriad)
    {
        base.SetDuty(permyriad);
        if (permyriad > 0)
        {
            Tones.Add(FrequencyHz);
        }
    }
}

public static class OutputSmoke
{
    public static void RunRelay()
    {
        SimDigitalOutput pin = new SimDigitalOutput();
        Relay low = new Relay(pin, true);
        low.On();
        Check.True("relay active-low on = low", !pin.State && low.IsOn);
        low.Off();
        Check.True("relay active-low off = high", pin.State);
        List<int> sleeps = Expect.RecordSleeps();
        low.Pulse(250);
        Expect.StopRecordingSleeps();
        Expect.Levels("relay pulse", "0101", pin.History);
        Expect.Duties("relay pulse length", "250", sleeps);

        SimDigitalOutput hi = new SimDigitalOutput();
        Relay high = new Relay(hi, false);
        high.Toggle();
        Check.True("relay active-high toggle on", hi.State && high.IsOn);
        high.Toggle();
        Check.True("relay active-high toggle off", !hi.State);
    }

    /// <summary>Equal temperament: f = 440 * 2^((n-69)/12); C4 261.626, C#5 554.365, Bb3 233.082 (python).</summary>
    public static void RunPiezo()
    {
        Check.Equal("note A4", 69, Notes.Parse("A4"));
        Check.Equal("note C4", 60, Notes.Parse("C4"));
        Check.Equal("note C#5", 73, Notes.Parse("C#5"));
        Check.Equal("note Bb3", 58, Notes.Parse("Bb3"));
        Check.Equal("note C-1", 0, Notes.Parse("C-1"));
        Check.Equal("note bad letter", -1, Notes.Parse("H4"));
        Check.Equal("note no octave", -1, Notes.Parse("C#"));
        Check.Near("freq A4", 440.0, Notes.Frequency("A4"), 0.0001);
        Check.Near("freq C4", 261.6256, Notes.Frequency("C4"), 0.001);
        Check.Near("freq C#5", 554.3653, Notes.Frequency("C#5"), 0.001);
        Check.Near("freq Bb3", 233.0819, Notes.Frequency("Bb3"), 0.001);
        Check.Near("freq A5 octave", 880.0, Notes.Frequency(81), 0.0001);

        TonePwm pwm = new TonePwm();
        PiezoSpeaker spk = new PiezoSpeaker(pwm);
        List<int> sleeps = Expect.RecordSleeps();
        int played = spk.PlayMelody("C4:4 E4:8  R:4 G4:2", 120);
        Expect.StopRecordingSleeps();
        Check.Equal("melody tokens", 4, played);
        // C4 262 Hz, E4 330 Hz, G4 392 Hz at 50 % duty.
        Expect.Duties("melody pitches", "262,330,392", pwm.Tones);
        // 120 bpm: quarter 500 ms, eighth 250, half 1000; 10 ms articulation gap.
        Expect.Duties("melody timing", "490,10,240,10,500,990,10", sleeps);
        Check.Equal("melody ends silent", 0, pwm.Duty);
        Check.Equal("tone volume", 5000, pwm.History[0]);
    }
}
