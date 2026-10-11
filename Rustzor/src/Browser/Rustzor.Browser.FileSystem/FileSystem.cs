using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.Browser.FileSystem;

/// <summary>A file the user picked or dropped: name, size, type and contents.</summary>
public sealed class PickedFile
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string Type { get; init; } = "";
    public DateTime LastModified { get; init; }
    /// <summary>The handle for reading the contents and, where allowed, writing back.</summary>
    public int Handle { get; init; }

    public async Task<string> ReadTextAsync() => (await Js.CallAsync("fs.readText", Js.Args("h", Handle))).AsString();

    public async Task<byte[]> ReadBytesAsync() => Convert.FromBase64String((await Js.CallAsync("fs.readBytes", Js.Args("h", Handle))).AsString());

    /// <summary>A data URL of the file (images for preview).</summary>
    public async Task<string> ReadDataUrlAsync() => (await Js.CallAsync("fs.readUrl", Js.Args("h", Handle))).AsString();

    /// <summary>Writes back to the same file, when it came from the File System Access picker.</summary>
    public Task WriteTextAsync(string text) => Js.CallAsync("fs.writeBack", Js.Args("h", Handle, "text", text));
}

/// <summary>
/// Files: pickers and saving (File System Access, with a download fallback),
/// drag and drop, and the origin-private file system (OPFS) for app data.
/// </summary>
public static class Files
{
    public static bool HasNativePickers => Js.Call("fs.native").AsBool();

    /// <summary>Shows a file picker; <paramref name="accept"/> like ".json,.csv" or "image/*".</summary>
    public static async Task<List<PickedFile>> PickAsync(string accept = "", bool multiple = false) =>
        Read(await Js.CallAsync("fs.pick", Js.Args("accept", accept, "multiple", multiple)));

    /// <summary>Saves text: a native save dialog where available, a download otherwise.</summary>
    public static Task SaveTextAsync(string suggestedName, string text, string mimeType = "text/plain") =>
        Js.CallAsync("fs.save", Js.Args("name", suggestedName, "text", text, "type", mimeType));

    /// <summary>Files dropped on an element.</summary>
    public static IDisposable OnDrop(ElementReference target, Action<List<PickedFile>> handler)
    {
        var id = Callbacks.Register(v => handler(Read(v)));
        var token = Js.Call("fs.drop", Js.Args("el", target.Id, "id", id)).AsInt();
        return new Off(id, token);
    }

    // ── origin-private file system ───────────────────────────────────────

    public static Task WritePrivateAsync(string path, string text) => Js.CallAsync("fs.opfsWrite", Js.Args("path", path, "text", text));

    public static async Task<string?> ReadPrivateAsync(string path) => (await Js.CallAsync("fs.opfsRead", Js.Args("path", path))).AsStringOrNull();

    public static Task DeletePrivateAsync(string path) => Js.CallAsync("fs.opfsDelete", Js.Args("path", path));

    public static async Task<List<string>> ListPrivateAsync(string directory = "")
    {
        var list = new List<string>();
        foreach (var n in (await Js.CallAsync("fs.opfsList", Js.Args("path", directory))).Items) list.Add(n.AsString());
        return list;
    }

    private static List<PickedFile> Read(JsonValue v)
    {
        var list = new List<PickedFile>();
        foreach (var f in v.Items)
        {
            list.Add(new PickedFile
            {
                Name = f["name"].AsString(),
                Size = f["size"].AsLong(),
                Type = f["type"].AsString(),
                LastModified = new DateTime(1970, 1, 1).AddMilliseconds(f["modified"].AsDouble()),
                Handle = f["h"].AsInt(),
            });
        }
        return list;
    }

    private sealed class Off : IDisposable
    {
        private readonly int _id;
        private int _token;

        public Off(int id, int token)
        {
            _id = id;
            _token = token;
        }

        public void Dispose()
        {
            if (_token == 0) return;
            Js.Send("js.off", Js.Args("token", _token));
            Callbacks.Unregister(_id);
            _token = 0;
        }
    }
}
