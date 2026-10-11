using Rustzor.Interop;

namespace Rustzor.Browser.Notifications;

/// <summary>System notifications, shown outside the page.</summary>
public static class Notifier
{
    public static bool IsSupported => Js.Call("notify.supported").AsBool();

    /// <summary>"default", "granted" or "denied".</summary>
    public static string Permission => Js.Call("notify.permission").AsString();

    /// <summary>Asks the user; must be called from a click or key handler.</summary>
    public static async Task<bool> RequestPermissionAsync() => (await Js.CallAsync("notify.request")).AsString() == "granted";

    /// <summary>Shows a notification; <paramref name="onClick"/> runs when it is clicked.</summary>
    public static void Show(string title, string? body = null, string? icon = null, string? tag = null, Action? onClick = null)
    {
        var id = 0;
        if (onClick is not null) id = Callbacks.Register(_ => onClick());
        Js.Send("notify.show", Js.Args("title", title, "body", body, "icon", icon, "tag", tag, "id", id));
    }
}

/// <summary>Short haptic feedback on phones.</summary>
public static class Vibration
{
    public static bool Vibrate(params int[] pattern) => Js.Call("notify.vibrate", Js.Args("pattern", pattern)).AsBool();
}

/// <summary>The badge on an installed app icon.</summary>
public static class AppBadge
{
    public static Task SetAsync(int count) => Js.CallAsync("notify.badge", Js.Args("count", count));

    public static Task ClearAsync() => Js.CallAsync("notify.badge", Js.Args("count", 0));
}
