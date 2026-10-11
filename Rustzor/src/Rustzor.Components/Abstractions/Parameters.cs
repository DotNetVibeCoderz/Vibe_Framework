using System.Reflection;

namespace Microsoft.AspNetCore.Components;

/// <summary>One parameter passed to a component.</summary>
public readonly struct ParameterValue
{
    public ParameterValue(string name, object? value, bool cascading = false)
    {
        Name = name;
        Value = value;
        Cascading = cascading;
    }

    public string Name { get; }
    public object? Value { get; }
    public bool Cascading { get; }
}

/// <summary>The parameters a parent supplied, by name.</summary>
public readonly struct ParameterView
{
    private static readonly List<ParameterValue> NoValues = new();
    private readonly List<ParameterValue>? _values;

    internal ParameterView(List<ParameterValue> values) => _values = values;

    public static ParameterView Empty => new(NoValues);

    internal List<ParameterValue> Values => _values ?? NoValues;

    public static ParameterView FromDictionary(IDictionary<string, object?> parameters)
    {
        var list = new List<ParameterValue>();
        foreach (var kv in parameters) list.Add(new ParameterValue(kv.Key, kv.Value));
        return new ParameterView(list);
    }

    public bool TryGetValue<TValue>(string parameterName, out TValue result)
    {
        foreach (var p in Values)
        {
            if (string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                result = (TValue)p.Value!;
                return true;
            }
        }
        result = default!;
        return false;
    }

    public TValue? GetValueOrDefault<TValue>(string parameterName) =>
        TryGetValue<TValue>(parameterName, out var v) ? v : default;

    public TValue? GetValueOrDefault<TValue>(string parameterName, TValue defaultValue) =>
        TryGetValue<TValue>(parameterName, out var v) ? v : defaultValue;

    public IReadOnlyDictionary<string, object?> ToDictionary()
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in Values) d[p.Name] = p.Value;
        return d;
    }

    public List<ParameterValue>.Enumerator GetEnumerator() => Values.GetEnumerator();

    /// <summary>Assigns every parameter to the matching <c>[Parameter]</c> or <c>[CascadingParameter]</c> property.</summary>
    public void SetParameterProperties(object target)
    {
        var descriptor = ComponentDescriptor.For(target.GetType());
        Dictionary<string, object?>? unmatched = null;
        foreach (var p in Values)
        {
            var property = p.Cascading ? descriptor.FindCascading(p.Name) : descriptor.FindParameter(p.Name);
            if (property is not null)
            {
                property.SetValue(target, p.Value);
                continue;
            }
            if (descriptor.CatchAll is null || p.Cascading) continue;
            unmatched ??= new Dictionary<string, object?>();
            unmatched[p.Name] = p.Value;
        }
        if (descriptor.CatchAll is not null && unmatched is not null)
            descriptor.CatchAll.SetValue(target, unmatched);
    }
}

/// <summary>What reflection found on a component type, worked out once per type.</summary>
internal sealed class ComponentDescriptor
{
    private static readonly Dictionary<Type, ComponentDescriptor> Cache = new();

    public readonly Dictionary<string, PropertyInfo> Parameters = new();
    public readonly List<(PropertyInfo Property, string? Name)> Cascading = new();
    public readonly List<PropertyInfo> Injected = new();
    public PropertyInfo? CatchAll;

    public static ComponentDescriptor For(Type type)
    {
        if (Cache.TryGetValue(type, out var d)) return d;
        d = new ComponentDescriptor();
        // Each type in the chain, declared members only: Razor's @inject makes
        // *private* properties, and a private property of a base class is only
        // visible when that base class is asked directly.
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var seen = new HashSet<string>();
        for (var t = type; t is not null && t != typeof(object) && t != typeof(ComponentBase); t = t.BaseType)
        {
            foreach (var p in t.GetProperties(Declared))
            {
                if (!seen.Add(p.Name)) continue;
                foreach (var attr in p.GetCustomAttributes(true))
                {
                    switch (attr)
                    {
                        case ParameterAttribute pa:
                            d.Parameters[p.Name.ToLowerInvariant()] = p;
                            if (pa.CaptureUnmatchedValues) d.CatchAll = p;
                            break;
                        case CascadingParameterAttribute ca:
                            d.Cascading.Add((p, ca.Name));
                            break;
                        case InjectAttribute:
                            d.Injected.Add(p);
                            break;
                    }
                }
            }
        }
        Cache[type] = d;
        return d;
    }

    public PropertyInfo? FindParameter(string name) =>
        Parameters.TryGetValue(name.ToLowerInvariant(), out var p) ? p : null;

    public PropertyInfo? FindCascading(string name)
    {
        foreach (var (property, _) in Cascading)
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property;
        return null;
    }
}
