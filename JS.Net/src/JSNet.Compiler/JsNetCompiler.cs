using System.Collections.Concurrent;
using JSNet.CodeGen;
using JSNet.Parsing;
using JSNet.Semantics;
using JSNet.Syntax;

namespace JSNet.Compiler;

public sealed class JsNetCompilationOptions
{
    public string RootNamespace { get; init; } = "JsNetApp";
    /// <summary>Used to derive folder-based namespaces; defaults to the common directory of the inputs.</summary>
    public string? ProjectDirectory { get; init; }
    public bool IsLibrary { get; init; }
    public bool EmitLineDirectives { get; init; } = true;
    public IReadOnlySet<string> SuppressedDiagnostics { get; init; } = new HashSet<string>();
}

/// <summary>A generated C# file. <see cref="HintName"/> is a unique, path-safe file name.</summary>
public sealed record GeneratedFile(string SourcePath, string HintName, string Text);

public sealed class JsNetCompilationResult
{
    public required IReadOnlyList<GeneratedFile> Files { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<CompilationUnit> Units { get; init; }
    public SemanticModel? Model { get; init; }
    public bool Success => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Entry point of the JS.Net front end. Parsing and emission run in parallel per file; binding is a single
/// project-wide pass so that files can reference each other's declarations without imports.
/// </summary>
public static class JsNetCompiler
{
    public const string GlobalUsingsFileName = "JSNet.GlobalUsings.g.cs";

    public static readonly string[] DefaultNamespaces =
    [
        "System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks", "JSNet.Runtime",
    ];

    public static JsNetCompilationResult CompileFiles(IEnumerable<string> paths, JsNetCompilationOptions options)
    {
        var missing = new List<Diagnostic>();
        var sources = new List<SourceText>();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                missing.Add(new Diagnostic(DiagnosticCodes.FileNotFound, DiagnosticSeverity.Error, $"Source file '{path}' was not found.", null, default));
                continue;
            }
            sources.Add(SourceText.FromFile(path));
        }
        var result = Compile(sources, options);
        if (missing.Count == 0) return result;
        return new JsNetCompilationResult
        {
            Files = result.Files,
            Diagnostics = missing.Concat(result.Diagnostics).ToList(),
            Units = result.Units,
            Model = result.Model,
        };
    }

    public static JsNetCompilationResult Compile(IReadOnlyList<SourceText> sources, JsNetCompilationOptions options)
    {
        // 1. Parse (parallel, one diagnostic bag per file to stay lock-free).
        var units = new CompilationUnit[sources.Count];
        var bags = new DiagnosticBag[sources.Count];
        Parallel.For(0, sources.Count, i =>
        {
            bags[i] = new DiagnosticBag();
            units[i] = Parser.ParseUnit(sources[i], bags[i]);
        });
        var diagnostics = new DiagnosticBag();
        foreach (var b in bags) diagnostics.AddRange(b);

        // 2. Bind (project-wide).
        var model = Binder.Bind(units, diagnostics, options.IsLibrary);

        // 3. Emit (parallel; the semantic model is read-only from here on).
        string projectDir = options.ProjectDirectory ?? CommonDirectory(sources.Select(s => s.Path));
        var typeNames = new HashSet<string>(model.Symbols.Classes.Keys.Concat(model.Symbols.Interfaces.Keys).Concat(model.Symbols.Enums), StringComparer.Ordinal);
        var contexts = units.Select(u => CreateContext(u, options, projectDir, typeNames, model)).ToArray();
        var files = new GeneratedFile[units.Length];
        var hintNames = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Parallel.For(0, units.Length, i =>
        {
            string text = CSharpEmitter.Emit(units[i], model, contexts[i]);
            files[i] = new GeneratedFile(units[i].Source.Path, HintName(units[i].Source.Path, projectDir, hintNames), text);
        });

        var all = files.ToList();
        all.Add(new GeneratedFile(string.Empty, GlobalUsingsFileName, GlobalUsings(units, contexts)));

        var filtered = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error || !options.SuppressedDiagnostics.Contains(d.Code))
            .OrderBy(d => d.Source?.Path, StringComparer.Ordinal).ThenBy(d => d.Span.Start)
            .ToList();
        return new JsNetCompilationResult { Files = all, Diagnostics = filtered, Units = units, Model = model };
    }

    private static EmitContext CreateContext(CompilationUnit unit, JsNetCompilationOptions options, string projectDir, HashSet<string> typeNames, SemanticModel model)
    {
        string ns = unit.Statements.OfType<NamespaceDeclaration>().FirstOrDefault()?.Name ?? FolderNamespace(options.RootNamespace, projectDir, unit.Source.Path);
        string module = CSharpNames.PascalCase(Path.GetFileNameWithoutExtension(unit.Source.Path));
        if (typeNames.Contains(module) || module == "Main" || ns.Split('.').Contains(module)) module += "Module";
        return new EmitContext
        {
            Namespace = ns,
            ModuleClassName = module,
            IsEntry = !options.IsLibrary && model.EntryUnits.Contains(unit),
            EmitLineDirectives = options.EmitLineDirectives,
            IsProgram = !options.IsLibrary && model.EntryUnits.Count == 0,
        };
    }

    private static string GlobalUsings(IReadOnlyList<CompilationUnit> units, EmitContext[] contexts)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("// <auto-generated>JS.Net global usings — Gravicode Studios.</auto-generated>\n");
        foreach (var ns in DefaultNamespaces) sb.Append("global using ").Append(ns).Append(";\n");
        sb.Append("global using static JSNet.Runtime.Globals;\n");
        foreach (var ns in contexts.Select(c => c.Namespace).Distinct(StringComparer.Ordinal))
            sb.Append("global using ").Append(ns).Append(";\n");
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i].Statements.Any(s => s is FunctionDeclaration or VariableStatement))
                sb.Append("global using static ").Append(contexts[i].Namespace).Append('.').Append(contexts[i].ModuleClassName).Append(";\n");
        }
        return sb.ToString();
    }

    private static string FolderNamespace(string root, string projectDir, string path)
    {
        string rootNs = string.IsNullOrWhiteSpace(root) ? "JsNetApp" : root;
        if (string.IsNullOrEmpty(projectDir) || path.StartsWith('<')) return rootNs;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        string rel = Path.GetRelativePath(projectDir, dir);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return rootNs;
        var parts = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Select(CSharpNames.PascalCase);
        return rootNs + "." + string.Join(".", parts);
    }

    private static string HintName(string path, string projectDir, ConcurrentDictionary<string, int> used)
    {
        string rel = path.StartsWith('<') || string.IsNullOrEmpty(projectDir) ? Path.GetFileName(path) : Path.GetRelativePath(projectDir, path);
        if (rel.StartsWith("..", StringComparison.Ordinal)) rel = Path.GetFileName(path);
        string baseName = rel.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.').Replace('<', '_').Replace('>', '_');
        int n = used.AddOrUpdate(baseName, 0, (_, v) => v + 1);
        return n == 0 ? baseName + ".g.cs" : $"{baseName}.{n}.g.cs";
    }

    private static string CommonDirectory(IEnumerable<string> paths)
    {
        string? common = null;
        foreach (var p in paths)
        {
            if (p.StartsWith('<')) continue;
            string dir = Path.GetDirectoryName(Path.GetFullPath(p)) ?? string.Empty;
            if (common is null) { common = dir; continue; }
            while (!dir.StartsWith(common, StringComparison.OrdinalIgnoreCase) && common.Length > 0)
                common = Path.GetDirectoryName(common) ?? string.Empty;
        }
        return common ?? string.Empty;
    }
}
