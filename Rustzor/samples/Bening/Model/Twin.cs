using System.Globalization;
using System.Text;
using Rustzor.Browser.Storage;
using Rustzor.Interop;
using Rustzor.UI;

namespace Bening.Model;

public sealed class AlarmEntry
{
    public string Key { get; init; } = "";
    public AlarmDef Def { get; init; } = null!;
    public long Raised { get; init; }
    public long Cleared { get; set; } = -1;
    public double Value { get; init; }
    public string? AckBy { get; set; }
    public long AckAt { get; set; } = -1;
    public bool Active => Cleared < 0;
    public bool Acked => AckBy is not null;
}

public sealed record LogEntry(long T, string Who, string TextId, string TextEn)
{
    public string Text => L.T(TextId, TextEn);
}

public sealed class WorkOrder
{
    public string No { get; set; } = "";
    public string Asset { get; set; } = "";
    public string Title { get; set; } = "";
    public int Priority { get; set; } = 3;
    public string Assignee { get; set; } = "";
    public string Status { get; set; } = "open"; // open, doing, done
    public long Created { get; set; }
    public string? Alarm { get; set; }
    public string Kind { get; set; } = "inspect"; // inspect, repair, clean
}

public sealed class Note
{
    public int Id { get; set; }
    public string Asset { get; set; } = "";
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public long T { get; set; }
}

public sealed class SavedView
{
    public string Name { get; set; } = "";
    public string Camera { get; set; } = "{}";
    public string? Asset { get; set; }
}

/// <summary>
/// The twin as the operator sees it. The engine (rustzor.module.js) simulates and draws; this
/// decides what the plant is, what may be done to it, and keeps the shift's record.
/// </summary>
public static class Twin
{
    public static event Action? Changed;   // something structural: alarms, orders, selection, language
    public static event Action? Ticked;    // new values, twice a second
    public static void Notify() => Changed?.Invoke();

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── the run ─────────────────────────────────────────────────────────
    public static bool Ready { get; private set; }
    public static bool ViewReady { get; set; }
    public static string? Error { get; set; }
    public static string[] TagIds { get; private set; } = Array.Empty<string>();
    private static readonly Dictionary<string, int> Index = new();
    public static double[] Values { get; private set; } = Array.Empty<double>();
    /// <summary>Today so far: m³ produced, kWh, kWh/m³, OEE, availability, performance, quality, kg PAC.</summary>
    public static double[] Kpi { get; private set; } = new double[8];
    public static long Now { get; private set; }
    public static long ViewT { get; private set; }
    public static bool Live { get; private set; } = true;
    public static int Speed { get; private set; } = 60;
    public static int LoadMs { get; set; }
    public static string Hash { get; private set; } = "";
    private static int _callback;

    // ── the record ──────────────────────────────────────────────────────
    public static List<AlarmEntry> Alarms { get; } = new();
    public static List<LogEntry> Log { get; } = new();
    public static List<WorkOrder> Orders { get; } = new();
    public static List<Note> Notes { get; } = new();
    public static List<SavedView> Views { get; } = new();
    public static string? Selected { get; private set; }
    public static string Panel { get; set; } = "asset";
    private static readonly Dictionary<string, (string By, long At)> Acks = new();

    public static double V(string tag) => Index.TryGetValue(tag, out var i) && i < Values.Length ? Values[i] : double.NaN;

    public static int ActiveCount => Alarms.Count(a => a.Active);
    public static int UnackedCount => Alarms.Count(a => !a.Acked && (a.Active || a.Cleared >= 0));

    // ── start ───────────────────────────────────────────────────────────
    public static void Boot(bool fresh = false)
    {
        if (!Js.IsBrowser) return;
        if (_callback == 0) _callback = Callbacks.Register(OnMessage);
        LoadStore();
        var ready = Js.Call(fresh ? "bn.reset" : "bn.boot", w =>
        {
            w.BeginObject().Prop("seed", Plant.Seed).Prop("fresh", fresh).Prop("id", _callback).Name("defs").BeginArray();
            foreach (var a in Plant.Alarms)
                w.BeginObject().Prop("id", a.Id).Prop("tag", a.Tag).Prop("op", a.Op).Prop("v", a.Limit).Prop("db", a.Deadband)
                    .Prop("delay", a.Delay).Prop("prio", a.Priority).Prop("asset", a.Asset).EndObject();
            w.EndArray().EndObject();
        });
        OnReady(ready);
        SendNames();
        SendAcks();
        SendPins();
    }

    private static void OnReady(JsonValue m)
    {
        TagIds = m["tags"].Items.Select(x => x.AsString()).ToArray();
        Index.Clear();
        for (var i = 0; i < TagIds.Length; i++) Index[TagIds[i]] = i;
        Values = new double[TagIds.Length];
        Now = ViewT = m["t"].AsLong();
        Speed = m["speed"].AsInt();
        Hash = m["hash"].AsString();
        Live = true;
        Alarms.Clear();
        foreach (var a in m["alarms"].Items)
        {
            if (!Plant.AlarmById.TryGetValue(a["def"].AsString(), out var def)) continue;
            var e = new AlarmEntry { Key = a["key"].AsString(), Def = def, Raised = a["t"].AsLong(), Cleared = a["off"].AsLong(), Value = a["val"].AsDouble() };
            Alarms.Add(e);
        }
        // the night shift acknowledged what came and went before the morning shift took over
        foreach (var e in Alarms)
        {
            if (Acks.TryGetValue(e.Key, out var ack)) { e.AckBy = ack.By; e.AckAt = ack.At; }
            else if (e.Raised < Plant.Start && e.Cleared >= 0 && e.Cleared < Plant.Start) { e.AckBy = Plant.NightOperator; e.AckAt = e.Raised + 300; }
        }
        Log.Clear();
        foreach (var c in m["cmds"].Items) Log.Add(Describe(c["t"].AsLong(), c["who"].AsString(), c["kind"].AsString(), c["target"].AsString(), c["value"]));
        foreach (var e in m["log"].Items) Log.Add(Describe(e["t"].AsLong(), "", e["kind"].AsString(), e["target"].AsString(), e["value"]));
        Log.Sort((a, b) => a.T.CompareTo(b.T));
        Ready = true;
        Notify();
    }

    private static void OnMessage(JsonValue m)
    {
        switch (m["type"].AsString())
        {
            case "tick":
                Now = m["t"].AsLong();
                ViewT = m["vt"].AsLong();
                var live = m["live"].AsBool();
                Speed = m["speed"].AsInt();
                ParseInto(m["v"].AsString(), Values);
                ParseInto(m["k"].AsString(), Kpi);
                if (live != Live) { Live = live; Notify(); }
                Ticked?.Invoke();
                break;
            case "events":
                foreach (var a in m["alarms"].Items) OnAlarm(a);
                foreach (var e in m["log"].Items)
                {
                    var entry = Describe(e["t"].AsLong(), "", e["kind"].AsString(), e["target"].AsString(), e["value"]);
                    Log.Add(entry);
                    // a protection trip says why; one the operator caused needs no news
                    if (e["kind"].AsString() == "tripped" && e["value"].AsString() != "operator") Toasts.Error(entry.Text);
                }
                Notify();
                break;
            case "pick":
                var asset = m["asset"].AsString();
                Selected = asset == "" ? null : asset;
                if (Selected is not null) Panel = "asset";
                Notify();
                break;
        }
    }

    private static void OnAlarm(JsonValue a)
    {
        if (!Plant.AlarmById.TryGetValue(a["def"].AsString(), out var def)) return;
        var key = a["key"].AsString();
        if (a["on"].AsBool())
        {
            if (Alarms.Any(x => x.Key == key)) return;
            Alarms.Add(new AlarmEntry { Key = key, Def = def, Raised = a["t"].AsLong(), Value = a["val"].AsDouble() });
            if (def.Priority <= 2 && !def.Id.EndsWith(".trip")) Toasts.Warning($"{Clock(a["t"].AsLong())} · {def.Text}");
        }
        else if (Alarms.FirstOrDefault(x => x.Key == key) is { } e)
        {
            e.Cleared = a["t"].AsLong();
        }
    }

    private static void ParseInto(string csv, double[] into)
    {
        var i = 0;
        var start = 0;
        for (var k = 0; k <= csv.Length && i < into.Length; k++)
        {
            if (k == csv.Length || csv[k] == ',')
            {
                into[i++] = double.Parse(csv.AsSpan(start, k - start), NumberStyles.Float, Inv);
                start = k + 1;
            }
        }
    }

    // ── what the operator may do ────────────────────────────────────────
    /// <summary>Why a command is refused, or null when the rules allow it.</summary>
    public static string? Refusal(string kind, string target)
    {
        if (!Live) return L.T("Kembali ke waktu sekarang dulu: rekaman hanya bisa dilihat.", "Go back to the present first: a replay can only be watched.");
        if (target.StartsWith("RW-") || target.StartsWith("DS-"))
        {
            var state = V($"{target}.state");
            var group = target[..2];
            var running = Enumerable.Range(1, 3).Count(n => V($"{group}-{n}.state") == 1);
            switch (kind)
            {
                case "start" when state == 2:
                    return L.T($"{target} masih trip. Reset dulu setelah alarmnya diakui.", $"{target} is still tripped. Reset it once its alarm is acknowledged.");
                case "start" when state == 1:
                    return L.T($"{target} sudah berjalan.", $"{target} is already running.");
                case "stop" when state != 1:
                    return L.T($"{target} tidak sedang berjalan.", $"{target} is not running.");
                case "stop" when running <= 1:
                    return L.T($"{target} satu-satunya pompa yang berjalan. Jalankan pompa lain dulu.", $"{target} is the only pump running. Start another one first.");
                case "reset" when state != 2:
                    return L.T($"{target} tidak dalam kondisi trip.", $"{target} is not tripped.");
                case "reset" when Alarms.Any(a => a.Def.Asset == target && a.Def.Id.EndsWith(".trip") && !a.Acked):
                    return L.T("Akui alarm trip-nya dulu, baru reset.", "Acknowledge the trip alarm before resetting.");
                case "repair" when state == 1:
                    return L.T($"Hentikan {target} dulu: pompa yang diperbaiki harus mati dan terkunci (LOTO).", $"Stop {target} first: a pump under repair must be off and locked out (LOTO).");
            }
        }
        if (target.StartsWith("F-"))
        {
            var states = Enumerable.Range(1, 4).Select(n => V($"F-{n}.state")).ToArray();
            var mine = V($"{target}.state");
            var online = states.Count(s => s == 0);
            switch (kind)
            {
                case "backwash" when mine != 0:
                    return L.T($"{target} tidak sedang menyaring.", $"{target} is not filtering.");
                case "backwash" when states.Any(s => s == 1):
                    return L.T("Satu saringan sedang dicuci. Tangki air pencuci hanya cukup untuk satu.", "A filter is already backwashing. The wash water tank only serves one at a time.");
                case "backwash" or "offline" when online <= 3:
                    return L.T("Minimal tiga saringan harus tetap menyaring.", "At least three filters must keep filtering.");
                case "online" when mine != 2:
                    return L.T($"{target} sudah menyaring.", $"{target} is already in service.");
            }
        }
        return null;
    }

    public static bool Command(string kind, string target, double value = 0)
    {
        if (Refusal(kind, target) is { } why)
        {
            Toasts.Warning(why);
            return false;
        }
        var r = Js.Call("bn.cmd", Js.Args("kind", kind, "target", target, "value", value, "who", Plant.Operator));
        Log.Add(Describe(r["t"].AsLong(), Plant.Operator, kind, target, JsonValue.From(value)));
        Notify();
        return true;
    }

    public static void SetSpeed(int speed) => Js.Send("bn.speed", Js.Args("speed", speed));

    public static void GoLive() => Js.Send("bn.view", Js.Args("live", true));

    public static void ViewAt(long t) => Js.Send("bn.view", Js.Args("t", t, "live", false));

    public static void Reset()
    {
        Orders.RemoveAll(o => o.Status != "done" && o.Alarm is not null);
        Acks.Clear();
        StoreAcks();
        Boot(fresh: true);
        Toasts.Info(L.T("Simulasi dimulai ulang dari pukul 06.00.", "The simulation restarted from 06:00."));
    }

    // ── selection and views ─────────────────────────────────────────────
    public static void Select(string? asset, bool focus = true)
    {
        Selected = asset;
        if (asset is not null) Panel = "asset";
        Js.Send("bn.select", Js.Args("asset", asset ?? "", "focus", focus));
        Notify();
    }

    public static void SaveView(string name)
    {
        name = name.Trim();
        if (name == "") return;
        var cam = Js.Call("bn.getCamera").ToJson();
        Views.RemoveAll(v => v.Name == name);
        Views.Add(new SavedView { Name = name, Camera = cam, Asset = Selected });
        StoreViews();
        Toasts.Success(L.T($"Tampilan “{name}” disimpan.", $"Saved the view “{name}”."));
        Notify();
    }

    public static void OpenView(SavedView v)
    {
        Js.Send("bn.setCamera", w => w.BeginObject().Name("cam").Raw(v.Camera).EndObject());
        Selected = v.Asset;
        Js.Send("bn.select", Js.Args("asset", v.Asset ?? "", "focus", false));
        Notify();
    }

    public static void DeleteView(SavedView v)
    {
        Views.Remove(v);
        StoreViews();
        Notify();
    }

    // ── alarms ──────────────────────────────────────────────────────────
    public static void Ack(AlarmEntry a)
    {
        if (a.Acked) return;
        a.AckBy = Plant.Operator;
        a.AckAt = Now;
        Acks[a.Key] = (a.AckBy, a.AckAt);
        StoreAcks();
        SendAcks();
        Notify();
    }

    public static void AckAll()
    {
        foreach (var a in Alarms.Where(a => !a.Acked))
        {
            a.AckBy = Plant.Operator;
            a.AckAt = Now;
            Acks[a.Key] = (a.AckBy, a.AckAt);
        }
        StoreAcks();
        SendAcks();
        Notify();
    }

    public static void JumpTo(AlarmEntry a)
    {
        ViewAt(Math.Max(0, a.Raised - 60));
        Select(a.Def.Asset);
    }

    private static void SendAcks() => Js.Send("bn.acks", w =>
    {
        w.BeginObject().Name("keys").BeginArray();
        foreach (var a in Alarms.Where(a => a.Acked)) w.String(a.Key);
        w.EndArray().EndObject();
    });

    public static void SendNames() => Js.Send("bn.names", w =>
    {
        w.BeginObject().Name("names").BeginObject();
        foreach (var a in Plant.Assets) w.Prop(a.Id, a.Name);
        foreach (var d in Plant.Alarms) w.Prop("alarm:" + d.Id, d.Short);
        w.EndObject().EndObject();
    });

    // ── work orders ─────────────────────────────────────────────────────
    public static WorkOrder NewOrder(string asset, string title, int priority, string kind, string? alarm = null)
    {
        var n = Orders.Count == 0 ? 118 : Orders.Max(o => int.Parse(o.No[^3..], Inv)) + 1;
        var o = new WorkOrder { No = $"PK-2610-{n:000}", Asset = asset, Title = title, Priority = priority, Kind = kind, Created = Now, Alarm = alarm, Assignee = Plant.Technicians[n % Plant.Technicians.Length] };
        Orders.Insert(0, o);
        StoreOrders();
        Toasts.Success(L.T($"Perintah kerja {o.No} dibuat untuk {asset}.", $"Work order {o.No} raised for {asset}."));
        Notify();
        return o;
    }

    public static void Advance(WorkOrder o)
    {
        if (o.Status == "done") return;
        if (o.Status == "open") o.Status = "doing";
        else
        {
            // finishing a repair puts the pump back as new, if the rules let the work happen
            if (o.Kind == "repair" && (o.Asset.StartsWith("RW-") || o.Asset.StartsWith("DS-")))
            {
                if (Refusal("repair", o.Asset) is { } why) { Toasts.Warning(why); return; }
                Js.Call("bn.cmd", Js.Args("kind", "repair", "target", o.Asset, "value", 0, "who", o.Assignee));
                Log.Add(new LogEntry(Now, o.Assignee, $"{o.Asset} selesai diperbaiki ({o.No})", $"{o.Asset} repaired ({o.No})"));
            }
            o.Status = "done";
        }
        StoreOrders();
        Notify();
    }

    public static void Assign(WorkOrder o, string who)
    {
        o.Assignee = who;
        StoreOrders();
        Notify();
    }

    // ── notes ───────────────────────────────────────────────────────────
    public static void AddNote(string asset, string text)
    {
        text = text.Trim();
        if (text == "") return;
        Notes.Add(new Note { Id = Notes.Count == 0 ? 1 : Notes.Max(n => n.Id) + 1, Asset = asset, Text = text, Author = Plant.Operator, T = Now });
        StoreNotes();
        SendPins();
        Notify();
    }

    public static void DeleteNote(Note n)
    {
        Notes.Remove(n);
        StoreNotes();
        SendPins();
        Notify();
    }

    private static void SendPins() => Js.Send("bn.pins", w =>
    {
        w.BeginObject().Name("pins").BeginArray();
        var i = 0;
        foreach (var g in Notes.GroupBy(n => n.Asset))
            w.BeginObject().Prop("asset", g.Key).Prop("n", g.Count()).Prop("i", ++i).EndObject();
        w.EndArray().EndObject();
    });

    // ── storage: this viewer's own record ───────────────────────────────
    private static bool _loaded;

    private static void LoadStore()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (WebStorage.Local.Get("bening.acks") is { } acks)
                foreach (var x in JsonValue.Parse(acks).Items) Acks[x["key"].AsString()] = (x["by"].AsString(), x["at"].AsLong());
            Orders.Clear();
            if (WebStorage.Local.Get("bening.orders") is { } orders)
                foreach (var x in JsonValue.Parse(orders).Items)
                    Orders.Add(new WorkOrder
                    {
                        No = x["no"].AsString(), Asset = x["asset"].AsString(), Title = x["title"].AsString(), Priority = x["prio"].AsInt(),
                        Assignee = x["who"].AsString(), Status = x["status"].AsString(), Created = x["t"].AsLong(),
                        Alarm = x["alarm"].AsStringOrNull(), Kind = x["kind"].AsString(),
                    });
            else SeedOrders();
            Notes.Clear();
            if (WebStorage.Local.Get("bening.notes") is { } notes)
                foreach (var x in JsonValue.Parse(notes).Items)
                    Notes.Add(new Note { Id = x["id"].AsInt(), Asset = x["asset"].AsString(), Text = x["text"].AsString(), Author = x["by"].AsString(), T = x["t"].AsLong() });
            Views.Clear();
            if (WebStorage.Local.Get("bening.views") is { } views)
                foreach (var x in JsonValue.Parse(views).Items)
                    Views.Add(new SavedView { Name = x["name"].AsString(), Camera = x["cam"].ToJson(), Asset = x["asset"].AsStringOrNull() });
        }
        catch (Exception e)
        {
            Js.Log(2, "bening: stored record unreadable, starting clean: " + e.Message);
        }
    }

    private static void SeedOrders()
    {
        Orders.Add(new WorkOrder { No = "PK-2610-117", Asset = "RW-2", Title = "Ganti bantalan atas motor RW-2 (SKF 6316), suhu naik sejak 19 Okt", Priority = 2, Assignee = "Dadang Suryana", Status = "open", Created = 0, Kind = "repair" });
        Orders.Add(new WorkOrder { No = "PK-2610-116", Asset = "F-3", Title = "Periksa nosel underdrain F-3, backwash tidak merata", Priority = 3, Assignee = "Euis Rahmawati", Status = "doing", Created = 0, Kind = "inspect" });
        Orders.Add(new WorkOrder { No = "PK-2610-115", Asset = "DS-3", Title = "Kalibrasi transmiter tekanan header distribusi", Priority = 3, Assignee = "Yusuf Hidayat", Status = "done", Created = 0, Kind = "inspect" });
    }

    private static void StoreAcks()
    {
        var w = new JsonWriter();
        w.BeginArray();
        foreach (var (k, v) in Acks) w.BeginObject().Prop("key", k).Prop("by", v.By).Prop("at", v.At).EndObject();
        w.EndArray();
        WebStorage.Local.Set("bening.acks", w.ToString());
    }

    private static void StoreOrders()
    {
        var w = new JsonWriter();
        w.BeginArray();
        foreach (var o in Orders)
            w.BeginObject().Prop("no", o.No).Prop("asset", o.Asset).Prop("title", o.Title).Prop("prio", o.Priority).Prop("who", o.Assignee)
                .Prop("status", o.Status).Prop("t", o.Created).Prop("alarm", o.Alarm).Prop("kind", o.Kind).EndObject();
        w.EndArray();
        WebStorage.Local.Set("bening.orders", w.ToString());
    }

    private static void StoreNotes()
    {
        var w = new JsonWriter();
        w.BeginArray();
        foreach (var n in Notes) w.BeginObject().Prop("id", n.Id).Prop("asset", n.Asset).Prop("text", n.Text).Prop("by", n.Author).Prop("t", n.T).EndObject();
        w.EndArray();
        WebStorage.Local.Set("bening.notes", w.ToString());
    }

    private static void StoreViews()
    {
        var w = new JsonWriter();
        w.BeginArray();
        foreach (var v in Views) w.BeginObject().Prop("name", v.Name).Name("cam").Raw(v.Camera).Prop("asset", v.Asset).EndObject();
        w.EndArray();
        WebStorage.Local.Set("bening.views", w.ToString());
    }

    // ── words ───────────────────────────────────────────────────────────
    public static string Clock(long t) => $"{t / 3600 % 24:00}.{t / 60 % 60:00}";

    public static string ClockSec(long t) => $"{t / 3600 % 24:00}.{t / 60 % 60:00}.{t % 60:00}";

    public static string Day(long t)
    {
        var d = new DateTime(2026, 10, 22).AddDays(t / 86400);
        string[] hari = { "Minggu", "Senin", "Selasa", "Rabu", "Kamis", "Jumat", "Sabtu" };
        string[] bulan = { "Januari", "Februari", "Maret", "April", "Mei", "Juni", "Juli", "Agustus", "September", "Oktober", "November", "Desember" };
        return L.English ? d.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture) : $"{hari[(int)d.DayOfWeek]}, {d.Day} {bulan[d.Month - 1]} {d.Year}";
    }

    /// <summary>A value in the tag's own unit and precision, with Indonesian decimal commas when the UI is Indonesian.</summary>
    public static string Fmt(double v, int decimals)
    {
        if (double.IsNaN(v)) return "–";
        var s = v.ToString("N" + decimals, CultureInfo.InvariantCulture);
        return L.English ? s : s.Replace(",", " ").Replace(".", ",").Replace(" ", ".");
    }

    public static string Fmt(string tag, double factor = 1)
    {
        var info = Plant.TagById.GetValueOrDefault(tag);
        return Fmt(V(tag) * factor, info?.Decimals ?? 1);
    }

    public static string StateText(string asset)
    {
        var s = V($"{asset}.state");
        if (asset.StartsWith("F-"))
            return s switch { 0 => L.T("Menyaring", "Filtering"), 1 => L.T("Backwash", "Backwashing"), _ => L.T("Tidak beroperasi", "Out of service") };
        return s switch { 1 => L.T("Berjalan", "Running"), 2 => L.T("Trip", "Tripped"), _ => L.T("Berhenti", "Stopped") };
    }

    private static LogEntry Describe(long t, string who, string kind, string target, JsonValue value)
    {
        var v = value.IsNumber ? value.AsDouble() : 0;
        var s = value.IsString ? value.AsString() : "";
        var who2 = who == "" ? L.T("Sistem", "System") : who;
        return kind switch
        {
            "rain" => new(t, "BMKG", "Hujan lebat di hulu Ci Sangga", "Heavy rain upstream on the Ci Sangga"),
            "pac" => new(t, who2, $"Dosis PAC diubah ke {Fmt(v, 0)} mg/L", $"PAC dose set to {Fmt(v, 0)} mg/L"),
            "cl" => new(t, who2, $"Dosis klor diubah ke {Fmt(v, 2)} mg/L", $"Chlorine dose set to {Fmt(v, 2)} mg/L"),
            "flow" => new(t, who2, $"Setpoin debit diubah ke {Fmt(v, 0)} L/s", $"Flow setpoint set to {Fmt(v, 0)} L/s"),
            "refill" => new(t, who2, "Tangki PAC diisi ulang", "PAC tank refilled"),
            "start" => new(t, who2, $"{target} dijalankan", $"{target} started"),
            "stop" => new(t, who2, $"{target} dihentikan", $"{target} stopped"),
            "trip" => new(t, who2, $"{target} di-trip manual (uji skenario)", $"{target} tripped by hand (what-if)"),
            "reset" => new(t, who2, $"Trip {target} di-reset", $"{target} trip reset"),
            "auto" => new(t, who2, v != 0 ? $"{target} ke mode otomatis" : $"{target} ke mode manual", v != 0 ? $"{target} set to automatic" : $"{target} set to manual"),
            "backwash" => new(t, who2, $"Backwash {target} dimulai", $"{target} backwash started"),
            "autobackwash" => new(t, who2, $"Backwash otomatis {target}: kehilangan tekanan 2,6 m", $"{target} backwashed itself at 2.6 m head loss"),
            "offline" => new(t, who2, $"{target} dikeluarkan dari operasi", $"{target} taken out of service"),
            "online" => new(t, who2, $"{target} kembali beroperasi", $"{target} back in service"),
            "repair" => new(t, who2, $"{target} selesai diperbaiki", $"{target} repaired"),
            "tripped" => new(t, who2, s switch
            {
                "temp" => $"{target} trip: suhu bantalan lewat 90 °C",
                "vib" => $"{target} trip: getaran lewat 7,1 mm/s",
                "level" => $"{target} trip: reservoir terlalu rendah",
                _ => $"{target} trip",
            }, s switch
            {
                "temp" => $"{target} tripped: bearing above 90 °C",
                "vib" => $"{target} tripped: vibration above 7.1 mm/s",
                "level" => $"{target} tripped: reservoir too low",
                _ => $"{target} tripped",
            }),
            "autostart" => new(t, who2, $"{target} cadangan menyala otomatis", $"Standby {target} started itself"),
            _ => new(t, who2, $"{kind} {target}", $"{kind} {target}"),
        };
    }

    // ── exports ─────────────────────────────────────────────────────────
    private static string Q(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static void ExportHistory()
    {
        var tags = new[] { "raw.turb", "raw.flow", "pac.dose", "settled.turb", "treated.turb", "prod.flow", "cl.res", "res.level", "demand", "dist.press", "plant.kw",
            "RW-1.state", "RW-2.state", "RW-3.state", "RW-2.temp", "RW-2.vib", "F-1.hl", "F-2.hl", "F-3.hl", "F-4.hl", "DS-1.state", "DS-2.state", "DS-3.state" };
        var r = Js.Call("bn.csv", w =>
        {
            w.BeginObject().Prop("name", $"bening-{Plant.Date}-{(L.English ? "history" : "riwayat")}.csv").Prop("step", 60).Prop("date", Plant.Date).Name("cols").BeginArray();
            foreach (var t in tags)
            {
                var info = Plant.TagById[t];
                var label = t.Contains('-') ? $"{t.Split('.')[0]} {info.Name}" : info.Name;
                w.BeginObject().Prop("tag", t).Prop("label", info.Unit == "" ? label : $"{label} ({info.Unit})").EndObject();
            }
            w.EndArray().EndObject();
        });
        Toasts.Success(L.T($"Riwayat diekspor: {r["rows"].AsInt()} baris, satu per menit.", $"Exported the history: {r["rows"].AsInt()} rows, one a minute."));
    }

    public static void ExportAlarms()
    {
        var sb = new StringBuilder();
        sb.Append(L.T("muncul,hilang,prioritas,aset,alarm,nilai,diakui_oleh,diakui_pada", "raised,cleared,priority,asset,alarm,value,acknowledged_by,acknowledged_at")).Append("\r\n");
        foreach (var a in Alarms.OrderBy(a => a.Raised))
            sb.Append(ClockSec(a.Raised)).Append(',').Append(a.Cleared < 0 ? "" : ClockSec(a.Cleared)).Append(',').Append(a.Def.Priority).Append(',')
              .Append(a.Def.Asset).Append(',').Append(Q(a.Def.Text)).Append(',').Append(a.Value.ToString("0.###", Inv)).Append(',')
              .Append(Q(a.AckBy ?? "")).Append(',').Append(a.AckAt < 0 ? "" : ClockSec(a.AckAt)).Append("\r\n");
        Js.Send("bn.download", Js.Args("name", $"bening-{Plant.Date}-alarm.csv", "text", sb.ToString(), "mime", "text/csv"));
        Toasts.Success(L.T($"Log alarm diekspor: {Alarms.Count} baris.", $"Exported the alarm log: {Alarms.Count} rows."));
    }

    public static void ExportOrders()
    {
        var sb = new StringBuilder();
        sb.Append(L.T("nomor,aset,pekerjaan,prioritas,teknisi,status,dibuat", "number,asset,work,priority,technician,status,raised")).Append("\r\n");
        foreach (var o in Orders)
            sb.Append(o.No).Append(',').Append(o.Asset).Append(',').Append(Q(o.Title)).Append(',').Append(o.Priority).Append(',').Append(Q(o.Assignee)).Append(',')
              .Append(o.Status).Append(',').Append(ClockSec(o.Created)).Append("\r\n");
        Js.Send("bn.download", Js.Args("name", $"bening-{Plant.Date}-{(L.English ? "work-orders" : "perintah-kerja")}.csv", "text", sb.ToString(), "mime", "text/csv"));
        Toasts.Success(L.T($"Perintah kerja diekspor: {Orders.Count} baris.", $"Exported the work orders: {Orders.Count} rows."));
    }
}
