using RustNet.Deploy;

namespace RustNet.Cli;

internal static class DeviceCommands
{
    private static RndpClient Client(string[] args) => RndpClient.Connect(Cli.DeviceSpec(args));

    public static int Info(string[] args)
    {
        using var client = Client(args);
        Console.WriteLine(client.Info());
        return 0;
    }

    /// <summary>Simulated I/O snapshot (pins, buses, netifs) as JSON.</summary>
    public static int Io(string[] args)
    {
        using var client = Client(args);
        Console.WriteLine(client.IoState());
        return 0;
    }

    /// <summary>
    /// HIL stage 0: identify real Espressif silicon on a serial port via
    /// its ROM bootloader. `rustnet probe --port COM4 [--baud 115200] [--log]`.
    /// </summary>
    public static int Probe(string[] args)
    {
        string port = Cli.Opt(args, "--port")
            ?? throw new ArgumentException("usage: rustnet probe --port COM4 [--baud 115200] [--log]");
        int baud = int.Parse(Cli.Opt(args, "--baud") ?? "115200");
        if (Cli.Flag(args, "--log"))
        {
            Console.WriteLine($"resetting device on {port} and capturing boot log...");
            Console.WriteLine(EspRom.CaptureBootLog(port, baud, 3.0));
            return 0;
        }
        Console.WriteLine($"probing ROM bootloader on {port} @ {baud}...");
        var result = EspRom.Probe(port, baud);
        Console.WriteLine($"chip     : {result.ChipName}");
        Console.WriteLine($"magic    : 0x{result.Magic:X8}");
        Console.WriteLine($"mac      : {result.Mac ?? "n/a"}");
        Console.WriteLine("device rebooted back to its application");
        return 0;
    }

    public static int Logs(string[] args)
    {
        int count = int.TryParse(Cli.Opt(args, "-n"), out int n) ? n : 100;
        using var client = Client(args);
        if (!Cli.Flag(args, "--follow"))
        {
            Console.WriteLine(client.GetLogs(count));
            return 0;
        }
        string last = "";
        Console.Error.WriteLine("following logs (Ctrl+C to stop)...");
        while (true)
        {
            string logs = client.GetLogs(500);
            if (logs != last)
            {
                // Print only the suffix that is new.
                int idx = logs.IndexOf(last, StringComparison.Ordinal);
                string fresh = last.Length > 0 && idx == 0 ? logs[last.Length..] : logs;
                fresh = fresh.TrimStart('\n');
                if (fresh.Length > 0)
                {
                    Console.WriteLine(fresh);
                }
                last = logs;
            }
            Thread.Sleep(500);
        }
    }

    public static int Profile(string[] args)
    {
        using var client = Client(args);
        do
        {
            Console.WriteLine(client.GetPerf());
            if (!Cli.Flag(args, "--watch"))
            {
                return 0;
            }
            Thread.Sleep(1000);
        } while (true);
    }

    public static int Reboot(string[] args)
    {
        using var client = Client(args);
        client.Reboot();
        Console.WriteLine("device rebooted");
        return 0;
    }

    public static int Keys(string[] args)
    {
        if (args.Length == 0 || args[0] != "generate")
        {
            Console.Error.WriteLine("usage: rustnet keys generate --out <dir>");
            return 2;
        }
        string outDir = Cli.Opt(args, "--out") ?? ".";
        Directory.CreateDirectory(outDir);
        var (priv, pub) = Signing.GenerateKeypair();
        string privPath = Path.Combine(outDir, "rustnet-signing.key");
        string pubPath = Path.Combine(outDir, "rustnet-signing.pub");
        File.WriteAllBytes(privPath, priv);
        File.WriteAllBytes(pubPath, pub);
        Console.WriteLine($"wrote {privPath} (KEEP SECRET) and {pubPath}");
        return 0;
    }

    public static int Provision(string[] args)
    {
        string keyPath = Cli.Opt(args, "--key")
            ?? throw new ArgumentException("--key <public.der> is required");
        using var client = Client(args);
        client.ProvisionKey(File.ReadAllBytes(keyPath));
        Console.WriteLine("device provisioned with signing public key");
        return 0;
    }

    public static int Apps(string[] args)
    {
        string sub = args.Length > 0 ? args[0] : "list";
        using var client = Client(args);
        switch (sub)
        {
            case "list":
                Console.WriteLine(client.ListApps());
                return 0;
            case "start":
                client.StartApp(Require(args, 1, "app name"));
                Console.WriteLine("started");
                return 0;
            case "stop":
                client.StopApp();
                Console.WriteLine("stopped");
                return 0;
            case "erase":
                client.EraseApp(Require(args, 1, "app name"));
                Console.WriteLine("erased");
                return 0;
            default:
                Console.Error.WriteLine("usage: rustnet apps list|start|stop|erase [name]");
                return 2;
        }
    }

    public static int Data(string[] args)
    {
        using var client = Client(args);
        switch (args.ElementAtOrDefault(0))
        {
            case "push":
            {
                string local = Require(args, 1, "local file");
                string remote = Require(args, 2, "remote path");
                client.FlashData(remote, File.ReadAllBytes(local));
                Console.WriteLine($"pushed {local} -> {remote}");
                return 0;
            }
            case "pull":
            {
                string remote = Require(args, 1, "remote path");
                byte[] data = client.ReadData(remote);
                string local = args.Length > 2 && !args[2].StartsWith('-')
                    ? args[2]
                    : Path.GetFileName(remote);
                File.WriteAllBytes(local, data);
                Console.WriteLine($"pulled {remote} -> {local} ({data.Length} bytes)");
                return 0;
            }
            default:
                Console.Error.WriteLine("usage: rustnet data push <local> <remote> | pull <remote> [local]");
                return 2;
        }
    }

    public static int Config(string[] args)
    {
        using var client = Client(args);
        switch (args.ElementAtOrDefault(0))
        {
            case "set":
                client.SetConfig(Require(args, 1, "key"), Require(args, 2, "value"));
                Console.WriteLine("ok (stored encrypted)");
                return 0;
            case "get":
                Console.WriteLine(client.GetConfig(Require(args, 1, "key")));
                return 0;
            default:
                Console.Error.WriteLine("usage: rustnet config set <key> <value> | get <key>");
                return 2;
        }
    }

    public static int Wifi(string[] args)
    {
        string ssid = Cli.Opt(args, "--ssid") ?? throw new ArgumentException("--ssid required");
        string psk = Cli.Opt(args, "--psk") ?? "";
        using var client = Client(args);
        client.ConfigureWifi(ssid, psk);
        Console.WriteLine($"wifi configured for '{ssid}'");
        return 0;
    }

    public static int BootImage(string[] args)
    {
        using var client = Client(args);
        switch (args.ElementAtOrDefault(0))
        {
            case "set":
            {
                string file = Require(args, 1, "rgb565 file");
                int w = int.Parse(Cli.Opt(args, "--width") ?? throw new ArgumentException("--width required"));
                int h = int.Parse(Cli.Opt(args, "--height") ?? throw new ArgumentException("--height required"));
                client.SetBootImage((ushort)w, (ushort)h, File.ReadAllBytes(file));
                Console.WriteLine($"boot image set ({w}x{h})");
                return 0;
            }
            case "get":
            {
                byte[] data = client.GetBootImage();
                string outPath = Cli.Opt(args, "-o") ?? "bootimg.bin";
                File.WriteAllBytes(outPath, data);
                Console.WriteLine($"boot image saved to {outPath}");
                return 0;
            }
            default:
                Console.Error.WriteLine("usage: rustnet bootimg set <file> --width W --height H | get [-o out]");
                return 2;
        }
    }

    public static int Display(string[] args)
    {
        if (args.ElementAtOrDefault(0) != "capture")
        {
            Console.Error.WriteLine("usage: rustnet display capture [-o out.ppm]");
            return 2;
        }
        using var client = Client(args);
        var (w, h, pixels) = client.GetDisplay();
        string outPath = Cli.Opt(args, "-o") ?? "display.ppm";
        // RGB565 LE -> PPM P6
        using var f = File.Create(outPath);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        f.Write(header);
        for (int i = 0; i < w * h; i++)
        {
            ushort px = BitConverter.ToUInt16(pixels, i * 2);
            f.WriteByte((byte)(((px >> 11) & 0x1F) << 3));
            f.WriteByte((byte)(((px >> 5) & 0x3F) << 2));
            f.WriteByte((byte)((px & 0x1F) << 3));
        }
        Console.WriteLine($"captured {w}x{h} display -> {outPath}");
        return 0;
    }

    public static int Ota(string[] args)
    {
        if (args.ElementAtOrDefault(0) == "campaign")
        {
            return OtaCampaignCmd(args);
        }
        using var client = Client(args);
        switch (args.ElementAtOrDefault(0))
        {
            case "push":
            {
                string file = Require(args, 1, "firmware file");
                string keyPath = Cli.Opt(args, "--key") ?? throw new ArgumentException("--key <priv.der> required");
                var chip = Signing.ParseChip(Cli.Opt(args, "--chip") ?? "host-sim");
                byte[] sealedFw = Signing.Seal(ImageKind.Firmware, chip, File.ReadAllBytes(file),
                    File.ReadAllBytes(keyPath));
                client.OtaUpdate(sealedFw, (done, total) =>
                    Console.Write($"\r  uploading {done * 100 / total}%"));
                Console.WriteLine("\nupdate verified and staged; device booted into new slot");
                Console.WriteLine("run 'rustnet ota confirm' after checking health, or 'rustnet ota rollback'");
                return 0;
            }
            case "confirm":
                Console.WriteLine($"active slot: {client.OtaConfirm()}");
                return 0;
            case "rollback":
                Console.WriteLine($"rolled back to slot: {client.OtaRollback()}");
                return 0;
            default:
                Console.Error.WriteLine("usage: rustnet ota push <file> --key k [--chip c] | confirm | rollback | campaign <file> --fleet f --key k");
                return 2;
        }
    }

    /// <summary>
    /// rustnet ota campaign &lt;file&gt; --fleet devices.txt --key k [--chip c]
    /// [--canary N] [--batch N] [--abort-after N] [--confirm]
    /// — staged OTA rollout across a fleet listed one device spec per line.
    /// </summary>
    private static int OtaCampaignCmd(string[] args)
    {
        string file = Require(args, 1, "firmware file");
        string keyPath = Cli.Opt(args, "--key") ?? throw new ArgumentException("--key <priv.der> required");
        string fleetPath = Cli.Opt(args, "--fleet") ?? throw new ArgumentException("--fleet <devices.txt> required");
        var chip = Signing.ParseChip(Cli.Opt(args, "--chip") ?? "host-sim");
        bool confirm = Cli.Flag(args, "--confirm");

        var devices = File.ReadAllLines(fleetPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
        if (devices.Count == 0)
        {
            Console.Error.WriteLine($"fleet file {fleetPath} lists no devices");
            return 2;
        }

        var policy = new OtaCampaignPolicy
        {
            CanarySize = ParseInt(Cli.Opt(args, "--canary"), 1),
            BatchSize = ParseInt(Cli.Opt(args, "--batch"), 0),
            AbortAfterFailures = ParseInt(Cli.Opt(args, "--abort-after"), 1),
        };

        byte[] sealedFw = Signing.Seal(ImageKind.Firmware, chip, File.ReadAllBytes(file),
            File.ReadAllBytes(keyPath));

        Console.WriteLine($"campaign: {devices.Count} device(s), canary {policy.CanarySize}, " +
            $"abort after {policy.AbortAfterFailures} failure(s)");
        var result = OtaCampaign.Run(devices, policy, spec => PushOne(spec, sealedFw, confirm));

        foreach (var o in result.Outcomes)
        {
            string tag = o.Status.ToString().ToLowerInvariant();
            Console.WriteLine(o.Error is null ? $"  {o.Device}: {tag}" : $"  {o.Device}: {tag} ({o.Error})");
        }
        Console.WriteLine($"campaign: {result.Succeeded} ok, {result.Failed} failed, " +
            $"{result.Skipped} skipped{(result.Aborted ? " — ABORTED" : "")}");
        return result.Failed > 0 || result.Aborted ? 1 : 0;
    }

    private static DeviceOutcome PushOne(string spec, byte[] sealedFw, bool confirm)
    {
        try
        {
            using var c = RndpClient.Connect(spec);
            c.OtaUpdate(sealedFw);
            if (confirm)
            {
                c.OtaConfirm();
                return new DeviceOutcome(spec, OtaStatus.Confirmed);
            }
            return new DeviceOutcome(spec, OtaStatus.Updated);
        }
        catch (Exception ex)
        {
            return new DeviceOutcome(spec, OtaStatus.Failed, ex.Message);
        }
    }

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out int n) ? n : fallback;

    public static int Debug(string[] args)
    {
        using var client = Client(args);
        switch (args.ElementAtOrDefault(0))
        {
            case "bp":
                client.DebugSetBreakpoint(uint.Parse(Require(args, 1, "method index")),
                    uint.Parse(Require(args, 2, "il offset")));
                Console.WriteLine("breakpoint queued (applies at next app start)");
                return 0;
            case "stack":
                Console.WriteLine(client.DebugStack());
                return 0;
            default:
                Console.Error.WriteLine("usage: rustnet debug bp <method#> <ilOffset> | stack");
                return 2;
        }
    }

    private static string Require(string[] args, int index, string what)
    {
        var positional = Cli.Positional(args);
        return positional.Length > index
            ? positional[index]
            : throw new ArgumentException($"missing {what}");
    }
}
