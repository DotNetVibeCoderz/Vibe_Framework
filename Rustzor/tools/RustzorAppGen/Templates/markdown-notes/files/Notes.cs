using Rustzor.Browser.Storage;

namespace {{Namespace}};

public sealed class Note
{
    public string Id { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Pinned { get; set; }
    public long Updated { get; set; }

    /// <summary>The first heading or line, without Markdown marks.</summary>
    public string Title
    {
        get
        {
            var first = Body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            first = first.TrimStart('#', ' ', '>', '-', '*').Trim();
            return first.Length == 0 ? "Untitled" : first.Length > 60 ? first[..60] + "…" : first;
        }
    }
}

public static class NoteStore
{
    private const string Key = "md-notes";

    public static List<Note> Load()
    {
        var notes = WebStorage.Local.GetList<Note>(Key);
        if (notes.Count == 0)
        {
            notes.Add(new Note { Id = "welcome", Pinned = true, Updated = Now(), Body = Welcome });
            notes.Add(new Note { Id = "groceries", Updated = Now() - 3_600_000, Body = "# Groceries\n\n- [x] Coffee beans\n- [ ] Tempeh\n- [ ] Chillies\n- [ ] Limes\n" });
            Save(notes);
        }
        return notes;
    }

    public static void Save(List<Note> notes) => WebStorage.Local.SetJson(Key, notes);

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private const string Welcome = """
        # Welcome to your notes

        Write in **Markdown** on the left and see it on the right. Notes save as you type.

        ## What works

        - Headings, *emphasis*, **bold**, ~~strikethrough~~ and `inline code`
        - [Links](https://github.com) and > block quotes
        - Task lists:
        - [x] Write a note
        - [ ] Pin the important ones

        > The renderer is `Markdown.cs`, plain C# you can extend.

        ```csharp
        var html = Markdown.ToHtml(note.Body);
        ```

        | Shortcut | Does |
        | --- | --- |
        | Ctrl+S | Export as .md |
        | Ctrl+Alt+N | New note |
        """;
}
