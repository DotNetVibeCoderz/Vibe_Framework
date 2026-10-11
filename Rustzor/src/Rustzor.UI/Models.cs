using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.UI;

/// <summary>A sidebar entry, or a section heading when <see cref="Section"/> is set.</summary>
public sealed class NavItem
{
    public string Title { get; set; } = "";
    public string Href { get; set; } = "";
    public string Icon { get; set; } = "dot";
    public string? Section { get; set; }

    public static NavItem Link(string title, string href, string icon) => new() { Title = title, Href = href, Icon = icon };

    public static NavItem Heading(string section) => new() { Section = section };
}

/// <summary>One column of a <see cref="DataGrid{TItem}"/>.</summary>
public sealed class GridColumn<TItem>
{
    public GridColumn(string title, Func<TItem, object?> value)
    {
        Title = title;
        Value = value;
    }

    public string Title { get; }
    public Func<TItem, object?> Value { get; }
    /// <summary>Optional format for numbers and dates: "N2", "C", "yyyy-MM-dd".</summary>
    public string? Format { get; init; }
    /// <summary>Right-aligned, monospaced figures.</summary>
    public bool Numeric { get; init; }
    public bool Sortable { get; init; } = true;
    /// <summary>Custom cell content; overrides the formatted value.</summary>
    public RenderFragment<TItem>? Template { get; init; }

    internal string Text(TItem item)
    {
        var v = Value(item);
        return v switch
        {
            null => "",
            double d when Format is not null => d.ToString(Format),
            int i when Format is not null => i.ToString(Format),
            long l when Format is not null => l.ToString(Format),
            DateTime dt => dt.ToString(Format ?? "yyyy-MM-dd"),
            _ => v.ToString() ?? "",
        };
    }
}

/// <summary>A series for the SVG charts.</summary>
public sealed class ChartSeries
{
    public ChartSeries(string name, IReadOnlyList<double> values)
    {
        Name = name;
        Values = values;
    }

    public string Name { get; }
    public IReadOnlyList<double> Values { get; }
    /// <summary>A CSS colour; defaults to the theme chart palette by position.</summary>
    public string? Color { get; init; }
}

/// <summary>Toast messages, shown by the <c>Toaster</c> the app shell renders.</summary>
public static class Toasts
{
    internal static event Action? Changed;
    internal static readonly List<(int Id, string Text, string Kind)> Items = new();
    private static int _next;

    public static void Show(string text, string kind = "good", int milliseconds = 3200)
    {
        var id = ++_next;
        Items.Add((id, text, kind));
        Changed?.Invoke();
        Scheduler.SetTimeout(() =>
        {
            Items.RemoveAll(t => t.Id == id);
            Changed?.Invoke();
        }, milliseconds);
    }

    public static void Success(string text) => Show(text, "good");

    public static void Error(string text) => Show(text, "bad", 5000);

    public static void Warning(string text) => Show(text, "warn", 4500);

    public static void Info(string text) => Show(text, "info");
}

/// <summary>Light and dark theme, persisted per browser.</summary>
public static class Theme
{
    public static string Current => Js.IsBrowser ? Js.Call("ui.theme").AsString() : "light";

    public static void Set(string theme) => Js.Send("ui.setTheme", Js.Args("theme", theme));

    public static void Toggle() => Set(Current == "dark" ? "light" : "dark");
}

/// <summary>Colours for avatars and categories, stable per name.</summary>
public static class Palette
{
    private static readonly string[] Colors = { "#c2410c", "#0f766e", "#a16207", "#475569", "#9f1239", "#4d7c0f", "#7c3aed", "#0369a1" };

    public static string For(string key)
    {
        var h = 0;
        foreach (var c in key) h = (h * 31 + c) & 0x7fffffff;
        return Colors[h % Colors.Length];
    }

    public static string Chart(int index) => $"var(--rz-chart-{index % 5 + 1})";

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0].Substring(0, 1) + parts[^1].Substring(0, 1)).ToUpperInvariant();
    }
}

/// <summary>SVG pieces written as markup (Razor reserves &lt;text&gt; inside code blocks).</summary>
public static class Svg
{
    public static MarkupString Text(double x, double y, string anchor, string content) =>
        new($"<text x=\"{Num(x)}\" y=\"{Num(y)}\" text-anchor=\"{anchor}\">{Escape(content)}</text>");

    public static string Num(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    public static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
