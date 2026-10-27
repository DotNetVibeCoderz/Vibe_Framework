using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace JSNet.Runtime;

/// <summary>
/// Helpers the JS.Net compiler emits calls to: JavaScript truthiness, <c>typeof</c>, string conversion,
/// value inspection (<c>console.log</c> formatting) and a few operators without a direct C# equivalent.
/// Overloads are resolved at compile time, so <c>truthy(bool)</c> costs nothing.
/// </summary>
public static class JS
{
    // ---------------------------------------------------------------- truthiness

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool truthy(bool value) => value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool truthy(bool? value) => value == true;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool truthy(int value) => value != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool truthy(long value) => value != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool truthy(double value) => value != 0 && !double.IsNaN(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool truthy(float value) => value != 0 && !float.IsNaN(value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool truthy(decimal value) => value != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool truthy(string? value) => !string.IsNullOrEmpty(value);

    public static bool truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        double d => d != 0 && !double.IsNaN(d),
        float f => f != 0 && !float.IsNaN(f),
        int i => i != 0,
        long l => l != 0,
        decimal m => m != 0,
        short sh => sh != 0,
        byte by => by != 0,
        _ => true,
    };

    // ---------------------------------------------------------------- operators

    public static string typeOf(object? value) => value switch
    {
        null => "object",
        string or char => "string",
        bool => "boolean",
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => "number",
        System.Numerics.BigInteger => "bigint",
        Delegate => "function",
        _ => "object",
    };

    public static object? discard(object? _) => null;

    public static T seq<T>(params T[] values) => values.Length == 0 ? default! : values[^1];

    public static bool delete(object? target, object? key)
    {
        switch (target)
        {
            case IDictionary dict when key is not null:
                bool had = dict.Contains(key);
                dict.Remove(key);
                return had;
            case IList list when key is int i && i >= 0 && i < list.Count:
                list[i] = null;
                return true;
        }
        return false;
    }

    public static bool hasKey(object? target, object? key) => target switch
    {
        null => false,
        IDictionary dict => key is not null && dict.Contains(key),
        IList list => key is int i && i >= 0 && i < list.Count,
        _ => key is string name && target.GetType().GetProperty(name) is not null,
    };

    /// <summary>Keys for <c>for (k in obj)</c>: dictionary keys, list indices or public property names.</summary>
    public static IEnumerable<string> keys(object? target) => target switch
    {
        null => [],
        JsObject jo => [.. jo.Keys],
        IDictionary dict => dict.Keys.Cast<object>().Select(k => k?.ToString() ?? "null").ToList(),
        IList list => Enumerable.Range(0, list.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)),
        string s => Enumerable.Range(0, s.Length).Select(i => i.ToString(CultureInfo.InvariantCulture)),
        _ => target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name),
    };

    public static double toNumber(object? value) => value switch
    {
        null => 0,
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        decimal m => (double)m,
        bool b => b ? 1 : 0,
        string s => string.IsNullOrWhiteSpace(s) ? 0 : double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : double.NaN,
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _ => double.NaN,
    };

    public static Exception toException(object? value) => value switch
    {
        Exception e => e,
        null => new Error("null"),
        _ => new Error(str(value)) { value = value },
    };

    // ---------------------------------------------------------------- formatting

    /// <summary>JavaScript <c>String(value)</c> semantics (used for template literal holes).</summary>
    public static string str(object? value) => value switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        double d => FormatNumber(d),
        float f => FormatNumber(f),
        IFormattable fmt when value is not Enum => fmt.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable e when value is not IDictionary && !HasCustomToString(value) => string.Join(",", e.Cast<object?>().Select(str)),
        _ => HasCustomToString(value) ? value.ToString() ?? string.Empty : inspect(value),
    };

    public static string str(bool value) => value ? "true" : "false";
    public static string str(string? value) => value ?? "null";
    public static string str(double value) => FormatNumber(value);
    public static string str(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string FormatNumber(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e21) return d.ToString("0", CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Node.js-style <c>util.inspect</c>: <c>{ name: 'Ada', tags: [ 'a', 'b' ] }</c>.</summary>
    public static string inspect(object? value, int depth = 4)
    {
        var sb = new StringBuilder();
        Inspect(sb, value, depth, nested: false, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return sb.ToString();
    }

    private static void Inspect(StringBuilder sb, object? value, int depth, bool nested, HashSet<object> seen)
    {
        switch (value)
        {
            case null: sb.Append("null"); return;
            case string s:
                if (nested) sb.Append('\'').Append(s.Replace("'", "\\'", StringComparison.Ordinal)).Append('\'');
                else sb.Append(s);
                return;
            case char c: sb.Append(nested ? $"'{c}'" : c.ToString()); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case double d: sb.Append(FormatNumber(d)); return;
            case float f: sb.Append(FormatNumber(f)); return;
            case Enum en: sb.Append(en); return;
            case DateTime or DateTimeOffset or Date or Guid or TimeSpan or Uri: sb.Append(value); return;
            case IFormattable fmt: sb.Append(fmt.ToString(null, CultureInfo.InvariantCulture)); return;
            case Delegate del: sb.Append("[Function: ").Append(del.Method.Name).Append(']'); return;
            case Exception ex: sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message); return;
            case Type t: sb.Append("[class ").Append(t.Name).Append(']'); return;
        }

        if (!seen.Add(value)) { sb.Append("[Circular]"); return; }
        try
        {
            if (depth < 0) { sb.Append(value is IEnumerable ? "[Array]" : "[Object]"); return; }

            if (value is JsObject jo)
            {
                AppendObject(sb, jo.Select(kv => (kv.Key, kv.Value)), depth, seen);
                return;
            }
            if (value is IDictionary dict)
            {
                var entries = dict.Cast<DictionaryEntry>().Select(e => (Key: str(e.Key), e.Value)).ToList();
                sb.Append(value.GetType().IsGenericType && value.GetType().Name.StartsWith("Dictionary", StringComparison.Ordinal) ? $"Map({entries.Count}) " : string.Empty);
                AppendObject(sb, entries, depth, seen);
                return;
            }
            if (value is IEnumerable seq && !HasCustomToString(value))
            {
                var items = seq.Cast<object?>().ToList();
                if (value.GetType().IsGenericType && value.GetType().Name.StartsWith("HashSet", StringComparison.Ordinal)) sb.Append($"Set({items.Count}) ");
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[ ");
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    if (i >= 100) { sb.Append($"... {items.Count - 100} more items"); break; }
                    Inspect(sb, items[i], depth - 1, nested: true, seen);
                }
                sb.Append(" ]");
                return;
            }
            if (HasCustomToString(value))
            {
                sb.Append(value);
                return;
            }

            var type = value.GetType();
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.CanRead).ToList();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var pairs = props.Select(p => (p.Name, SafeGet(() => p.GetValue(value))))
                .Concat(fields.Select(f => (f.Name, f.GetValue(value))));
            bool anonymous = type.Name.Contains("AnonymousType", StringComparison.Ordinal);
            if (!anonymous && !type.Name.Contains("__Data", StringComparison.Ordinal) && !type.Name.StartsWith("JsType_", StringComparison.Ordinal))
                sb.Append(type.Name).Append(' ');
            AppendObject(sb, pairs, depth, seen);
        }
        finally
        {
            seen.Remove(value);
        }
    }

    private static object? SafeGet(Func<object?> get)
    {
        try { return get(); }
        catch (Exception ex) { return $"[{ex.GetType().Name}]"; }
    }

    private static void AppendObject(StringBuilder sb, IEnumerable<(string Key, object? Value)> entries, int depth, HashSet<object> seen)
    {
        var list = entries.ToList();
        if (list.Count == 0) { sb.Append("{}"); return; }
        sb.Append("{ ");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(list[i].Key).Append(": ");
            Inspect(sb, list[i].Value, depth - 1, nested: true, seen);
        }
        sb.Append(" }");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> CustomToString = new();

    private static bool HasCustomToString(object value) => CustomToString.GetOrAdd(value.GetType(), static t =>
    {
        if (t.Name.Contains("AnonymousType", StringComparison.Ordinal)) return false;
        var m = t.GetMethod("ToString", Type.EmptyTypes);
        if (m is null || m.DeclaringType == typeof(object) || m.DeclaringType == typeof(ValueType)) return false;
        // Records synthesize ToString; prefer the JS inspect format for them too.
        if (t.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance) is not null && m.IsDefined(typeof(CompilerGeneratedAttribute))) return false;
        return true;
    });
}
