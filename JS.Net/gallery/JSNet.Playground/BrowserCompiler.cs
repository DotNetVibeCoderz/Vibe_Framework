using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using JSNet.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using JsSourceText = JSNet.Syntax.SourceText;

namespace JSNet.Playground;

public sealed record PlaygroundResult(bool Success, string Output, string Lowered, IReadOnlyList<string> Diagnostics, double CompileMs, double RunMs);

/// <summary>
/// Compiles JS.Net to IL inside the browser: the JS.Net front end lowers to C#, Roslyn emits IL against metadata read
/// straight from the assemblies already loaded in the WebAssembly runtime, and the result is loaded and executed.
/// </summary>
public static class BrowserCompiler
{
    private static MetadataReference[]? _references;

    private static readonly string[] ExtraAssemblies =
    [
        "System.Runtime", "netstandard", "System.Collections", "System.Linq", "System.Console", "System.Threading",
        "System.Threading.Tasks", "System.Text.RegularExpressions", "System.Linq.Expressions", "Microsoft.CSharp",
        "System.Collections.Concurrent", "System.ComponentModel.Primitives", "System.Text.Json", "System.Memory",
        "System.Runtime.InteropServices", "System.ObjectModel", "System.Net.Http", "System.Numerics.Vectors",
        "System.Runtime.Numerics", "System.Dynamic.Runtime", "System.Text.Encoding.Extensions",
    ];

    private static unsafe MetadataReference[] References()
    {
        if (_references is not null) return _references;
        GC.KeepAlive(typeof(JSNet.Runtime.JS));
        foreach (var name in ExtraAssemblies)
        {
            try { Assembly.Load(name); } catch { /* not part of this runtime */ }
        }
        var list = new List<MetadataReference>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || assembly.GetName().Name is not { } n || n.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)) continue;
            if (assembly.TryGetRawMetadata(out byte* blob, out int length))
                list.Add(AssemblyMetadata.Create(ModuleMetadata.CreateFromMetadata((IntPtr)blob, length)).GetReference());
        }
        return _references = [.. list];
    }

    public static async Task<PlaygroundResult> RunAsync(string code)
    {
        var sw = Stopwatch.StartNew();
        var front = JsNetCompiler.Compile([new JsSourceText(code, "playground.jsn")], new JsNetCompilationOptions { RootNamespace = "Playground" });
        var diagnostics = front.Diagnostics.Select(d => d.ToString()).ToList();
        string lowered = string.Join("\n\n", front.Files.Where(f => f.HintName != JsNetCompiler.GlobalUsingsFileName)
            .Select(f => string.Join("\n", f.Text.Split('\n').Where(l => !l.TrimStart().StartsWith("#line", StringComparison.Ordinal)))));
        if (!front.Success) return new PlaygroundResult(false, "", lowered, diagnostics, sw.Elapsed.TotalMilliseconds, 0);

        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = front.Files.Select(f => CSharpSyntaxTree.ParseText(f.Text, parse, f.HintName)).ToList();
        var compilation = CSharpCompilation.Create("Playground_" + Guid.NewGuid().ToString("N"), trees, References(),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release,
                concurrentBuild: false, nullableContextOptions: NullableContextOptions.Disable));
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        diagnostics.AddRange(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d =>
        {
            var span = d.Location.GetMappedLineSpan();
            return $"{Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): error {d.Id}: {d.GetMessage()}";
        }));
        double compileMs = sw.Elapsed.TotalMilliseconds;
        if (!emit.Success) return new PlaygroundResult(false, "", lowered, diagnostics, compileMs, 0);

        sw.Restart();
        var assembly = Assembly.Load(pe.ToArray());
        var writer = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(writer);
        JSNet.Runtime.console.setOut(writer);
        bool ok = true;
        try
        {
            var entry = assembly.EntryPoint!;
            // An async Main gets a synthesized `<Main>` that blocks on the task, which the single-threaded
            // WebAssembly runtime cannot do; call the user's async Main and await it instead.
            if (entry.Name == "<Main>" && entry.DeclaringType?.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is { } asyncMain)
                entry = asyncMain;
            var result = entry.Invoke(null, entry.GetParameters().Length == 0 ? [] : [Array.Empty<string>()]);
            if (result is Task task) await task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            ok = false;
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            writer.WriteLine($"Uncaught {inner.GetType().Name}: {inner.Message}");
        }
        finally
        {
            JSNet.Runtime.console.setOut(null);
            Console.SetOut(previous);
        }
        return new PlaygroundResult(ok, writer.ToString(), lowered, diagnostics, compileMs, sw.Elapsed.TotalMilliseconds);
    }
}
