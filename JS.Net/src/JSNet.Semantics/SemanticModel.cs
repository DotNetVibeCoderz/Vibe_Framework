using JSNet.Syntax;

namespace JSNet.Semantics;

/// <summary>Project-wide declarations, collected before binding so files can reference each other freely.</summary>
public sealed class ProjectSymbols
{
    public Dictionary<string, ClassInfo> Classes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FunctionDeclaration> Functions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, VariableDeclarator> ModuleVariables { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, InterfaceDeclaration> Interfaces { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Enums { get; } = new(StringComparer.Ordinal);
}

public sealed class ClassInfo(ClassDeclaration declaration)
{
    public ClassDeclaration Declaration { get; } = declaration;
    public string? BaseName { get; } = declaration.Extends is NamedType n ? n.Name : null;
    public Dictionary<string, List<ClassMember>> Methods { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ClassMember> Fields { get; } = new(StringComparer.Ordinal);
}

/// <summary>Result of semantic analysis: inferred CLR types, symbol tables and diagnostics.</summary>
public sealed class SemanticModel
{
    internal SemanticModel(ProjectSymbols symbols, DiagnosticBag diagnostics)
    {
        Symbols = symbols;
        Diagnostics = diagnostics;
    }

    public ProjectSymbols Symbols { get; }
    public DiagnosticBag Diagnostics { get; }

    /// <summary>Inferred C# types keyed by declarator, field member or function (return type).</summary>
    internal Dictionary<Node, string> Types { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Compilation units that contain top-level executable statements (program entry).</summary>
    public HashSet<CompilationUnit> EntryUnits { get; } = new(ReferenceEqualityComparer.Instance);

    public string? GetInferredType(Node node) => Types.TryGetValue(node, out var t) ? t : null;

    /// <summary>Declarations initialised with an integer literal but later assigned fractional values (→ <c>double</c>).</summary>
    internal HashSet<Node> PromotedNumbers { get; } = new(ReferenceEqualityComparer.Instance);

    public bool IsPromotedToNumber(Node declaration) => PromotedNumbers.Contains(declaration);

    /// <summary>
    /// True when <paramref name="method"/> redefines a method of a JS.Net base class in the same project
    /// (JavaScript methods are implicitly overridable, so <c>override</c> is inferred).
    /// </summary>
    public bool OverridesProjectMethod(ClassDeclaration cls, ClassMember method)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? baseName = cls.Extends is NamedType n ? n.Name : null;
        while (baseName is not null && visited.Add(baseName) && Symbols.Classes.TryGetValue(baseName, out var info))
        {
            if (method.Kind is MemberKind.Getter or MemberKind.Setter)
            {
                if (info.Methods.TryGetValue(method.Name, out var accessors) && accessors.Any(a => a.Kind is MemberKind.Getter or MemberKind.Setter)) return true;
            }
            else if (info.Methods.TryGetValue(method.Name, out var list) && list.Any(m => m.Kind == MemberKind.Method && m.Parameters.Count == method.Parameters.Count && !m.IsStatic))
                return true;
            baseName = info.BaseName;
        }
        return false;
    }

    /// <summary>True when a project class is used as a base class (its members are emitted <c>virtual</c>).</summary>
    public bool IsExtended(string className) => Symbols.Classes.Values.Any(c => c.BaseName == className);
}
