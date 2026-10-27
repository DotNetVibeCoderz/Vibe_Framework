using System.Text;
using JSNet.Syntax;

namespace JSNet.Semantics;

/// <summary>Maps JS.Net type syntax to C#/CLR type syntax (<c>number</c> → <c>double</c>, <c>Promise&lt;T&gt;</c> → <c>Task&lt;T&gt;</c>, ...).</summary>
public static class TypeMapper
{
    private static readonly Dictionary<string, string> Primitives = new(StringComparer.Ordinal)
    {
        ["number"] = "double",
        ["int"] = "int",
        ["long"] = "long",
        ["float"] = "float",
        ["double"] = "double",
        ["decimal"] = "decimal",
        ["byte"] = "byte",
        ["sbyte"] = "sbyte",
        ["short"] = "short",
        ["ushort"] = "ushort",
        ["uint"] = "uint",
        ["ulong"] = "ulong",
        ["char"] = "char",
        ["bool"] = "bool",
        ["boolean"] = "bool",
        ["string"] = "string",
        ["object"] = "object",
        ["Object"] = "object",
        ["any"] = "dynamic",
        ["unknown"] = "object",
        ["dynamic"] = "dynamic",
        ["void"] = "void",
        ["never"] = "void",
        ["undefined"] = "object",
        ["null"] = "object",
        ["symbol"] = "object",
        ["bigint"] = "global::System.Numerics.BigInteger",
        ["Function"] = "global::System.Delegate",
        ["RegExp"] = "global::System.Text.RegularExpressions.Regex",
    };

    private static readonly Dictionary<string, string> Generics = new(StringComparer.Ordinal)
    {
        ["Array"] = "global::System.Collections.Generic.List",
        ["ReadonlyArray"] = "global::System.Collections.Generic.IReadOnlyList",
        ["Map"] = "global::System.Collections.Generic.Dictionary",
        ["Record"] = "global::System.Collections.Generic.Dictionary",
        ["ReadonlyMap"] = "global::System.Collections.Generic.IReadOnlyDictionary",
        ["Set"] = "global::System.Collections.Generic.HashSet",
        ["ReadonlySet"] = "global::System.Collections.Generic.IReadOnlySet",
        ["Promise"] = "global::System.Threading.Tasks.Task",
        ["PromiseLike"] = "global::System.Threading.Tasks.Task",
        ["Iterable"] = "global::System.Collections.Generic.IEnumerable",
        ["Iterator"] = "global::System.Collections.Generic.IEnumerable",
        ["IterableIterator"] = "global::System.Collections.Generic.IEnumerable",
        ["Generator"] = "global::System.Collections.Generic.IEnumerable",
        ["AsyncIterable"] = "global::System.Collections.Generic.IAsyncEnumerable",
        ["AsyncGenerator"] = "global::System.Collections.Generic.IAsyncEnumerable",
    };

    /// <summary>Types that are transparent wrappers in TypeScript.</summary>
    private static readonly HashSet<string> Transparent = new(StringComparer.Ordinal) { "Partial", "Readonly", "Required", "NonNullable" };

    public static string Map(TypeNode? type, DiagnosticBag? diagnostics = null, SourceText? source = null)
    {
        if (type is null) return "dynamic";
        var sb = new StringBuilder();
        Append(sb, type, diagnostics, source);
        return sb.ToString();
    }

    public static bool IsVoid(TypeNode? type) => type is NamedType { Name: "void" or "never", TypeArgs.Count: 0 };

    /// <summary>Returns the element type text of <c>Promise&lt;T&gt;</c>/<c>Task&lt;T&gt;</c>, or null.</summary>
    public static string? UnwrapTask(string csharpType)
    {
        foreach (var prefix in new[] { "global::System.Threading.Tasks.Task<", "Task<", "global::System.Threading.Tasks.ValueTask<", "ValueTask<" })
        {
            if (csharpType.StartsWith(prefix, StringComparison.Ordinal) && csharpType.EndsWith('>'))
                return csharpType[prefix.Length..^1];
        }
        return null;
    }

    private static void Append(StringBuilder sb, TypeNode type, DiagnosticBag? diagnostics, SourceText? source)
    {
        switch (type)
        {
            case NamedType n:
                AppendNamed(sb, n, diagnostics, source);
                break;
            case ArrayType a:
                Append(sb, a.Element, diagnostics, source);
                sb.Append("[]");
                break;
            case NullableType nt:
                Append(sb, nt.Inner, diagnostics, source);
                if (sb.Length > 0 && sb[^1] != '?' && !sb.ToString().EndsWith("dynamic", StringComparison.Ordinal)) sb.Append('?');
                break;
            case TupleType t:
                if (t.Elements.Count == 1) { Append(sb, t.Elements[0], diagnostics, source); break; }
                sb.Append('(');
                for (int i = 0; i < t.Elements.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Append(sb, t.Elements[i], diagnostics, source);
                }
                sb.Append(')');
                break;
            case FunctionType f:
                AppendFunction(sb, f, diagnostics, source);
                break;
            case UnionType u:
                diagnostics?.Warning(DiagnosticCodes.LooseUnion,
                    "Union types other than 'T | null' have no CLR equivalent and are lowered to 'object'.", source, u.Span);
                sb.Append("object");
                break;
        }
    }

    private static void AppendFunction(StringBuilder sb, FunctionType f, DiagnosticBag? diagnostics, SourceText? source)
    {
        bool isVoid = IsVoid(f.ReturnType);
        if (isVoid && f.Parameters.Count == 0)
        {
            sb.Append("global::System.Action");
            return;
        }
        sb.Append(isVoid ? "global::System.Action<" : "global::System.Func<");
        for (int i = 0; i < f.Parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            if (f.Parameters[i].Type is null) sb.Append("object");
            else Append(sb, f.Parameters[i].Type!, diagnostics, source);
        }
        if (!isVoid)
        {
            if (f.Parameters.Count > 0) sb.Append(", ");
            Append(sb, f.ReturnType, diagnostics, source);
        }
        sb.Append('>');
    }

    private static void AppendNamed(StringBuilder sb, NamedType n, DiagnosticBag? diagnostics, SourceText? source)
    {
        if (n.TypeArgs.Count == 0 && Primitives.TryGetValue(n.Name, out var prim))
        {
            sb.Append(prim);
            return;
        }
        if (Transparent.Contains(n.Name) && n.TypeArgs.Count == 1)
        {
            Append(sb, n.TypeArgs[0], diagnostics, source);
            return;
        }
        if (n.Name is "Promise" or "PromiseLike" && (n.TypeArgs.Count == 0 || IsVoid(n.TypeArgs[0])))
        {
            sb.Append("global::System.Threading.Tasks.Task");
            return;
        }
        if (n.Name == "Array" && n.TypeArgs.Count == 0)
        {
            sb.Append("global::System.Collections.Generic.List<dynamic>");
            return;
        }
        sb.Append(n.TypeArgs.Count > 0 && Generics.TryGetValue(n.Name, out var g) ? g : n.Name);
        if (n.TypeArgs.Count > 0)
        {
            sb.Append('<');
            for (int i = 0; i < n.TypeArgs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Append(sb, n.TypeArgs[i], diagnostics, source);
            }
            sb.Append('>');
        }
    }
}
