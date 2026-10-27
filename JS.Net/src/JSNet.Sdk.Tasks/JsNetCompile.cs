using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace JSNet.Sdk.Tasks;

/// <summary>
/// Generates C# from .jsn files (<c>jsnet compile</c>). By default the work goes to a JS.Net build server: a long-lived
/// compiler process that stays loaded and JIT-compiled between builds, reached over a named pipe. The first build
/// starts it; it exits after a period without builds. When the server cannot be used, the task runs the compiler as
/// a child process, exactly like before.
/// </summary>
public sealed class JsNetCompile : Task
{
    [Required] public string CompilerPath { get; set; } = string.Empty;
    [Required] public string OutputDirectory { get; set; } = string.Empty;
    [Required] public string ResponseFile { get; set; } = string.Empty;
    public string DotnetHost { get; set; } = "dotnet";
    public string RootNamespace { get; set; } = string.Empty;
    public string ProjectDirectory { get; set; } = string.Empty;
    public bool IsLibrary { get; set; }
    public bool EmitLineDirectives { get; set; } = true;
    public string NoWarn { get; set; } = string.Empty;
    public ITaskItem[] Analyzers { get; set; } = [];
    public bool UseBuildServer { get; set; } = true;

    /// <summary>"server" or "process": how the compiler ran (for diagnostics and tests).</summary>
    [Output] public string CompilerMode { get; set; } = string.Empty;

    public override bool Execute()
    {
        var args = Arguments();
        bool useServer = UseBuildServer && Environment.GetEnvironmentVariable("JSNET_NO_BUILD_SERVER") is not ("1" or "true");
        (int ExitCode, List<OutputLine> Lines)? result = useServer ? BuildServerClient.TryCompile(CompilerPath, DotnetHost, args, Log) : null;
        CompilerMode = result is null ? "process" : "server";
        result ??= RunProcess(args);

        foreach (var line in result.Value.Lines)
            Log.LogMessageFromText(line.Text, MessageImportance.Low);   // canonical "file(l,c): error X: ..." lines become errors/warnings
        if (result.Value.ExitCode != 0 && !Log.HasLoggedErrors)
            Log.LogError(null, "JSN3000", null, null, 0, 0, 0, 0, $"The JS.Net compiler exited with code {result.Value.ExitCode}.");
        Log.LogMessage(MessageImportance.Low, $"JS.Net compiler mode: {CompilerMode}");
        return !Log.HasLoggedErrors;
    }

    private List<string> Arguments()
    {
        var args = new List<string> { "compile", "--out", Path.GetFullPath(OutputDirectory) };
        if (RootNamespace.Length > 0) args.AddRange(["--root-namespace", RootNamespace]);
        if (ProjectDirectory.Length > 0) args.AddRange(["--project-dir", Path.GetFullPath(ProjectDirectory)]);
        if (IsLibrary) args.Add("--library");
        if (!EmitLineDirectives) args.Add("--no-line");
        if (NoWarn.Length > 0) args.AddRange(["--nowarn", NoWarn]);
        foreach (var a in Analyzers) args.AddRange(["--analyzer", a.GetMetadata("FullPath")]);
        args.Add("@" + Path.GetFullPath(ResponseFile));
        return args;
    }

    private (int, List<OutputLine>) RunProcess(List<string> args)
    {
        var psi = new ProcessStartInfo(DotnetHost, "exec " + Quote(CompilerPath) + " " + string.Join(" ", args.Select(Quote)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var lines = new List<OutputLine>();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(new OutputLine(false, e.Data)); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(new OutputLine(true, e.Data)); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return (process.ExitCode, lines);
    }

    /// <summary>Quotes one argument for the Windows/.NET command-line parser.</summary>
    internal static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}

/// <summary>Talks to (and if needed starts) the JS.Net build server for one compiler build.</summary>
public static class BuildServerClient
{
    /// <summary>Returns null when the server is unavailable; the caller then runs the compiler itself.</summary>
    public static (int ExitCode, List<OutputLine> Lines)? TryCompile(string compilerPath, string dotnetHost, IList<string> args, TaskLoggingHelper? log)
    {
        string pipe = BuildServerProtocol.PipeName(compilerPath);
        try
        {
            var client = Connect(pipe, 200);
            if (client is null)
            {
                Start(compilerPath, dotnetHost, pipe, log);
                var sw = Stopwatch.StartNew();
                while (client is null && sw.Elapsed < TimeSpan.FromSeconds(20)) client = Connect(pipe, 500);
            }
            if (client is null)
            {
                log?.LogMessage(MessageImportance.Low, "JS.Net build server did not start; running the compiler directly.");
                return null;
            }
            using (client)
            {
                BuildServerProtocol.WriteRequest(client, Directory.GetCurrentDirectory(), args);
                return BuildServerProtocol.ReadResponse(client);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or EndOfStreamException or InvalidOperationException)
        {
            // Compilation is idempotent, so running it again directly is always safe.
            log?.LogMessage(MessageImportance.Low, $"JS.Net build server unavailable ({ex.Message}); running the compiler directly.");
            return null;
        }
    }

    /// <summary>Asks the server for this compiler build to exit. Returns false when none was running.</summary>
    public static bool Stop(string compilerPath)
    {
        using var client = Connect(BuildServerProtocol.PipeName(compilerPath), 500);
        if (client is null) return false;
        try
        {
            BuildServerProtocol.WriteRequest(client, Directory.GetCurrentDirectory(), [BuildServerProtocol.StopCommand]);
            BuildServerProtocol.ReadResponse(client);
            return true;
        }
        catch (IOException)
        {
            return false;   // a server that was already shutting down
        }
    }

    private static NamedPipeClientStream? Connect(string pipe, int timeoutMs)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None);
        try
        {
            client.Connect(timeoutMs);
            return client;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            client.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Starts the server from a private copy of the compiler (so rebuilding or updating the compiler never hits locked
    /// files), without inheriting MSBuild's handles (so the build never waits for the server to exit).
    /// </summary>
    private static void Start(string compilerPath, string dotnetHost, string pipe, TaskLoggingHelper? log)
    {
        string dir = ShadowCopy(Path.GetDirectoryName(Path.GetFullPath(compilerPath))!, pipe);
        string compiler = Path.Combine(dir, Path.GetFileName(compilerPath));
        string arguments = "exec " + JsNetCompile.Quote(compiler) + " server --pipe " + pipe;
        log?.LogMessage(MessageImportance.Low, $"Starting the JS.Net build server: {dotnetHost} {arguments}");
        ProcessLauncher.StartDetached(dotnetHost, arguments, dir);
    }

    private static string ShadowCopy(string sourceDir, string pipe)
    {
        string root = Path.Combine(Path.GetTempPath(), "jsnet-server");
        string target = Path.Combine(root, pipe);
        if (File.Exists(Path.Combine(target, ".complete"))) return target;
        CleanOldCopies(root);
        string staging = target + ".tmp-" + Guid.NewGuid().ToString("N");
        CopyDirectory(sourceDir, staging);
        File.WriteAllText(Path.Combine(staging, ".complete"), DateTime.UtcNow.ToString("o"));
        try { Directory.Move(staging, target); }
        catch (IOException)
        {
            // Another build made the same copy first; use theirs.
            TryDelete(staging);
            if (!File.Exists(Path.Combine(target, ".complete"))) return sourceDir;
        }
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(source)) CopyDirectory(sub, Path.Combine(target, Path.GetFileName(sub)));
    }

    /// <summary>
    /// Removes copies older than a day that no server is running from. A running server keeps its files open, so the
    /// folder cannot be renamed; renaming first means a copy in use is never half-deleted.
    /// </summary>
    internal static void CleanOldCopies(string root, TimeSpan? age = null)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (Directory.GetLastWriteTimeUtc(dir) > DateTime.UtcNow - (age ?? TimeSpan.FromDays(1))) continue;
            string doomed = dir + ".delete-" + Guid.NewGuid().ToString("N");
            try { Directory.Move(dir, doomed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }   // in use
            TryDelete(doomed);
        }
    }

    /// <summary>The folder a server for this compiler build runs from.</summary>
    internal static string ShadowDirectory(string compilerPath) =>
        Path.Combine(Path.GetTempPath(), "jsnet-server", BuildServerProtocol.PipeName(compilerPath));

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
