using Rustzor.Interop;

namespace Rustzor.Browser.Push;

/// <summary>
/// Web Push: subscribe this browser so a server can send notifications while
/// the app is closed. Requires a registered service worker that handles the
/// <c>push</c> event (the PWA template has one) and the server VAPID public key.
/// </summary>
public static class PushManager
{
    public static bool IsSupported => Js.Call("push.supported").AsBool();

    /// <summary>
    /// Subscribes and returns the subscription as JSON (endpoint and keys), to
    /// send to the server.
    /// </summary>
    public static async Task<string> SubscribeAsync(string vapidPublicKey) =>
        (await Js.CallAsync("push.subscribe", Js.Args("key", vapidPublicKey))).AsString();

    /// <summary>The current subscription JSON, or null.</summary>
    public static async Task<string?> GetSubscriptionAsync() => (await Js.CallAsync("push.get")).AsStringOrNull();

    public static async Task<bool> UnsubscribeAsync() => (await Js.CallAsync("push.unsubscribe")).AsBool();
}
