using Rustzor.Interop;

namespace Rustzor.Browser.Workers;

/// <summary>
/// A Web Worker running a JavaScript module off the main thread, talking to C#
/// by messages.
/// </summary>
/// <remarks>
/// The worker is JavaScript: the C# runtime runs on the page thread. Hand a
/// worker what JavaScript does well in parallel (decoding, hashing, parsing a
/// large file) and receive the result as a message.
/// </remarks>
public sealed class WebWorker : IDisposable
{
    private readonly int _handle;
    private readonly int _callback;

    /// <summary>Starts a worker from a script URL.</summary>
    public WebWorker(string scriptUrl, bool module = true)
    {
        _callback = Callbacks.Register(OnEvent);
        _handle = Js.Call("worker.start", Js.Args("url", scriptUrl, "module", module, "id", _callback)).AsInt();
    }

    private WebWorker(int handle, int callback)
    {
        _handle = handle;
        _callback = callback;
    }

    /// <summary>
    /// Starts a worker from source text. The source sees <c>onmessage</c> and
    /// <c>postMessage</c> as any worker does.
    /// </summary>
    public static WebWorker FromSource(string javascript)
    {
        WebWorker? worker = null;
        var callback = Callbacks.Register(e => worker?.OnEvent(e));
        var handle = Js.Call("worker.source", Js.Args("code", javascript, "id", callback)).AsInt();
        worker = new WebWorker(handle, callback);
        return worker;
    }

    public event Action<JsonValue>? OnMessage;
    public event Action<string>? OnError;

    private void OnEvent(JsonValue e)
    {
        if (e["type"].AsString() == "error") OnError?.Invoke(e["message"].AsString());
        else OnMessage?.Invoke(e["data"]);
    }

    public void Post(object? message) => Js.Send("worker.post", Js.Args("h", _handle, "data", message));

    /// <summary>Posts and waits for the next message back.</summary>
    public Task<JsonValue> RequestAsync(object? message)
    {
        var tcs = new TaskCompletionSource<JsonValue>();
        void Once(JsonValue v)
        {
            OnMessage -= Once;
            tcs.TrySetResult(v);
        }
        OnMessage += Once;
        Post(message);
        return tcs.Task;
    }

    public void Dispose()
    {
        Js.Send("worker.stop", Js.Args("h", _handle));
        Callbacks.Unregister(_callback);
    }
}

/// <summary>Service worker registration, for offline apps (PWA) and push.</summary>
public static class ServiceWorkers
{
    public static bool IsSupported => Js.Call("sw.supported").AsBool();

    /// <summary>Registers a service worker; returns its scope.</summary>
    public static async Task<string> RegisterAsync(string scriptUrl = "service-worker.js", string scope = "./") =>
        (await Js.CallAsync("sw.register", Js.Args("url", scriptUrl, "scope", scope))).AsString();

    /// <summary>True when a newer version of the app is waiting to activate.</summary>
    public static async Task<bool> UpdateAvailableAsync() => (await Js.CallAsync("sw.waiting")).AsBool();

    /// <summary>Activates the waiting version and reloads the page.</summary>
    public static void ActivateUpdate() => Js.Send("sw.activate");

    /// <summary>Fires when the browser offers to install the app (PWA install prompt).</summary>
    public static IDisposable OnInstallAvailable(Action handler) => JsObject.OnWindow("rustzor:installable", _ => handler());

    /// <summary>Shows the PWA install prompt captured earlier.</summary>
    public static async Task<bool> PromptInstallAsync() => (await Js.CallAsync("sw.install")).AsBool();
}
