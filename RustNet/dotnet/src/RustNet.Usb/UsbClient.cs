using RustNet.Core;

namespace RustNet.Usb;

/// <summary>
/// USB device (client) role: the board presents itself to a host/PC as a USB
/// peripheral (v0.8, chip-gated). <see cref="BeginCdc"/> exposes a CDC-ACM
/// virtual serial port — the standard way to talk to a PC over USB. On the
/// virtual device the endpoints loop through the USB simulator so the flow is
/// testable without hardware.
/// </summary>
public static class UsbClient
{
    /// <summary>Present a CDC-ACM (virtual serial) device with the given IDs
    /// and product name. Returns true once enumerable.</summary>
    [InternalCall]
    public static bool BeginCdc(int vendorId, int productId, string product)
        => throw new RuntimeOnlyException();

    /// <summary>Read bytes the host/PC has sent to this device.</summary>
    [InternalCall]
    public static byte[] Read() => throw new RuntimeOnlyException();

    /// <summary>Queue bytes for the host/PC to read from this device.</summary>
    [InternalCall]
    public static void Write(byte[] data) => throw new RuntimeOnlyException();
}
