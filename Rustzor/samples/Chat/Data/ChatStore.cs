using Rustzor.Browser.Storage;
using Rustzor.Browser.WebSocket;
using Rustzor.Data;
using Rustzor.Interop;

namespace Chat.Data;

/// <summary>
/// The chat's state. Messages live in IndexedDB, which every tab of this
/// origin shares; a BroadcastChannel tells the other tabs the moment something
/// changes, so two tabs signed in as two people talk in real time with no
/// server. Each tab remembers who it is in sessionStorage.
/// </summary>
public static class ChatStore
{
    public static readonly List<Person> People = new()
    {
        new() { Id = "ayu", Name = "Ayu Lestari", Title = "Product designer", Color = "#c2410c" },
        new() { Id = "bima", Name = "Bima Santoso", Title = "Frontend engineer", Color = "#0f766e" },
        new() { Id = "citra", Name = "Citra Wijaya", Title = "Marketing lead", Color = "#be185d" },
        new() { Id = "dewi", Name = "Dewi Anggraini", Title = "Design lead", Color = "#7c3aed", Bot = true },
        new() { Id = "raka", Name = "Raka Pratama", Title = "Backend engineer", Color = "#1d4ed8", Bot = true },
        new() { Id = "sari", Name = "Sari Kusuma", Title = "Product manager", Color = "#b45309", Bot = true },
    };

    public static readonly List<Room> Channels = new()
    {
        new() { Id = "umum", Name = "umum", Topic = "Everything for the whole studio. Say hello." },
        new() { Id = "desain", Name = "desain", Topic = "Work in progress, critique and the design system." },
        new() { Id = "rilis", Name = "rilis", Topic = "Release 2.4: what ships on Thursday, and what is blocking it." },
        new() { Id = "random", Name = "random", Topic = "Lunch, music, cats, the weekend." },
    };

    public static readonly string[] QuickReactions = { "👍", "❤️", "😂", "🎉", "👀", "🙏" };

    private static Task? _loading;
    private static IndexedDb? _db;
    private static BroadcastChannel? _channel;
    private static DateTime _lastTypingSent;

    public static List<Message> Messages { get; private set; } = new();
    public static Person? Me { get; private set; }

    /// <summary>Room id to person id to when their typing indicator expires.</summary>
    public static Dictionary<string, Dictionary<string, DateTime>> Typing { get; } = new();

    /// <summary>Raised whenever messages, reactions or typing change, from this tab or another.</summary>
    public static event Action? Changed;

    public static Person PersonOf(string id) => People.FirstOrDefault(p => p.Id == id) ?? new Person { Id = id, Name = id };

    public static Task ReadyAsync() => _loading ??= LoadAsync();

    private static async Task LoadAsync()
    {
        _db = await IndexedDb.OpenAsync("serambi-chat", 1, new StoreSchema("messages", "id", Indexes: new IndexSchema("byRoom", "room")));
        var store = _db.Store<Message>("messages");
        if (await store.CountAsync() == 0) await store.PutManyAsync(Seed.Messages());
        Messages = (await store.AllAsync()).OrderBy(m => m.Time).ToList();

        var me = WebStorage.Session.Get("serambi-me");
        Me = People.FirstOrDefault(p => p.Id == me && !p.Bot);

        _channel = new BroadcastChannel("serambi");
        _channel.OnMessage += Receive;
        Scheduler.SetInterval(ExpireTyping, 1000);
    }

    public static void SignIn(Person p)
    {
        Me = p;
        WebStorage.Session.Set("serambi-me", p.Id);
        Changed?.Invoke();
    }

    public static void SignOut()
    {
        Me = null;
        WebStorage.Session.Remove("serambi-me");
        Changed?.Invoke();
    }

    // ── rooms ────────────────────────────────────────────────────────────

    public static string DmId(string a, string b) => string.CompareOrdinal(a, b) < 0 ? $"dm-{a}-{b}" : $"dm-{b}-{a}";

    public static IEnumerable<Room> DirectRooms() => Me is null
        ? Enumerable.Empty<Room>()
        : People.Where(p => p.Id != Me.Id).Select(p => new Room { Id = DmId(Me.Id, p.Id), Name = p.Name, Kind = "dm", With = p.Id, Topic = p.Title });

    public static Room? RoomOf(string id) => Channels.FirstOrDefault(c => c.Id == id) ?? DirectRooms().FirstOrDefault(r => r.Id == id);

    public static IEnumerable<Message> In(string room) => Messages.Where(m => m.Room == room && m.Parent.Length == 0);

    public static List<Message> Replies(string parent) => Messages.Where(m => m.Parent == parent).ToList();

    // ── reading ──────────────────────────────────────────────────────────

    private static Dictionary<string, string> Marks()
    {
        if (Me is null) return new();
        try { return Json.DeserializeDictionary<string>(WebStorage.Local.Get("serambi-read-" + Me.Id) ?? "{}"); }
        catch { return new(); }
    }

    public static int Unread(string room)
    {
        if (Me is null) return 0;
        var since = Marks().TryGetValue(room, out var t) ? t : "";
        return Messages.Count(m => m.Room == room && m.Author != Me.Id && !m.Deleted && string.CompareOrdinal(m.Time, since) > 0);
    }

    /// <summary>The time this person last read the room, so the view can draw a "new" line.</summary>
    public static string ReadUpTo(string room) => Marks().TryGetValue(room, out var t) ? t : "";

    public static void MarkRead(string room)
    {
        if (Me is null) return;
        var marks = Marks();
        var last = Messages.Where(m => m.Room == room).Select(m => m.Time).DefaultIfEmpty("").Max() ?? "";
        if (marks.TryGetValue(room, out var t) && string.CompareOrdinal(t, last) >= 0) return;
        marks[room] = last;
        WebStorage.Local.Set("serambi-read-" + Me.Id, Json.Serialize(marks));
    }

    public static List<Message> Search(string query)
    {
        var q = query.Trim();
        if (q.Length < 2) return new();
        return Messages.Where(m => !m.Deleted && m.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(m => !m.Room.StartsWith("dm-") || (Me is not null && m.Room.Contains(Me.Id)))
            .OrderByDescending(m => m.Time).Take(40).ToList();
    }

    // ── writing ──────────────────────────────────────────────────────────

    public static async Task<Message> SendAsync(string room, string text, string parent = "", string image = "", Person? author = null)
    {
        var who = author ?? Me ?? throw new InvalidOperationException("Nobody is signed in on this tab.");
        var m = new Message
        {
            Id = DateTime.Now.ToString("yyyyMMddHHmmssfff") + who.Id,
            Room = room,
            Author = who.Id,
            Text = text.Trim(),
            Time = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Parent = parent,
            Image = image,
        };
        if (Typing.TryGetValue(room, out var t)) t.Remove(who.Id);
        await SaveAsync(m);
        if (author is null) Bots.Consider(m);
        return m;
    }

    public static Task EditAsync(Message m, string text)
    {
        m.Text = text.Trim();
        m.Edited = true;
        return SaveAsync(m);
    }

    public static Task DeleteAsync(Message m)
    {
        m.Deleted = true;
        m.Text = "";
        m.Image = "";
        return SaveAsync(m);
    }

    public static Task TogglePinAsync(Message m)
    {
        m.Pinned = !m.Pinned;
        return SaveAsync(m);
    }

    public static Task ReactAsync(Message m, string emoji, string? who = null)
    {
        var id = who ?? Me?.Id ?? "";
        if (!m.Reactions.TryGetValue(emoji, out var list)) m.Reactions[emoji] = list = new List<string>();
        if (!list.Remove(id)) list.Add(id);
        if (list.Count == 0) m.Reactions.Remove(emoji);
        return SaveAsync(m);
    }

    private static async Task SaveAsync(Message m)
    {
        var i = Messages.FindIndex(x => x.Id == m.Id);
        if (i < 0) Messages.Add(m);
        else Messages[i] = m;
        await _db!.Store<Message>("messages").PutAsync(m);
        _channel?.Post(Json.Serialize(new Wire { Kind = "upsert", Message = m }));
        Changed?.Invoke();
    }

    /// <summary>Tells the other tabs this person is typing; at most every 1.5 seconds.</summary>
    public static void SendTyping(string room)
    {
        if (Me is null || (DateTime.Now - _lastTypingSent).TotalSeconds < 1.5) return;
        _lastTypingSent = DateTime.Now;
        _channel?.Post(Json.Serialize(new Wire { Kind = "typing", Room = room, Person = Me.Id }));
    }

    public static void ShowTyping(string room, string person, double seconds)
    {
        if (!Typing.TryGetValue(room, out var t)) Typing[room] = t = new Dictionary<string, DateTime>();
        t[person] = DateTime.Now.AddSeconds(seconds);
        Changed?.Invoke();
    }

    public static async Task ResetAsync()
    {
        await _db!.Store<Message>("messages").ClearAsync();
        await _db.Store<Message>("messages").PutManyAsync(Seed.Messages());
        Messages = (await _db.Store<Message>("messages").AllAsync()).OrderBy(m => m.Time).ToList();
        _channel?.Post(Json.Serialize(new Wire { Kind = "reload" }));
        Changed?.Invoke();
    }

    // ── other tabs ───────────────────────────────────────────────────────

    private sealed class Wire
    {
        public string Kind { get; set; } = "";
        public Message? Message { get; set; }
        public string Room { get; set; } = "";
        public string Person { get; set; } = "";
    }

    private static async void Receive(string json)
    {
        var w = Json.Deserialize<Wire>(json);
        if (w is null) return;
        switch (w.Kind)
        {
            case "upsert" when w.Message is { } m:
                var i = Messages.FindIndex(x => x.Id == m.Id);
                if (i < 0) Messages.Add(m);
                else Messages[i] = m;
                if (Typing.TryGetValue(m.Room, out var t)) t.Remove(m.Author);
                break;
            case "typing":
                ShowTyping(w.Room, w.Person, 3);
                return;
            case "reload":
                Messages = (await _db!.Store<Message>("messages").AllAsync()).OrderBy(x => x.Time).ToList();
                break;
        }
        Changed?.Invoke();
    }

    private static void ExpireTyping()
    {
        var changed = false;
        foreach (var room in Typing.Values)
            foreach (var who in room.Where(kv => kv.Value < DateTime.Now).Select(kv => kv.Key).ToList())
            {
                room.Remove(who);
                changed = true;
            }
        if (changed) Changed?.Invoke();
    }
}
