using Rustzor.Interop;

namespace Rustzor.Browser.WebSocket;

/// <summary>A WebSocket connection with events on the C# side.</summary>
/// <example>
/// <code>
/// var ws = new WebSocketClient("wss://echo.example.org");
/// ws.OnMessage += text => messages.Add(text);
/// await ws.ConnectAsync();
/// ws.Send("hello");
/// </code>
/// </example>
public sealed class WebSocketClient : IDisposable
{
    private readonly string _url;
    private int _handle;
    private int _callback;
    private TaskCompletionSource<bool>? _opening;

    public WebSocketClient(string url) => _url = url;

    public event Action<string>? OnMessage;
    public event Action? OnOpen;
    public event Action<int, string>? OnClose;
    public event Action<string>? OnError;

    /// <summary>"connecting", "open", "closing" or "closed".</summary>
    public string State { get; private set; } = "closed";

    /// <summary>Reconnect automatically after an unexpected close, with backoff.</summary>
    public bool AutoReconnect { get; set; }

    public Task ConnectAsync()
    {
        _opening = new TaskCompletionSource<bool>();
        if (_callback == 0) _callback = Callbacks.Register(OnEvent);
        _handle = Js.Call("ws.open", Js.Args("url", _url, "id", _callback, "reconnect", AutoReconnect)).AsInt();
        State = "connecting";
        return _opening.Task;
    }

    private void OnEvent(JsonValue e)
    {
        switch (e["type"].AsString())
        {
            case "open":
                State = "open";
                _opening?.TrySetResult(true);
                OnOpen?.Invoke();
                break;
            case "message":
                OnMessage?.Invoke(e["data"].AsString());
                break;
            case "close":
                State = "closed";
                _opening?.TrySetException(new JsException("ws", "closed before opening"));
                OnClose?.Invoke(e["code"].AsInt(), e["reason"].AsString());
                break;
            case "error":
                OnError?.Invoke(e["message"].AsString());
                break;
        }
    }

    public void Send(string text) => Js.Send("ws.send", Js.Args("h", _handle, "data", text));

    /// <summary>Sends any value as JSON.</summary>
    public void SendJson(object? value) => Send(Json.Serialize(value));

    public void Close(int code = 1000, string reason = "") => Js.Send("ws.close", Js.Args("h", _handle, "code", code, "reason", reason));

    public void Dispose()
    {
        if (_handle != 0) Close();
        if (_callback != 0) Callbacks.Unregister(_callback);
        _handle = 0;
        _callback = 0;
    }
}

/// <summary>
/// Messages between tabs and windows of the same origin, with no server:
/// shared carts, cross-tab chat, multiplayer on one machine.
/// </summary>
public sealed class BroadcastChannel : IDisposable
{
    private readonly int _handle;
    private readonly int _callback;

    public BroadcastChannel(string name)
    {
        _callback = Callbacks.Register(Receive);
        _handle = Js.Call("bc.open", Js.Args("name", name, "id", _callback)).AsInt();
    }

    /// <summary>Every message, as text (a message posted with <see cref="PostRaw"/> arrives as its JSON).</summary>
    public event Action<string>? OnMessage;

    /// <summary>
    /// Messages posted with <see cref="PostRaw"/>, already parsed. They cross as objects
    /// (structured clone), so a receiver that subscribes here parses each message once.
    /// </summary>
    public event Action<JsonValue>? OnJson;

    public void Post(string message) => Js.Send("bc.post", Js.Args("h", _handle, "data", message));

    /// <summary>
    /// Posts a JSON object or array as an object rather than as text. Nothing is escaped
    /// on the way out or unescaped on the way in, which matters for messages sent many
    /// times a second, such as game snapshots. <paramref name="json"/> must be valid JSON.
    /// </summary>
    public void PostRaw(string json) => Js.Send("bc.post", new JsArgs("{\"h\":" + _handle + ",\"data\":" + json + "}"));

    private void Receive(JsonValue v)
    {
        if (v.IsString)
        {
            OnMessage?.Invoke(v.AsString());
            return;
        }
        OnJson?.Invoke(v);
        OnMessage?.Invoke(v.ToJson());
    }

    public void PostJson(object? value) => Post(Json.Serialize(value));

    public void Dispose()
    {
        Js.Send("bc.close", Js.Args("h", _handle));
        Callbacks.Unregister(_callback);
    }
}
