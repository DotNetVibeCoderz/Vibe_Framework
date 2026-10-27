using System.Collections.Concurrent;
using JSNet.Compiler;
using JSNet.Semantics;
using JSNet.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SemanticModel = Microsoft.CodeAnalysis.SemanticModel;

namespace JSNet.LanguageServer;

/// <summary>One completion / hover entry for a .NET (or JS.Net) member.</summary>
public sealed record MemberInfo(string Name, int Kind, string Detail);

/// <summary>
/// Semantic member lookup for the language server. The receiver's CLR type comes from the JS.Net binder (annotations
/// and inference); members come from Roslyn over the project's generated C#, the .NET platform, JSNet.Runtime (so
/// JavaScript-style extension members appear) and the assemblies in the project's <c>bin</c> folder (NuGet packages).
/// </summary>
public static class DotNetMembers
{
    private static readonly Lazy<MetadataReference[]> Platform = new(() =>
    {
        var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !Path.GetFileName(p).StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
            .ToList();
        string runtime = typeof(JSNet.Runtime.JS).Assembly.Location;
        if (!paths.Contains(runtime, StringComparer.OrdinalIgnoreCase)) paths.Add(runtime);
        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    });

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, MetadataReference[] Refs)> BinCache = new(StringComparer.OrdinalIgnoreCase);

    private const string ProbeMarker = "/*__jsnet_probe__*/";

    /// <summary>Members available after <c>receiver.</c> at <paramref name="offset"/> in <paramref name="unit"/>.</summary>
    public static IReadOnlyList<MemberInfo> Complete(JsNetCompilationResult result, CompilationUnit unit, string receiver, int offset)
    {
        var resolved = Resolve(result, unit, receiver, offset);
        if (resolved is null) return [];
        var (model, position, container, isStatic) = resolved.Value;
        var symbols = isStatic
            ? model.LookupStaticMembers(position, container)
            : model.LookupSymbols(position, container, includeReducedExtensionMethods: true);
        return symbols
            .Where(s => s.CanBeReferencedByName && s.DeclaredAccessibility is Accessibility.Public or Accessibility.NotApplicable
                        && s.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event or SymbolKind.NamedType or SymbolKind.Namespace
                        && !(s is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.ReducedExtension) }))
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => new MemberInfo(g.Key, KindOf(g.First()), Detail(g.First(), g.Count())))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Signature of <c>receiver.member</c> for hover.</summary>
    public static string? Describe(JsNetCompilationResult result, CompilationUnit unit, string receiver, string member, int offset)
    {
        var resolved = Resolve(result, unit, receiver, offset);
        if (resolved is null) return null;
        var (model, position, container, isStatic) = resolved.Value;
        var symbols = (isStatic ? model.LookupStaticMembers(position, container, member) : model.LookupSymbols(position, container, member, includeReducedExtensionMethods: true))
            .Where(s => s.CanBeReferencedByName).ToList();
        if (symbols.Count == 0) return null;
        var lines = symbols.Take(6).Select(s => s.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)).ToList();
        if (symbols.Count > 6) lines.Add($"(+{symbols.Count - 6} overloads)");
        return string.Join("\n", lines);
    }

    private static int KindOf(ISymbol s) => s switch
    {
        IMethodSymbol => 2,
        IPropertySymbol => 10,
        IFieldSymbol { IsConst: true } => 21,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => 20,
        IFieldSymbol => 5,
        IEventSymbol => 23,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => 8,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => 13,
        INamedTypeSymbol => 7,
        INamespaceSymbol => 9,
        _ => 1,
    };

    private static string Detail(ISymbol s, int overloads)
    {
        string text = s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return overloads > 1 ? $"{text} (+{overloads - 1} overloads)" : text;
    }

    /// <summary>Builds a Roslyn compilation and returns the receiver's type (or namespace) and a lookup position.</summary>
    private static (SemanticModel Model, int Position, INamespaceOrTypeSymbol Container, bool IsStatic)? Resolve(
        JsNetCompilationResult result, CompilationUnit unit, string receiver, int offset)
    {
        var parts = receiver.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        string? rootType = RootType(result, unit, parts[0], offset);
        bool isStatic = rootType is null;
        string typeExpression = rootType ?? parts[0];

        // Usings of the document (imports, aliases) plus the project-wide global usings.
        var generated = result.Files.FirstOrDefault(f => string.Equals(f.SourcePath, unit.Source.Path, StringComparison.OrdinalIgnoreCase));
        var usings = generated?.Text.Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("using ", StringComparison.Ordinal) && l.EndsWith(';')) ?? [];
        string probe = string.Join("\n", usings) + $$"""

            namespace __JsNetLsp
            {
                static class __Probe
                {
                    static void __M()
                    {
                        {{(isStatic ? $"_ = typeof({typeExpression});" : $"{typeExpression} __r = default!;")}}
                        {{ProbeMarker}}
                    }
                }
            }
            """;

        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = result.Files.Select(f => CSharpSyntaxTree.ParseText(f.Text, parse, f.HintName)).ToList();
        var probeTree = CSharpSyntaxTree.ParseText(probe, parse, "__probe.cs");
        trees.Add(probeTree);
        var references = Platform.Value.Concat(BinReferences(unit.Source.Path)).ToList();
        var compilation = CSharpCompilation.Create("__jsnet_lsp", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
        var model = compilation.GetSemanticModel(probeTree);
        var root = probeTree.GetRoot();
        int position = probe.IndexOf(ProbeMarker, StringComparison.Ordinal);

        INamespaceOrTypeSymbol? container;
        if (isStatic)
        {
            var typeOf = root.DescendantNodes().OfType<TypeOfExpressionSyntax>().First();
            container = model.GetSymbolInfo(typeOf.Type).Symbol as INamespaceOrTypeSymbol;
            // A namespace (System.IO.) does not bind inside typeof; resolve it directly.
            container ??= ResolveNamespace(compilation, model, position, typeExpression);
        }
        else
        {
            var local = root.DescendantNodes().OfType<VariableDeclarationSyntax>().First();
            container = model.GetTypeInfo(local.Type).Type;
        }
        if (container is null or IErrorTypeSymbol) return null;

        // Walk the rest of the chain: receiver.member.member.
        for (int i = 1; i < parts.Length; i++)
        {
            var next = (isStatic ? model.LookupStaticMembers(position, container, parts[i]) : model.LookupSymbols(position, container, parts[i], includeReducedExtensionMethods: true))
                .FirstOrDefault();
            switch (next)
            {
                case IPropertySymbol prop: container = prop.Type; isStatic = false; break;
                case IFieldSymbol field: container = field.Type; isStatic = false; break;
                case INamespaceOrTypeSymbol nested: container = nested; isStatic = true; break;
                default: return null;
            }
        }
        return (model, position, container, isStatic);
    }

    private static INamespaceSymbol? ResolveNamespace(CSharpCompilation compilation, SemanticModel model, int position, string name)
    {
        INamespaceSymbol? ns = compilation.GlobalNamespace;
        foreach (var part in name.Split('.'))
        {
            ns = ns?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (ns is null) return null;
        }
        return ns;
    }

    /// <summary>CLR type text of a JS.Net identifier visible at <paramref name="offset"/>, or null when it is not a value.</summary>
    private static string? RootType(JsNetCompilationResult result, CompilationUnit unit, string name, int offset)
    {
        var model = result.Model;
        if (name == "this")
            return unit.Statements.OfType<ClassDeclaration>().LastOrDefault(c => c.Span.Start <= offset && offset <= c.Span.End)?.Name;

        string? best = null;
        int bestStart = -1;
        foreach (var node in AstWalker.DescendantsAndSelf(unit))
        {
            if (node.Span.Start > offset) continue;
            switch (node)
            {
                case VariableDeclarator { Target: IdentifierPattern ip } d when ip.Name == name && d.Span.Start > bestStart:
                    best = d.Type is not null ? TypeMapper.Map(d.Type) : model?.GetInferredType(d)
                        ?? (d.Init is NewExpression n ? TypeMapper.Map(n.Type) : null);
                    bestStart = d.Span.Start;
                    break;
                case Parameter p when p.Name == name && p.Span.Start > bestStart:
                    best = p.Type is not null ? TypeMapper.Map(p.Type) : model?.GetInferredType(p);
                    bestStart = p.Span.Start;
                    break;
                case ForOfStatement { Target: IdentifierPattern fp } fo when fp.Name == name && fo.Span.Start > bestStart && fo.Span.End >= offset:
                    best = fo.Type is not null ? TypeMapper.Map(fo.Type) : null;
                    bestStart = fo.Span.Start;
                    break;
            }
        }
        if (best is "dynamic") return null;
        return best;
    }

    /// <summary>Assemblies built for the document's project (NuGet dependencies), so their APIs complete too.</summary>
    private static IEnumerable<MetadataReference> BinReferences(string documentPath)
    {
        string? dir = Path.GetDirectoryName(documentPath);
        string? projectFile = null;
        while (dir is not null)
        {
            projectFile = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault() : null;
            if (projectFile is not null) break;
            dir = Path.GetDirectoryName(dir);
        }
        if (projectFile is null) return [];
        string bin = Path.Combine(Path.GetDirectoryName(projectFile)!, "bin");
        if (!Directory.Exists(bin)) return [];
        string projectName = Path.GetFileNameWithoutExtension(projectFile);
        var own = Directory.EnumerateFiles(bin, projectName + ".dll", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (own is null) return [];
        if (BinCache.TryGetValue(own.DirectoryName!, out var cached) && cached.Stamp == own.LastWriteTimeUtc) return cached.Refs;

        var platformNames = new HashSet<string>(Platform.Value.OfType<PortableExecutableReference>().Select(r => Path.GetFileName(r.FilePath!)), StringComparer.OrdinalIgnoreCase);
        var refs = Directory.EnumerateFiles(own.DirectoryName!, "*.dll")
            .Where(f => !string.Equals(Path.GetFileNameWithoutExtension(f), projectName, StringComparison.OrdinalIgnoreCase) && !platformNames.Contains(Path.GetFileName(f)))
            .Select(f =>
            {
                try { return (MetadataReference?)MetadataReference.CreateFromFile(f); }
                catch { return null; }
            })
            .OfType<MetadataReference>()
            .ToArray();
        BinCache[own.DirectoryName!] = (own.LastWriteTimeUtc, refs);
        return refs;
    }
}
