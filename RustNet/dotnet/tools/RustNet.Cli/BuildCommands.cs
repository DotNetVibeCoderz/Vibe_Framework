using RustNet.Deploy;
using RustNet.MetadataProcessor;

namespace RustNet.Cli;

internal static class BuildCommands
{
    /// <summary>rustnet build app.dll [-o app.rnx]</summary>
    public static int Build(string[] args)
    {
        string input = Cli.Positional(args).FirstOrDefault()
            ?? throw new ArgumentException("usage: rustnet build <app.dll> [-o app.rnx]");
        string output = Cli.Opt(args, "-o") ?? Path.ChangeExtension(input, ".rnx");
        byte[] rnx = RnxCompiler.Compile(input, out var warnings);
        foreach (string w in warnings)
        {
            Console.Error.WriteLine($"warning: {w}");
        }
        File.WriteAllBytes(output, rnx);
        Console.WriteLine($"{Path.GetFileName(input)} -> {output} ({rnx.Length} bytes)");
        return 0;
    }

    /// <summary>rustnet flash app.dll|app.rnx --name blinky --key priv.der [--chip host-sim] [--start]</summary>
    public static int Flash(string[] args)
    {
        string input = Cli.Positional(args).FirstOrDefault()
            ?? throw new ArgumentException("usage: rustnet flash <app.dll|app.rnx> --name <n> --key <priv.der>");
        string name = Cli.Opt(args, "--name")
            ?? Path.GetFileNameWithoutExtension(input).ToLowerInvariant();
        string keyPath = Cli.Opt(args, "--key")
            ?? throw new ArgumentException("--key <private.der> is required (rustnet keys generate)");
        var chip = Signing.ParseChip(Cli.Opt(args, "--chip") ?? "host-sim");

        byte[] rnx;
        if (input.EndsWith(".rnx", StringComparison.OrdinalIgnoreCase))
        {
            rnx = File.ReadAllBytes(input);
        }
        else
        {
            rnx = RnxCompiler.Compile(input, out var warnings);
            foreach (string w in warnings)
            {
                Console.Error.WriteLine($"warning: {w}");
            }
        }
        byte[] sealedApp = Signing.Seal(ImageKind.App, chip, rnx, File.ReadAllBytes(keyPath));
        using var client = RndpClient.Connect(Cli.DeviceSpec(args));
        client.FlashApp(name, sealedApp);
        Console.WriteLine($"flashed '{name}' ({sealedApp.Length} bytes, signed, chip={chip})");
        if (Cli.Flag(args, "--start") || Cli.Flag(args, "--run"))
        {
            client.StartApp(name);
            Console.WriteLine("started");
        }
        return 0;
    }

    /// <summary>rustnet run <name> — start an installed app and follow its logs.</summary>
    public static int Run(string[] args)
    {
        string name = Cli.Positional(args).FirstOrDefault()
            ?? throw new ArgumentException("usage: rustnet run <app-name>");
        using var client = RndpClient.Connect(Cli.DeviceSpec(args));
        client.StartApp(name);
        Console.Error.WriteLine($"'{name}' started — following logs (Ctrl+C to stop)");
        return DeviceCommands.Logs(args.Concat(new[] { "--follow" }).ToArray());
    }
}
