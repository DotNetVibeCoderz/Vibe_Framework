namespace Rustzor.Interop;

/// <summary>
/// A JavaScript object held by C#: a number naming an entry in rustzor.js's
/// handle table. Reaches anything in the browser by property path, which is
/// the escape hatch for APIs no typed <c>Rustzor.Browser.*</c> library covers.
/// </summary>
/// <example>
/// <code>
/// var nav = JsObject.Global("navigator");
/// string agent = nav.Get("userAgent").AsString();
/// </code>
/// </example>
public sealed class JsObject : IDisposable
{
    public int Handle { get; private set; }

    public JsObject(int handle) => Handle = handle;

    /// <summary>The object at a path from <c>globalThis</c>, e.g. <c>"navigator.gpu"</c>.</summary>
    public static JsObject? Global(string path) => FromWire(Js.Call("js.get", w => w.BeginObject().Prop("h", 0).Prop("path", path).EndObject()));

    /// <summary><c>new ctor(...args)</c>, e.g. <c>JsObject.New("AudioContext")</c>.</summary>
    public static JsObject? New(string ctor, params object?[] args) =>
        FromWire(Js.Call("js.new", w =>
        {
            w.BeginObject().Prop("ctor", ctor).Name("args");
            WriteArgs(w, args);
            w.EndObject();
        }));

    /// <summary>True when the browser has something at <paramref name="path"/>.</summary>
    public static bool Has(string path) => Js.Call("js.has", w => w.BeginObject().Prop("path", path).EndObject()).AsBool();

    /// <summary>Evaluates JavaScript. For prototyping; typed libraries are faster.</summary>
    public static JsonValue Eval(string code) => Js.Call("js.eval", w => w.BeginObject().Prop("code", code).EndObject());

    public JsonValue Get(string path) => Js.Call("js.get", w => w.BeginObject().Prop("h", Handle).Prop("path", path).EndObject());

    public JsObject? GetObject(string path) => FromWire(Get(path));

    public void Set(string path, object? value) => Js.Send("js.set", w =>
    {
        w.BeginObject().Prop("h", Handle).Prop("path", path).Name("value");
        WriteArg(w, value);
        w.EndObject();
    });

    public JsonValue Call(string method, params object?[] args) => Js.Call("js.call", w =>
    {
        w.BeginObject().Prop("h", Handle).Prop("path", method).Name("args");
        WriteArgs(w, args);
        w.EndObject();
    });

    public JsObject? CallObject(string method, params object?[] args) => FromWire(Call(method, args));

    /// <summary>Calls a method that returns a promise and awaits it.</summary>
    public Task<JsonValue> CallAsync(string method, params object?[] args) => Js.CallAsync("js.call", w =>
    {
        w.BeginObject().Prop("h", Handle).Prop("path", method).Name("args");
        WriteArgs(w, args);
        w.EndObject();
    });

    /// <summary>Subscribes to an event; dispose the result to unsubscribe.</summary>
    public IDisposable On(string eventName, Action<JsonValue> handler) => Subscribe(Handle, eventName, handler);

    /// <summary>Subscribes to an event on <c>window</c>.</summary>
    public static IDisposable OnWindow(string eventName, Action<JsonValue> handler) => Subscribe(0, eventName, handler);

    private static IDisposable Subscribe(int handle, string eventName, Action<JsonValue> handler)
    {
        var id = Callbacks.Register(handler);
        var token = Js.Call("js.on", w => w.BeginObject().Prop("h", handle).Prop("event", eventName).Prop("id", id).EndObject()).AsInt();
        return new Subscription(id, token);
    }

    public static JsObject? FromWire(JsonValue v) => v.IsObject && v.Has("$h") ? new JsObject(v["$h"].AsInt()) : null;

    internal static void WriteArgs(JsonWriter w, object?[] args)
    {
        w.BeginArray();
        foreach (var a in args) WriteArg(w, a);
        w.EndArray();
    }

    internal static void WriteArg(JsonWriter w, object? a)
    {
        if (a is JsObject o) w.BeginObject().Prop("$h", o.Handle).EndObject();
        else w.Any(a);
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        var h = Handle;
        Handle = 0;
        Js.Send("js.release", w => w.BeginObject().Prop("h", h).EndObject());
    }

    private sealed class Subscription : IDisposable
    {
        private readonly int _id;
        private int _token;

        public Subscription(int id, int token)
        {
            _id = id;
            _token = token;
        }

        public void Dispose()
        {
            if (_token == 0) return;
            var token = _token;
            _token = 0;
            Js.Send("js.off", w => w.BeginObject().Prop("token", token).EndObject());
            Callbacks.Unregister(_id);
        }
    }
}
