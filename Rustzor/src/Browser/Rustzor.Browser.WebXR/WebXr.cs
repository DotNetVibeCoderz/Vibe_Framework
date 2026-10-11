using Rustzor.Interop;

namespace Rustzor.Browser.WebXR;

/// <summary>Where the viewer is, each XR frame: position in metres, orientation as a quaternion.</summary>
public readonly record struct XrPose(double X, double Y, double Z, double Qx, double Qy, double Qz, double Qw, double Time);

/// <summary>
/// Virtual and augmented reality sessions. A session must start from a user
/// gesture. Each frame reports the viewer pose; draw with
/// <c>Rustzor.Browser.WebGL</c> on a context created with <c>xrCompatible</c>.
/// </summary>
public static class Xr
{
    /// <summary>"immersive-vr", "immersive-ar" or "inline".</summary>
    public static async Task<bool> IsSessionSupportedAsync(string mode) =>
        (await Js.CallAsync("xr.supported", Js.Args("mode", mode))).AsBool();

    public static async Task<XrSession> StartAsync(string mode, Action<XrPose> onFrame, params string[] optionalFeatures)
    {
        var id = Callbacks.Register(v => onFrame(new XrPose(
            v["x"].AsDouble(), v["y"].AsDouble(), v["z"].AsDouble(),
            v["qx"].AsDouble(), v["qy"].AsDouble(), v["qz"].AsDouble(), v["qw"].AsDouble(), v["t"].AsDouble())));
        var handle = (await Js.CallAsync("xr.start", Js.Args("mode", mode, "id", id, "features", optionalFeatures))).AsInt();
        return new XrSession(handle, id);
    }
}

public sealed class XrSession : IDisposable
{
    private readonly int _handle;
    private readonly int _callback;

    internal XrSession(int handle, int callback)
    {
        _handle = handle;
        _callback = callback;
    }

    public Task EndAsync() => Js.CallAsync("xr.end", Js.Args("h", _handle));

    public void Dispose()
    {
        _ = EndAsync();
        Callbacks.Unregister(_callback);
    }
}
