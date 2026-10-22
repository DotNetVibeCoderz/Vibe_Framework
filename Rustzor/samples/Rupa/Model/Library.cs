using System.Globalization;
using System.Text;
using Rustzor.Browser.FileSystem;
using Rustzor.Browser.Storage;
using Rustzor.Interop;

namespace Rupa.Model;

public sealed class FileEntry
{
    public string Id = "";
    public string Name = "";
    public DateTime Updated;
    public int Layers;
    public string Thumb = "";
}

/// <summary>
/// The design files kept in this browser: each file in the origin-private file
/// system (localStorage when that is missing), and a list with names and
/// thumbnails in localStorage.
/// </summary>
public static class Library
{
    private const string IndexKey = "rupa.files";
    private const string SeededKey = "rupa.seeded";

    public static List<FileEntry> List()
    {
        var list = new List<FileEntry>();
        var j = JsonValue.Parse(WebStorage.Local.Get(IndexKey) ?? "[]");
        foreach (var e in j.Items)
            list.Add(new FileEntry
            {
                Id = e["id"].AsString(),
                Name = e["name"].AsString(),
                Updated = DateTime.Parse(e["updated"].AsString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Layers = e["layers"].AsInt(),
                Thumb = e["thumb"].AsStringOrNull() ?? "",
            });
        list.Sort((a, b) => b.Updated.CompareTo(a.Updated));
        return list;
    }

    private static void WriteIndex(List<FileEntry> list)
    {
        var w = new JsonWriter();
        w.BeginArray();
        foreach (var e in list)
            w.BeginObject().Prop("id", e.Id).Prop("name", e.Name).Prop("updated", e.Updated.ToString("o", CultureInfo.InvariantCulture))
                .Prop("layers", e.Layers).Prop("thumb", e.Thumb).EndObject();
        w.EndArray();
        WebStorage.Local.Set(IndexKey, w.ToString());
    }

    public static async Task<Doc?> LoadAsync(string id)
    {
        string? json = null;
        try
        {
            json = await Files.ReadPrivateAsync("rupa/" + id + ".json");
        }
        catch
        {
        }
        json ??= WebStorage.Local.Get("rupa.doc." + id);
        if (json is null) return null;
        var doc = Doc.FromJson(json);
        doc.Id = id;
        return doc;
    }

    public static async Task SaveAsync(Doc doc)
    {
        var json = doc.ToJson();
        try
        {
            await Files.WritePrivateAsync("rupa/" + doc.Id + ".json", json);
        }
        catch
        {
            WebStorage.Local.Set("rupa.doc." + doc.Id, json);
        }
        var list = List();
        list.RemoveAll(e => e.Id == doc.Id);
        list.Add(new FileEntry { Id = doc.Id, Name = doc.Name, Updated = DateTime.UtcNow, Layers = doc.Count, Thumb = Thumb(doc) });
        WriteIndex(list);
    }

    public static async Task DeleteAsync(string id)
    {
        try
        {
            await Files.DeletePrivateAsync("rupa/" + id + ".json");
        }
        catch
        {
        }
        WebStorage.Local.Remove("rupa.doc." + id);
        var list = List();
        list.RemoveAll(e => e.Id == id);
        WriteIndex(list);
    }

    public static string NewId() => "d" + DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>On the first visit, the sample file.</summary>
    public static async Task EnsureSampleAsync()
    {
        if (WebStorage.Local.Get(SeededKey) is not null) return;
        WebStorage.Local.Set(SeededKey, "1");
        var doc = Seed.Create();
        doc.Id = "lintas";
        await SaveAsync(doc);
    }

    /// <summary>Puts the sample file back as it shipped.</summary>
    public static async Task<Doc> ResetSampleAsync()
    {
        var doc = Seed.Create();
        doc.Id = "lintas";
        await SaveAsync(doc);
        return doc;
    }

    /// <summary>A small SVG of the page: the frames and their larger layers, enough to recognise a file.</summary>
    public static string Thumb(Doc doc)
    {
        Box? all = null;
        foreach (var c in doc.Root.Children)
        {
            if (c.Hidden || c.Kind != NodeKind.Frame) continue;
            var b = G.WorldBounds(c);
            all = all is null ? b : Box.Union(all.Value, b);
        }
        if (all is null) return "";
        var box = all.Value;
        var sb = new StringBuilder();
        sb.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='").Append(G.N(box.X - 20)).Append(' ').Append(G.N(box.Y - 20)).Append(' ')
            .Append(G.N(box.W + 40)).Append(' ').Append(G.N(box.H + 40)).Append("' preserveAspectRatio='xMidYMid meet'>");
        var count = 0;
        void Draw(Node n, int depth)
        {
            if (n.Hidden || count > 160 || depth > 3) return;
            var b = G.WorldBounds(n);
            var fill = n.Kind == NodeKind.Instance && doc.Find(n.MainId) is { } m ? doc.Css(m.Fill) : doc.Css(n.Fill);
            var small = b.W * b.H < box.W * box.H * 0.0004;
            if (n.Kind is NodeKind.Frame or NodeKind.Rect or NodeKind.Instance or NodeKind.Ellipse && fill != "none" && !small)
            {
                count++;
                sb.Append("<rect x='").Append(G.N(b.X)).Append("' y='").Append(G.N(b.Y)).Append("' width='").Append(G.N(b.W))
                    .Append("' height='").Append(G.N(b.H)).Append("' rx='").Append(G.N(Math.Min(n.Radius, Math.Min(b.W, b.H) / 2)))
                    .Append("' fill='").Append(fill == "none" ? "transparent" : fill).Append(depth == 0 ? "' stroke='rgba(31,35,40,.18)' stroke-width='" + G.N(box.W / 300) : "").Append("'/>");
            }
            else if (n.Kind == NodeKind.Text && !small)
            {
                count++;
                sb.Append("<rect x='").Append(G.N(b.X)).Append("' y='").Append(G.N(b.Y + b.H * 0.3)).Append("' width='").Append(G.N(b.W))
                    .Append("' height='").Append(G.N(b.H * 0.4)).Append("' rx='2' fill='").Append(doc.Css(n.Fill)).Append("' opacity='.55'/>");
            }
            foreach (var c in n.Children) Draw(c, depth + 1);
        }
        foreach (var c in doc.Root.Children)
            if (c.Kind == NodeKind.Frame) Draw(c, 0);
        sb.Append("</svg>");
        return sb.ToString();
    }
}
