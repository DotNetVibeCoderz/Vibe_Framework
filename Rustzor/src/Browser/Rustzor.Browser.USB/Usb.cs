using Rustzor.Interop;

namespace Rustzor.Browser.USB;

/// <summary>
/// WebUSB: vendor-specific USB devices (Chrome and Edge). Requesting a device
/// needs a user gesture.
/// </summary>
public sealed class UsbDevice
{
    private readonly int _handle;

    private UsbDevice(int handle, string product, string manufacturer, int vendorId, int productId)
    {
        _handle = handle;
        ProductName = product;
        ManufacturerName = manufacturer;
        VendorId = vendorId;
        ProductId = productId;
    }

    public string ProductName { get; }
    public string ManufacturerName { get; }
    public int VendorId { get; }
    public int ProductId { get; }

    public static bool IsSupported => Js.Call("usb.supported").AsBool();

    public static async Task<UsbDevice> RequestAsync(int vendorId = 0)
    {
        var v = await Js.CallAsync("usb.request", Js.Args("vendor", vendorId));
        return new UsbDevice(v["h"].AsInt(), v["product"].AsString(), v["maker"].AsString(), v["vendor"].AsInt(), v["productId"].AsInt());
    }

    /// <summary>Opens the device, selects configuration 1 and claims the interface.</summary>
    public Task OpenAsync(int interfaceNumber = 0) => Js.CallAsync("usb.open", Js.Args("h", _handle, "iface", interfaceNumber));

    public Task TransferOutAsync(int endpoint, byte[] data) =>
        Js.CallAsync("usb.out", Js.Args("h", _handle, "ep", endpoint, "data", Convert.ToBase64String(data)));

    public async Task<byte[]> TransferInAsync(int endpoint, int length) =>
        Convert.FromBase64String((await Js.CallAsync("usb.in", Js.Args("h", _handle, "ep", endpoint, "len", length))).AsString());

    /// <summary>A vendor control transfer to the device.</summary>
    public Task ControlOutAsync(int request, int value, int index, byte[]? data = null) =>
        Js.CallAsync("usb.control", Js.Args("h", _handle, "request", request, "value", value, "index", index, "data", data is null ? null : Convert.ToBase64String(data)));

    public Task CloseAsync() => Js.CallAsync("usb.close", Js.Args("h", _handle));
}
