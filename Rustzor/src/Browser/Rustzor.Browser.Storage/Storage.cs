using Rustzor.Interop;

namespace Rustzor.Browser.Storage;

/// <summary>
/// <c>localStorage</c> (survives the browser closing) or <c>sessionStorage</c>
/// (survives reloads of this tab). Strings in, strings out; the JSON helpers
/// store objects.
/// </summary>
public sealed class WebStorage
{
    private readonly string _area;

    private WebStorage(string area) => _area = area;

    public static readonly WebStorage Local = new("localStorage");
    public static readonly WebStorage Session = new("sessionStorage");

    public string? Get(string key) => Js.Call("storage.get", Js.Args("area", _area, "key", key)).AsStringOrNull();

    public void Set(string key, string value) => Js.Send("storage.set", Js.Args("area", _area, "key", key, "value", value));

    public void Remove(string key) => Js.Send("storage.remove", Js.Args("area", _area, "key", key));

    public void Clear() => Js.Send("storage.clear", Js.Args("area", _area));

    public bool Contains(string key) => Get(key) is not null;

    public int Count => Js.Call("storage.count", Js.Args("area", _area)).AsInt();

    public List<string> Keys()
    {
        var list = new List<string>();
        foreach (var k in Js.Call("storage.keys", Js.Args("area", _area)).Items) list.Add(k.AsString());
        return list;
    }

    /// <summary>Stores any value as JSON.</summary>
    public void SetJson(string key, object? value) => Set(key, Json.Serialize(value));

    /// <summary>The stored JSON, parsed; <see cref="JsonValue.Null"/> when absent.</summary>
    public JsonValue GetJson(string key) => JsonValue.Parse(Get(key) ?? "");

    public T? GetObject<T>(string key) where T : new()
    {
        var raw = Get(key);
        return raw is null ? default : Json.Deserialize<T>(raw);
    }

    public List<T> GetList<T>(string key)
    {
        var raw = Get(key);
        return raw is null ? new List<T>() : Json.DeserializeList<T>(raw);
    }

    /// <summary>Fires when another tab changes this storage.</summary>
    public IDisposable OnChanged(Action<string?, string?> handler) =>
        JsObject.OnWindow("storage", e => handler(e["key"].AsStringOrNull(), e["newValue"].AsStringOrNull()));
}

/// <summary>How much the origin may store, and asking for it to persist.</summary>
public static class StorageManager
{
    public static async Task<(long Usage, long Quota)> EstimateAsync()
    {
        var e = await Js.CallAsync("storage.estimate");
        return (e["usage"].AsLong(), e["quota"].AsLong());
    }

    /// <summary>Asks the browser not to evict this origin's data under pressure.</summary>
    public static async Task<bool> PersistAsync() => (await Js.CallAsync("storage.persist")).AsBool();
}

/// <summary>Cookies of the current document.</summary>
public static class Cookies
{
    public static string? Get(string name) => Js.Call("storage.cookieGet", Js.Args("name", name)).AsStringOrNull();

    public static void Set(string name, string value, int days = 30, string path = "/") =>
        Js.Send("storage.cookieSet", Js.Args("name", name, "value", value, "days", days, "path", path));

    public static void Delete(string name, string path = "/") => Set(name, "", -1, path);
}
