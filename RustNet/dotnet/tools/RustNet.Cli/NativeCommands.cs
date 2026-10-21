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
        string? mcuTarget = Cli.Opt(args, "--target");
        if (mcuTarget is not null && mcuTarget.StartsWith("thumb", StringComparison.Ordinal))
        {
            return BuildMcu(manifest, mcuTarget);
        }
        if (mcuTarget is not null && mcuTarget.StartsWith("xtensa-esp32", StringComparison.Ordinal))
        {
            return BuildXtensa(manifest, mcuTarget);
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

    /// <summary>
    /// A module for a microcontroller: build the crate as a <c>no_std</c>
    /// staticlib for <paramref name="target"/>, link it at address 0 with its
    /// relocations kept, and convert it to RNPC (see <see cref="NpcModule"/>).
    /// Prints the .rnpc path.
    /// </summary>
    private static int BuildMcu(string manifest, string target)
    {
        var arch = target.StartsWith("thumbv6m", StringComparison.Ordinal) ? NpcModule.Arch.ThumbV6M
            : target.StartsWith("thumbv7", StringComparison.Ordinal) ? NpcModule.Arch.ThumbV7EM
            : throw new ArgumentException($"no RNPC loader for {target}; supported: thumbv6m-none-eabi, thumbv7em-none-eabi[hf]");
        var psi = new ProcessStartInfo("cargo") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string a in new[] { "build", "--lib", "--release", "--target", target, "--message-format=json-render-diagnostics", "--manifest-path", manifest })
        {
            psi.ArgumentList.Add(a);
        }
        string? archive = null;
        string? crateName = null;
        using (var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start cargo"))
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                if (!line.StartsWith('{'))
                {
                    continue;
                }
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("reason", out var reason) && reason.GetString() == "compiler-artifact"
                    && root.GetProperty("target").GetProperty("crate_types").EnumerateArray().Any(t => t.GetString() == "staticlib"))
                {
                    archive = root.GetProperty("filenames").EnumerateArray().Select(f => f.GetString()!).FirstOrDefault(f => f.EndsWith(".a", StringComparison.Ordinal));
                    crateName = root.GetProperty("target").GetProperty("name").GetString()!.Replace('-', '_');
                }
            }
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine("error: cargo build failed");
                return proc.ExitCode;
            }
        }
        if (archive is null || crateName is null)
        {
            Console.Error.WriteLine("error: the crate produced no staticlib — set `crate-type = [\"staticlib\"]` under [lib] and `#![no_std]`");
            return 1;
        }

        string tools = RustTools();
        // The module's own exported functions: global text symbols defined in
        // the crate's object files (not core's or compiler_builtins').
        var exports = Run(Path.Combine(tools, "llvm-nm"), "--defined-only", "-g", "-A", archive)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains($"{crateName}-", StringComparison.Ordinal) && l.Contains(" T ", StringComparison.Ordinal))
            .Select(l => l[(l.LastIndexOf(' ') + 1)..])
            .Where(n => !n.StartsWith('_') && n != "rust_begin_unwind")
            .Distinct()
            .ToList();
        if (exports.Count == 0)
        {
            Console.Error.WriteLine("error: the module exports no procedures — mark them #[no_mangle] pub extern \"C\"");
            return 1;
        }

        string dir = Path.GetDirectoryName(archive)!;
        string script = Path.Combine(dir, "rnpc.ld");
        File.WriteAllText(script, NpcModule.LinkerScript);
        string elf = Path.Combine(dir, crateName + ".npc.elf");
        var link = new List<string> { "-flavor", "gnu", "--emit-relocs", "--gc-sections", "--no-undefined", "-e", "0", "-T", script, "-o", elf };
        foreach (string e in exports)
        {
            link.Add("-u");
            link.Add(e);
        }
        link.Add(archive);
        Run(Path.Combine(tools, "rust-lld"), link.ToArray());

        var converted = NpcModule.FromElf(File.ReadAllBytes(elf), arch, exports);
        string output = Path.Combine(dir, crateName + ".rnpc");
        File.WriteAllBytes(output, converted.Rnpc);
        Console.Error.WriteLine(
            $"{converted.ImageLength} bytes of code+data, {converted.BssLength} of bss, {converted.Relocations.Count} relocations; " +
            $"exports {string.Join(", ", converted.Exports.Select(e => e.Name))}");
        Console.WriteLine(output);
        return 0;
    }

    /// <summary>
    /// An ESP32 module: the Espressif Rust toolchain (`+esp`) builds the
    /// staticlib with `-Zbuild-std=core` (no prebuilt Xtensa core exists), its
    /// GNU linker places code at 0 and data at 0x1000_0000 keeping relocations,
    /// and the result becomes RNPC version 2 (separate code and data segments).
    /// </summary>
    private static int BuildXtensa(string manifest, string target)
    {
        var psi = new ProcessStartInfo("cargo") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string a in new[] { "+esp", "build", "--lib", "--release", "--target", target, "-Zbuild-std=core", "--message-format=json-render-diagnostics", "--manifest-path", manifest })
        {
            psi.ArgumentList.Add(a);
        }
        string? archive = null;
        string? crateName = null;
        using (var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start cargo"))
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                if (!line.StartsWith('{'))
                {
                    continue;
                }
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("reason", out var reason) && reason.GetString() == "compiler-artifact"
                    && root.GetProperty("target").GetProperty("crate_types").EnumerateArray().Any(t => t.GetString() == "staticlib"))
                {
                    archive = root.GetProperty("filenames").EnumerateArray().Select(f => f.GetString()!).FirstOrDefault(f => f.EndsWith(".a", StringComparison.Ordinal));
                    crateName = root.GetProperty("target").GetProperty("name").GetString()!.Replace('-', '_');
                }
            }
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine("error: cargo +esp build failed (install the Espressif toolchain with espup)");
                return proc.ExitCode;
            }
        }
        if (archive is null || crateName is null)
        {
            Console.Error.WriteLine("error: the crate produced no staticlib");
            return 1;
        }
        string bin = Path.Combine(Run("rustc", "+esp", "--print", "sysroot").Trim(), "xtensa-esp-elf", "bin");
        string Tool(string name) => Path.Combine(bin, OperatingSystem.IsWindows() ? name + ".exe" : name);
        var exports = Run(Tool("xtensa-esp32-elf-nm"), "--defined-only", "-g", "-A", archive)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains($"{crateName}-", StringComparison.Ordinal) && l.Contains(" T ", StringComparison.Ordinal))
            .Select(l => l[(l.LastIndexOf(' ') + 1)..])
            .Where(n => !n.StartsWith('_') && n != "rust_begin_unwind")
            .Distinct()
            .ToList();
        if (exports.Count == 0)
        {
            Console.Error.WriteLine("error: the module exports no procedures — mark them #[no_mangle] pub extern \"C\"");
            return 1;
        }
        string dir = Path.GetDirectoryName(archive)!;
        string script = Path.Combine(dir, "rnpc-xtensa.ld");
        File.WriteAllText(script, NpcModule.XtensaLinkerScript);
        string elf = Path.Combine(dir, crateName + ".npc.elf");
        var link = new List<string> { "--emit-relocs", "--gc-sections", "--no-undefined", "-e", "0", "-T", script, "-o", elf };
        foreach (string e in exports)
        {
            link.Add("-u");
            link.Add(e);
        }
        link.Add(archive);
        Run(Tool("xtensa-esp32-elf-ld"), link.ToArray());
        var converted = NpcModule.FromXtensaElf(File.ReadAllBytes(elf), exports);
        string output = Path.Combine(dir, crateName + ".rnpc");
        File.WriteAllBytes(output, converted.Rnpc);
        Console.Error.WriteLine(
            $"{converted.ImageLength} bytes of code+data, {converted.BssLength} of bss, {converted.Relocations.Count} relocations; " +
            $"exports {string.Join(", ", converted.Exports.Select(e => e.Name))}");
        Console.WriteLine(output);
        return 0;
    }

    /// <summary>The LLVM tools that ship with the active Rust toolchain (rust-lld, llvm-nm).</summary>
    private static string RustTools()
    {
        string sysroot = Run("rustc", "--print", "sysroot").Trim();
        string host = Run("rustc", "-vV").Split('\n').First(l => l.StartsWith("host:", StringComparison.Ordinal))[5..].Trim();
        return Path.Combine(sysroot, "lib", "rustlib", host, "bin");
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {stderr.Trim()}");
        }
        return stdout;
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
