using System.Reflection;
using System.Runtime.Loader;
using JSNet.Syntax;

namespace JSNet.Analyzers;

/// <summary>
/// Base class of every JS.Net analyzer rule. In JS.Net:
/// <code>
/// rule NoConsoleLog {
///     id: "APP001";
///     severity: "warning";
///     description: "console.log should not ship in libraries";
///     check(node: Node, ctx: RuleContext): void {
///         if (node instanceof CallExpression &amp;&amp; node.Callee instanceof MemberExpression) { ... ctx.report(node, "..."); }
///     }
/// }
/// </code>
/// <see cref="check"/> is called for every node of every file.
/// </summary>
public abstract class Rule
{
    public abstract string id { get; }
    /// <summary><c>error</c>, <c>warning</c> (default) or <c>info</c>.</summary>
    public virtual string severity => "warning";
    public virtual string description => GetType().Name;
    public abstract void check(Node node, RuleContext ctx);
}

/// <summary>Passed to <see cref="Rule.check"/>; collects reports for the current file.</summary>
public sealed class RuleContext(SourceText source, Rule rule, DiagnosticBag diagnostics)
{
    public SourceText source { get; } = source;
    public string fileName => Path.GetFileName(source.Path);

    /// <summary>The source text of a node.</summary>
    public string text(Node node) => source.Text.Substring(node.Span.Start, Math.Min(node.Span.Length, source.Length - node.Span.Start));

    public void report(Node node, string? message = null)
    {
        var severity = rule.severity.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "info" => DiagnosticSeverity.Info,
            _ => DiagnosticSeverity.Warning,
        };
        diagnostics.Add(new Diagnostic(rule.id, severity, message ?? rule.description, source, node.Span));
    }
}

/// <summary>Runs rules over compilation units and loads rule assemblies.</summary>
public static class RuleRunner
{
    public static void Run(IEnumerable<Rule> rules, IEnumerable<CompilationUnit> units, DiagnosticBag diagnostics)
    {
        var list = rules.ToList();
        if (list.Count == 0) return;
        foreach (var unit in units)
        {
            var contexts = list.Select(r => (Rule: r, Ctx: new RuleContext(unit.Source, r, diagnostics))).ToList();
            foreach (var node in AstWalker.DescendantsAndSelf(unit))
            {
                foreach (var (rule, ctx) in contexts)
                {
                    try { rule.check(node, ctx); }
                    catch (Exception ex)
                    {
                        diagnostics.Add(new Diagnostic("JSN2900", DiagnosticSeverity.Warning,
                            $"Analyzer rule '{rule.id}' failed: {ex.Message}", unit.Source, node.Span));
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Loads every concrete <see cref="Rule"/> from an assembly built from JS.Net (or C#) rule sources. The assembly is read
    /// into memory and loaded into a collectible context, so a long-lived compiler (the build server) never locks the file.
    /// </summary>
    public static IReadOnlyList<Rule> Load(string assemblyPath)
    {
        var context = new RulesLoadContext(Path.GetFullPath(assemblyPath));
        var assembly = context.LoadFromBytes(Path.GetFullPath(assemblyPath));
        return FromAssembly(assembly);
    }

    public static IReadOnlyList<Rule> FromAssembly(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => typeof(Rule).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (Rule)Activator.CreateInstance(t)!)
            .ToList();

    /// <summary>Shares the compiler's JS.Net assemblies with rule assemblies; resolves their other dependencies locally.</summary>
    private sealed class RulesLoadContext(string mainPath) : AssemblyLoadContext("jsnet-rules", isCollectible: true)
    {
        public Assembly LoadFromBytes(string path)
        {
            using var image = new MemoryStream(File.ReadAllBytes(path));
            return LoadFromStream(image);
        }

        private readonly AssemblyDependencyResolver _resolver = new(mainPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is { } n && n.StartsWith("JSNet.", StringComparison.Ordinal))
            {
                var shared = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == n);
                if (shared is not null) return shared;
            }
            string? path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromBytes(path);
        }
    }
}
