using System.Diagnostics;
using RustNet.Deploy;

namespace RustNet.Cli;

/// <summary>
/// Multi-chip firmware manager: builds per-chip firmware variants from the
/// Rust workspace, lists built images, launches the virtual device.
/// </summary>
internal static class FirmwareCommands
{
    private static readonly string[] Chips = ["host", "esp32", "esp32c3", "k210", "stm32", "ti", "nxp"];

    public static int Dispatch(string[] args) => args.ElementAtOrDefault(0) switch
    {
        "build" => Build(args.Skip(1).ToArray()),
        "list" => List(),
        "run" => RunVirtual(args.Skip(1).ToArray()),
        "flash" => FlashHardware(args.Skip(1).ToArray()),
        "boards" => Boards(),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.Error.WriteLine("""
            usage: rustnet firmware boards
                   rustnet firmware build --chip <c> [--release]     (host virtual device)
                   rustnet firmware build --board <b> [--flash] [--port <serial>] [--device <spec>]
                   rustnet firmware list | run [--port p]
                   rustnet firmware flash --board <b> [--port <serial>] [--device <spec>]
            """);
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

    /// <summary>
    /// Everything this repository has firmware for, and what each one needs.
    /// </summary>
    private static int Boards()
    {
        string? root = BoardFlasher.FindRepoRoot(AppContext.BaseDirectory);
        Console.WriteLine($"{"BOARD",-12} {"CHIP",-9} {"FLASHED WITH",-12} REQUIRES");
        foreach (BoardRecipe b in BoardCatalog.All)
        {
            string built = root is not null && File.Exists(BoardCatalog.ArtifactPath(root, b)) ? " *" : "";
            Console.WriteLine($"{b.Id,-12} {b.Chip,-9} {b.Flash.ToString().ToLowerInvariant(),-12} {b.Requires}{built}");
        }
        Console.WriteLine();
        Console.WriteLine("* = already built. Chip must match when signing an app: a device refuses");
        Console.WriteLine("  an image built for another chip.");
        return 0;
    }

    private static int Build(string[] args)
    {
        // Two shapes share this verb. `--board` builds real firmware for real
        // silicon out of that port's own workspace; `--chip` builds the host
        // virtual device with one chip identity linked in. They are different
        // enough that conflating them would only confuse.
        if (Cli.Opt(args, "--board") is { } boardId)
        {
            return BuildBoard(boardId, args);
        }

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

    /// <summary>Build a bare-metal port, and optionally flash what came out.</summary>
    private static int BuildBoard(string boardId, string[] args)
    {
        BoardRecipe board = BoardCatalog.Find(boardId)
            ?? throw new ArgumentException(
                $"unknown board '{boardId}' (try: {string.Join(", ", BoardCatalog.All.Select(b => b.Id))})");
        string root = BoardFlasher.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("RustNet repo (Cargo.toml) not found above the CLI");

        Console.WriteLine($"{board.Name} ({board.Chip})");
        Console.WriteLine($"note: {board.Note}");
        if (!BoardFlasher.Build(root, board, Console.WriteLine))
        {
            return 1;
        }
        return Cli.Flag(args, "--flash") ? FlashBoard(board, root, args) : 0;
    }

    private static int FlashHardware(string[] args)
    {
        string boardId = Cli.Opt(args, "--board")
            // `--chip` used to be the only spelling and named the host
            // firmware's identity, not a board. Accepting it keeps old
            // invocations working where the two names coincide.
            ?? Cli.Opt(args, "--chip")
            ?? throw new ArgumentException("--board <b> required (see 'rustnet firmware boards')");
        BoardRecipe board = BoardCatalog.Find(boardId)
            ?? throw new ArgumentException(
                $"unknown board '{boardId}' (try: {string.Join(", ", BoardCatalog.All.Select(b => b.Id))})");
        string root = BoardFlasher.FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo not found");
        return FlashBoard(board, root, args);
    }

    private static int FlashBoard(BoardRecipe board, string root, string[] args)
    {
        if (board.Flash == FlashKind.None)
        {
            Console.WriteLine($"'{board.Id}' is not silicon — run it instead: rustnet firmware run");
            return 0;
        }
        string? port = Cli.Opt(args, "--port");
        if (board.NeedsPort && port is null)
        {
            throw new ArgumentException($"--port <serial> required for {board.Id}");
        }
        if (!File.Exists(BoardCatalog.ArtifactPath(root, board)))
        {
            throw new InvalidOperationException(
                $"nothing built for '{board.Id}' — run 'rustnet firmware build --board {board.Id}' first");
        }

        Console.WriteLine($"needs: {board.Requires}");
        var plan = BoardCatalog.FlashPlan(root, board, port, Cli.Opt(args, "--device"));
        return BoardFlasher.Flash(plan, Console.WriteLine) ? 0 : 1;
    }
}
