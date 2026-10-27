using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using JSNet.Compiler;
using JSNet.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace JSNet.Scripting;

public sealed class ScriptOptions
{
    public string[] Arguments { get; init; } = [];
    /// <summary>Capture <c>console.*</c> and <see cref="Console.Out"/> output instead of writing to the terminal.</summary>
    public bool CaptureOutput { get; init; }
    public IReadOnlyList<Assembly> AdditionalReferences { get; init; } = [];
    public bool Optimize { get; init; } = true;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class ScriptResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public IReadOnlyList<GeneratedFile> GeneratedFiles { get; init; } = [];
    public Exception? Exception { get; init; }
    public TimeSpan CompileTime { get; init; }
    public TimeSpan RunTime { get; init; }
}

/// <summary>
/// Compiles JS.Net source straight to an in-memory assembly with Roslyn and runs it in a collectible
/// <see cref="AssemblyLoadContext"/>. Powers <c>jsnet run</c>, the test-suite and the Gallery playground.
/// </summary>
public static class JsNetScript
{
    private static readonly Lazy<MetadataReference[]> PlatformReferences = new(() =>
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return tpa.Where(p => !p.Contains("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    });

    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    public static Task<ScriptResult> RunFileAsync(string path, ScriptOptions? options = null) =>
        RunAsync([SourceText.FromFile(path)], options);

    public static Task<ScriptResult> RunAsync(string code, string fileName = "script.jsn", ScriptOptions? options = null) =>
        RunAsync([new SourceText(code, fileName)], options);

    public static async Task<ScriptResult> RunAsync(IReadOnlyList<SourceText> sources, ScriptOptions? options = null)
    {
        options ??= new ScriptOptions();
        var sw = Stopwatch.StartNew();
        var compiled = Compile(sources, options);
        var compileTime = sw.Elapsed;
        if (compiled.Assembly is null)
            return new ScriptResult { Success = false, Diagnostics = compiled.Diagnostics, GeneratedFiles = compiled.Files, CompileTime = compileTime };

        var entry = compiled.Assembly.EntryPoint;
        if (entry is null)
            return new ScriptResult { Success = false, Diagnostics = ["error JSN3002: The script has no entry point (add top-level statements)."], CompileTime = compileTime };

        sw.Restart();
        Exception? failure = null;
        string output = string.Empty;

        async Task Invoke()
        {
            try
            {
                object?[] args = entry.GetParameters().Length == 0 ? [] : [options.Arguments];
                var result = entry.Invoke(null, args);
                if (result is Task t)
                {
                    var finished = await Task.WhenAny(t, Task.Delay(options.Timeout)).ConfigureAwait(false);
                    if (finished != t) throw new TimeoutException($"Script exceeded the {options.Timeout.TotalSeconds:0}s time limit.");
                    await t.ConfigureAwait(false);
                }
            }
            catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
            catch (Exception ex) { failure = ex; }
        }

        if (options.CaptureOutput)
        {
            await ConsoleGate.WaitAsync().ConfigureAwait(false);
            var previous = Console.Out;
            var writer = new StringWriter();
            try
            {
                // One writer for both Console.Out and console.* keeps the output in order.
                Console.SetOut(writer);
                JSNet.Runtime.console.setOut(writer);
                await Invoke().ConfigureAwait(false);
            }
            finally
            {
                JSNet.Runtime.console.setOut(null);
                Console.SetOut(previous);
                ConsoleGate.Release();
            }
            output = writer.ToString();
        }
        else await Invoke().ConfigureAwait(false);

        compiled.Context?.Unload();
        return new ScriptResult
        {
            Success = failure is null,
            Output = output,
            Exception = failure,
            Diagnostics = compiled.Diagnostics,
            GeneratedFiles = compiled.Files,
            CompileTime = compileTime,
            RunTime = sw.Elapsed,
        };
    }

    public sealed record CompiledScript(Assembly? Assembly, AssemblyLoadContext? Context, IReadOnlyList<string> Diagnostics, IReadOnlyList<GeneratedFile> Files);

    /// <summary>JS.Net → C# → IL (in memory).</summary>
    public static CompiledScript Compile(IReadOnlyList<SourceText> sources, ScriptOptions options)
    {
        var front = JsNetCompiler.Compile(sources, new JsNetCompilationOptions { RootNamespace = "JsNetScript" });
        var messages = front.Diagnostics.Select(d => d.ToString()).ToList();
        if (!front.Success) return new CompiledScript(null, null, messages, front.Files);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = front.Files.Select(f => CSharpSyntaxTree.ParseText(f.Text, parseOptions, path: f.HintName, encoding: System.Text.Encoding.UTF8));
        var references = PlatformReferences.Value.ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(JSNet.Runtime.JS).Assembly.Location));
        foreach (var asm in options.AdditionalReferences)
            if (!string.IsNullOrEmpty(asm.Location)) references.Add(MetadataReference.CreateFromFile(asm.Location));

        var compilation = CSharpCompilation.Create(
            "JsNetScript_" + Guid.NewGuid().ToString("N"),
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                optimizationLevel: options.Optimize ? OptimizationLevel.Release : OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: false));

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult emit = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        messages.AddRange(emit.Diagnostics
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(FormatRoslyn));
        if (!emit.Success) return new CompiledScript(null, null, messages, front.Files);

        pe.Position = 0;
        pdb.Position = 0;
        var context = new AssemblyLoadContext("jsnet-script", isCollectible: true);
        context.Resolving += (ctx, name) =>
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name);
            return loaded;
        };
        var assembly = context.LoadFromStream(pe, pdb);
        return new CompiledScript(assembly, context, messages, front.Files);
    }

    private static string FormatRoslyn(Microsoft.CodeAnalysis.Diagnostic d)
    {
        var span = d.Location.GetMappedLineSpan();
        string file = span.HasMappedPath ? span.Path : Path.GetFileName(span.Path);
        return $"{file}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): error {d.Id}: {d.GetMessage()}";
    }
}
