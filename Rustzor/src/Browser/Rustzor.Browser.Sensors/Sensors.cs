using Rustzor.Interop;

namespace Rustzor.Browser.Sensors;

public readonly record struct Orientation(double Alpha, double Beta, double Gamma, bool Absolute);
public readonly record struct Motion(double X, double Y, double Z, double IncludingGravityX, double IncludingGravityY, double IncludingGravityZ, double IntervalMs);
public readonly record struct Reading3(double X, double Y, double Z);
public sealed record BatteryState(double Level, bool Charging, double ChargingTimeSeconds, double DischargingTimeSeconds);

/// <summary>Device orientation and motion, the Generic Sensor API, battery and wake lock.</summary>
public static class DeviceSensors
{
    /// <summary>iOS asks for permission before motion and orientation events flow.</summary>
    public static async Task<bool> RequestPermissionAsync() => (await Js.CallAsync("sensors.permission")).AsBool();

    public static IDisposable OnOrientation(Action<Orientation> handler) =>
        Subscribe("sensors.orientation", v => handler(new Orientation(v["alpha"].AsDouble(), v["beta"].AsDouble(), v["gamma"].AsDouble(), v["absolute"].AsBool())));

    public static IDisposable OnMotion(Action<Motion> handler) => Subscribe("sensors.motion", v => handler(new Motion(
        v["x"].AsDouble(), v["y"].AsDouble(), v["z"].AsDouble(), v["gx"].AsDouble(), v["gy"].AsDouble(), v["gz"].AsDouble(), v["interval"].AsDouble())));

    /// <summary>
    /// A Generic Sensor: "Accelerometer", "Gyroscope", "Magnetometer",
    /// "LinearAccelerationSensor" or "GravitySensor", at <paramref name="frequency"/> Hz.
    /// </summary>
    public static IDisposable Start(string sensor, double frequency, Action<Reading3> handler, Action<string>? onError = null) =>
        Subscribe("sensors.generic", v =>
        {
            if (v.Has("error")) onError?.Invoke(v["error"].AsString());
            else handler(new Reading3(v["x"].AsDouble(), v["y"].AsDouble(), v["z"].AsDouble()));
        }, "sensor", sensor, "frequency", frequency);

    public static IDisposable OnAmbientLight(Action<double> lux, Action<string>? onError = null) =>
        Subscribe("sensors.light", v =>
        {
            if (v.Has("error")) onError?.Invoke(v["error"].AsString());
            else lux(v["lux"].AsDouble());
        });

    public static async Task<BatteryState?> GetBatteryAsync()
    {
        var b = await Js.CallAsync("sensors.battery");
        return b.IsNull ? null : new BatteryState(b["level"].AsDouble(), b["charging"].AsBool(), b["chargingTime"].AsDouble(), b["dischargingTime"].AsDouble());
    }

    /// <summary>Keeps the screen on (Wake Lock) until the returned handle is disposed.</summary>
    public static async Task<IDisposable> KeepScreenOnAsync()
    {
        var token = (await Js.CallAsync("sensors.wakeLock")).AsInt();
        return new Off(0, token);
    }

    private static IDisposable Subscribe(string op, Action<JsonValue> handler, params object?[] extra)
    {
        var id = Callbacks.Register(handler);
        var args = new object?[extra.Length + 2];
        args[0] = "id";
        args[1] = id;
        Array.Copy(extra, 0, args, 2, extra.Length);
        var token = Js.Call(op, Js.Args(args)).AsInt();
        return new Off(id, token);
    }

    private sealed class Off : IDisposable
    {
        private readonly int _id;
        private int _token;

        public Off(int id, int token)
        {
            _id = id;
            _token = token;
        }

        public void Dispose()
        {
            if (_token == 0) return;
            Js.Send("js.off", Js.Args("token", _token));
            if (_id != 0) Callbacks.Unregister(_id);
            _token = 0;
        }
    }
}
