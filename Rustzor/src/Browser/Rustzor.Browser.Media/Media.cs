using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.Browser.Media;

/// <summary>A camera, microphone or screen stream.</summary>
public sealed class MediaStream : IDisposable
{
    private readonly bool _owned;
    private int _endedCallback;

    internal MediaStream(int handle, bool owned = true)
    {
        Handle = handle;
        _owned = owned;
    }

    /// <summary>The handle other libraries (WebRTC) take.</summary>
    public int Handle { get; private set; }

    /// <summary>Whether the stream has an audio track.</summary>
    public bool HasAudio => Handle != 0 && Js.Call("media.kinds", Js.Args("h", Handle))["audio"].AsBool();

    /// <summary>Whether the stream has a video track.</summary>
    public bool HasVideo => Handle != 0 && Js.Call("media.kinds", Js.Args("h", Handle))["video"].AsBool();

    /// <summary>Shows the stream in a &lt;video&gt; element.</summary>
    public void AttachTo(ElementReference video, bool muted = true) =>
        Js.Send("media.attach", Js.Args("h", Handle, "el", video.Id, "muted", muted));

    public void SetAudioEnabled(bool on) => Js.Send("media.tracks", Js.Args("h", Handle, "kind", "audio", "on", on));

    public void SetVideoEnabled(bool on) => Js.Send("media.tracks", Js.Args("h", Handle, "kind", "video", "on", on));

    /// <summary>Microphone loudness 0..1, for "speaking" indicators.</summary>
    public double Level() => Handle == 0 ? 0 : Js.Call("media.level", Js.Args("h", Handle)).AsDouble();

    /// <summary>
    /// Calls <paramref name="handler"/> once when the stream ends from outside:
    /// the user clicks the browser's "Stop sharing", or a camera is unplugged.
    /// </summary>
    public void OnEnded(Action handler)
    {
        if (_endedCallback != 0) Callbacks.Unregister(_endedCallback);
        _endedCallback = Callbacks.Register(_ => handler());
        Js.Send("media.onEnded", Js.Args("h", Handle, "id", _endedCallback));
    }

    /// <summary>Stops every track: the camera light goes off.</summary>
    public void Dispose()
    {
        if (_endedCallback != 0) Callbacks.Unregister(_endedCallback);
        _endedCallback = 0;
        if (Handle == 0 || !_owned) return;
        Js.Send("media.stop", Js.Args("h", Handle));
        Handle = 0;
    }
}

public sealed record MediaDevice(string DeviceId, string Kind, string Label);

/// <summary>Camera, microphone and screen capture, devices, and recording.</summary>
public static class MediaDevices
{
    /// <param name="audioDeviceId">A microphone from <see cref="EnumerateAsync"/>; the default one when null.</param>
    public static async Task<MediaStream> GetUserMediaAsync(bool video = true, bool audio = true, int width = 1280, int height = 720, string? videoDeviceId = null, string facingMode = "user", string? audioDeviceId = null) =>
        new((await Js.CallAsync("media.user", Js.Args("video", video, "audio", audio, "w", width, "h", height, "device", videoDeviceId, "facing", facingMode, "mic", audioDeviceId))).AsInt());

    /// <summary>Asks the user to pick a screen, window or tab to share.</summary>
    public static async Task<MediaStream> GetDisplayMediaAsync(bool audio = false) =>
        new((await Js.CallAsync("media.display", Js.Args("audio", audio))).AsInt());

    public static async Task<List<MediaDevice>> EnumerateAsync()
    {
        var list = new List<MediaDevice>();
        foreach (var d in (await Js.CallAsync("media.devices")).Items)
            list.Add(new MediaDevice(d["id"].AsString(), d["kind"].AsString(), d["label"].AsString()));
        return list;
    }
}

/// <summary>A finished recording: a blob URL for a player or a download link, and its size.</summary>
public sealed record Recording(string Url, long Bytes);

/// <summary>Records a stream to a downloadable file (webm).</summary>
public sealed class MediaRecorder
{
    private readonly int _handle;

    public MediaRecorder(MediaStream stream, string mimeType = "video/webm") : this(stream.Handle, mimeType) { }

    /// <summary>Records any stream by handle: a remote WebRTC stream, or a <see cref="MediaMixer"/>'s output.</summary>
    public MediaRecorder(int streamHandle, string mimeType = "video/webm") =>
        _handle = Js.Call("media.recorder", Js.Args("h", streamHandle, "type", mimeType)).AsInt();

    public void Start() => Js.Send("media.recStart", Js.Args("r", _handle));

    /// <summary>Stops and returns a blob URL of the recording, for a player or a download.</summary>
    public async Task<string> StopAsync() => (await FinishAsync()).Url;

    /// <summary>Stops and returns the recording with its size.</summary>
    public async Task<Recording> FinishAsync()
    {
        var r = await Js.CallAsync("media.recStop", Js.Args("r", _handle));
        return new Recording(r["url"].AsString(), (long)r["size"].AsDouble());
    }
}

/// <summary>One stream in a <see cref="MediaMixer"/>: its handle, a name label, and initials shown when there is no picture.</summary>
public sealed record MixSource(int Handle, string Label, string Initials, bool ShowVideo = true);

/// <summary>
/// Draws several streams into one picture (a grid with name labels) and mixes
/// their sound: record a whole call with <see cref="MediaRecorder"/>.
/// </summary>
public sealed class MediaMixer : IDisposable
{
    private int _handle;

    public MediaMixer(int width = 1280, int height = 720, int fps = 24)
    {
        _handle = Js.Call("media.mixCreate", Js.Args("w", width, "h", height, "fps", fps)).AsInt();
        Output = new MediaStream(Js.Call("media.mixStream", Js.Args("m", _handle)).AsInt(), owned: false);
    }

    /// <summary>The mixed stream; the mixer owns it.</summary>
    public MediaStream Output { get; }

    /// <summary>Sets what is drawn, in order, and the background and initials colours.</summary>
    public void SetSources(IEnumerable<MixSource> sources, string background = "#111111", string foreground = "#ffffff")
    {
        Js.Send("media.mixSet", w =>
        {
            w.BeginObject().Prop("m", _handle).Prop("bg", background).Prop("fg", foreground).Name("items").BeginArray();
            foreach (var s in sources)
                w.BeginObject().Prop("h", s.Handle).Prop("label", s.Label).Prop("initials", s.Initials).Prop("video", s.ShowVideo).EndObject();
            w.EndArray().EndObject();
        });
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        Js.Send("media.mixClose", Js.Args("m", _handle));
        _handle = 0;
    }
}
