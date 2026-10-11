using System.Diagnostics;
using System.Text;

namespace Rustzor.Cli;

/// <summary>Coloured console output, plain when redirected.</summary>
public static class Out
{
    private static readonly bool Color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    private static string Paint(string code, string text) => Color ? $"\u001b[{code}m{text}\u001b[0m" : text;

    public static void Step(string text) => Console.WriteLine(Paint("38;5;208", "▸ ") + text);
    public static void Ok(string text) => Console.WriteLine(Paint("32", "✓ ") + text);
    public static void Warn(string text) => Console.WriteLine(Paint("33", "! ") + text);
    public static void Error(string text) => Console.Error.WriteLine(Paint("31", "✗ ") + text);
    public static string Dim(string text) => Paint("2", text);
    public static string Bold(string text) => Paint("1", text);

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.00} MB",
    };
}

/// <summary>Runs dotnet and other programs.</summary>
public static class Proc
{
    /// <summary>Runs a command with its output passed through; returns the exit code.</summary>
    public static async Task<int> RunAsync(string file, string args, string? cwd = null)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { WorkingDirectory = cwd ?? Environment.CurrentDirectory, UseShellExecute = false })
            ?? throw new CliException($"Could not start {file}.");
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    /// <summary>Runs a command and captures stdout and stderr together.</summary>
    public static async Task<(int Code, string Output)> CaptureAsync(string file, string args, string? cwd = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new CliException($"Could not start {file}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, (await stdout) + (await stderr));
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch
        {
            Out.Warn($"Could not open a browser; go to {url}");
        }
    }
}

/// <summary>Finds the project a command works on.</summary>
public static class Project
{
    /// <summary>The .csproj named, or the only one in the folder named (default: the current folder).</summary>
    public static string Find(string? pathOrFolder)
    {
        var path = Path.GetFullPath(pathOrFolder ?? ".");
        if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return path;
        if (!Directory.Exists(path)) throw new CliException($"No project at {path}.");
        var projects = Directory.GetFiles(path, "*.csproj");
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new CliException($"No .csproj in {path}. Run this in a Rustzor project folder, or name one."),
            _ => throw new CliException($"More than one .csproj in {path}: name the one to use."),
        };
    }

    /// <summary>A property as MSBuild evaluates it for the project.</summary>
    public static async Task<string> PropertyAsync(string project, string property, string configuration = "Debug")
    {
        var (code, output) = await Proc.CaptureAsync("dotnet", $"msbuild \"{project}\" -nologo -getProperty:{property} -p:Configuration={configuration}");
        if (code != 0) throw new CliException($"Could not read {property} from {Path.GetFileName(project)}:\n{output.Trim()}");
        return output.Trim();
    }

    /// <summary>The folder the Rustzor bundler writes the site to.</summary>
    public static async Task<string> SiteAsync(string project, string configuration = "Debug")
    {
        var outDir = await PropertyAsync(project, "OutDir", configuration);
        var dir = Path.IsPathRooted(outDir) ? outDir : Path.Combine(Path.GetDirectoryName(project)!, outDir);
        return Path.GetFullPath(Path.Combine(dir, "wwwroot"));
    }
}
