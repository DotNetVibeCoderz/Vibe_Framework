using System.Diagnostics;
using JSNet.Compiler;
using JSNet.Scripting;
using JSNet.Syntax;

namespace JSNet.Cli;

/// <summary>
/// <c>jsnet</c> — the JS.Net command-line tool. The <c>compile</c> command is what the JSNet.Sdk MSBuild
/// targets invoke during <c>dotnet build</c>; the others are for developers.
/// </summary>
public static class Program
{
    private const string Banner = "JS.Net — JavaScript/TypeScript syntax, .NET performance. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Help();
        string command = args[0];
        var rest = args.Skip(1).ToArray();
        try
        {
            return command switch
            {
                "compile" => Compile(rest),
                "run" => await Run(rest),
                "emit" => Emit(rest),
                "check" => Check(rest),
                "fmt" => Format(rest),
                "lsp" => await JSNet.LanguageServer.LanguageServerHost.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput()),
                "new" => DotnetNew(rest),
                "--version" or "version" => Version(),
                "info" => Info(),
                _ when command.EndsWith(".jsn", StringComparison.OrdinalIgnoreCase) => await Run(args),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"jsnet: error JSN3000: {ex.Message}");
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine(Banner);
        Console.WriteLine();
        Console.WriteLine("Usage: jsnet <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  run <file.jsn|dir> [-- args]   Compile in memory and run (no project needed)");
        Console.WriteLine("  emit <file.jsn>                Print the generated C# (what Roslyn compiles to IL)");
        Console.WriteLine("  check <files|dirs>             Report diagnostics without generating code");
        Console.WriteLine("  compile [options] <files>      Generate C# for MSBuild (used by JSNet.Sdk)");
        Console.WriteLine("  fmt <files|dirs> [--check]     Format code with the native (Rust) jsnet-tools formatter");
        Console.WriteLine("  new <template> [args]          Shortcut for 'dotnet new jsnet-<template>'");
        Console.WriteLine("  lsp                            Start the JS.Net language server on stdio");
        Console.WriteLine("  info | --version               Show version information");
        Console.WriteLine();
        Console.WriteLine("compile options:");
        Console.WriteLine("  --out <dir>  --root-namespace <ns>  --project-dir <dir>  --library  --no-line  --nowarn <codes>  @<response-file>");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"jsnet: unknown command '{command}'. Run 'jsnet --help'.");
        return 1;
    }

    private static int Version()
    {
        Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        return 0;
    }

    private static int Info()
    {
        Console.WriteLine(Banner);
        Console.WriteLine($"  jsnet      {typeof(Program).Assembly.GetName().Version?.ToString(3)}");
        Console.WriteLine($"  .NET       {Environment.Version}");
        Console.WriteLine($"  OS         {Environment.OSVersion}");
        Console.WriteLine($"  rust tools {RustTools.Locate() ?? "(not found — jsnet fmt unavailable)"}");
        return 0;
    }

    /// <summary>Expands '@file.rsp' arguments (one argument per line) as MSBuild passes long file lists that way.</summary>
    private static List<string> ExpandResponseFiles(IEnumerable<string> args)
    {
        var result = new List<string>();
        foreach (var a in args)
        {
            if (a.StartsWith('@') && File.Exists(a[1..]))
                result.AddRange(File.ReadAllLines(a[1..]).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
            else result.Add(a);
        }
        return result;
    }

    private static IEnumerable<string> ExpandInputs(IEnumerable<string> inputs)
    {
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                foreach (var f in Directory.EnumerateFiles(input, "*.jsn", SearchOption.AllDirectories)
                             .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                             .OrderBy(f => f, StringComparer.Ordinal))
                    yield return f;
            }
            else yield return input;
        }
    }

    private static int Compile(string[] rawArgs)
    {
        var args = ExpandResponseFiles(rawArgs);
        string? outDir = null, rootNs = null, projectDir = null;
        bool library = false, lines = true;
        var nowarn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = args[++i]; break;
                case "--root-namespace": rootNs = args[++i]; break;
                case "--project-dir": projectDir = args[++i]; break;
                case "--library": library = true; break;
                case "--no-line": lines = false; break;
                case "--nowarn":
                    foreach (var c in args[++i].Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) nowarn.Add(c);
                    break;
                default: files.Add(args[i]); break;
            }
        }
        if (outDir is null)
        {
            Console.Error.WriteLine("jsnet: error JSN3000: --out <dir> is required.");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var result = JsNetCompiler.CompileFiles(ExpandInputs(files), new JsNetCompilationOptions
        {
            RootNamespace = rootNs ?? "JsNetApp",
            ProjectDirectory = projectDir is null ? null : Path.GetFullPath(projectDir),
            IsLibrary = library,
            EmitLineDirectives = lines,
            SuppressedDiagnostics = nowarn,
        });
        PrintDiagnostics(result.Diagnostics);
        if (!result.Success) return 1;

        Directory.CreateDirectory(outDir);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in result.Files)
        {
            string path = Path.Combine(outDir, f.HintName);
            expected.Add(Path.GetFullPath(path));
            // Only touch files whose content changed so incremental builds stay incremental.
            if (!File.Exists(path) || File.ReadAllText(path) != f.Text) File.WriteAllText(path, f.Text);
        }
        foreach (var stale in Directory.EnumerateFiles(outDir, "*.g.cs"))
            if (!expected.Contains(Path.GetFullPath(stale))) File.Delete(stale);

        Console.WriteLine($"jsnet: compiled {result.Units.Count} file(s) in {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    private static void PrintDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            var writer = d.Severity == DiagnosticSeverity.Error ? Console.Error : Console.Out;
            writer.WriteLine(d.ToString());
        }
    }

    private static async Task<int> Run(string[] args)
    {
        int sep = Array.IndexOf(args, "--");
        var inputs = sep < 0 ? args : args[..sep];
        var programArgs = sep < 0 ? [] : args[(sep + 1)..];
        var files = ExpandInputs(inputs).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("jsnet: error JSN3001: no .jsn input files.");
            return 1;
        }
        var sources = files.Select(SourceText.FromFile).ToList();
        var result = await JsNetScript.RunAsync(sources, new ScriptOptions { Arguments = programArgs, Timeout = TimeSpan.FromDays(1) });
        foreach (var d in result.Diagnostics) Console.Error.WriteLine(d);
        if (result.Exception is not null)
        {
            Console.Error.WriteLine($"Uncaught {result.Exception.GetType().Name}: {result.Exception.Message}");
            if (Environment.GetEnvironmentVariable("JSNET_TRACE") == "1") Console.Error.WriteLine(result.Exception.StackTrace);
            return 1;
        }
        return result.Success ? 0 : 1;
    }

    private static int Emit(string[] args)
    {
        var files = ExpandInputs(args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))).ToList();
        bool lines = args.Contains("--line");
        var result = JsNetCompiler.CompileFiles(files, new JsNetCompilationOptions { EmitLineDirectives = lines, RootNamespace = "JsNetApp" });
        PrintDiagnostics(result.Diagnostics);
        foreach (var f in result.Files.Where(f => args.Contains("--all") || f.HintName != JsNetCompiler.GlobalUsingsFileName))
        {
            Console.WriteLine($"// ===== {f.HintName} =====");
            Console.WriteLine(f.Text);
        }
        return result.Success ? 0 : 1;
    }

    private static int Check(string[] args)
    {
        var files = ExpandInputs(args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))).ToList();
        var result = JsNetCompiler.CompileFiles(files, new JsNetCompilationOptions { IsLibrary = args.Contains("--library") });
        PrintDiagnostics(result.Diagnostics);
        int errors = result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        Console.WriteLine($"jsnet: {files.Count} file(s), {errors} error(s), {warnings} warning(s)");
        return errors > 0 ? 1 : 0;
    }

    private static int Format(string[] args)
    {
        var tool = RustTools.Locate();
        if (tool is null)
        {
            Console.Error.WriteLine("jsnet: error JSN3003: the native formatter 'jsnet-tools' was not found. Build rust/jsnet-tools or set JSNET_TOOLS_PATH.");
            return 1;
        }
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false };
        psi.ArgumentList.Add("fmt");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    private static int DotnetNew(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Templates: console, classlib, webapi, web, blazor, wpf, winforms, avalonia, worker, xunit");
            return 0;
        }
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        psi.ArgumentList.Add("new");
        psi.ArgumentList.Add(args[0] == "console" ? "jsnet" : "jsnet-" + args[0]);
        foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}

/// <summary>Finds the native Rust helper (<c>jsnet-tools</c>): env var, next to this tool, or on PATH.</summary>
internal static class RustTools
{
    public static string? Locate()
    {
        string exe = OperatingSystem.IsWindows() ? "jsnet-tools.exe" : "jsnet-tools";
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("JSNET_TOOLS_PATH"),
            Path.Combine(AppContext.BaseDirectory, exe),
            Path.Combine(AppContext.BaseDirectory, "native", exe),
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, exe)));
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }
}
