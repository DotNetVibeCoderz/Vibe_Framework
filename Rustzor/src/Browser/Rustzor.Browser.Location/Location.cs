using Rustzor.Interop;

namespace Rustzor.Browser.Location;

/// <summary>A position fix from the device.</summary>
public sealed record GeoPosition(double Latitude, double Longitude, double Accuracy, double? Altitude, double? Heading, double? Speed, DateTime Timestamp);

/// <summary>The device location, with the user permission.</summary>
public static class Geolocation
{
    public static bool IsSupported => Js.Call("geo.supported").AsBool();

    public static async Task<GeoPosition> GetCurrentPositionAsync(bool highAccuracy = false, int timeoutMs = 15000) =>
        Read(await Js.CallAsync("geo.current", Js.Args("high", highAccuracy, "timeout", timeoutMs)));

    /// <summary>Reports every new fix until disposed. Errors arrive through <paramref name="onError"/>.</summary>
    public static IDisposable Watch(Action<GeoPosition> onPosition, Action<string>? onError = null, bool highAccuracy = true)
    {
        var id = Callbacks.Register(v =>
        {
            if (v.Has("error")) onError?.Invoke(v["error"].AsString());
            else onPosition(Read(v));
        });
        var watch = Js.Call("geo.watch", Js.Args("id", id, "high", highAccuracy)).AsInt();
        return new Stop(id, watch);
    }

    /// <summary>Great-circle distance in metres (haversine).</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        static double ToRad(double d) => d * Math.PI / 180;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static GeoPosition Read(JsonValue v) => new(
        v["lat"].AsDouble(), v["lon"].AsDouble(), v["acc"].AsDouble(),
        v["alt"].IsNull ? null : v["alt"].AsDouble(),
        v["heading"].IsNull ? null : v["heading"].AsDouble(),
        v["speed"].IsNull ? null : v["speed"].AsDouble(),
        new DateTime(1970, 1, 1).AddMilliseconds(v["ts"].AsDouble()));

    private sealed class Stop : IDisposable
    {
        private readonly int _id;
        private int _watch;

        public Stop(int id, int watch)
        {
            _id = id;
            _watch = watch;
        }

        public void Dispose()
        {
            if (_watch < 0) return;
            Js.Send("geo.clear", Js.Args("watch", _watch));
            Callbacks.Unregister(_id);
            _watch = -1;
        }
    }
}
