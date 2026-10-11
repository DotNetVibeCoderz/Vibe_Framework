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
/// </remarks>
public sealed class PeerConnection : IDisposable
{
    private readonly int _handle;
    private readonly int _callback;

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

    /// <summary>"new", "connecting", "connected", "disconnected", "failed", "closed".</summary>
    public event Action<string>? OnStateChange;

    public string State { get; private set; } = "new";

    private void OnEvent(JsonValue e)
    {
        switch (e["type"].AsString())
        {
            case "signal": OnSignal?.Invoke(e["data"].AsString()); break;
            case "stream": OnRemoteStream?.Invoke(new RemoteStream(e["h"].AsInt())); break;
            case "data": OnData?.Invoke(e["data"].AsString()); break;
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

    public void SendData(string text) => Js.Send("rtc.send", Js.Args("h", _handle, "data", text));

    public void Dispose()
    {
        Js.Send("rtc.close", Js.Args("h", _handle));
        Callbacks.Unregister(_callback);
    }
}

/// <summary>A stream received from a peer.</summary>
public sealed class RemoteStream
{
    internal RemoteStream(int handle) => Handle = handle;

    public int Handle { get; }

    public void AttachTo(ElementReference video) => Js.Send("rtc.attach", Js.Args("h", Handle, "el", video.Id));
}
