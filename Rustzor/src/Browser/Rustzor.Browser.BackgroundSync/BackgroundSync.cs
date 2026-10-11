using Rustzor.Interop;

namespace Rustzor.Browser.BackgroundSync;

/// <summary>
/// Background Sync: ask the service worker to run a <c>sync</c> event when the
/// connection returns, so work queued offline (orders, messages) is sent even
/// if the page was closed. Periodic sync runs on a schedule for installed apps.
/// </summary>
public static class SyncManager
{
    public static bool IsSupported => Js.Call("sync.supported").AsBool();

    public static Task RegisterAsync(string tag) => Js.CallAsync("sync.register", Js.Args("tag", tag));

    public static async Task<List<string>> GetTagsAsync()
    {
        var list = new List<string>();
        foreach (var t in (await Js.CallAsync("sync.tags")).Items) list.Add(t.AsString());
        return list;
    }

    /// <summary>Periodic sync, at most every <paramref name="minIntervalMinutes"/> (installed PWAs).</summary>
    public static Task RegisterPeriodicAsync(string tag, int minIntervalMinutes) =>
        Js.CallAsync("sync.periodic", Js.Args("tag", tag, "minutes", minIntervalMinutes));
}

/// <summary>
/// An outbox in IndexedDB that the service worker drains on <c>sync</c>:
/// <see cref="EnqueueAsync"/> stores the request; the PWA template service
/// worker posts every queued item when the network is back.
/// </summary>
public static class Outbox
{
    public static async Task EnqueueAsync(string url, string method, string jsonBody)
    {
        await Js.CallAsync("sync.enqueue", Js.Args("url", url, "method", method, "body", jsonBody));
        if (SyncManager.IsSupported) await SyncManager.RegisterAsync("rustzor-outbox");
    }

    public static async Task<int> CountAsync() => (await Js.CallAsync("sync.count")).AsInt();
}
