using System.Globalization;
using System.Reflection;

namespace RustNet.Deploy;

/// <summary>The GATT layout RustNet devices serve RNDP on (Nordic UART Service UUIDs).
/// Mirrors <c>runtime/firmware/src/ble.rs</c>.</summary>
public static class RustNetBle
{
    public static readonly Guid ServiceUuid = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    /// <summary>Host → device (write / write-without-response).</summary>
    public static readonly Guid RxCharacteristicUuid = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    /// <summary>Device → host (notify).</summary>
    public static readonly Guid TxCharacteristicUuid = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
    /// <summary>Every RustNet device advertises as <c>RustNet-XXXX</c>.</summary>
    public const string NamePrefix = "RustNet-";
}

/// <summary>One advertising RustNet device seen by a BLE scan.</summary>
public sealed record BleAdvertisement(ulong Address, string Name, short Rssi)
{
    public string AddressText => BleTarget.FormatAddress(Address);
}

/// <summary>
/// What a <c>ble:</c> device spec names: a 48-bit address
/// (<c>AA:BB:CC:DD:EE:FF</c>, <c>AA-BB-…</c> or 12 bare hex digits), or an
/// advertised name such as <c>RustNet-AB0F</c>. An empty spec or <c>*</c> means
/// "the first RustNet device found".
/// </summary>
public sealed record BleTarget(ulong? Address, string? Name)
{
    public static BleTarget Parse(string spec)
    {
        string s = spec.Trim();
        if (s.Length == 0 || s == "*")
        {
            return new BleTarget(null, null);
        }
        if (TryParseAddress(s, out ulong address))
        {
            return new BleTarget(address, null);
        }
        return new BleTarget(null, s);
    }

    public static bool TryParseAddress(string text, out ulong address)
    {
        address = 0;
        string hex;
        string[] parts = text.Split(':', '-');
        if (parts.Length == 6)
        {
            if (parts.Any(p => p.Length != 2))
            {
                return false;
            }
            hex = string.Concat(parts);
        }
        else if (parts.Length == 1 && text.Length == 12)
        {
            hex = text;
        }
        else
        {
            return false;
        }
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    public static string FormatAddress(ulong address)
        => string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    /// <summary>Does an advertisement with this name match the target?</summary>
    public bool MatchesName(string? advertisedName)
    {
        if (string.IsNullOrEmpty(advertisedName))
        {
            return false;
        }
        return Name is null
            ? advertisedName.StartsWith(RustNetBle.NamePrefix, StringComparison.Ordinal)
            : string.Equals(advertisedName, Name, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Address is { } a ? FormatAddress(a) : Name ?? "first RustNet-* device";
}

/// <summary>
/// Finds and registers the platform BLE backend, which lives outside this
/// cross-platform library because it needs OS projections (WinRT on Windows).
/// <para>
/// The backend is an ordinary assembly copied next to the tool
/// (<c>RustNet.Deploy.Ble.Windows.dll</c>) and is bound by reflection: a
/// <c>public static void Register()</c> on a known type, which sets
/// <see cref="TransportFactory.BleLinkProvider"/> and
/// <see cref="TransportFactory.BleScanProvider"/>. That keeps every project
/// that references RustNet.Deploy — the CLI, the tests — on plain
/// <c>net10.0</c>, buildable on Linux, with BLE simply absent there.
/// </para>
/// </summary>
public static class BlePlatform
{
    public const string WindowsAssembly = "RustNet.Deploy.Ble.Windows";
    public const string WindowsEntryType = "RustNet.Deploy.Ble.Windows.WinRtBle";

    /// <summary>Why the last <see cref="TryRegister"/> did not register, if it did not.</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Register the backend for this OS if one is available. Never throws:
    /// a tool without BLE is still a working tool, and the reason is reported
    /// when a <c>ble:</c> spec is actually used.
    /// </summary>
    public static bool TryRegister(string? searchDirectory = null)
    {
        if (TransportFactory.BleLinkProvider is not null)
        {
            return true;
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            LastError = NonWindowsMessage;
            return false;
        }
        string dir = searchDirectory ?? AppContext.BaseDirectory;
        string path = Path.Combine(dir, WindowsAssembly + ".dll");
        if (!File.Exists(path))
        {
            LastError = $"the Windows BLE backend ({WindowsAssembly}.dll) was not found next to the tool in {dir}";
            return false;
        }
        try
        {
            var assembly = Assembly.LoadFrom(path);
            var register = assembly.GetType(WindowsEntryType, throwOnError: true)!
                .GetMethod("Register", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)
                ?? throw new MissingMethodException(WindowsEntryType, "Register");
            register.Invoke(null, null);
            LastError = null;
            return TransportFactory.BleLinkProvider is not null;
        }
        catch (Exception ex)
        {
            LastError = $"the Windows BLE backend failed to load: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}";
            return false;
        }
    }

    internal const string NonWindowsMessage =
        "BLE (ble:<address>) is only supported on Windows 10 2004+ for now (WinRT GATT); "
        + "Linux/BlueZ and macOS are not implemented — use serial: or tcp: here";

    /// <summary>The message for a <c>ble:</c> spec used with no backend registered.</summary>
    public static string UnavailableReason()
        => !OperatingSystem.IsWindows()
            ? NonWindowsMessage
            : "BLE transport needs a platform BLE backend — "
              + (LastError ?? "none registered (set TransportFactory.BleLinkProvider or call BlePlatform.TryRegister)");
}
