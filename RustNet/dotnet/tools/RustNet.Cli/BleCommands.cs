using RustNet.Deploy;

namespace RustNet.Cli;

/// <summary>
/// `rustnet ble scan` — list advertising RustNet devices (BLE builds of the
/// firmware advertise as RustNet-XXXX). Connecting is not a command of its own:
/// every device command takes `--device ble:&lt;address|name&gt;`.
/// </summary>
internal static class BleCommands
{
    public static int Dispatch(string[] args)
    {
        string sub = args.Length > 0 ? args[0] : "";
        return sub switch
        {
            "scan" => Scan(args.Skip(1).ToArray()),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: rustnet ble scan [--timeout <seconds>]");
        return 2;
    }

    private static int Scan(string[] args)
    {
        var scanner = TransportFactory.BleScanProvider
            ?? throw new NotSupportedException(BlePlatform.UnavailableReason());
        double seconds = double.TryParse(Cli.Opt(args, "--timeout"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 5;
        Console.Error.WriteLine($"scanning for RustNet devices ({seconds:0.#} s)...");
        var found = scanner(TimeSpan.FromSeconds(seconds));
        if (found.Count == 0)
        {
            Console.WriteLine("no RustNet devices found (firmware built with --features ble? in range? not connected elsewhere?)");
            return 1;
        }
        Console.WriteLine($"{"ADDRESS",-17}  {"RSSI",5}  NAME");
        foreach (var ad in found)
        {
            Console.WriteLine($"{ad.AddressText,-17}  {ad.Rssi,5}  {(ad.Name.Length > 0 ? ad.Name : "(name not received)")}");
        }
        Console.WriteLine();
        Console.WriteLine($"connect with: rustnet info --device ble:{found[0].AddressText}");
        return 0;
    }
}
