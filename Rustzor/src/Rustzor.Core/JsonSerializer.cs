using System.Collections;
using System.Reflection;

namespace Rustzor.Interop;

/// <summary>
/// Object ⇄ JSON by reflection over public properties.
/// </summary>
/// <summary>Text that is already JSON, written as is.</summary>
public sealed class JsonRaw
{
    public JsonRaw(string json) => Json = json;

    public string Json { get; }
}

public static class Json
{
    public static string Serialize(object? value)
    {
        var w = new JsonWriter();
        WriteObject(w, value);
        return w.ToString();
    }

    /// <summary>Deeper than this is almost certainly a cycle (a parent its child points back to).</summary>
    private const int MaxDepth = 64;

    internal static void WriteObject(JsonWriter w, object? value) => WriteObject(w, value, 0);

    private static void WriteObject(JsonWriter w, object? value, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidOperationException($"Json.Serialize went {MaxDepth} levels deep at a {value?.GetType().Name}. The object graph probably has a cycle.");
        switch (value)
        {
            case null: w.Null(); return;
            case string s: w.String(s); return;
            case bool b: w.Bool(b); return;
            case int i: w.Number(i); return;
            case long l: w.Number(l); return;
            case double d: w.Number(d); return;
            case float f: w.Number((double)f); return;
            case short sh: w.Number((int)sh); return;
            case byte by: w.Number((int)by); return;
            case char ch: w.String(ch.ToString()); return;
            case DateTime dt: w.String(dt.ToString("yyyy-MM-ddTHH:mm:ss")); return;
            case DateTimeOffset dto: w.String(dto.ToString("o")); return;
            case TimeSpan ts: w.String(ts.ToString()); return;
            case Guid g: w.String(g.ToString()); return;
            case decimal m: w.Raw(m.ToString(System.Globalization.CultureInfo.InvariantCulture)); return;
            case uint ui: w.Number((double)ui); return;
            case ulong ul: w.Number((double)ul); return;
            case sbyte sb: w.Number((int)sb); return;
            case ushort us: w.Number((int)us); return;
            case JsonValue jv: w.Value(jv); return;
            case JsonRaw raw: w.Raw(raw.Json); return;
            case Enum e: w.Number(Convert.ToInt32(e)); return;
            case IDictionary dict:
                w.BeginObject();
                foreach (DictionaryEntry entry in dict)
                {
                    w.Name(entry.Key?.ToString() ?? "");
                    WriteObject(w, entry.Value, depth + 1);
                }
                w.EndObject();
                return;
            case IEnumerable seq:
                w.BeginArray();
                foreach (var item in seq) WriteObject(w, item, depth + 1);
                w.EndArray();
                return;
        }
        w.BeginObject();
        foreach (var p in value.GetType().GetProperties())
        {
            if (!p.CanRead) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            w.Name(CamelCase(p.Name));
            WriteObject(w, p.GetValue(value), depth + 1);
        }
        w.EndObject();
    }

    public static T Deserialize<T>(string json) where T : new() => (T)FromJson(JsonValue.Parse(json), typeof(T))!;

    public static T Deserialize<T>(JsonValue value) where T : new() => (T)FromJson(value, typeof(T))!;

    public static List<T> DeserializeList<T>(string json) => DeserializeList<T>(JsonValue.Parse(json));

    public static List<T> DeserializeList<T>(JsonValue value)
    {
        var list = new List<T>();
        foreach (var item in value.Items) list.Add((T)FromJson(item, typeof(T))!);
        return list;
    }

    /// <summary>
    /// Builds a value of <paramref name="type"/> from JSON: primitives, enums,
    /// nullables, arrays, <c>List&lt;T&gt;</c> and other lists, dictionaries
    /// with string keys, and objects with settable properties (matched by name
    /// or camelCase). A property the JSON leaves out keeps its initial value.
    /// </summary>
    /// <summary>A JSON object as a dictionary whose values are <typeparamref name="TValue"/>.</summary>
    public static Dictionary<string, TValue> DeserializeDictionary<TValue>(string json) => DeserializeDictionary<TValue>(JsonValue.Parse(json));

    public static Dictionary<string, TValue> DeserializeDictionary<TValue>(JsonValue value)
    {
        var dict = new Dictionary<string, TValue>();
        foreach (var name in value.Keys) dict[name] = (TValue)FromJson(value[name], typeof(TValue))!;
        return dict;
    }

    public static object? FromJson(JsonValue v, Type type)
    {
        if (type == typeof(string)) return v.AsStringOrNull();
        if (type == typeof(int)) return v.AsInt();
        if (type == typeof(long)) return v.AsLong();
        if (type == typeof(double)) return v.AsDouble();
        if (type == typeof(float)) return (float)v.AsDouble();
        if (type == typeof(short)) return (short)v.AsInt();
        if (type == typeof(byte)) return (byte)v.AsInt();
        if (type == typeof(bool)) return v.AsBool();
        if (type == typeof(decimal)) return v.IsString ? decimal.Parse(v.AsString(), System.Globalization.CultureInfo.InvariantCulture) : (decimal)v.AsDouble();
        if (type == typeof(DateTime)) return v.IsNull ? default : DateTime.Parse(v.AsString());
        if (type == typeof(DateTimeOffset)) return v.IsNull ? default : DateTimeOffset.Parse(v.AsString());
        if (type == typeof(Guid)) return v.IsNull ? Guid.Empty : Guid.Parse(v.AsString());
        if (type == typeof(TimeSpan)) return v.IsNull ? TimeSpan.Zero : TimeSpan.Parse(v.AsString());
        if (type == typeof(JsonValue)) return v;
        if (type == typeof(object)) return v;
        if (Nullable.GetUnderlyingType(type) is { } inner) return v.IsNull ? null : FromJson(v, inner);
        if (type.IsEnum) return v.IsNull ? Enum.ToObject(type, 0) : v.IsString ? Enum.Parse(type, v.AsString(), true) : Enum.ToObject(type, v.AsInt());
        if (v.IsNull) return null;

        if (v.IsArray) return ListFromJson(v, type);
        if (!v.IsObject) return null;
        if (typeof(IDictionary).IsAssignableFrom(type)) return DictionaryFromJson(v, type);

        var obj = Activator.CreateInstance(type)!;
        foreach (var p in type.GetProperties())
        {
            if (!p.CanWrite) continue;
            var key = v.Has(p.Name) ? p.Name : CamelCase(p.Name);
            if (!v.Has(key)) continue;
            var converted = FromJson(v[key], p.PropertyType);
            // Nothing usable (an array for a scalar, say): keep the initial value.
            if (converted is null && !v[key].IsNull) continue;
            if (converted is not null || !p.PropertyType.IsValueType) p.SetValue(obj, converted);
        }
        return obj;
    }

    /// <summary>The element type of an array, a <c>List&lt;T&gt;</c> or any <c>IEnumerable&lt;T&gt;</c>.</summary>
    private static Type? ElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        return TypeArguments(type) is { Length: 1 } args ? args[0] : null;
    }

    /// <summary>
    /// A type's arguments, or none. On RustNet a framework generic named as a
    /// method's <c>T</c> (<c>Deserialize&lt;List&lt;Item&gt;&gt;</c>) arrives as the
    /// open definition, whose arguments are unknown; property types are exact.
    /// </summary>
    private static Type[] TypeArguments(Type type) =>
        type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericArguments() : Array.Empty<Type>();

    /// <summary>A JSON value as the plain .NET value it reads as, for when no type says otherwise.</summary>
    private static object? Loose(JsonValue v) =>
        v.IsNull ? null : v.IsString ? v.AsString() : v.Kind == JsonKind.Number ? v.AsDouble() : v.Kind == JsonKind.Bool ? v.AsBool() : v;

    private static object? ListFromJson(JsonValue v, Type type)
    {
        var element = ElementType(type);
        var items = v.Items.ToList();
        object? Item(JsonValue item) => element is null ? Loose(item) : FromJson(item, element);
        if (type.IsArray)
        {
            var array = Array.CreateInstance(element ?? typeof(object), items.Count);
            for (var i = 0; i < items.Count; i++) array.SetValue(Item(items[i]), i);
            return array;
        }
        // An interface (IList<T>, IEnumerable<T>, IReadOnlyList<T>) gets a List<T>.
        var target = type.IsInterface || type.IsAbstract ? typeof(List<>).MakeGenericType(element ?? typeof(object)) : type;
        if (Activator.CreateInstance(target) is not IList list) return null;
        foreach (var item in items) list.Add(Item(item));
        return list;
    }

    private static object? DictionaryFromJson(JsonValue v, Type type)
    {
        var args = TypeArguments(type);
        var valueType = args.Length == 2 ? args[1] : null;
        var target = type.IsInterface ? typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType ?? typeof(object)) : type;
        if (Activator.CreateInstance(target) is not IDictionary dict) return null;
        foreach (var name in v.Keys) dict[name] = valueType is null ? Loose(v[name]) : FromJson(v[name], valueType);
        return dict;
    }

    private static string CamelCase(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
