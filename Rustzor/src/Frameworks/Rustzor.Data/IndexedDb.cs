using Rustzor.Interop;

namespace Rustzor.Data;

public sealed record IndexSchema(string Name, string KeyPath, bool Unique = false);

/// <summary>An object store to create on upgrade. Key paths use the JSON (camelCase) property names.</summary>
public sealed record StoreSchema(string Name, string? KeyPath = "id", bool AutoIncrement = false, params IndexSchema[] Indexes);

/// <summary>
/// An IndexedDB database. Bump <c>version</c> when adding stores or indexes.
/// <code>
/// var db = await IndexedDb.OpenAsync("shop", 1, new StoreSchema("orders", "id", Indexes: new IndexSchema("byCustomer", "customerId")));
/// var orders = db.Store&lt;Order&gt;("orders");
/// await orders.PutAsync(order);
/// var mine = await orders.WhereAsync("byCustomer", 42);
/// </code>
/// </summary>
public sealed class IndexedDb : IDisposable
{
    private IndexedDb(string name, int handle)
    {
        Name = name;
        Handle = handle;
    }

    public string Name { get; }
    internal int Handle { get; }

    public static async Task<IndexedDb> OpenAsync(string name, int version, params StoreSchema[] stores)
    {
        var w = new JsonWriter().BeginArray();
        foreach (var s in stores)
        {
            w.BeginObject().Prop("name", s.Name).Prop("keyPath", s.KeyPath).Prop("autoIncrement", s.AutoIncrement).Name("indexes").BeginArray();
            foreach (var ix in s.Indexes) w.BeginObject().Prop("name", ix.Name).Prop("keyPath", ix.KeyPath).Prop("unique", ix.Unique).EndObject();
            w.EndArray().EndObject();
        }
        w.EndArray();
        var handle = (await Js.CallAsync("idb.open", Js.Args("name", name, "version", version, "stores", new JsonRaw(w.ToString())))).AsInt();
        return new IndexedDb(name, handle);
    }

    public ObjectStore<T> Store<T>(string name) where T : new() => new(this, name);

    /// <summary>Deletes a whole database.</summary>
    public static Task DeleteAsync(string name) => Js.CallAsync("idb.drop", Js.Args("name", name));

    public void Dispose() => Js.Send("idb.close", Js.Args("h", Handle));
}

/// <summary>Typed access to one object store; values travel as JSON.</summary>
public sealed class ObjectStore<T> where T : new()
{
    private readonly IndexedDb _db;

    internal ObjectStore(IndexedDb db, string name)
    {
        _db = db;
        Name = name;
    }

    public string Name { get; }

    /// <summary>Inserts or replaces; returns the key (useful with auto-increment).</summary>
    public async Task<JsonValue> PutAsync(T item) =>
        await Js.CallAsync("idb.put", Js.Args("h", _db.Handle, "store", Name, "value", new JsonRaw(Json.Serialize(item))));

    public async Task<int> PutManyAsync(IEnumerable<T> items) =>
        (await Js.CallAsync("idb.putMany", Js.Args("h", _db.Handle, "store", Name, "values", new JsonRaw(Json.Serialize(items.ToList()))))).AsInt();

    public async Task<T?> GetAsync(object key)
    {
        var v = await Js.CallAsync("idb.get", Js.Args("h", _db.Handle, "store", Name, "key", key));
        return v.IsNull ? default : Json.Deserialize<T>(v);
    }

    public async Task<List<T>> AllAsync(int limit = 0) =>
        Json.DeserializeList<T>(await Js.CallAsync("idb.getAll", Js.Args("h", _db.Handle, "store", Name, "limit", limit)));

    /// <summary>Items whose index equals a value.</summary>
    public async Task<List<T>> WhereAsync(string index, object value, int limit = 0) =>
        Json.DeserializeList<T>(await Js.CallAsync("idb.getAll", Js.Args("h", _db.Handle, "store", Name, "index", index, "value", value, "limit", limit)));

    /// <summary>Items whose key (or index) lies in [lower, upper]; either bound may be null.</summary>
    public async Task<List<T>> RangeAsync(object? lower, object? upper, string? index = null, int limit = 0) =>
        Json.DeserializeList<T>(await Js.CallAsync("idb.range", Js.Args("h", _db.Handle, "store", Name, "index", index, "lower", lower, "upper", upper, "limit", limit)));

    public Task DeleteAsync(object key) => Js.CallAsync("idb.delete", Js.Args("h", _db.Handle, "store", Name, "key", key));
    public Task ClearAsync() => Js.CallAsync("idb.clear", Js.Args("h", _db.Handle, "store", Name));
    public async Task<int> CountAsync() => (await Js.CallAsync("idb.count", Js.Args("h", _db.Handle, "store", Name))).AsInt();
}
