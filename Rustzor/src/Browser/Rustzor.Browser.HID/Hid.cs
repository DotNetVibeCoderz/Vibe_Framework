using Rustzor.Interop;

namespace Rustzor.Browser.HID;

/// <summary>
/// WebHID: game controllers, macro pads, barcode scanners and other HID
/// devices beyond keyboard and mouse (Chrome and Edge). Needs a user gesture.
/// </summary>
public sealed class HidDevice
{
    private readonly int _handle;

    private HidDevice(int handle, string name, int vendorId, int productId)
    {
        _handle = handle;
        ProductName = name;
        VendorId = vendorId;
        ProductId = productId;
    }

    public string ProductName { get; }
    public int VendorId { get; }
    public int ProductId { get; }

    public static bool IsSupported => Js.Call("hid.supported").AsBool();

    public static async Task<HidDevice?> RequestAsync(int vendorId = 0, int usagePage = 0)
    {
        var v = await Js.CallAsync("hid.request", Js.Args("vendor", vendorId, "usagePage", usagePage));
        return v.IsNull ? null : new HidDevice(v["h"].AsInt(), v["name"].AsString(), v["vendor"].AsInt(), v["product"].AsInt());
    }

    public Task OpenAsync() => Js.CallAsync("hid.open", Js.Args("h", _handle));

    /// <summary>Input reports: the report id and its bytes.</summary>
    public void OnInputReport(Action<int, byte[]> handler)
    {
        var id = Callbacks.Register(v => handler(v["report"].AsInt(), Convert.FromBase64String(v["data"].AsString())));
        Js.Send("hid.listen", Js.Args("h", _handle, "id", id));
    }

    public Task SendReportAsync(int reportId, byte[] data) =>
        Js.CallAsync("hid.send", Js.Args("h", _handle, "report", reportId, "data", Convert.ToBase64String(data)));

    public Task CloseAsync() => Js.CallAsync("hid.close", Js.Args("h", _handle));
}
