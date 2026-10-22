using Rustzor.Browser.Storage;
using Rustzor.Interop;

namespace Loket.Model;

/// <summary>The interface language: Indonesian first, English on request. Replies follow the resident's language.</summary>
public static class L
{
    public static bool English { get; private set; }

    public static event Action? Changed;

    public static string T(string id, string en) => English ? en : id;

    public static void Load() => English = WebStorage.Local.Get("loket.lang") == "en";

    public static void Set(bool english)
    {
        English = english;
        WebStorage.Local.Set("loket.lang", english ? "en" : "id");
        Changed?.Invoke();
    }
}

/// <summary>A call the assistant made, and what came back.</summary>
public sealed class ToolRun
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Args { get; set; } = "{}";
    public string Result { get; set; } = "";
    public bool Failed { get; set; }
    public bool Running { get; set; }
}

/// <summary>A passage an answer can cite as [N].</summary>
public sealed class Source
{
    public int N { get; set; }
    public string DocId { get; set; } = "";
    public string DocTitle { get; set; } = "";
    public string Section { get; set; } = "";
    public int Chunk { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>One bubble: what the resident asked, or how the desk answered.</summary>
public sealed class Item
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public string? Image { get; set; }
    public List<ToolRun> Tools { get; set; } = new();
    public List<Source> Sources { get; set; } = new();
    public bool Stopped { get; set; }
    public string Error { get; set; } = "";
    public string Model { get; set; } = "";
    public int In { get; set; }
    public int Out { get; set; }
    public double Cost { get; set; }
    public DateTime At { get; set; }
}

/// <summary>A conversation: a queue ticket with a number, like the ones at the counter.</summary>
public sealed class Conv
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public DateTime Updated { get; set; }
    public List<Item> Items { get; set; } = new();

    public string Ticket => "A-" + Number.ToString("000");
    public int TokensIn => Items.Sum(i => i.In);
    public int TokensOut => Items.Sum(i => i.Out);
    public double Cost => Items.Sum(i => i.Cost);
}

/// <summary>A follow-up the assistant wrote down with the create_note tool.</summary>
public sealed class Note
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Due { get; set; } = "";
    public bool Done { get; set; }
    public DateTime Created { get; set; }
    public string ConvId { get; set; } = "";
}

/// <summary>Which model answers, and how it is reached.</summary>
public sealed class Settings
{
    /// <summary>demo, openai, azure, anthropic or compatible.</summary>
    public string Provider { get; set; } = "demo";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    /// <summary>Price per million tokens, in US dollars, for the cost counter.</summary>
    public double PriceIn { get; set; }
    public double PriceOut { get; set; }
    public string Persona { get; set; } = "";
    public bool Speak { get; set; }
}

/// <summary>
/// Everything the desk keeps, in this browser's localStorage: the queue of conversations,
/// the notes, the settings (the API key included: it never leaves this browser except
/// in calls to the provider), and the documents residents and staff added.
/// </summary>
public static class Desk
{
    private const string IndexKey = "loket.convs";
    private const string NotesKey = "loket.notes";
    private const string SettingsKey = "loket.settings";
    private const string NextKey = "loket.next";

    public static event Action? Changed;

    public static List<Conv> Convs { get; private set; } = new();
    public static List<Note> Notes { get; private set; } = new();
    public static Settings Settings { get; private set; } = new();
    private static bool _loaded;

    public static void Notify() => Changed?.Invoke();

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        L.Load();
        Settings = WebStorage.Local.GetObject<Settings>(SettingsKey) ?? new Settings();
        if (string.IsNullOrWhiteSpace(Settings.Persona)) Settings.Persona = Personas.Desk;
        Notes = WebStorage.Local.GetList<Note>(NotesKey);
        Convs = new();
        foreach (var id in WebStorage.Local.GetList<string>(IndexKey))
        {
            if (WebStorage.Local.Get("loket.conv." + id) is not { } json) continue;
            try
            {
                Convs.Add(Json.Deserialize<Conv>(json));
            }
            catch (Exception)
            {
                // A conversation that no longer parses is skipped, not fatal.
            }
        }
        Convs.Sort((a, b) => b.Updated.CompareTo(a.Updated));
        Knowledge.Load();
    }

    public static Conv? Find(string id) => Convs.FirstOrDefault(c => c.Id == id);

    public static Conv Create()
    {
        var number = (int.TryParse(WebStorage.Local.Get(NextKey), out var n) ? n : 14);
        WebStorage.Local.Set(NextKey, (number + 1).ToString());
        var conv = new Conv { Id = NewId(), Number = number, Title = "", Updated = DateTime.UtcNow };
        Convs.Insert(0, conv);
        Save(conv);
        return conv;
    }

    public static void Save(Conv conv)
    {
        conv.Updated = DateTime.UtcNow;
        WebStorage.Local.Set("loket.conv." + conv.Id, Json.Serialize(conv));
        SaveIndex();
        Changed?.Invoke();
    }

    public static void Rename(Conv conv, string title)
    {
        conv.Title = title.Trim();
        Save(conv);
    }

    public static void Delete(Conv conv)
    {
        Convs.Remove(conv);
        WebStorage.Local.Remove("loket.conv." + conv.Id);
        SaveIndex();
        Changed?.Invoke();
    }

    public static void SaveSettings()
    {
        WebStorage.Local.SetJson(SettingsKey, Settings);
        Changed?.Invoke();
    }

    public static Note AddNote(string title, string detail, string due, string convId)
    {
        var note = new Note { Id = NewId(), Title = title, Detail = detail, Due = due, Created = DateTime.UtcNow, ConvId = convId };
        Notes.Insert(0, note);
        SaveNotes();
        return note;
    }

    public static void SaveNotes()
    {
        WebStorage.Local.SetJson(NotesKey, Notes);
        Changed?.Invoke();
    }

    /// <summary>Clears conversations, notes and added documents; keeps the settings.</summary>
    public static void ResetData()
    {
        foreach (var c in Convs) WebStorage.Local.Remove("loket.conv." + c.Id);
        Convs.Clear();
        Notes.Clear();
        WebStorage.Local.Remove(IndexKey);
        WebStorage.Local.Remove(NotesKey);
        WebStorage.Local.Remove(NextKey);
        Knowledge.ResetUploads();
        Changed?.Invoke();
    }

    private static void SaveIndex() => WebStorage.Local.SetJson(IndexKey, Convs.Select(c => c.Id).ToList());

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Western Indonesia Time, the office's clock.</summary>
    public static DateTime Wib => DateTime.UtcNow.AddHours(7);

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return L.T("baru saja", "just now");
        if (d.TotalHours < 1) return L.T($"{(int)d.TotalMinutes} mnt lalu", $"{(int)d.TotalMinutes} min ago");
        if (d.TotalDays < 1) return L.T($"{(int)d.TotalHours} jam lalu", $"{(int)d.TotalHours} h ago");
        return L.T($"{(int)d.TotalDays} hari lalu", $"{(int)d.TotalDays} d ago");
    }
}
