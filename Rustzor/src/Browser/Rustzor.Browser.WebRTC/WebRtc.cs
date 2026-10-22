using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.Browser.WebRTC;

/// <summary>
/// A peer-to-peer connection: audio, video and data channels.
/// </summary>
/// <remarks>
/// WebRTC needs a signalling path to exchange the offer, the answer and ICE
/// candidates; any transport works — a WebSocket server, or a
/// <c>BroadcastChannel</c> between two tabs. Wire <see cref="OnSignal"/> to
/// send, and <see cref="ReceiveSignalAsync"/> to receive.
/// Every connection has an audio and a video slot even when there is nothing
/// to send, so a peer without a camera still receives video and a track can be
/// swapped in later with <see cref="ReplaceTrackAsync"/> (screen sharing).
/// </remarks>
public sealed class PeerConnection : IDisposable
{
    private readonly int _handle;
    private readonly int _callback;
    private bool _disposed;

    public PeerConnection(params string[] stunServers)
    {
        _callback = Callbacks.Register(OnEvent);
        var servers = stunServers.Length > 0 ? stunServers : new[] { "stun:stun.l.google.com:19302" };
        _handle = Js.Call("rtc.create", Js.Args("id", _callback, "stun", servers)).AsInt();
    }

    /// <summary>A signalling message (JSON) to deliver to the other peer.</summary>
    public event Action<string>? OnSignal;

    /// <summary>A remote media stream arrived; attach it to a &lt;video&gt;.</summary>
    public event Action<RemoteStream>? OnRemoteStream;

    /// <summary>A text message on the data channel.</summary>
    public event Action<string>? OnData;

    /// <summary>The data channel opened: <see cref="SendData"/> now delivers.</summary>
    public event Action? OnDataOpen;

    /// <summary>"new", "connecting", "connected", "disconnected", "failed", "closed".</summary>
    public event Action<string>? OnStateChange;

    public string State { get; private set; } = "new";

    /// <summary>Whether the data channel is open.</summary>
    public bool DataOpen { get; private set; }

    /// <summary>The stream the other peer sends, once it arrived.</summary>
    public RemoteStream? Remote { get; private set; }

    private void OnEvent(JsonValue e)
    {
        switch (e["type"].AsString())
        {
            case "signal": OnSignal?.Invoke(e["data"].AsString()); break;
            case "stream":
                Remote = new RemoteStream(e["h"].AsInt());
                OnRemoteStream?.Invoke(Remote);
                break;
            case "data": OnData?.Invoke(e["data"].AsString()); break;
            case "open":
                DataOpen = true;
                OnDataOpen?.Invoke();
                break;
            case "closed": DataOpen = false; break;
            case "state":
                State = e["state"].AsString();
                OnStateChange?.Invoke(State);
                break;
        }
    }

    /// <summary>Sends a local stream (from Rustzor.Browser.Media) by its handle.</summary>
    public void AddStream(int mediaStreamHandle) => Js.Send("rtc.addStream", Js.Args("h", _handle, "stream", mediaStreamHandle));

    /// <summary>The caller side: creates the data channel and an offer, sent through <see cref="OnSignal"/>.</summary>
    public Task CallAsync() => Js.CallAsync("rtc.call", Js.Args("h", _handle));

    /// <summary>Feeds a signalling message from the other peer.</summary>
    public Task ReceiveSignalAsync(string signal) => Js.CallAsync("rtc.signal", Js.Args("h", _handle, "data", signal));

    /// <summary>
    /// Sends another stream's <paramref name="kind"/> track ("audio" or "video")
    /// in place of the current one, without renegotiating; a handle of 0 sends nothing.
    /// </summary>
    public Task ReplaceTrackAsync(string kind, int mediaStreamHandle) =>
        Js.CallAsync("rtc.replace", Js.Args("h", _handle, "kind", kind, "stream", mediaStreamHandle));

    public void SendData(string text) => Js.Send("rtc.send", Js.Args("h", _handle, "data", text));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Remote is not null) Js.Send("rtc.release", Js.Args("h", Remote.Handle));
        Js.Send("rtc.close", Js.Args("h", _handle));
        Callbacks.Unregister(_callback);
    }
}

/// <summary>A stream received from a peer.</summary>
public sealed class RemoteStream
{
    internal RemoteStream(int handle) => Handle = handle;

    /// <summary>The handle other libraries take (Rustzor.Browser.Media's recorder and mixer).</summary>
    public int Handle { get; }

    /// <summary>Plays the stream in a &lt;video&gt; (or &lt;audio&gt;); attaching again to the same element is cheap.</summary>
    public void AttachTo(ElementReference video, bool muted = false) => Js.Send("rtc.attach", Js.Args("h", Handle, "el", video.Id, "muted", muted));

    /// <summary>Loudness of the received audio, 0..1.</summary>
    public double Level() => Js.Call("rtc.level", Js.Args("h", Handle)).AsDouble();

    /// <summary>The decoded size of the video in <paramref name="video"/>; 0×0 until frames arrive.</summary>
    public static (int Width, int Height) VideoSize(ElementReference video)
    {
        var s = Js.Call("rtc.videoSize", Js.Args("el", video.Id));
        return (s["w"].AsInt(), s["h"].AsInt());
    }
}
