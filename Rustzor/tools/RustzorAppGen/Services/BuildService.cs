using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RustzorAppGen.Services;

/// <summary>Builds, runs and deploys Rustzor projects with the dotnet CLI.</summary>
public sealed partial class BuildService(AppSettings settings, Action<string, LogKind> log)
{
    private StaticSiteServer? _server;

    public bool IsServing => _server is not null;
    public string? ServingUrl => _server?.Url;

    /// <summary>The .csproj of a project folder (the first one at its top level).</summary>
    public static string? ProjectFile(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.csproj").FirstOrDefault() : null;

    public async Task<BuildResult> BuildAsync(string folder, string configuration = "Debug", CancellationToken cancel = default)
    {
        var project = ProjectFile(folder) ?? throw new InvalidOperationException("This folder has no .csproj to build.");
        await EnsureToolchainAsync(project, cancel);
        log($"dotnet build {Path.GetFileName(project)} -c {configuration}", LogKind.Command);
        var (code, output) = await RunAsync("dotnet", $"build \"{project}\" -c {configuration} -nologo -clp:NoSummary", folder, cancel);
        var problems = ParseProblems(output);
        var result = new BuildResult(code == 0, problems, output);
        var errors = problems.Count(p => p.Severity == "error");
        log(code == 0 ? $"Build succeeded ({problems.Count - errors} warning(s))" : $"Build failed: {errors} error(s)", code == 0 ? LogKind.Success : LogKind.Error);
        return result;
    }

    /// <summary>Builds and serves the site; returns its URL.</summary>
    public async Task<string> RunAsync(string folder, CancellationToken cancel = default)
    {
        var result = await BuildAsync(folder, "Debug", cancel);
        if (!result.Succeeded) throw new InvalidOperationException("The build failed; see Problems.");
        var site = SiteFolder(folder, "Debug") ?? throw new InvalidOperationException("The build produced no wwwroot folder. Is this a Rustzor app?");
        Stop();
        _server = new StaticSiteServer(site, settings.ServePort, log);
        _server.Start();
        log($"Serving {site} at {_server.Url}", LogKind.Success);
        OpenBrowser(_server.Url);
        return _server.Url;
    }

    public void Stop()
    {
        if (_server is null) return;
        _server.Dispose();
        _server = null;
        log("Stopped the local server", LogKind.Info);
    }

    /// <summary>Publishes a Release build, zips the site and copies it to the deploy folder; returns where it went.</summary>
    public async Task<string> DeployAsync(string folder, CancellationToken cancel = default)
    {
        var project = ProjectFile(folder) ?? throw new InvalidOperationException("This folder has no .csproj.");
        await EnsureToolchainAsync(project, cancel);
        var publish = Path.Combine(folder, "publish");
        if (Directory.Exists(publish)) Directory.Delete(publish, true);
        log($"dotnet publish -c Release -o publish", LogKind.Command);
        var (code, output) = await RunAsync("dotnet", $"publish \"{project}\" -c Release -o \"{publish}\" -nologo", folder, cancel);
        if (code != 0)
        {
            foreach (var p in ParseProblems(output).Where(p => p.Severity == "error").Take(20)) log(p.ToString(), LogKind.Error);
            throw new InvalidOperationException("Publishing failed; see the log.");
        }
        var site = Directory.Exists(Path.Combine(publish, "wwwroot")) ? Path.Combine(publish, "wwwroot") : SiteFolder(folder, "Release") ?? publish;
        var name = Path.GetFileNameWithoutExtension(project);
        var zip = Path.Combine(folder, $"{name}-site.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(site, zip);
        log($"Packaged the site: {zip}", LogKind.Success);
        if (!string.IsNullOrWhiteSpace(settings.DeployFolder))
        {
            var target = Path.Combine(settings.DeployFolder, name);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            CopyDirectory(site, target);
            log($"Deployed to {target}", LogKind.Success);
            return target;
        }
        return zip;
    }

    /// <summary>bin/{config}/net10.0/wwwroot, where the Rustzor SDK writes the site.</summary>
    public static string? SiteFolder(string folder, string configuration)
    {
        var bin = Path.Combine(folder, "bin", configuration);
        if (!Directory.Exists(bin)) return null;
        return Directory.EnumerateDirectories(bin, "wwwroot", SearchOption.AllDirectories)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "_rustzor", "boot.json")) || File.Exists(Path.Combine(d, "index.html")));
    }

    /// <summary>In source mode, the Rustzor build tool and runtime must exist in the checkout.</summary>
    private async Task EnsureToolchainAsync(string project, CancellationToken cancel)
    {
        var text = await File.ReadAllTextAsync(project, cancel);
        var m = RootRegex().Match(text);
        if (!m.Success) return;
        var root = m.Groups[1].Value.Trim();
        if (!Directory.Exists(root)) throw new InvalidOperationException($"The project points at a Rustzor checkout that does not exist: {root}. Fix RustzorRoot in the .csproj or in Settings.");
        var tool = Path.Combine(root, "src", "Rustzor.Build", "bin", "Release", "net10.0", "Rustzor.Build.dll");
        if (!File.Exists(tool))
        {
            log("Building the Rustzor build tool (first time only)…", LogKind.Info);
            var (code, _) = await RunAsync("dotnet", $"build \"{Path.Combine(root, "src", "Rustzor.Build")}\" -c Release -nologo", root, cancel);
            if (code != 0) throw new InvalidOperationException("Could not build the Rustzor build tool; see the log.");
        }
        if (!File.Exists(Path.Combine(root, "src", "Rustzor.Sdk", "runtime", "rustzor.wasm")))
            throw new InvalidOperationException($"The Rustzor runtime is not built yet. Run scripts/build-runtime.sh in {root} once.");
    }

    public async Task<(int Code, string Output)> RunAsync(string exe, string args, string workingDirectory, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        void OnLine(string? line, bool error)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            var kind = ErrorLineRegex().IsMatch(line) ? LogKind.Error : WarningLineRegex().IsMatch(line) ? LogKind.Warning : error ? LogKind.Warning : LogKind.Info;
            if (line.Length > 0) log(line, kind);
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data, false);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data, true);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (cancel.Register(() => { try { process.Kill(true); } catch { } }))
            await process.WaitForExitAsync(cancel);
        return (process.ExitCode, output.ToString());
    }

    public static List<BuildProblem> ParseProblems(string output)
    {
        var seen = new HashSet<string>();
        var list = new List<BuildProblem>();
        foreach (Match m in ProblemRegex().Matches(output))
        {
            var p = new BuildProblem(m.Groups["file"].Value.Trim(), int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value),
                m.Groups["sev"].Value, m.Groups["code"].Value, m.Groups["msg"].Value.Trim());
            if (seen.Add(p.ToString() + p.File)) list.Add(p);
        }
        return list;
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No default browser: the URL is in the log.
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    [GeneratedRegex(@"^\s*(?<file>[^\r\n]+?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\):\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+):\s*(?<msg>[^\r\n]*?)(?:\s+\[[^\]]+\])?\s*$", RegexOptions.Multiline)]
    private static partial Regex ProblemRegex();
    [GeneratedRegex(@":\s*error\s+[A-Z]+\d+:|\berror\b:", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLineRegex();
    [GeneratedRegex(@":\s*warning\s+[A-Z]+\d+:")]
    private static partial Regex WarningLineRegex();
    [GeneratedRegex(@"<RustzorRoot>([^<]+)</RustzorRoot>")]
    private static partial Regex RootRegex();
}

/// <summary>A small static file server for the built site, with SPA fallback to index.html.</summary>
public sealed class StaticSiteServer : IDisposable
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript", [".mjs"] = "text/javascript", [".css"] = "text/css",
        [".json"] = "application/json", [".wasm"] = "application/wasm", [".dll"] = "application/octet-stream", [".png"] = "image/png",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon",
        [".webp"] = "image/webp", [".txt"] = "text/plain", [".woff2"] = "font/woff2", [".mp3"] = "audio/mpeg", [".obj"] = "text/plain",
        [".csv"] = "text/csv", [".webmanifest"] = "application/manifest+json",
    };

    private readonly string _root;
    private HttpListener _listener = new();
    private readonly Action<string, LogKind> _log;
    private readonly CancellationTokenSource _stop = new();

    public StaticSiteServer(string root, int port, Action<string, LogKind> log)
    {
        _root = Path.GetFullPath(root);
        _log = log;
        // Find a free port starting at the configured one.
        for (var p = port; p < port + 50; p++)
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{p}/");
                _listener.Start();
                Url = $"http://localhost:{p}/";
                return;
            }
            catch (HttpListenerException)
            {
                _listener.Close();
                _listener = new HttpListener();
            }
        }
        throw new InvalidOperationException("No free port for the local server.");
    }

    public string Url { get; } = "";

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    private void Serve(HttpListenerContext ctx)
    {
        try
        {
            var rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
            var path = Path.GetFullPath(Path.Combine(_root, rel.Length == 0 ? "index.html" : rel));
            if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) path = Path.Combine(_root, "index.html");
            if (Directory.Exists(path)) path = Path.Combine(path, "index.html");
            if (!File.Exists(path) && !Path.HasExtension(path)) path = Path.Combine(_root, "index.html");
            if (!File.Exists(path))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            var bytes = File.ReadAllBytes(path);
            ctx.Response.ContentType = Types.TryGetValue(Path.GetExtension(path), out var t) ? t : "application/octet-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
        catch (Exception e)
        {
            _log($"Server: {e.Message}", LogKind.Warning);
            try { ctx.Response.Abort(); } catch { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
