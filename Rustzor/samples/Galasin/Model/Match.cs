using Rustzor.Browser.Storage;
using Rustzor.Game;
using Rustzor.Interop;

namespace Galasin.Model;

/// <summary>A line across the screen when something happens: a tag, a trip home, the result.</summary>
public sealed record Banner(string Kind, string Title, string Sub, char Team, double Until);

/// <summary>
/// The match, as every tab sees it. Tabs of one browser find each other over a
/// BroadcastChannel (Rustzor.Game's <see cref="NetSession"/>). One tab is the host: it runs
/// the rules and the bots twenty times a second and broadcasts a snapshot; every other tab
/// sends its own player's position and draws the rest from the snapshots. The host is the
/// tab with the smallest id; when it closes, the next one carries on from the last snapshot.
/// Each tab moves its own player itself (in the yard engine), so the controls never wait
/// for the network.
/// </summary>
public static partial class Match
{
    public const int TickMs = 50;

    public static event Action? Changed;
    public static event Action<Banner>? Announced;

    public static NetSession? Net { get; private set; }
    public static string Room { get; private set; } = "rt05";
    public static string MeId => Net?.PlayerId ?? "";
    public static Profile Me { get; } = new();
    public static readonly Dictionary<string, Profile> Lobby = new();

    public static Phase Phase { get; private set; } = Phase.Lobby;
    public static double PhaseTime { get; private set; }
    public static double TimeLeft { get; private set; }
    public static readonly int[] Score = { 0, 0 };
    public static char Attack { get; private set; } = 'p';
    public static int Gen { get; private set; }
    public static string Winner { get; private set; } = "";
    public static List<Player> Roster { get; private set; } = new();
    public static int Minutes { get; set; } = 3;
    public static int Target { get; set; } = 5;
    public static bool FillWithBots { get; set; } = true;
    public static bool Sound { get; private set; } = true;

    public static string HostId { get; private set; } = "";
    public static bool IsHost => HostId != "" && HostId == MeId;
    public static bool EngineReady { get; private set; }
    public static string? EngineError { get; private set; }
    /// <summary>Milliseconds the last tick took in C#, smoothed: the host's rules and snapshot, or a client's report.</summary>
    public static double TickCost { get; private set; }
    public static int Ticks { get; private set; }
    /// <summary>Milliseconds a redraw of the page's bars takes, smoothed (about once a second, and on events).</summary>
    public static double RenderCost { get; private set; }
    /// <summary>Whether this tab is on screen. A hidden tab's timers are throttled, so it should not referee.</summary>
    public static bool Visible { get; private set; } = true;

    private static IDisposable? _timer;
    private static double _lastTick, _lastSnapAt, _lastRoster;
    private static int _shownSecond = -1;
    private static int _pushedRole = -1;
    private static int _pushedGen = -1;

    public static int ScoreOf(char team) => Score[team == 'm' ? 0 : 1];
    public static Player? MePlayer => Roster.Find(p => p.Id == MeId);
    public static Role MyRole => MePlayer is { } p ? p.RoleWhen(Attack) : new Role(RoleKind.Watching, 0);
    public static string NameOf(string id) => Roster.Find(p => p.Id == id)?.Name ?? (Lobby.TryGetValue(id, out var l) ? l.Name : id == MeId ? Me.Name : "?");

    // ── joining and leaving ──────────────────────────────────────────────
    private static readonly string[] KidNames = { "Ujang", "Siti", "Asep", "Rina", "Budi", "Dewi", "Joko", "Lilis", "Tono", "Wulan", "Agus", "Euis" };

    public static void Open(string room)
    {
        if (Net is not null && Room == room) return;
        Close();
        Room = room;
        LoadPrefs();
        Net = NetSession.Local("galasin:" + room);
        Net.On("hi", m => OnHello(m.From, m.Data))
           .On("go", m => OnGo(m.Data))
           .On("p", m => OnPosition(m.From, m.Data))
           .On("s", m => OnSnapshot(m.From, m.Data))
           .On("r", m => OnRoster(m.From, m.Data))
           .On("ev", m => OnEvent(m.Data));
        Net.OnPeerJoined += _ => Hello();
        Net.OnPeerLeft += OnPeerLeft;
        Phase = Phase.Lobby;
        HostId = "";
        Roster = new();
        Lobby.Clear();
        Hello();
        _lastTick = Js.Now();
        _timer = Scheduler.SetInterval(Tick, TickMs);
        Notify();
    }

    public static void Close()
    {
        _timer?.Dispose();
        _timer = null;
        Net?.Dispose();
        Net = null;
        EngineReady = false;
        _pushedRole = -1;
        _pushedGen = -1;
    }

    private static void LoadPrefs()
    {
        var s = WebStorage.Local;
        Me.Name = s.Get("galasin.name") ?? KidNames[Dice.Range(0, KidNames.Length)];
        Me.Team = (s.Get("galasin.team") ?? "m") == "p" ? 'p' : 'm';
        if (int.TryParse(s.Get("galasin.minutes"), out var m) && m is >= 1 and <= 10) Minutes = m;
        if (int.TryParse(s.Get("galasin.target"), out var t) && t is >= 1 and <= 9) Target = t;
        FillWithBots = s.Get("galasin.bots") != "0";
        Sound = s.Get("galasin.sound") != "0";
    }

    public static void SetProfile(string? name = null, char? team = null)
    {
        if (name is not null) Me.Name = name.Trim().Length == 0 ? Me.Name : name.Trim()[..Math.Min(14, name.Trim().Length)];
        if (team is { } t) Me.Team = t;
        WebStorage.Local.Set("galasin.name", Me.Name);
        WebStorage.Local.Set("galasin.team", Me.Team.ToString());
        Hello();
        PushLobby();
        Notify();
    }

    public static void SetOptions(int? minutes = null, int? target = null, bool? bots = null)
    {
        if (minutes is { } m) Minutes = m;
        if (target is { } t) Target = t;
        if (bots is { } b) FillWithBots = b;
        WebStorage.Local.Set("galasin.minutes", Minutes.ToString());
        WebStorage.Local.Set("galasin.target", Target.ToString());
        WebStorage.Local.Set("galasin.bots", FillWithBots ? "1" : "0");
        Notify();
    }

    public static void SetSound(bool on)
    {
        Sound = on;
        WebStorage.Local.Set("galasin.sound", on ? "1" : "0");
        if (EngineReady) Js.Send("gs.sound", Js.Args("on", on));
        Notify();
    }

    private static void Hello() => Net?.Send("hi", new Dictionary<string, object?> { ["name"] = Me.Name, ["team"] = Me.Team.ToString(), ["vis"] = Visible });

    private static void OnHello(string from, JsonValue d)
    {
        if (!Lobby.TryGetValue(from, out var p)) Lobby[from] = p = new Profile { Id = from };
        p.Name = d["name"].AsString();
        p.Team = d["team"].AsString() == "p" ? 'p' : 'm';
        p.Visible = !d.Has("vis") || d["vis"].AsBool();
        if (IsHost && Phase != Phase.Lobby) BroadcastRoster();   // a newcomer mid-match learns who is who at once
        PushLobby();
        Notify();
    }

    private static void OnPeerLeft(string id)
    {
        // the referee's next tick hands a departed player to a bot (see Tick)
        Lobby.Remove(id);
        PushLobby();
        Notify();
    }

    /// <summary>
    /// The referee plays anyone who has gone quiet with a bot, so the teams stay whole: a
    /// closed tab, or one hidden so long that its timers have all but stopped. If that tab
    /// starts sending again, the person gets their kid back.
    /// </summary>
    private static void ReplaceDeparted()
    {
        if (Net is null || Phase is not (Phase.Play or Phase.Swap)) return;
        var now = Js.Now();
        var changed = false;
        foreach (var p in Roster)
        {
            if (p.Bot || p.Id == MeId) continue;
            if (Net.Peers.Contains(p.Id) && now - p.LastHeard < 3000) continue;
            p.Bot = p.StandIn = true;
            p.Courage = Dice.Range(1.55, 2.35);
            p.Think = 0;
            changed = true;
            Event("left", p.Id, "");
        }
        if (changed) BroadcastRoster();
    }

    /// <summary>Everyone in the room, this tab first.</summary>
    public static List<Profile> People()
    {
        var list = new List<Profile> { new() { Id = MeId, Name = Me.Name, Team = Me.Team } };
        if (Net is null) return list;
        foreach (var id in Net.Peers.OrderBy(i => i, StringComparer.Ordinal))
            if (Lobby.TryGetValue(id, out var p)) list.Add(p);
        return list;
    }

    // ── the engine ───────────────────────────────────────────────────────
    public static void OnEngine(JsonValue m)
    {
        switch (m["type"].AsString())
        {
            case "ready":
                EngineReady = true;
                Js.Send("gs.sound", Js.Args("on", Sound));
                _pushedRole = -1;
                _pushedGen = -1;
                if (Phase == Phase.Lobby) PushLobby(); else PushRoster();
                PushRole(force: true);
                break;
            case "error":
                EngineError = m["message"].AsString();
                break;
            case "leaving":
                Close();
                return;
            case "visible":
                Visible = m["v"].AsBool();
                Hello();
                break;
        }
        Notify();
    }

    private static void PushLobby()
    {
        if (!EngineReady || Phase != Phase.Lobby) return;
        var people = People();
        var w = new JsonWriter().BeginObject().Prop("me", MeId).Prop("view", "lobby").Name("list").BeginArray();
        // stand them in two rows inside the first box, Red on the left of the view and White on the right
        var counts = new[] { 0, 0 };
        foreach (var p in people)
        {
            var side = p.Team == 'm' ? 0 : 1;
            var i = counts[side]++;
            var x = (side == 0 ? 1 : -1) * (1.2 + i * 1.1);
            w.BeginObject().Prop("id", p.Id).Prop("name", p.Name).Prop("team", p.Team.ToString()).Prop("bot", false)
             .Prop("x", x).Prop("z", 2.2 + i * 0.4).Prop("yaw", Math.PI).EndObject();
        }
        Js.Send("gs.roster", new JsArgs(w.EndArray().EndObject().ToString()));
        Js.Send("gs.role", Js.Args("mode", "none"));
        _pushedRole = -1;
    }

    private static void PushRoster()
    {
        if (!EngineReady) return;
        var w = new JsonWriter().BeginObject().Prop("me", MeId).Prop("view", "play").Name("list").BeginArray();
        foreach (var p in Roster)
            w.BeginObject().Prop("id", p.Id).Prop("name", p.Name).Prop("team", p.Team.ToString()).Prop("bot", p.Bot)
             .Prop("x", p.X).Prop("z", p.Z).Prop("yaw", p.Yaw).EndObject();
        Js.Send("gs.roster", new JsArgs(w.EndArray().EndObject().ToString()));
    }

    /// <summary>Tells the engine what the local player may do, and moves them when a new round starts.</summary>
    private static void PushRole(bool force = false)
    {
        if (!EngineReady || Phase == Phase.Lobby) return;
        var me = MePlayer;
        var role = MyRole;
        var frozen = Phase != Phase.Play;
        var key = (int)role.Kind * 10 + role.Line + (frozen ? 100 : 0);
        var teleport = Gen != _pushedGen;
        if (!force && key == _pushedRole && !teleport) return;
        _pushedRole = key;
        _pushedGen = Gen;
        Js.Send("gs.role", Js.Args("mode", role.Mode, "line", role.Kind == RoleKind.LineGuard ? Field.Lines[role.Line] : 0.0,
            "speed", role.Speed, "frozen", frozen, "x", me?.X ?? 0, "z", me?.Z ?? 0, "yaw", me?.Yaw ?? 0, "teleport", teleport || force, "gen", Gen));
    }

    // ── the clock ────────────────────────────────────────────────────────
    private static string ComputeHost()
    {
        if (Net is null) return "";
        var now = Js.Now();
        bool Shown(string id) => id == MeId ? Visible : !Lobby.TryGetValue(id, out var p) || p.Visible;
        var anyShown = Visible;
        foreach (var id in Net.Peers) anyShown |= Shown(id);
        // a match keeps its host while that host is still sending, unless it went off screen and another tab is on
        var alive = HostId == MeId || Net.Peers.Contains(HostId) && now - _lastSnapAt < 2500;
        if (Phase != Phase.Lobby && HostId != "" && alive && (Shown(HostId) || !anyShown))
            return HostId;
        // otherwise: during a match, a tab that plays in it (one that has not even heard the
        // roster yet cannot run it); then the smallest id, among the tabs on screen if any are
        var inMatch = Phase != Phase.Lobby;
        var players = inMatch && Roster.Count > 0 && Roster.Exists(p => !p.Bot && (p.Id == MeId || Net.Peers.Contains(p.Id)));
        bool Candidate(string id) =>
            (!inMatch || Roster.Count > 0 || id != MeId)
            && (!players || Roster.Exists(p => p.Id == id && !p.Bot))
            && (!anyShown || Shown(id));
        var best = "";
        foreach (var id in Net.Peers.Append(MeId))
            if (Candidate(id) && (best == "" || string.CompareOrdinal(id, best) < 0)) best = id;
        if (best == "")
            foreach (var id in Net.Peers.Append(MeId))
                if ((!inMatch || Roster.Count > 0 || id != MeId) && (best == "" || string.CompareOrdinal(id, best) < 0)) best = id;
        return best;
    }

    private static void Tick()
    {
        if (Net is null) return;
        _inTick = true;
        var start = Js.Now();
        var dt = Math.Clamp((start - _lastTick) / 1000.0, 0, 0.25);
        _lastTick = start;
        var was = HostId;
        HostId = ComputeHost();
        if (was != HostId) Notify();

        if (was != HostId && IsHost && Phase != Phase.Lobby) TakeOver(was);
        if (IsHost && Phase != Phase.Lobby) ReplaceDeparted();
        if (EngineReady && Phase is Phase.Count or Phase.Play or Phase.Swap && MePlayer is { } me)
        {
            var mine = Js.Call("gs.me").AsStringOrNull();
            if (mine is not null)
            {
                if (IsHost) TakePosition(me, mine);
                else Net.Send("p", mine);
            }
        }
        if (IsHost && Phase != Phase.Lobby)
        {
            Step(dt);
            if (Phase != Phase.Lobby) BroadcastSnapshot();
            if (start - _lastRoster > 2000) BroadcastRoster();
        }
        var second = (int)Math.Ceiling(Phase == Phase.Play ? TimeLeft : PhaseTime);
        if (second != _shownSecond)
        {
            _shownSecond = second;
            if (!IsHost && Phase != Phase.Lobby) UnpackEntities(_lastEntities);   // for the player list, once a second
            Notify();
        }
        Ticks++;
        TickCost = TickCost * 0.9 + (Js.Now() - start) * 0.1;
        _inTick = false;
        if (_dirty)
        {
            _dirty = false;
            var r = Js.Now();
            Changed?.Invoke();
            RenderCost = RenderCost * 0.8 + (Js.Now() - r) * 0.2;
        }
    }

    // Positions travel as short strings of whole numbers, "x,z,yaw,lunge,gen" in centimetres
    // and centiradians: far cheaper to write and read than JSON numbers in the interpreter.
    private static readonly int[] Fields = new int[6];

    /// <summary>Reads up to <paramref name="count"/> comma-separated integers from s at i; returns where it stopped.</summary>
    private static int ReadInts(string s, int i, int count)
    {
        for (var k = 0; k < count; k++)
        {
            var neg = false;
            var v = 0;
            if (i < s.Length && s[i] == '-') { neg = true; i++; }
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') v = v * 10 + (s[i++] - '0');
            Fields[k] = neg ? -v : v;
            if (i < s.Length && s[i] == ',') i++;
        }
        return i;
    }

    private static void TakePosition(Player p, string packed)
    {
        ReadInts(packed, 0, 5);
        var role = p.RoleWhen(Attack);
        p.X = Fields[0] / 100.0;
        p.Z = Fields[1] / 100.0;
        p.Yaw = Fields[2] / 100.0;
        p.Lunge = Fields[3] == 1 ? 0.1 : 0;
        // the engine keeps a person inside their role; the host checks it all the same
        switch (role.Kind)
        {
            case RoleKind.LineGuard: p.Z = Field.Lines[role.Line]; p.X = Math.Clamp(p.X, -Field.Half, Field.Half); break;
            case RoleKind.Sodor: p.X = 0; p.Z = Math.Clamp(p.Z, 0, Field.Length); break;
            default: p.X = Math.Clamp(p.X, -Field.Half, Field.Half); p.Z = Math.Clamp(p.Z, Field.BackOfHome, Field.BackOfGoal); break;
        }
    }

    private static void OnPosition(string from, JsonValue d)
    {
        if (!IsHost || Phase == Phase.Lobby) return;
        var p = Roster.Find(r => r.Id == from);
        if (p is null || p.Bot && !p.StandIn) return;
        p.LastHeard = Js.Now();
        if (p.StandIn)
        {
            // back from away: the bot hands the kid back
            p.Bot = p.StandIn = false;
            BroadcastRoster();
            Notify();
        }
        if (Phase != Phase.Play && Phase != Phase.Count) return;
        var packed = d.AsString();
        ReadInts(packed, 0, 5);
        // a position from before the last reset (sent while the teleport was on its way) is stale
        if (Fields[4] != Gen) return;
        TakePosition(p, packed);
    }

    // ── starting ─────────────────────────────────────────────────────────
    /// <summary>Asks the host to start; the host may be this tab.</summary>
    public static void RequestStart()
    {
        var data = new Dictionary<string, object?> { ["min"] = Minutes, ["tgt"] = Target, ["bots"] = FillWithBots };
        if (IsHost || HostId == "") OnGo(JsonValue.Parse(Json.Serialize(data)));
        else Net?.Send("go", data);
    }

    private static void OnGo(JsonValue d)
    {
        if (!IsHost || Phase != Phase.Lobby) return;
        Minutes = Math.Clamp(d["min"].AsInt(), 1, 10);
        Target = Math.Clamp(d["tgt"].AsInt(), 1, 9);
        FillWithBots = d["bots"].AsBool();
        var people = People();
        Roster = new List<Player>();
        foreach (var team in new[] { 'm', 'p' })
        {
            var slot = 0;
            foreach (var p in people.Where(p => p.Team == team))
            {
                if (slot >= Field.TeamSize) break;
                Roster.Add(new Player { Id = p.Id, Name = p.Name, Team = team, Slot = slot++ });
            }
        }
        // people who did not fit their team join the other one if it has room
        foreach (var p in people)
        {
            if (Roster.Any(r => r.Id == p.Id)) continue;
            var other = p.Team == 'm' ? 'p' : 'm';
            var n = Roster.Count(r => r.Team == other);
            if (n < Field.TeamSize) Roster.Add(new Player { Id = p.Id, Name = p.Name, Team = other, Slot = n });
        }
        var names = new Queue<string>(KidNames.Where(k => !people.Any(p => p.Name == k)).OrderBy(_ => Dice.Next()));
        foreach (var team in new[] { 'm', 'p' })
        {
            var n = Roster.Count(r => r.Team == team);
            var want = FillWithBots ? Field.TeamSize : Math.Max(1, n);
            for (var i = n; i < want; i++)
                Roster.Add(new Player
                {
                    Id = $"bot-{team}{i}", Name = names.Count > 0 ? names.Dequeue() : $"Bot {i}", Team = team, Bot = true, Slot = i,
                    Courage = Dice.Range(1.55, 2.35),
                });
        }
        foreach (var p in Roster) p.LastHeard = Js.Now();
        Score[0] = Score[1] = 0;
        Attack = 'p';
        TimeLeft = Minutes * 60;
        Winner = "";
        Gen++;
        Phase = Phase.Count;
        PhaseTime = 3;
        Reset();
        BroadcastRoster();
        Event("start", "", "");
        PushRoster();
        PushRole(force: true);
        Notify();
    }

    /// <summary>Everyone to their places: attackers home, guards on their lines.</summary>
    private static void Reset()
    {
        var attackers = Roster.Where(p => p.Team == Attack).OrderBy(p => p.Slot).ToList();
        for (var i = 0; i < attackers.Count; i++)
        {
            var a = attackers[i];
            a.X = attackers.Count == 1 ? 0 : -3 + 6.0 * i / (attackers.Count - 1);
            a.Z = -1.8;
            a.Yaw = 0;
            a.Reached = false;
            a.Lunge = 0;
        }
        foreach (var g in Roster.Where(p => p.Team != Attack))
        {
            if (g.Slot < 3) { g.Z = Field.Lines[g.Slot]; g.X = (g.Slot - 1) * 1.6; }
            else { g.X = 0; g.Z = 3; }
            g.Yaw = Math.PI;
            g.Lunge = 0;
            g.Reached = false;
            g.TargetX = g.X;
            g.TargetZ = g.Z;
        }
    }

    // ── the rules (host only) ────────────────────────────────────────────
    private static void Step(double dt)
    {
        PhaseTime -= dt;
        switch (Phase)
        {
            case Phase.Count:
                if (PhaseTime <= 0) { Phase = Phase.Play; Event("go", "", ""); }
                break;
            case Phase.Swap:
                if (PhaseTime <= 0) { Phase = Phase.Play; Event("go", "", ""); }
                break;
            case Phase.End:
                if (PhaseTime <= 0) BackToLobby();
                break;
            case Phase.Play:
                TimeLeft = Math.Max(0, TimeLeft - dt);
                Sort();
                RunBots(dt);
                if (CheckTags()) break;
                CheckTrips();
                if (Phase == Phase.Play && TimeLeft <= 0) Finish();
                break;
        }
        PushRole();
    }

    private static bool CheckTags()
    {
        foreach (var g in GuardBySlot)
        {
            if (g is null) continue;
            var reach = g.Lunge > 0 ? Field.LungeReach : Field.Reach;
            foreach (var a in Attackers)
            {
                if (!Field.OnCourt(a.Z)) continue;
                var dx = a.X - g.X;
                var dz = a.Z - g.Z;
                if (dx * dx + dz * dz > reach * reach) continue;
                // caught: the teams change over
                Attack = Attack == 'm' ? 'p' : 'm';
                Gen++;
                Phase = Phase.Swap;
                PhaseTime = 2.2;
                Reset();
                Event("swap", a.Id, g.Id);
                return true;
            }
        }
        return false;
    }

    private static void CheckTrips()
    {
        foreach (var a in Attackers)
        {
            if (!a.Reached && a.Z >= Field.Goal)
            {
                a.Reached = true;
                Event("reach", a.Id, "");
            }
            else if (a.Reached && a.Z <= Field.Home)
            {
                a.Reached = false;
                Score[a.Team == 'm' ? 0 : 1]++;
                Event("score", a.Id, "");
                if (ScoreOf(a.Team) >= Target)
                {
                    Finish();
                    return;
                }
            }
        }
    }

    private static void Finish()
    {
        Phase = Phase.End;
        PhaseTime = 7;
        Winner = Score[0] > Score[1] ? "m" : Score[1] > Score[0] ? "p" : "draw";
        Gen++;
        Event("end", "", "");
    }

    private static void BackToLobby()
    {
        Phase = Phase.Lobby;
        BroadcastSnapshot();
        Roster = new();
        Gen++;
        _pushedRole = -1;
        PushLobby();
        Notify();
    }

    // ── snapshots ────────────────────────────────────────────────────────
    private static string PhaseCode(Phase p) => p switch { Phase.Count => "count", Phase.Play => "play", Phase.Swap => "swap", Phase.End => "end", _ => "lobby" };

    private static Phase ParsePhase(string s) => s switch { "count" => Phase.Count, "play" => Phase.Play, "swap" => Phase.Swap, "end" => Phase.End, _ => Phase.Lobby };

    private static double R3(double v) => Math.Round(v * 1000) / 1000;

    private static readonly System.Text.StringBuilder Packed = new();

    /// <summary>Every kid as "id,x,z,yaw,lunge,reached" in centimetres and centiradians, separated by ';'.</summary>
    private static string PackEntities()
    {
        var b = Packed.Clear();
        foreach (var p in Roster)
        {
            if (b.Length > 0) b.Append(';');
            b.Append(p.Id).Append(',').Append((int)Math.Round(p.X * 100)).Append(',').Append((int)Math.Round(p.Z * 100))
             .Append(',').Append((int)Math.Round(p.Yaw * 100)).Append(',').Append(p.Lunge > 0 ? 1 : 0).Append(',').Append(p.Reached ? 1 : 0);
        }
        return b.ToString();
    }

    /// <summary>Back from the packed form, when this tab takes the match over.</summary>
    private static void UnpackEntities(string e)
    {
        var i = 0;
        while (i < e.Length)
        {
            var comma = e.IndexOf(',', i);
            if (comma < 0) break;
            var id = e.Substring(i, comma - i);
            i = ReadInts(e, comma + 1, 5);
            if (i < e.Length && e[i] == ';') i++;
            var p = Roster.Find(r => r.Id == id);
            if (p is null) continue;
            p.X = Fields[0] / 100.0;
            p.Z = Fields[1] / 100.0;
            p.Yaw = Fields[2] / 100.0;
            p.Lunge = Fields[3] == 1 ? 0.1 : 0;
            p.Reached = Fields[4] == 1;
        }
    }

    private static string _lastEntities = "";

    /// <summary>Whether one kid's entry ends in ",1" (reached the far side), without unpacking the rest.</summary>
    private static bool ReachedIn(string e, string id)
    {
        var at = e.IndexOf(id + ",", StringComparison.Ordinal);
        if (at < 0) return false;
        var end = e.IndexOf(';', at);
        if (end < 0) end = e.Length;
        return e[end - 1] == '1';
    }

    /// <summary>The host went: carry on from the last snapshot it sent.</summary>
    private static void TakeOver(string previous)
    {
        UnpackEntities(_lastEntities);
        var now = Js.Now();
        foreach (var p in Roster)
        {
            p.Think = 0;
            p.TargetX = p.X;
            p.TargetZ = p.Z;
            // everyone gets a fresh few seconds to be heard, except the referee that stopped
            p.LastHeard = p.Id == previous ? 0 : now;
        }
        BroadcastRoster();
    }

    private static void BroadcastSnapshot()
    {
        var packedE = PackEntities();
        var w = new JsonWriter().BeginObject()
            .Prop("ph", PhaseCode(Phase)).Prop("pt", (int)Math.Round(PhaseTime * 100)).Prop("tl", (int)Math.Round(TimeLeft * 100))
            .Name("sc").BeginArray().Number(Score[0]).Number(Score[1]).EndArray()
            .Prop("atk", Attack.ToString()).Prop("gen", Gen).Prop("win", Winner).Prop("tgt", Target)
            .Prop("e", packedE);
        var json = w.EndObject().ToString();
        Net?.SendJson("s", json);
        if (EngineReady) Js.Send("gs.snap", new JsArgs(json));
    }

    private static void OnSnapshot(string from, JsonValue d)
    {
        if (IsHost && string.CompareOrdinal(from, MeId) > 0) return;   // two hosts for a moment: the smaller id keeps it
        HostId = from;
        _lastSnapAt = Js.Now();
        var phase = ParsePhase(d["ph"].AsString());
        if (phase == Phase.Lobby)
        {
            if (Phase != Phase.Lobby)
            {
                Phase = Phase.Lobby;
                Roster = new();
                _pushedRole = -1;
                PushLobby();
                Notify();
            }
            return;
        }
        var changed = phase != Phase || d["sc"][0].AsInt() != Score[0] || d["sc"][1].AsInt() != Score[1];
        Phase = phase;
        PhaseTime = d["pt"].AsInt() / 100.0;
        TimeLeft = d["tl"].AsInt() / 100.0;
        Score[0] = d["sc"][0].AsInt();
        Score[1] = d["sc"][1].AsInt();
        Attack = d["atk"].AsString() == "m" ? 'm' : 'p';
        Gen = d["gen"].AsInt();
        Winner = d["win"].AsString();
        Target = d["tgt"].AsInt();
        // keep the positions too, unread: if the host goes, this tab carries on from here
        _lastEntities = d["e"].AsString();
        // a new round moves everyone: read the positions now, so this tab's player is put in its new place
        if (Gen != _pushedGen) UnpackEntities(_lastEntities);
        var me = MePlayer;
        if (me is not null) me.Reached = ReachedIn(_lastEntities, me.Id);
        // hand the engine just what it draws from, put together by hand: re-serialising the parsed value costs more
        if (EngineReady)
            Js.Send("gs.snap", new JsArgs(string.Concat(new[] {
                "{\"ph\":\"", d["ph"].AsString(), "\",\"atk\":\"", Attack == 'm' ? "m" : "p", "\",\"win\":\"", Winner,
                "\",\"gen\":", Gen.ToString(), ",\"tl\":", d["tl"].AsInt().ToString(), ",\"e\":\"", _lastEntities, "\"}" })));
        PushRole();
        if (changed) Notify();
    }

    private static void BroadcastRoster()
    {
        _lastRoster = Js.Now();
        var w = new JsonWriter().BeginObject().Prop("min", Minutes).Prop("tgt", Target).Prop("bots", FillWithBots).Name("list").BeginArray();
        foreach (var p in Roster)
            w.BeginObject().Prop("id", p.Id).Prop("n", p.Name).Prop("t", p.Team.ToString()).Prop("b", p.Bot).Prop("s", p.Slot).Prop("c", Math.Round(p.Courage * 100) / 100).EndObject();
        Net?.SendJson("r", w.EndArray().EndObject().ToString());
    }

    private static void OnRoster(string from, JsonValue d)
    {
        if (IsHost && from != MeId && string.CompareOrdinal(from, MeId) > 0) return;
        var list = new List<Player>();
        var same = Roster.Count == d["list"].Count;
        foreach (var r in d["list"].Items)
        {
            var id = r["id"].AsString();
            var old = Roster.Find(p => p.Id == id);
            var p = old ?? new Player { Id = id };
            same &= old is not null && old.Bot == r["b"].AsBool() && old.Name == r["n"].AsString();
            p.Name = r["n"].AsString();
            p.Team = r["t"].AsString() == "m" ? 'm' : 'p';
            p.Bot = r["b"].AsBool();
            p.Slot = r["s"].AsInt();
            p.Courage = r["c"].AsDouble();
            list.Add(p);
        }
        Minutes = d["min"].AsInt();
        Target = d["tgt"].AsInt();
        FillWithBots = d["bots"].AsBool();
        if (same) return;
        Roster = list;
        if (Phase == Phase.Lobby) { Phase = Phase.Count; Gen = -1; }
        PushRoster();
        PushRole(force: true);
        Notify();
    }

    // ── events ───────────────────────────────────────────────────────────
    private static void Event(string kind, string who, string by)
    {
        var data = new Dictionary<string, object?> { ["k"] = kind, ["who"] = who, ["by"] = by, ["sc"] = new[] { Score[0], Score[1] }, ["win"] = Winner };
        Net?.Send("ev", data);
        OnEvent(JsonValue.Parse(Json.Serialize(data)));
    }

    private static void OnEvent(JsonValue d)
    {
        var kind = d["k"].AsString();
        var who = d["who"].AsString();
        var by = d["by"].AsString();
        var until = Js.Now();
        var team = Roster.Find(p => p.Id == who)?.Team ?? ' ';
        Banner? b = kind switch
        {
            "start" => new Banner("start", L.T("Siap-siap!", "Get ready!"), L.T($"{L.Team(Attack)} menyerang dulu", $"{L.Team(Attack)} attack first"), Attack, until + 2600),
            "go" => new Banner("go", L.T("Main!", "Go!"), "", ' ', until + 900),
            "swap" => new Banner("swap", L.T("Ganti!", "Swap!"),
                L.T($"{NameOf(by)} menangkap {NameOf(who)}", $"{NameOf(by)} caught {NameOf(who)}"), Roster.Find(p => p.Id == by)?.Team ?? ' ', until + 2100),
            "score" => new Banner("score", L.T("Pulang!", "Home!"),
                L.T($"{NameOf(who)} bolak-balik · +1 {L.Team(team)}", $"{NameOf(who)} made the round trip · +1 {L.Team(team)}"), team, until + 1800),
            "reach" => who == MeId ? new Banner("reach", L.T("Sampai ujung!", "Far side!"), L.T("Sekarang pulang ke rumah", "Now run back home"), team, until + 1500) : null,
            "end" => new Banner("end", d["win"].AsString() == "draw" ? L.T("Seri!", "A draw!") : L.T($"{L.Team(d["win"].AsString() == "m" ? 'm' : 'p')} menang!", $"{L.Team(d["win"].AsString() == "m" ? 'm' : 'p')} win!"),
                $"{d["sc"][0].AsInt()} – {d["sc"][1].AsInt()}", d["win"].AsString() == "m" ? 'm' : d["win"].AsString() == "p" ? 'p' : ' ', until + 6800),
            "left" => new Banner("left", L.T($"{NameOf(who)} keluar", $"{NameOf(who)} left"), L.T("bot meneruskan", "a bot takes over"), team, until + 2000),
            _ => null,
        };
        if (EngineReady) Js.Send("gs.event", Js.Args("kind", kind, "who", who));
        if (b is not null) Announced?.Invoke(b);
        Notify();
    }

    private static bool _inTick, _dirty;

    /// <summary>Redraws the page, or, inside a tick, once the tick is done (so its cost is measured apart).</summary>
    private static void Notify()
    {
        if (_inTick) _dirty = true;
        else Changed?.Invoke();
    }
}
