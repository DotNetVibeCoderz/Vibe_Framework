using Rustzor.Browser.Fetch;
using Rustzor.Browser.WebSocket;
using Rustzor.Interop;

namespace Rustzor.Net.SignalR;

public enum HubConnectionState { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>An error the hub returned for an invocation.</summary>
public sealed class HubException : Exception
{
    public HubException(string message) : base(message) { }
}

/// <summary>Builds a <see cref="HubConnection"/>, in the shape of Microsoft's client.</summary>
public sealed class HubConnectionBuilder
{
    private string _url = "";
    private bool _reconnect;
    private bool _skipNegotiation;

    /// <summary>The hub's URL: absolute, or relative to the page ("/hubs/chat").</summary>
    public HubConnectionBuilder WithUrl(string url, bool skipNegotiation = false)
    {
        _url = url;
        _skipNegotiation = skipNegotiation;
        return this;
    }

    /// <summary>Reconnect after an unexpected close, waiting 0, 2, 10 and 30 seconds between tries.</summary>
    public HubConnectionBuilder WithAutomaticReconnect()
    {
        _reconnect = true;
        return this;
    }

    public HubConnection Build() => new(_url, _reconnect, _skipNegotiation);
}

/// <summary>
/// A SignalR client over WebSockets with the JSON hub protocol: negotiate,
/// handshake, then invocations, completions, streams and pings, each message
/// a JSON object ended by the record separator 0x1E.
/// </summary>
/// <example>
/// <code>
/// var hub = new HubConnectionBuilder().WithUrl("/hubs/chat").WithAutomaticReconnect().Build();
/// hub.On&lt;string, string&gt;("Message", (who, text) => log.Add($"{who}: {text}"));
/// await hub.StartAsync();
/// await hub.SendAsync("Say", "Ayu", "Halo");
/// var sum = await hub.InvokeAsync&lt;int&gt;("Add", 2, 3);
/// </code>
/// </example>
public sealed class HubConnection : IAsyncDisposable
{
    private const char Separator = '\u001e';
    private static readonly int[] Backoff = { 0, 2000, 10000, 30000 };

    private readonly string _url;
    private readonly bool _reconnect;
    private readonly bool _skipNegotiation;
    private readonly Dictionary<string, List<Action<JsonValue[]>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<JsonValue>> _pending = new();
    private readonly Dictionary<string, Action<JsonValue>> _streams = new();
    private WebSocketClient? _socket;
    private TaskCompletionSource<bool>? _handshake;
    private IDisposable? _pinger;
    private int _nextId;
    private bool _stopping;
    private string _buffer = "";

    internal HubConnection(string url, bool reconnect, bool skipNegotiation)
    {
        _url = url;
        _reconnect = reconnect;
        _skipNegotiation = skipNegotiation;
    }

    public HubConnectionState State { get; private set; } = HubConnectionState.Disconnected;

    /// <summary>The id the server gave this connection when it negotiated.</summary>
    public string? ConnectionId { get; private set; }

    /// <summary>Every frame on the wire, as ("send" or "receive", JSON text). For logging and teaching.</summary>
    public event Action<string, string>? Frame;
    public event Action<Exception?>? Closed;
    public event Action? Reconnecting;
    public event Action? Reconnected;

    // ── lifecycle ────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        if (State == HubConnectionState.Connected) return;
        _stopping = false;
        State = HubConnectionState.Connecting;
        try
        {
            await ConnectAsync();
            State = HubConnectionState.Connected;
        }
        catch
        {
            State = HubConnectionState.Disconnected;
            throw;
        }
    }

    private async Task ConnectAsync()
    {
        var http = Absolute(_url);
        var token = "";
        if (!_skipNegotiation)
        {
            var negotiate = await new FetchClient().SendAsync(new FetchRequest(NegotiateUrl(http), "POST"));
            if (!negotiate.Ok) throw new HubException($"Negotiation failed: HTTP {negotiate.Status}");
            var body = negotiate.Json();
            ConnectionId = body["connectionId"].AsStringOrNull();
            token = body["connectionToken"].AsStringOrNull() ?? ConnectionId ?? "";
        }
        var ws = "ws" + http.Substring(4);
        if (token.Length > 0) ws = AddQuery(ws, "id=" + Uri.EscapeDataString(token));

        _buffer = "";
        _socket?.Dispose();
        _socket = new WebSocketClient(ws);
        _socket.OnMessage += Receive;
        _socket.OnClose += (code, reason) => _ = OnSocketClosed(code, reason);
        await _socket.ConnectAsync();

        _handshake = new TaskCompletionSource<bool>();
        Write("{\"protocol\":\"json\",\"version\":1}");
        await _handshake.Task;
        _pinger?.Dispose();
        _pinger = Scheduler.SetInterval(() => { if (State == HubConnectionState.Connected) Write("{\"type\":6}"); }, 15_000);
    }

    public Task StopAsync()
    {
        _stopping = true;
        _pinger?.Dispose();
        if (_socket is { State: "open" }) Write("{\"type\":7}");
        _socket?.Close();
        State = HubConnectionState.Disconnected;
        FailPending(new HubException("The connection was stopped."));
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _socket?.Dispose();
    }

    private async Task OnSocketClosed(int code, string reason)
    {
        _pinger?.Dispose();
        if (_stopping || State == HubConnectionState.Disconnected) return;
        var error = new HubException($"The connection closed ({code}{(reason.Length > 0 ? ", " + reason : "")}).");
        FailPending(error);
        if (!_reconnect)
        {
            State = HubConnectionState.Disconnected;
            Closed?.Invoke(error);
            return;
        }
        State = HubConnectionState.Reconnecting;
        Reconnecting?.Invoke();
        foreach (var wait in Backoff)
        {
            if (wait > 0) await Task.Delay(wait);
            if (_stopping) return;
            try
            {
                await ConnectAsync();
                State = HubConnectionState.Connected;
                Reconnected?.Invoke();
                return;
            }
            catch
            {
                // Try again after the next wait.
            }
        }
        State = HubConnectionState.Disconnected;
        Closed?.Invoke(error);
    }

    // ── calling the hub ──────────────────────────────────────────────────

    /// <summary>Calls a hub method without waiting for it to finish.</summary>
    public Task SendAsync(string method, params object?[] args)
    {
        EnsureConnected();
        Write(Invocation(1, null, method, args));
        return Task.CompletedTask;
    }

    /// <summary>Calls a hub method and waits for it to complete.</summary>
    public async Task InvokeAsync(string method, params object?[] args) => await Call(method, args);

    /// <summary>Calls a hub method and returns its result.</summary>
    public async Task<T> InvokeAsync<T>(string method, params object?[] args) => Convert<T>(await Call(method, args));

    /// <summary>
    /// Calls a streaming hub method (one returning <c>IAsyncEnumerable</c> or a
    /// channel); <paramref name="onItem"/> runs for each item, and the task ends
    /// with the stream.
    /// </summary>
    public async Task StreamAsync<T>(string method, Action<T> onItem, params object?[] args)
    {
        EnsureConnected();
        var id = (++_nextId).ToString();
        var done = new TaskCompletionSource<JsonValue>();
        _pending[id] = done;
        _streams[id] = item => onItem(Convert<T>(item));
        Write(Invocation(4, id, method, args));
        try
        {
            await done.Task;
        }
        finally
        {
            _streams.Remove(id);
        }
    }

    private Task<JsonValue> Call(string method, object?[] args)
    {
        EnsureConnected();
        var id = (++_nextId).ToString();
        var tcs = new TaskCompletionSource<JsonValue>();
        _pending[id] = tcs;
        Write(Invocation(1, id, method, args));
        return tcs.Task;
    }

    private void EnsureConnected()
    {
        if (State != HubConnectionState.Connected) throw new InvalidOperationException($"The hub connection is {State}; start it first.");
    }

    private static string Invocation(int type, string? id, string method, object?[] args)
    {
        var w = new JsonWriter();
        w.BeginObject();
        w.Prop("type", type);
        if (id is not null) w.Prop("invocationId", id);
        w.Prop("target", method);
        w.Name("arguments").Raw(Json.Serialize(args));
        w.EndObject();
        return w.ToString();
    }

    // ── handlers for calls from the hub ───────────────────────────────────

    /// <summary>Handles a method the hub calls on this client, with its raw arguments.</summary>
    public IDisposable On(string method, Action<JsonValue[]> handler)
    {
        if (!_handlers.TryGetValue(method, out var list)) _handlers[method] = list = new List<Action<JsonValue[]>>();
        list.Add(handler);
        return new Subscription(() => list.Remove(handler));
    }

    public IDisposable On(string method, Action handler) => On(method, _ => handler());

    public IDisposable On<T1>(string method, Action<T1> handler) =>
        On(method, a => handler(Convert<T1>(Arg(a, 0))));

    public IDisposable On<T1, T2>(string method, Action<T1, T2> handler) =>
        On(method, a => handler(Convert<T1>(Arg(a, 0)), Convert<T2>(Arg(a, 1))));

    public IDisposable On<T1, T2, T3>(string method, Action<T1, T2, T3> handler) =>
        On(method, a => handler(Convert<T1>(Arg(a, 0)), Convert<T2>(Arg(a, 1)), Convert<T3>(Arg(a, 2))));

    private static JsonValue Arg(JsonValue[] args, int i) => i < args.Length ? args[i] : JsonValue.Null;

    private static T Convert<T>(JsonValue v) => v.IsNull && default(T) is null ? default! : (T)Json.FromJson(v, typeof(T))!;

    // ── the wire ─────────────────────────────────────────────────────────

    private void Write(string json)
    {
        Frame?.Invoke("send", json);
        _socket?.Send(json + Separator);
    }

    private void Receive(string data)
    {
        _buffer += data;
        int end;
        while ((end = _buffer.IndexOf(Separator)) >= 0)
        {
            var json = _buffer.Substring(0, end);
            _buffer = _buffer.Substring(end + 1);
            Frame?.Invoke("receive", json);
            Handle(JsonValue.Parse(json));
        }
    }

    private void Handle(JsonValue m)
    {
        if (_handshake is { Task.IsCompleted: false })
        {
            if (m.Has("error")) _handshake.TrySetException(new HubException(m["error"].AsString()));
            else _handshake.TrySetResult(true);
            return;
        }
        switch (m["type"].AsInt())
        {
            case 1:
                if (_handlers.TryGetValue(m["target"].AsString(), out var list))
                {
                    var args = m["arguments"].Items.ToArray();
                    foreach (var h in list.ToList()) h(args);
                }
                break;
            case 2:
                if (_streams.TryGetValue(m["invocationId"].AsString(), out var onItem)) onItem(m["item"]);
                break;
            case 3:
                var id = m["invocationId"].AsString();
                if (_pending.TryGetValue(id, out var tcs))
                {
                    _pending.Remove(id);
                    if (m.Has("error")) tcs.TrySetException(new HubException(m["error"].AsString()));
                    else tcs.TrySetResult(m["result"]);
                }
                break;
            case 7:
                var error = m.Has("error") ? new HubException(m["error"].AsString()) : null;
                _stopping = !m["allowReconnect"].AsBool();
                FailPending(error ?? new HubException("The server closed the connection."));
                if (_stopping)
                {
                    State = HubConnectionState.Disconnected;
                    Closed?.Invoke(error);
                }
                _socket?.Close();
                break;
        }
    }

    private void FailPending(Exception e)
    {
        foreach (var tcs in _pending.Values) tcs.TrySetException(e);
        _pending.Clear();
        _streams.Clear();
    }

    // ── URLs ─────────────────────────────────────────────────────────────

    private static string Absolute(string url)
    {
        if (url.StartsWith("http://") || url.StartsWith("https://")) return url;
        var origin = Js.Call("js.eval", Js.Args("code", "location.origin")).AsString();
        return origin + (url.StartsWith('/') ? url : "/" + url);
    }

    /// <summary>"/hubs/chat?room=a" negotiates at "/hubs/chat/negotiate?room=a&amp;negotiateVersion=1".</summary>
    private static string NegotiateUrl(string url)
    {
        var q = url.IndexOf('?');
        var path = q < 0 ? url : url.Substring(0, q);
        var query = q < 0 ? "" : url.Substring(q + 1) + "&";
        return path.TrimEnd('/') + "/negotiate?" + query + "negotiateVersion=1";
    }

    private static string AddQuery(string url, string query) => url + (url.Contains('?') ? "&" : "?") + query;

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
