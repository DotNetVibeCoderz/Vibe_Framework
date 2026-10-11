using System.Runtime.CompilerServices;

namespace Rustzor.Interop;

/// <summary>
/// The bridge to the browser. Every <c>Rustzor.Browser.*</c> library is built on
/// <see cref="Invoke(string, string)"/>: an operation name from rustzor.js's
/// table, one JSON argument, one JSON result.
/// </summary>
/// <remarks>
/// The four <c>extern</c> methods are implemented by the runtime
/// (<c>runtime/rustzor-wasm/src/bridge.rs</c>). Off the browser, for example in a
/// unit test on the desktop CLR, they are not bound and calling them throws; use
/// <see cref="IsBrowser"/> to tell.
/// </remarks>
public static class Js
{
    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern string? Invoke(string op, string json);

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void InvokeVoid(string op, string json);

    /// <summary><c>performance.now()</c>: milliseconds, sub-millisecond resolution.</summary>
    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern double Now();

    /// <summary>Writes to the browser console. 0 = log, 1 = warn, 2 = error.</summary>
    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void Log(int level, string text);

    [MethodImpl(MethodImplOptions.InternalCall)]
    private static extern bool IsBrowserNative();

    public static bool IsBrowser => IsBrowserNative();

    /// <summary>Calls an operation and parses its JSON result.</summary>
    public static JsonValue Call(string op, JsonValue? arg = null)
    {
        var reply = Invoke(op, arg is null ? "" : arg.ToJson());
        return Check(op, reply);
    }

    /// <summary>Calls an operation whose argument is built with a writer.</summary>
    public static JsonValue Call(string op, Action<JsonWriter> arg)
    {
        var w = new JsonWriter();
        arg(w);
        return Check(op, Invoke(op, w.ToString()));
    }

    public static void Send(string op, JsonValue? arg = null) => InvokeVoid(op, arg is null ? "" : arg.ToJson());

    public static void Send(string op, Action<JsonWriter> arg)
    {
        var w = new JsonWriter();
        arg(w);
        InvokeVoid(op, w.ToString());
    }

    /// <summary>
    /// Runs an operation that returns a promise. The task completes when the
    /// browser settles it, through <see cref="Callbacks.Complete"/>.
    /// </summary>
    public static Task<JsonValue> CallAsync(string op, JsonValue? arg = null)
    {
        var (id, task) = Callbacks.Pending();
        var w = new JsonWriter();
        w.BeginObject();
        w.Prop("id", id);
        w.Prop("op", op);
        w.Name("arg");
        if (arg is null) w.Null(); else w.Raw(arg.ToJson());
        w.EndObject();
        InvokeVoid("async", w.ToString());
        return task;
    }

    public static Task<JsonValue> CallAsync(string op, Action<JsonWriter> arg)
    {
        var w = new JsonWriter();
        arg(w);
        return CallAsync(op, JsonValue.Parse(w.ToString()));
    }

    /// <summary>A JSON object from name/value pairs: <c>Js.Args("key", k, "value", v)</c>.</summary>
    public static JsArgs Args(params object?[] pairs)
    {
        var w = new JsonWriter();
        w.BeginObject();
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            w.Name((string)pairs[i]!);
            JsObject.WriteArg(w, pairs[i + 1]);
        }
        w.EndObject();
        return new JsArgs(w.ToString());
    }

    public static JsonValue Call(string op, JsArgs args) => Check(op, Invoke(op, args.Json));

    public static void Send(string op, JsArgs args) => InvokeVoid(op, args.Json);

    public static Task<JsonValue> CallAsync(string op, JsArgs args) => CallAsync(op, JsonValue.Parse(args.Json));

    private static JsonValue Check(string op, string? reply)
    {
        if (reply is null) return JsonValue.Null;
        var value = JsonValue.Parse(reply);
        if (value.IsObject && value.Has("$error"))
            throw new JsException(op, value["$error"].AsString());
        return value;
    }
}

/// <summary>An operation's argument, already serialised.</summary>
public readonly struct JsArgs
{
    public JsArgs(string json) => Json = json;

    public string Json { get; }
}

/// <summary>A browser operation failed; the message is the JavaScript error's.</summary>
public sealed class JsException : Exception
{
    public string Operation { get; }

    public JsException(string operation, string message) : base(operation + ": " + message)
    {
        Operation = operation;
    }

    /// <summary>A failed async operation: the JS error text is the whole message.</summary>
    internal JsException(string message) : base(message)
    {
        Operation = "async";
    }
}
