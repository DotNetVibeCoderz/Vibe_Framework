using RustNet.Core;

namespace RustNet.Net;

/// <summary>Wired Ethernet interface (DHCP by default).</summary>
public static class Ethernet
{
    [InternalCall]
    public static bool Up() => throw new RuntimeOnlyException();

    /// <summary>Bring up with a static address instead of DHCP.</summary>
    [InternalCall]
    public static bool Up(string staticIp, string gateway) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Down() => throw new RuntimeOnlyException();

    [InternalCall]
    public static string GetIp() => throw new RuntimeOnlyException();

    [InternalCall]
    public static bool IsUp() => throw new RuntimeOnlyException();
}

/// <summary>Point-to-point link over a serial modem (PPP).</summary>
public static class Ppp
{
    [InternalCall]
    public static bool Up(int uartPort, string username, string password) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Down() => throw new RuntimeOnlyException();

    [InternalCall]
    public static string GetIp() => throw new RuntimeOnlyException();

    [InternalCall]
    public static bool IsUp() => throw new RuntimeOnlyException();
}

/// <summary>LTE/NB-IoT cellular modem.</summary>
public static class Cellular
{
    [InternalCall]
    public static bool Up(string apn, string username, string password) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Down() => throw new RuntimeOnlyException();

    [InternalCall]
    public static string GetIp() => throw new RuntimeOnlyException();

    [InternalCall]
    public static bool IsUp() => throw new RuntimeOnlyException();

    [InternalCall]
    public static string GetOperator() => throw new RuntimeOnlyException();

    /// <summary>Signal strength in dBm (negative; closer to 0 is better).</summary>
    [InternalCall]
    public static int GetRssi() => throw new RuntimeOnlyException();
}

public static class Wifi
{
    /// <summary>Connect to an access point. Returns true when associated.</summary>
    [InternalCall]
    public static bool Connect(string ssid, string psk) => throw new RuntimeOnlyException();

    [InternalCall]
    public static bool IsConnected() => throw new RuntimeOnlyException();

    /// <summary>
    /// The network actually associated, as reported by the interface — not an
    /// echo of what <see cref="Connect"/> was given. On targets where the
    /// firmware joins the radio at boot (ESP32), this is the only way for an
    /// app to learn the real SSID. Empty when not associated.
    /// </summary>
    [InternalCall]
    public static string GetSsid() => throw new RuntimeOnlyException();

    /// <summary>
    /// Current IPv4 address as "a.b.c.d", empty while unassigned. Throws if
    /// the board exposes no WiFi interface.
    /// </summary>
    [InternalCall]
    public static string GetIp() => throw new RuntimeOnlyException();

    /// <summary>
    /// Leave the current network. Harmless when not associated.
    /// </summary>
    /// <remarks>
    /// Not just the inverse of <see cref="Connect"/>: on battery-powered
    /// boards an associated radio is the largest continuous draw there is, so
    /// an app that wakes, reports and sleeps wants to put it down explicitly
    /// rather than wait for the association to lapse.
    /// </remarks>
    [InternalCall]
    public static void Disconnect() => throw new RuntimeOnlyException();
}

/// <summary>MQTT 3.1.1 client (single connection per app).</summary>
public static class Mqtt
{
    /// <summary>Connect to host:port with a client id.</summary>
    [InternalCall]
    public static bool Connect(string address, string clientId) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Publish(string topic, string payload, int qos) => throw new RuntimeOnlyException();

    /// <summary>Connect with MQTT username/password (cloud IoT auth).
    /// Empty strings mean "omit that field".</summary>
    [InternalCall]
    public static bool ConnectAuth(string address, string clientId, string username, string password)
        => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Subscribe(string topic) => throw new RuntimeOnlyException();

    /// <summary>Block until a message arrives; returns "topic\npayload".</summary>
    [InternalCall]
    public static string Poll() => throw new RuntimeOnlyException();
}

public static class Http
{
    /// <summary>GET http://address/path and return the body as text.</summary>
    [InternalCall]
    public static string Get(string address, string path) => throw new RuntimeOnlyException();
}
