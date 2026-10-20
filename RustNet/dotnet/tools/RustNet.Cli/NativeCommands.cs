using System.Diagnostics;
using System.Text.Json;
using RustNet.Deploy;

namespace RustNet.Cli;

/// <summary>
/// NPC (Native Procedure Caller) tooling: build a Rust crate into a loadable
/// native module, seal it as a signed RNSB container of kind Native, and push
/// it to the device, where an app loads it with <c>Npc.Load</c>.
/// See docs/npc.md.
/// </summary>
internal static class NativeCommands
{
    public static int Dispatch(string[] args) => args.ElementAtOrDefault(0) switch
    {
        "build" => Build(args.Skip(1).ToArray()),
        "seal" => Seal(args.Skip(1).ToArray()),
        "push" => Push(args.Skip(1).ToArray()),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage: rustnet native build <crate-dir> [--release] [--target <triple>]\n" +
            "       rustnet native seal <module-file> --key <priv.der> [--chip host-sim] [-o out.rnsb]\n" +
            "       rustnet native push <module-file> --name <module> --key <priv.der> [--chip host-sim] [--device ...]");
        return 2;
    }

    /// <summary>Run cargo on the crate and print the path of the cdylib it produced.</summary>
    private static int Build(string[] args)
    {
        string dir = Cli.Positional(args).FirstOrDefault() ?? ".";
        string manifest = Path.GetFullPath(Path.Combine(dir, "Cargo.toml"));
        if (!File.Exists(manifest))
        {
            Console.Error.WriteLine($"error: no Cargo.toml in {dir}");
            return 1;
        }
        var cargoArgs = new List<string> { "build", "--lib", "--message-format=json-render-diagnostics", "--manifest-path", manifest };
        if (Cli.Flag(args, "--release"))
        {
            cargoArgs.Add("--release");
        }
        string? target = Cli.Opt(args, "--target");
        if (target is not null)
        {
            cargoArgs.Add("--target");
            cargoArgs.Add(target);
        }
        var psi = new ProcessStartInfo("cargo")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string a in cargoArgs)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start cargo");
        string? module = null;
        string? line;
        while ((line = proc.StandardOutput.ReadLine()) is not null)
        {
            string? found = CdylibFrom(line);
            if (found is not null)
            {
                module = found;
            }
        }
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            Console.Error.WriteLine("error: cargo build failed");
            return proc.ExitCode;
        }
        if (module is null)
        {
            Console.Error.WriteLine("error: the crate produced no cdylib — set `crate-type = [\"cdylib\"]` under [lib]");
            return 1;
        }
        Console.WriteLine(module);
        return 0;
    }

    /// <summary>The cdylib path from one cargo JSON message, if it names one.</summary>
    internal static string? CdylibFrom(string jsonLine)
    {
        if (!jsonLine.StartsWith('{'))
        {
            return null;
        }
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;
        if (!root.TryGetProperty("reason", out var reason) || reason.GetString() != "compiler-artifact")
        {
            return null;
        }
        if (!root.TryGetProperty("target", out var target)
            || !target.GetProperty("crate_types").EnumerateArray().Any(t => t.GetString() == "cdylib"))
        {
            return null;
        }
        foreach (var file in root.GetProperty("filenames").EnumerateArray())
        {
            string f = file.GetString() ?? "";
            if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".so", StringComparison.Ordinal)
                || f.EndsWith(".dylib", StringComparison.Ordinal))
            {
                return f;
            }
        }
        return null;
    }

    private static byte[] SealModule(string file, string[] args)
    {
        string keyPath = Cli.Opt(args, "--key")
            ?? throw new ArgumentException("--key <private.der> is required (rustnet keys generate)");
        var chip = Signing.ParseChip(Cli.Opt(args, "--chip") ?? "host-sim");
        return Signing.Seal(ImageKind.Native, chip, File.ReadAllBytes(file), File.ReadAllBytes(keyPath));
    }

    private static int Seal(string[] args)
    {
        string file = Cli.Positional(args).FirstOrDefault()
            ?? throw new ArgumentException("usage: rustnet native seal <module-file> --key <priv.der>");
        string output = Cli.Opt(args, "-o") ?? Path.ChangeExtension(file, ".rnsb");
        byte[] sealedModule = SealModule(file, args);
        File.WriteAllBytes(output, sealedModule);
        Console.WriteLine($"{Path.GetFileName(file)} -> {output} ({sealedModule.Length} bytes, signed)");
        return 0;
    }

    private static int Push(string[] args)
    {
        string file = Cli.Positional(args).FirstOrDefault()
            ?? throw new ArgumentException("usage: rustnet native push <module-file> --name <module> --key <priv.der>");
        string name = Cli.Opt(args, "--name")
            ?? Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        byte[] sealedModule = SealModule(file, args);
        using var client = RndpClient.Connect(Cli.DeviceSpec(args));
        client.FlashData(NativeModulePath(name), sealedModule);
        Console.WriteLine($"pushed native module '{name}' ({sealedModule.Length} bytes, signed) — load it with Npc.Load(\"{name}\", \"<procedure>\")");
        return 0;
    }

    /// <summary>Where a module lives under the device's data area (the firmware reads /data/native/&lt;name&gt;.rnsb).</summary>
    public static string NativeModulePath(string name) => $"native/{name}.rnsb";
}
