using Rustzor.Browser.WebSocket;
using Rustzor.Interop;

namespace Rustzor.Game;

/// <summary>A message between players: who sent it, what kind it is, and its JSON payload.</summary>
public sealed record NetMessage(string From, string Type, JsonValue Data);

/// <summary>
/// A multiplayer session. Every message is a small JSON envelope
/// <c>{from, type, data}</c> broadcast to the room.
/// <list type="bullet">
/// <item><see cref="Local"/>: tabs of one browser, over BroadcastChannel — no server.</item>
/// <item><see cref="Connect"/>: a WebSocket relay that rebroadcasts each message to the room.</item>
/// </list>
/// Peers announce themselves on join and send heartbeats, so <see cref="Peers"/>
/// tracks who is present and when someone leaves.
/// </summary>
public sealed class NetSession : IDisposable
{
    private readonly BroadcastChannel? _channel;
    private readonly WebSocketClient? _socket;
    private readonly Dictionary<string, double> _lastSeen = new();
    private readonly Dictionary<string, Action<NetMessage>> _handlers = new();
    private IDisposable? _heartbeat;

    private NetSession(string room, BroadcastChannel? channel, WebSocketClient? socket)
    {
        Room = room;
        PlayerId = Guid.NewGuid().ToString("N")[..8];
        _channel = channel;
        _socket = socket;
        // between tabs, envelopes cross as objects: no escaping out, one parse in
        if (channel is not null) channel.OnJson += Receive;
        if (socket is not null) socket.OnMessage += Receive;
    }

    public string Room { get; }
    public string PlayerId { get; }
    /// <summary>Other players seen within the last few seconds.</summary>
    public IReadOnlyCollection<string> Peers => _lastSeen.Keys;

    public event Action<NetMessage>? OnMessage;
    public event Action<string>? OnPeerJoined;
    public event Action<string>? OnPeerLeft;

    /// <summary>Players in other tabs of this browser.</summary>
    public static NetSession Local(string room)
    {
        var s = new NetSession(room, new BroadcastChannel("rustzor-game:" + room), null);
        s.Begin();
        return s;
    }

    /// <summary>Players anywhere, through a relay at <paramref name="url"/> (ws:// or wss://).</summary>
    public static async Task<NetSession> Connect(string url, string room)
    {
        var socket = new WebSocketClient(url) { AutoReconnect = true };
        var s = new NetSession(room, null, socket);
        await socket.ConnectAsync();
        s.Begin();
        return s;
    }

    /// <summary>Calls <paramref name="handler"/> for messages of one type.</summary>
    public NetSession On(string type, Action<NetMessage> handler)
    {
        _handlers[type] = handler;
        return this;
    }

    public void Send(string type, object? data = null) => Post(Envelope(type).Name("data").Any(data));

    /// <summary>
    /// Sends a payload that is already JSON, such as a snapshot written with
    /// <see cref="JsonWriter"/>, without building or walking a value tree for it.
    /// </summary>
    public void SendJson(string type, string json) => Post(Envelope(type).Name("data").Raw(json));

    private JsonWriter Envelope(string type) => new JsonWriter()
        .BeginObject()
        .Prop("room", Room)
        .Prop("from", PlayerId)
        .Prop("type", type);

    private void Post(JsonWriter w)
    {
        var json = w.EndObject().ToString();
        _channel?.PostRaw(json);
        _socket?.Send(json);
    }

    private void Begin()
    {
        Send("$join");
        _heartbeat = Scheduler.SetInterval(() =>
        {
            Send("$beat");
            var now = Js.Now();
            foreach (var peer in _lastSeen.Where(kv => now - kv.Value > 5000).Select(kv => kv.Key).ToList())
            {
                _lastSeen.Remove(peer);
                OnPeerLeft?.Invoke(peer);
            }
        }, 1500);
    }

    private void Receive(string raw)
    {
        JsonValue v;
        try { v = JsonValue.Parse(raw); }
        catch { return; }
        Receive(v);
    }

    private void Receive(JsonValue v)
    {
        if (!v.IsObject || v["room"].AsString() != Room) return;
        var from = v["from"].AsString();
        if (from == PlayerId) return;
        var type = v["type"].AsString();
        var known = _lastSeen.ContainsKey(from);
        _lastSeen[from] = Js.Now();
        if (!known && type != "$leave")
        {
            OnPeerJoined?.Invoke(from);
            // Answer a newcomer so it learns about us without waiting for a heartbeat.
            if (type == "$join") Send("$beat");
        }
        switch (type)
        {
            case "$leave":
                _lastSeen.Remove(from);
                OnPeerLeft?.Invoke(from);
                return;
            case "$join" or "$beat":
                return;
        }
        var message = new NetMessage(from, type, v["data"]);
        if (_handlers.TryGetValue(type, out var handler)) handler(message);
        OnMessage?.Invoke(message);
    }

    public void Dispose()
    {
        Send("$leave");
        _heartbeat?.Dispose();
        _channel?.Dispose();
        _socket?.Dispose();
    }
}
