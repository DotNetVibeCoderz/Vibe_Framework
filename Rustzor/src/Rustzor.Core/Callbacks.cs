namespace Rustzor.Interop;

/// <summary>
/// The way back in. JavaScript never holds a C# delegate; it holds a number,
/// and calls <see cref="Dispatch"/> or <see cref="Complete"/> with it.
/// </summary>
/// <remarks>
/// Keeping the delegates in a static dictionary is also what keeps them alive:
/// statics are collector roots, so a handler registered here cannot be
/// collected while the browser can still call it.
/// </remarks>
public static class Callbacks
{
    private static readonly Dictionary<int, Action<JsonValue>> Handlers = new();
    private static readonly Dictionary<int, TaskCompletionSource<JsonValue>> Waiting = new();
    private static int _next = 1;

    /// <summary>Fired when a callback throws; the default writes to the console.</summary>
    public static Action<Exception>? UnhandledException;

    public static int Register(Action<JsonValue> handler)
    {
        var id = _next++;
        Handlers[id] = handler;
        return id;
    }

    public static void Unregister(int id) => Handlers.Remove(id);

    internal static (int, Task<JsonValue>) Pending()
    {
        var id = _next++;
        var tcs = new TaskCompletionSource<JsonValue>();
        Waiting[id] = tcs;
        return (id, tcs.Task);
    }

    /// <summary>Called by rustzor.js: <c>"id|json"</c>.</summary>
    public static void Dispatch(string payload)
    {
        var bar = payload.IndexOf('|');
        var id = int.Parse(payload.Substring(0, bar));
        if (!Handlers.TryGetValue(id, out var handler)) return;
        try
        {
            handler(JsonValue.Parse(payload.Substring(bar + 1)));
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    /// <summary>Called by rustzor.js when a promise settles: <c>"id|1|json"</c> or <c>"id|0|message"</c>.</summary>
    public static void Complete(string payload)
    {
        var first = payload.IndexOf('|');
        var id = int.Parse(payload.Substring(0, first));
        var ok = payload[first + 1] == '1';
        var json = payload.Substring(first + 3);
        if (!Waiting.TryGetValue(id, out var tcs)) return;
        Waiting.Remove(id);
        try
        {
            if (ok) tcs.SetResult(JsonValue.Parse(json));
            else tcs.SetException(new JsException(JsonValue.Parse(json).AsString()));
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    internal static void Report(Exception e)
    {
        if (UnhandledException is not null) UnhandledException(e);
        else Js.Log(2, "Unhandled exception in a Rustzor callback: " + Describe(e));
    }

    /// <summary>
    /// Type, message and stack, for the browser console. Built from parts so it
    /// reads the same on every runtime, whatever its ToString override prints.
    /// </summary>
    public static string Describe(Exception e)
    {
        var text = e.GetType().FullName + ": " + e.Message;
        if (!string.IsNullOrEmpty(e.StackTrace)) text += "\n" + e.StackTrace;
        if (e.InnerException is { } inner) text += "\n---> " + Describe(inner);
        return text;
    }
}

/// <summary>Timers on the browser's event loop. <c>Task.Delay</c> is routed here.</summary>
public static class Scheduler
{
    /// <summary>What <c>Task.Delay(ms)</c> becomes in the browser.</summary>
    public static Task Delay(int milliseconds)
    {
        var tcs = new TaskCompletionSource<bool>();
        var id = 0;
        id = Callbacks.Register(_ =>
        {
            Callbacks.Unregister(id);
            tcs.SetResult(true);
        });
        Js.Send("timer.set", w => w.BeginObject().Prop("id", id).Prop("ms", milliseconds).Prop("repeat", false).EndObject());
        return tcs.Task;
    }

    /// <summary>Calls <paramref name="action"/> once after a delay. Returns a handle for <see cref="Clear"/>.</summary>
    public static int SetTimeout(Action action, int milliseconds)
    {
        var id = 0;
        id = Callbacks.Register(_ =>
        {
            Callbacks.Unregister(id);
            action();
        });
        return Js.Call("timer.set", w => w.BeginObject().Prop("id", id).Prop("ms", milliseconds).Prop("repeat", false).EndObject()).AsInt();
    }

    /// <summary>Calls <paramref name="action"/> every interval until disposed.</summary>
    public static IDisposable SetInterval(Action action, int milliseconds)
    {
        var id = Callbacks.Register(_ => action());
        var handle = Js.Call("timer.set", w => w.BeginObject().Prop("id", id).Prop("ms", milliseconds).Prop("repeat", true).EndObject()).AsInt();
        return new Interval(id, handle);
    }

    public static void Clear(int handle) => Js.Send("timer.clear", w => w.BeginObject().Prop("handle", handle).EndObject());

    /// <summary>Runs <paramref name="frame"/> on the next animation frame with its timestamp.</summary>
    public static void RequestAnimationFrame(Action<double> frame)
    {
        var id = 0;
        id = Callbacks.Register(t =>
        {
            Callbacks.Unregister(id);
            frame(t.AsDouble());
        });
        Js.Send("timer.raf", w => w.BeginObject().Prop("id", id).EndObject());
    }

    private sealed class Interval : IDisposable
    {
        private readonly int _id;
        private readonly int _handle;
        private bool _disposed;

        public Interval(int id, int handle)
        {
            _id = id;
            _handle = handle;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear(_handle);
            Callbacks.Unregister(_id);
        }
    }
}
