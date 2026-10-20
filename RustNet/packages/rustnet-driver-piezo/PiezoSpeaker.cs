using RustNet.Community.Bus;

namespace RustNet.Community.Piezo;

/// <summary>
/// Equal-temperament note frequencies, computed rather than tabulated:
/// f = 440 Hz × 2^((n − 69) / 12) for MIDI note n (A4 = 69 = 440 Hz,
/// middle C = C4 = 60).
/// </summary>
public static class Notes
{
    public const double A4Hz = 440.0;

    public static double Frequency(int midiNote) => A4Hz * Math.Pow(2.0, (midiNote - 69) / 12.0);

    /// <summary>
    /// MIDI number of a note name: letter A–G, optional '#' or 'b', octave
    /// digit(s), e.g. "A4" = 69, "C#5" = 73, "Bb3" = 58. Returns -1 if the name
    /// does not parse.
    /// </summary>
    public static int Parse(string name)
    {
        if (name.Length < 2)
        {
            return -1;
        }
        char letter = name[0];
        if (letter >= 'a' && letter <= 'g')
        {
            letter = (char)(letter - 32);
        }
        int semitone;
        if (letter == 'C')
        {
            semitone = 0;
        }
        else if (letter == 'D')
        {
            semitone = 2;
        }
        else if (letter == 'E')
        {
            semitone = 4;
        }
        else if (letter == 'F')
        {
            semitone = 5;
        }
        else if (letter == 'G')
        {
            semitone = 7;
        }
        else if (letter == 'A')
        {
            semitone = 9;
        }
        else if (letter == 'B')
        {
            semitone = 11;
        }
        else
        {
            return -1;
        }
        int i = 1;
        if (name[i] == '#')
        {
            semitone++;
            i++;
        }
        else if (name[i] == 'b')
        {
            semitone--;
            i++;
        }
        if (i >= name.Length)
        {
            return -1;
        }
        bool negative = false;
        if (name[i] == '-')
        {
            negative = true;
            i++;
        }
        int octave = 0;
        int digits = 0;
        for (; i < name.Length; i++)
        {
            char c = name[i];
            if (c < '0' || c > '9')
            {
                return -1;
            }
            octave = octave * 10 + (c - '0');
            digits++;
        }
        if (digits == 0)
        {
            return -1;
        }
        if (negative)
        {
            octave = -octave;
        }
        return (octave + 1) * 12 + semitone;
    }

    /// <summary>Frequency of a note name, or 0 if it does not parse.</summary>
    public static double Frequency(string name)
    {
        int n = Parse(name);
        return n < 0 ? 0.0 : Frequency(n);
    }
}

/// <summary>
/// A passive piezo buzzer (or small speaker through a transistor) on a PWM
/// channel: the PWM frequency is the pitch, 50 % duty gives the loudest
/// square wave, 0 % is silence.
/// </summary>
public class PiezoSpeaker
{
    private readonly PwmOutput _pwm;

    /// <summary>Duty while sounding, permyriad (5000 = 50 %).</summary>
    public int Volume = 5000;

    /// <summary>Silence between consecutive melody notes, ms (articulation).</summary>
    public int NoteGapMs = 10;

    public PiezoSpeaker(int pwmChannel) : this(new PwmOutput(pwmChannel, 1000))
    {
    }

    public PiezoSpeaker(PwmOutput pwm)
    {
        _pwm = pwm;
    }

    /// <summary>Start a tone at <paramref name="hz"/> (rounded to whole Hz) until <see cref="Silence"/>.</summary>
    public void StartTone(double hz)
    {
        if (hz <= 0)
        {
            Silence();
            return;
        }
        _pwm.FrequencyHz = (int)(hz + 0.5);
        _pwm.SetDuty(Volume);
    }

    public void Silence() => _pwm.SetDuty(0);

    /// <summary>Play a tone for <paramref name="durationMs"/> (blocking), then silence.</summary>
    public void PlayTone(double hz, int durationMs)
    {
        StartTone(hz);
        Timing.SleepMs(durationMs);
        Silence();
    }

    public void PlayNote(string note, int durationMs) => PlayTone(Notes.Frequency(note), durationMs);

    /// <summary>
    /// Play a melody written as space-separated tokens <c>NOTE:DIV</c>, where
    /// NOTE is a note name ("C4", "F#5") or "R" for a rest and DIV the note
    /// value (1 whole, 2 half, 4 quarter, 8 eighth, 16 sixteenth; default 4).
    /// A quarter note lasts 60000/<paramref name="bpm"/> ms. Example:
    /// <c>"C4:4 E4:4 G4:4 C5:2 R:4"</c>. Returns the number of tokens played.
    /// </summary>
    public int PlayMelody(string melody, int bpm)
    {
        int quarterMs = 60000 / bpm;
        int played = 0;
        int i = 0;
        int n = melody.Length;
        while (i < n)
        {
            while (i < n && melody[i] == ' ')
            {
                i++;
            }
            if (i >= n)
            {
                break;
            }
            int start = i;
            while (i < n && melody[i] != ' ')
            {
                i++;
            }
            string token = melody.Substring(start, i - start);
            int colon = token.IndexOf(':');
            string note = colon < 0 ? token : token.Substring(0, colon);
            int div = 4;
            if (colon >= 0)
            {
                div = 0;
                for (int k = colon + 1; k < token.Length; k++)
                {
                    div = div * 10 + (token[k] - '0');
                }
                if (div <= 0)
                {
                    div = 4;
                }
            }
            int ms = quarterMs * 4 / div;
            if (note == "R" || note == "r")
            {
                Silence();
                Timing.SleepMs(ms);
            }
            else
            {
                PlayTone(Notes.Frequency(note), ms - NoteGapMs);
                Timing.SleepMs(NoteGapMs);
            }
            played++;
        }
        return played;
    }
}
