using System.Text;
using Rustzor.Interop;

namespace Rustzor.UI.Tailwind;

/// <summary>Helpers for building Tailwind class lists from C#.</summary>
public static class Tw
{
    /// <summary>Joins class names, skipping empty ones: <c>Tw.Css("px-4 py-2", active ? "bg-rust-600" : null)</c>.</summary>
    public static string Css(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p.Trim());
        }
        return sb.ToString();
    }

    /// <summary>Picks the classes for a variant from a table, falling back to the first entry.</summary>
    public static string Variant(string key, IReadOnlyDictionary<string, string> table)
    {
        if (table.TryGetValue(key, out var css)) return css;
        foreach (var kv in table) return kv.Value;
        return "";
    }

    /// <summary>Merges extra settings into tailwind.config (any object; anonymous objects work).</summary>
    public static void Configure(object config) => Js.Send("tw.config", Js.Args("config", config));

    /// <summary>Switches the dark: variant on or off ("light" or "dark").</summary>
    public static void SetTheme(string theme) => Js.Send("tw.theme", Js.Args("theme", theme));
}
