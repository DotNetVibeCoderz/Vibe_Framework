using RustNet.Core;

namespace RustNet.Usb;

/// <summary>
/// USB host role: enumerate an attached USB device and exchange bulk data
/// with it (v0.8, chip-gated). The host matches a class driver (CDC/HID/MSC)
/// against the device descriptor. On the virtual device it talks to the
/// peripheral configured via <see cref="UsbClient"/> over the USB simulator.
/// </summary>
public static class UsbHost
{
    /// <summary>Enumerate the attached device. Returns
    /// <c>"vid:pid:class:product"</c> (hex ids; class one of
    /// cdc/hid/msc/vendor), or empty if nothing is attached/matched.</summary>
    [InternalCall]
    public static string Enumerate() => throw new RuntimeOnlyException();

    /// <summary>Send a bulk-OUT transfer to the attached device.</summary>
    [InternalCall]
    public static void BulkOut(byte[] data) => throw new RuntimeOnlyException();

    /// <summary>Read a bulk-IN transfer from the attached device.</summary>
    [InternalCall]
    public static byte[] BulkIn() => throw new RuntimeOnlyException();
}
