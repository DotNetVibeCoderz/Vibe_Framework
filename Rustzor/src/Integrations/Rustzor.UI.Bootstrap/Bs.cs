using System.Text;
using Rustzor.Interop;

namespace Rustzor.UI.Bootstrap;

/// <summary>Bootstrap's contextual colours, as used in class names (btn-primary, bg-success, ...).</summary>
public static class BsColor
{
    public const string Primary = "primary";
    public const string Secondary = "secondary";
    public const string Success = "success";
    public const string Danger = "danger";
    public const string Warning = "warning";
    public const string Info = "info";
    public const string Light = "light";
    public const string Dark = "dark";
}

public static class Bs
{
    /// <summary>Joins class names, skipping empty ones: <c>Bs.Css("btn", active ? "active" : null)</c>.</summary>
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

    /// <summary>Switches Bootstrap and Rustzor between "light" and "dark".</summary>
    public static void SetTheme(string theme) => Js.Send("bs.theme", Js.Args("theme", theme));
}

/// <summary>A navbar link.</summary>
public sealed record BsLink(string Text, string Href);

public sealed record BsToastMessage(int Id, string Title, string Body, string Color);

/// <summary>Queue toasts from anywhere; a <see cref="BsToastHost"/> shows them.</summary>
public static class BsToasts
{
    private static int _next;
    internal static readonly List<BsToastMessage> Items = new();
    internal static event Action? Changed;

    public static void Show(string title, string body, string color = BsColor.Primary, int milliseconds = 4000)
    {
        var t = new BsToastMessage(++_next, title, body, color);
        Items.Add(t);
        Changed?.Invoke();
        if (milliseconds > 0) Scheduler.SetTimeout(() => Dismiss(t.Id), milliseconds);
    }

    public static void Dismiss(int id)
    {
        if (Items.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
    }
}
