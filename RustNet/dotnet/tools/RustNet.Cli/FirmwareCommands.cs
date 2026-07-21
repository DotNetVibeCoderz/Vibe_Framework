using System.Diagnostics;

namespace RustNet.Cli;

/// <summary>
/// Multi-chip firmware manager: builds per-chip firmware variants from the
/// Rust workspace, lists built images, launches the virtual device.
/// </summary>
internal static class FirmwareCommands
{
    private static readonly string[] Chips = ["host", "esp32", "stm32", "ti", "nxp"];

    public static int Dispatch(string[] args) => args.ElementAtOrDefault(0) switch
    {
        "build" => Build(args.Skip(1).ToArray()),
        "list" => List(),
        "run" => RunVirtual(args.Skip(1).ToArray()),
        "flash" => FlashHardware(args.Skip(1).ToArray()),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.Error.WriteLine("usage: rustnet firmware build --chip <c> [--release] | list | run [--port p] | flash --chip <c> --port <serial>");
        return 2;
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml"))
                && Directory.Exists(Path.Combine(dir.FullName, "runtime")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static string ArtifactsDir(string root) => Path.Combine(root, "artifacts", "firmware");

    private static int Build(string[] args)
    {
        string chip = Cli.Opt(args, "--chip") ?? "host";
        if (!Chips.Contains(chip))
        {
            throw new ArgumentException($"unknown chip '{chip}' ({string.Join("|", Chips)})");
        }
        string root = RepoRoot()
            ?? throw new InvalidOperationException("RustNet repo (Cargo.toml) not found above the CLI");
        bool release = Cli.Flag(args, "--release");
        string features = $"chip-{chip}";
        var psi = new ProcessStartInfo
        {
            FileName = "cargo",
            Arguments = $"build -p rustnet-firmware --no-default-features --features {features}"
                + (release ? " --release" : ""),
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        Console.WriteLine($"cargo {psi.Arguments}");
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            return proc.ExitCode;
        }
        string profile = release ? "release" : "debug";
        string exe = OperatingSystem.IsWindows() ? "rustnet-firmware.exe" : "rustnet-firmware";
        string built = Path.Combine(root, "target", profile, exe);
        string outDir = ArtifactsDir(root);
        Directory.CreateDirectory(outDir);
        string dest = Path.Combine(outDir, $"rustnet-firmware-{chip}{(release ? "" : "-debug")}{Path.GetExtension(exe)}");
        File.Copy(built, dest, overwrite: true);
        Console.WriteLine($"firmware variant '{chip}' -> {dest}");
        return 0;
    }

    private static int List()
    {
        string root = RepoRoot() ?? throw new InvalidOperationException("repo not found");
        string dir = ArtifactsDir(root);
        if (!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0)
        {
            Console.WriteLine("no firmware built yet — run 'rustnet firmware build --chip <c>'");
            return 0;
        }
        foreach (string f in Directory.GetFiles(dir))
        {
            var fi = new FileInfo(f);
            Console.WriteLine($"{fi.Name,-40} {fi.Length / 1024,6} KiB  {fi.LastWriteTime:yyyy-MM-dd HH:mm}");
        }
        return 0;
    }

    private static int RunVirtual(string[] args)
    {
        string root = RepoRoot() ?? throw new InvalidOperationException("repo not found");
        string exe = OperatingSystem.IsWindows() ? "rustnet-firmware.exe" : "rustnet-firmware";
        string? binary = new[] { "debug", "release" }
            .Select(p => Path.Combine(root, "target", p, exe))
            .FirstOrDefault(File.Exists);
        if (binary is null)
        {
            throw new InvalidOperationException("firmware not built — run 'rustnet firmware build --chip host'");
        }
        string port = Cli.Opt(args, "--port") ?? "7878";
        string extra = Cli.Flag(args, "--ephemeral") ? " --ephemeral" : "";
        Console.WriteLine($"starting virtual device on port {port} (Ctrl+C to stop)");
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            Arguments = $"--port {port}{extra}",
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static int FlashHardware(string[] args)
    {
        string chip = Cli.Opt(args, "--chip") ?? throw new ArgumentException("--chip required");
        string port = Cli.Opt(args, "--port") ?? throw new ArgumentException("--port <serial> required");
        Console.WriteLine($"""
            Hardware flashing for '{chip}' on {port} requires the vendor bootloader tool:
              esp32: espflash flash artifacts/firmware/rustnet-firmware-esp32 --port {port}
              stm32: STM32CubeProgrammer / st-flash write <bin> 0x8000000
              ti:    UniFlash CLI
              nxp:   MCUXpresso / blhost

            The chip-specific PAC/SDK integration point is
            runtime/firmware/src/chip.rs — vendor targets currently build the
            full service stack against the simulator board.
            """);
        return 0;
    }
}
