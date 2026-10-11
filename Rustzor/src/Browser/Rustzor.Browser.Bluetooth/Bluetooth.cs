using Rustzor.Interop;

namespace Rustzor.Browser.Bluetooth;

/// <summary>
/// Web Bluetooth: pick a Bluetooth Low Energy device, connect over GATT, read,
/// write and subscribe to characteristics. Requesting a device needs a user gesture.
/// </summary>
public static class Ble
{
    public static async Task<bool> IsAvailableAsync() => (await Js.CallAsync("ble.available")).AsBool();

    /// <summary>
    /// Shows the device chooser. Filter by service UUIDs or short names such
    /// as "heart_rate" and "battery_service"; <paramref name="namePrefix"/> narrows it.
    /// </summary>
    public static async Task<BleDevice> RequestDeviceAsync(string[] services, string? namePrefix = null, string[]? optionalServices = null)
    {
        var v = await Js.CallAsync("ble.request", Js.Args("services", services, "prefix", namePrefix, "optional", optionalServices ?? Array.Empty<string>()));
        return new BleDevice(v["h"].AsInt(), v["name"].AsString(), v["id"].AsString());
    }
}

public sealed class BleDevice
{
    internal BleDevice(int handle, string name, string id)
    {
        Handle = handle;
        Name = name;
        Id = id;
    }

    public int Handle { get; }
    public string Name { get; }
    public string Id { get; }

    public Task ConnectAsync() => Js.CallAsync("ble.connect", Js.Args("h", Handle));

    public void Disconnect() => Js.Send("ble.disconnect", Js.Args("h", Handle));

    /// <summary>Reads a characteristic as bytes.</summary>
    public async Task<byte[]> ReadAsync(string service, string characteristic) =>
        Convert.FromBase64String((await Js.CallAsync("ble.read", Js.Args("h", Handle, "service", service, "ch", characteristic))).AsString());

    public Task WriteAsync(string service, string characteristic, byte[] value) =>
        Js.CallAsync("ble.write", Js.Args("h", Handle, "service", service, "ch", characteristic, "data", Convert.ToBase64String(value)));

    /// <summary>Subscribes to notifications; each value arrives as bytes.</summary>
    public async Task SubscribeAsync(string service, string characteristic, Action<byte[]> onValue)
    {
        var id = Callbacks.Register(v => onValue(Convert.FromBase64String(v.AsString())));
        await Js.CallAsync("ble.notify", Js.Args("h", Handle, "service", service, "ch", characteristic, "id", id));
    }

    /// <summary>A heart-rate measurement value (beats per minute) from its raw bytes.</summary>
    public static int ParseHeartRate(byte[] value) => value.Length == 0 ? 0 : (value[0] & 1) == 0 ? value[1] : value[1] | value[2] << 8;
}
