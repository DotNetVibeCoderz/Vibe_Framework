using Rustzor.Interop;

namespace Rustzor.Browser.Audio;

/// <summary>
/// Sound through the Web Audio API: synthesised tones and effects, decoded
/// clips, and music, all mixed through one master gain.
/// </summary>
/// <remarks>
/// Browsers start audio suspended until the user interacts with the page; call
/// <see cref="ResumeAsync"/> from a click handler (games do it on "Start").
/// </remarks>
public static class AudioEngine
{
    public static Task ResumeAsync() => Js.CallAsync("audio.resume");

    /// <summary>Overall volume, 0..1.</summary>
    public static void SetMasterVolume(double volume) => Js.Send("audio.master", Js.Args("volume", volume));

    /// <summary>
    /// Plays a synthesised tone. <paramref name="wave"/> is "sine", "square",
    /// "sawtooth" or "triangle"; attack and release are seconds.
    /// </summary>
    public static void Tone(double frequency, double seconds, string wave = "sine", double volume = 0.3, double attack = 0.01, double release = 0.1, double slideTo = 0) =>
        Js.Send("audio.tone", Js.Args("f", frequency, "d", seconds, "wave", wave, "v", volume, "a", attack, "r", release, "slide", slideTo));

    /// <summary>A burst of filtered noise: explosions, hits, wind.</summary>
    public static void Noise(double seconds, double volume = 0.3, double filterHz = 2000) =>
        Js.Send("audio.noise", Js.Args("d", seconds, "v", volume, "filter", filterHz));

    /// <summary>A quick chord or arpeggio of notes by MIDI number.</summary>
    public static void Notes(int[] midiNotes, double noteSeconds = 0.15, double gapSeconds = 0.05, string wave = "triangle", double volume = 0.2)
    {
        for (var i = 0; i < midiNotes.Length; i++)
            Js.Send("audio.tone", Js.Args("f", MidiToHz(midiNotes[i]), "d", noteSeconds, "wave", wave, "v", volume, "a", 0.01, "r", 0.08, "slide", 0, "at", i * (noteSeconds + gapSeconds)));
    }

    public static double MidiToHz(int note) => 440.0 * Math.Pow(2, (note - 69) / 12.0);

    /// <summary>Decodes a clip (wav, mp3, ogg) for low-latency playback.</summary>
    public static async Task<SoundClip> LoadAsync(string url) => new((await Js.CallAsync("audio.load", Js.Args("url", url))).AsInt());

    /// <summary>The current analyser spectrum (0..255 per band), for visualisers.</summary>
    public static byte[] Spectrum(int bands = 64)
    {
        var values = Js.Call("audio.spectrum", Js.Args("bands", bands));
        var result = new byte[values.Count];
        for (var i = 0; i < result.Length; i++) result[i] = (byte)values[i].AsInt();
        return result;
    }

    /// <summary>Speaks text with the browser voice (Speech Synthesis).</summary>
    public static void Speak(string text, string? lang = null, double rate = 1, double pitch = 1) =>
        Js.Send("audio.speak", Js.Args("text", text, "lang", lang, "rate", rate, "pitch", pitch));
}

/// <summary>A decoded sound.</summary>
public sealed class SoundClip
{
    internal SoundClip(int handle) => Handle = handle;

    public int Handle { get; }

    /// <summary>Plays the clip; returns a voice id for <see cref="Stop"/>.</summary>
    public int Play(double volume = 1, double rate = 1, bool loop = false, double pan = 0) =>
        Js.Call("audio.play", Js.Args("h", Handle, "v", volume, "rate", rate, "loop", loop, "pan", pan)).AsInt();

    public static void Stop(int voice) => Js.Send("audio.stop", Js.Args("voice", voice));
}

/// <summary>Background music from an &lt;audio&gt; element: streamed, not decoded up front.</summary>
public sealed class Music
{
    private readonly int _handle;

    public Music(string url, bool loop = true) => _handle = Js.Call("audio.music", Js.Args("url", url, "loop", loop)).AsInt();

    public Task PlayAsync() => Js.CallAsync("audio.musicPlay", Js.Args("h", _handle));

    public void Pause() => Js.Send("audio.musicPause", Js.Args("h", _handle));

    public double Volume
    {
        set => Js.Send("audio.musicVolume", Js.Args("h", _handle, "v", value));
    }
}
