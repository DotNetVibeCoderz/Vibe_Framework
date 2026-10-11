namespace Rustzor.Cli;

/// <summary>rustzor add &lt;package...&gt;: dotnet add package for Rustzor packages, by short name, at this tool's version.</summary>
public static class AddCommand
{
    public static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ui"] = "Rustzor.UI",
        ["bootstrap"] = "Rustzor.UI.Bootstrap",
        ["tailwind"] = "Rustzor.UI.Tailwind",
        ["mui"] = "Rustzor.UI.Mui",
        ["chartjs"] = "Rustzor.Charts.ChartJs",
        ["echarts"] = "Rustzor.Charts.ECharts",
        ["d3"] = "Rustzor.Charts.D3",
        ["data"] = "Rustzor.Data",
        ["sqlite"] = "Rustzor.Data",
        ["indexeddb"] = "Rustzor.Data",
        ["game"] = "Rustzor.Game",
        ["graphics"] = "Rustzor.Graphics",
        ["ai"] = "Rustzor.AI",
        ["signalr"] = "Rustzor.Net.SignalR",
        ["grpc"] = "Rustzor.Net.GrpcWeb",
        ["grpcweb"] = "Rustzor.Net.GrpcWeb",
        ["fetch"] = "Rustzor.Browser.Fetch",
        ["http"] = "Rustzor.Browser.Fetch",
        ["websocket"] = "Rustzor.Browser.WebSocket",
        ["ws"] = "Rustzor.Browser.WebSocket",
        ["storage"] = "Rustzor.Browser.Storage",
        ["dom"] = "Rustzor.Browser.DOM",
        ["files"] = "Rustzor.Browser.FileSystem",
        ["filesystem"] = "Rustzor.Browser.FileSystem",
        ["notifications"] = "Rustzor.Browser.Notifications",
        ["push"] = "Rustzor.Browser.Push",
        ["sync"] = "Rustzor.Browser.BackgroundSync",
        ["workers"] = "Rustzor.Browser.Workers",
        ["pwa"] = "Rustzor.Browser.Workers",
        ["location"] = "Rustzor.Browser.Location",
        ["geo"] = "Rustzor.Browser.Location",
        ["sensors"] = "Rustzor.Browser.Sensors",
        ["media"] = "Rustzor.Browser.Media",
        ["camera"] = "Rustzor.Browser.Media",
        ["audio"] = "Rustzor.Browser.Audio",
        ["video"] = "Rustzor.Browser.Video",
        ["canvas"] = "Rustzor.Browser.Canvas",
        ["offscreen"] = "Rustzor.Browser.OffscreenCanvas",
        ["webgl"] = "Rustzor.Browser.WebGL",
        ["webgpu"] = "Rustzor.Browser.WebGPU",
        ["webrtc"] = "Rustzor.Browser.WebRTC",
        ["webxr"] = "Rustzor.Browser.WebXR",
        ["bluetooth"] = "Rustzor.Browser.Bluetooth",
        ["usb"] = "Rustzor.Browser.USB",
        ["serial"] = "Rustzor.Browser.Serial",
        ["hid"] = "Rustzor.Browser.HID",
        ["nfc"] = "Rustzor.Browser.NFC",
        ["gamepad"] = "Rustzor.Browser.Gamepad",
        ["builtin-ai"] = "Rustzor.Browser.AI",
    };

    public static async Task<int> RunAsync(Options o)
    {
        if (o.Positional.Count == 0)
            throw new CliException("Name a package: rustzor add ui fetch storage (or a full id such as Rustzor.Browser.Media).");

        var project = Project.Find(o.Get("project"));
        foreach (var name in o.Positional)
        {
            var id = Resolve(name);
            Out.Step($"Adding {Out.Bold(id)} {Cli.Version}");
            var (code, output) = await Proc.CaptureAsync("dotnet", $"add \"{project}\" package {id} --version {Cli.Version}");
            if (code != 0) throw new CliException($"Could not add {id}:\n{output.Trim()}");
            Out.Ok($"{id} added to {Path.GetFileName(project)}");
        }
        return 0;
    }

    /// <summary>"fetch" is Rustzor.Browser.Fetch; "Browser.Media" and "Rustzor.Browser.Media" are themselves.</summary>
    public static string Resolve(string name)
    {
        if (Aliases.TryGetValue(name, out var id)) return id;
        if (name.StartsWith("Rustzor.", StringComparison.OrdinalIgnoreCase)) return name;
        if (name.Contains('.')) return "Rustzor." + name;
        throw new CliException($"'{name}' is not a Rustzor package short name. Known: {string.Join(", ", Aliases.Keys.Order())}.");
    }
}
