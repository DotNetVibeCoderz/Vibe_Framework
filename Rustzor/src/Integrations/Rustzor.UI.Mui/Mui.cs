using Rustzor.Interop;

namespace Rustzor.UI.Mui;

/// <summary>A property bag for MUI props; serialises as a JSON object.</summary>
public sealed class MuiProps : Dictionary<string, object?>
{
    /// <summary>Adds a prop unless the value is null, for fluent building.</summary>
    public MuiProps Set(string name, object? value)
    {
        if (value is not null) this[name] = value;
        return this;
    }
}

/// <summary>An MUI callback reaching C#: the event name and the value MUI reported.</summary>
public sealed record MuiEvent(string Name, JsonValue Value);

/// <summary>Builders for the JSON tree a <see cref="MuiIsland"/> renders.</summary>
public static class Mui
{
    /// <summary>An MUI element: <c>Mui.El("Chip", new { label = "New" })</c>.</summary>
    public static Dictionary<string, object?> El(string component, object? props = null, object? children = null)
    {
        var el = new Dictionary<string, object?> { ["$mui"] = component };
        if (props is not null) el["props"] = props;
        if (children is not null) el["children"] = children;
        return el;
    }

    /// <summary>Where Razor content goes: the island's ChildContent is the slot named "children".</summary>
    public static Dictionary<string, object?> Slot(string name = "children") => new() { ["$slot"] = name };

    /// <summary>A callback prop; reaches C# through the island's OnEvent as <paramref name="name"/>.</summary>
    public static Dictionary<string, object?> Event(string name) => new() { ["$event"] = name };

    /// <summary>A Material Icons ligature, such as "home", "search" or "delete".</summary>
    public static Dictionary<string, object?> Icon(string name, string? color = null) =>
        El("Icon", color is null ? null : new MuiProps { ["color"] = color }, name);

    /// <summary>Merges MUI theme options (createTheme format) over the Rustzor theme.</summary>
    public static void Theme(object options) => Js.Send("mui.theme", Js.Args("options", options));
}
