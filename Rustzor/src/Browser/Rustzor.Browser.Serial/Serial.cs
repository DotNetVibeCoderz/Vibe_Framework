using Rustzor.Interop;

namespace Rustzor.Browser.Serial;

/// <summary>
/// Web Serial: talk to microcontrollers, GPS modules and lab instruments over a
/// serial port (Chrome and Edge, desktop). Picking a port needs a user gesture.
/// </summary>
public sealed class SerialPort : IDisposable
{
    private readonly int _handle;
    private int _reader;

    private SerialPort(int handle) => _handle = handle;

    public static bool IsSupported => Js.Call("serial.supported").AsBool();

    /// <summary>Shows the port chooser, optionally narrowed to a USB vendor id.</summary>
    public static async Task<SerialPort> RequestAsync(int usbVendorId = 0) =>
        new((await Js.CallAsync("serial.request", Js.Args("vendor", usbVendorId))).AsInt());

    public Task OpenAsync(int baudRate = 115200) => Js.CallAsync("serial.open", Js.Args("h", _handle, "baud", baudRate));

    public Task WriteAsync(string text) => Js.CallAsync("serial.write", Js.Args("h", _handle, "text", text));

    public Task WriteLineAsync(string text) => WriteAsync(text + "\n");

    /// <summary>
    /// Starts reading; complete lines arrive through <paramref name="onLine"/>
    /// until the port closes.
    /// </summary>
    public void StartReading(Action<string> onLine)
    {
        _reader = Callbacks.Register(v => onLine(v.AsString()));
        Js.Send("serial.read", Js.Args("h", _handle, "id", _reader));
    }

    public Task CloseAsync() => Js.CallAsync("serial.close", Js.Args("h", _handle));

    public void Dispose()
    {
        _ = CloseAsync();
        if (_reader != 0) Callbacks.Unregister(_reader);
    }
}
