using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.Browser.Media;

/// <summary>A camera, microphone or screen stream.</summary>
public sealed class MediaStream : IDisposable
{
    internal MediaStream(int handle) => Handle = handle;

    /// <summary>The handle other libraries (WebRTC) take.</summary>
    public int Handle { get; private set; }

    /// <summary>Shows the stream in a &lt;video&gt; element.</summary>
    public void AttachTo(ElementReference video, bool muted = true) =>
        Js.Send("media.attach", Js.Args("h", Handle, "el", video.Id, "muted", muted));

    public void SetAudioEnabled(bool on) => Js.Send("media.tracks", Js.Args("h", Handle, "kind", "audio", "on", on));

    public void SetVideoEnabled(bool on) => Js.Send("media.tracks", Js.Args("h", Handle, "kind", "video", "on", on));

    /// <summary>Microphone loudness 0..1, for "speaking" indicators.</summary>
    public double Level() => Js.Call("media.level", Js.Args("h", Handle)).AsDouble();

    /// <summary>Stops every track: the camera light goes off.</summary>
    public void Dispose()
    {
        if (Handle == 0) return;
        Js.Send("media.stop", Js.Args("h", Handle));
        Handle = 0;
    }
}

public sealed record MediaDevice(string DeviceId, string Kind, string Label);

/// <summary>Camera, microphone and screen capture, devices, and recording.</summary>
public static class MediaDevices
{
    public static async Task<MediaStream> GetUserMediaAsync(bool video = true, bool audio = true, int width = 1280, int height = 720, string? videoDeviceId = null, string facingMode = "user") =>
        new((await Js.CallAsync("media.user", Js.Args("video", video, "audio", audio, "w", width, "h", height, "device", videoDeviceId, "facing", facingMode))).AsInt());

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

/// <summary>Records a stream to a downloadable file (webm).</summary>
public sealed class MediaRecorder
{
    private readonly int _handle;

    public MediaRecorder(MediaStream stream, string mimeType = "video/webm") =>
        _handle = Js.Call("media.recorder", Js.Args("h", stream.Handle, "type", mimeType)).AsInt();

    public void Start() => Js.Send("media.recStart", Js.Args("r", _handle));

    /// <summary>Stops and returns a blob URL of the recording, for a player or a download.</summary>
    public async Task<string> StopAsync() => (await Js.CallAsync("media.recStop", Js.Args("r", _handle))).AsString();
}
