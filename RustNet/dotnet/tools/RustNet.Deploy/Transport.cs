using System.IO.Ports;
using System.Net.Sockets;

namespace RustNet.Deploy;

/// <summary>Byte pipe to a device: TCP for the virtual device, serial for hardware.</summary>
public interface IDeviceTransport : IDisposable
{
    void Write(ReadOnlySpan<byte> data);
    /// <summary>Read available bytes (blocks until at least one or timeout). Returns 0 on timeout.</summary>
    int Read(byte[] buffer, int timeoutMs);
}

public sealed class TcpTransport : IDeviceTransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public TcpTransport(string host, int port)
    {
        _client = new TcpClient();
        _client.Connect(host, port);
        _stream = _client.GetStream();
    }

    public void Write(ReadOnlySpan<byte> data) => _stream.Write(data);

    public int Read(byte[] buffer, int timeoutMs)
    {
        _stream.ReadTimeout = timeoutMs;
        try
        {
            return _stream.Read(buffer, 0, buffer.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}

public sealed class SerialTransport : IDeviceTransport
{
    private readonly SerialPort _port;

    public SerialTransport(string portName, int baud = 115200)
    {
        _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One);
        _port.Open();
    }

    public void Write(ReadOnlySpan<byte> data) => _port.BaseStream.Write(data);

    public int Read(byte[] buffer, int timeoutMs)
    {
        _port.ReadTimeout = timeoutMs;
        try
        {
            return _port.BaseStream.Read(buffer, 0, buffer.Length);
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    public void Dispose() => _port.Dispose();
}

/// <summary>
/// Parses "tcp:host:port", "tcp:port", or "serial:COM3[:baud]" device
/// specifiers. Default is the local virtual device.
/// </summary>
public static class TransportFactory
{
    public const string DefaultSpec = "tcp:127.0.0.1:7878";

    /// <summary>
    /// Opens a BLE GATT link to the device at the address in a `ble:&lt;addr&gt;`
    /// spec. A platform with a BLE host stack registers this; without it, BLE
    /// specs are rejected with a clear message (the wire framing works — only the
    /// radio binding is platform-specific).
    /// </summary>
    public static Func<string, IBlePacketLink>? BleLinkProvider { get; set; }

    public static IDeviceTransport Open(string? spec)
    {
        spec = string.IsNullOrWhiteSpace(spec) ? DefaultSpec : spec;
        string[] parts = spec.Split(':');
        return parts[0].ToLowerInvariant() switch
        {
            "tcp" when parts.Length == 3 => new TcpTransport(parts[1], int.Parse(parts[2])),
            "tcp" when parts.Length == 2 => new TcpTransport("127.0.0.1", int.Parse(parts[1])),
            "serial" when parts.Length >= 2 => new SerialTransport(
                parts[1], parts.Length > 2 ? int.Parse(parts[2]) : 115200),
            "ble" when parts.Length >= 2 => OpenBle(spec[(spec.IndexOf(':') + 1)..]),
            _ => throw new ArgumentException(
                $"bad device spec '{spec}' (use tcp:host:port, serial:COM3[:baud] or ble:<address>)"),
        };
    }

    private static IDeviceTransport OpenBle(string address)
    {
        if (BleLinkProvider is null)
        {
            throw new NotSupportedException(
                "BLE transport needs a platform BLE backend — set TransportFactory.BleLinkProvider");
        }
        return new BleTransport(BleLinkProvider(address));
    }
}
