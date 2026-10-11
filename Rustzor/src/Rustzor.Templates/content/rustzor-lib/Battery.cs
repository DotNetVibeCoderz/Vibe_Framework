using Rustzor.Interop;

namespace RustzorLibrary;

/// <summary>The battery level, through the browser's Battery Status API (where there is one).</summary>
/// <remarks>Each method is one op implemented in rustzor.module.js.</remarks>
public static class Battery
{
    /// <summary>Whether this browser exposes the battery.</summary>
    public static bool IsSupported => Js.Call("battery.supported").AsBool();

    /// <summary>The charge from 0 to 1, or -1 when the browser does not tell.</summary>
    public static async Task<double> LevelAsync() => (await Js.CallAsync("battery.level")).AsDouble();

    /// <summary>Calls <paramref name="onChange"/> with the level whenever it changes, until disposed.</summary>
    public static IDisposable Watch(Action<double> onChange)
    {
        var id = Callbacks.Register(v => onChange(v["level"].AsDouble()));
        var handle = Js.Call("battery.watch", Js.Args("id", id)).AsInt();
        return new Subscription(() =>
        {
            Js.Send("battery.unwatch", Js.Args("h", handle));
            Callbacks.Unregister(id);
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
