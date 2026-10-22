using Rustzor.Browser.Media;
using Rustzor.Browser.Storage;
using Rustzor.Browser.WebRTC;
using Rustzor.Browser.WebSocket;
using Rustzor.Interop;
using Rustzor.UI;

namespace Meet.Data;

/// <summary>Another tab in the call: its connection, what it said about itself, and how loud it is.</summary>
public sealed class Peer
{
    public Peer(string id, PeerConnection pc)
    {
        Id = id;
        Pc = pc;
    }

    public string Id { get; }
    public PeerConnection Pc { get; }
    public PeerState State { get; } = new();
    public RemoteStream? Stream => Pc.Remote;
    public bool Connected { get; set; }
    public double DisconnectedAt { get; set; }
    public int Level { get; set; }
    public bool Recording { get; set; }
}

/// <summary>Everything that travels between tabs: on the BroadcastChannel (presence, signalling) or a data channel (state, chat).</summary>
public sealed class Wire
{
    public string T { get; set; } = "";
    public string Room { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Name { get; set; } = "";
    public string Bureau { get; set; } = "";
    public string Data { get; set; } = "";
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public double At { get; set; }
    public double Since { get; set; }
    public bool Mic { get; set; }
    public bool Cam { get; set; }
    public bool HasCamera { get; set; }
    public bool Sharing { get; set; }
    public bool Recording { get; set; }
    public double Hand { get; set; }
    public int Story { get; set; }
    public double StoryAt { get; set; }
}

/// <summary>
/// The call. Tabs of this browser find each other on a BroadcastChannel: each
/// tab in a room announces itself every 1.5 s, and for every pair the tab
/// with the smaller id calls the other, so nobody offers twice. Offers,
/// answers and ICE candidates go over the same channel; after that, media and
/// a data channel (state, chat, reactions) go peer to peer over WebRTC.
/// </summary>
public static class Call
{
    private const string ChannelName = "kabar-pesisir-redaksi";
    private static BroadcastChannel? _channel;
    private static bool _started;
    private static int _reactionId;
    private static MediaMixer? _mixer;
    private static MediaRecorder? _recorder;
    private static DateTime _recStarted;
    private static double _speakerSince;
    private static double _heardAt;

    /// <summary>This tab, for the whole time it is open.</summary>
    public static string MyId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);

    public static string Name { get; private set; } = "";
    public static string Bureau { get; private set; } = "mks";

    public static Desk? Room { get; private set; }
    public static MediaStream? Local { get; private set; }
    public static MediaStream? Screen { get; private set; }

    /// <summary>What the browser gave us: a camera, a microphone, both or neither.</summary>
    public static bool HasCamera { get; private set; }
    public static bool HasMic { get; private set; }
    public static string MediaProblem { get; private set; } = "";

    public static bool Mic { get; private set; } = true;
    public static bool Cam { get; private set; } = true;
    public static bool Sharing { get; private set; }
    public static double Hand { get; private set; }
    public static double Since { get; private set; }
    public static int Story { get; private set; }
    public static double StoryAt { get; private set; }
    public static int MyLevel { get; private set; }

    /// <summary>The peer id (or <see cref="MyId"/>) of whoever is talking, or "".</summary>
    public static string Speaker { get; private set; } = "";

    public static bool Recording => _recorder is not null;
    public static DateTime RecordingSince => _recStarted;
    public static string EndedBy { get; set; } = "";

    public static Dictionary<string, Peer> Peers { get; } = new();
    public static List<ChatLine> Chat { get; } = new();
    public static List<Reaction> Reactions { get; } = new();
    public static List<SavedRecording> Recordings { get; } = new();

    /// <summary>Other tabs in a room, from their announcements: the lobby shows who is in each.</summary>
    public static Dictionary<string, Presence> Lobby { get; } = new();

    /// <summary>People, chat, state or recording changed.</summary>
    public static event Action? Changed;

    /// <summary>Loudness or the active speaker changed (several times a second while people talk).</summary>
    public static event Action? Levels;

    public static double Now => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        var saved = WebStorage.Session.Get("redaksi-me");
        if (saved is not null && saved.Contains('|'))
        {
            var parts = saved.Split('|');
            Name = parts[0];
            Bureau = parts[1];
        }
        _channel = new BroadcastChannel(ChannelName);
        _channel.OnMessage += Receive;
        Post(new Wire { T = "hello" });
        Scheduler.SetInterval(Heartbeat, 1500);
        Scheduler.SetInterval(MeasureLevels, 200);
        JsObject.OnWindow("pagehide", _ => Post(new Wire { T = "bye", Room = Room?.Id ?? "" }));
    }

    public static void SetIdentity(string name, string bureau)
    {
        Name = name.Trim();
        Bureau = bureau;
        WebStorage.Session.Set("redaksi-me", Name + "|" + Bureau);
    }

    // ── devices ──────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the camera and microphone. Without a camera it falls back to the
    /// microphone alone, and without either the call still works: the tile shows initials.
    /// </summary>
    public static async Task PrepareMediaAsync(string? cameraId = null, string? micId = null)
    {
        var old = Local;
        Local = null;
        MediaProblem = "";
        try
        {
            Local = await MediaDevices.GetUserMediaAsync(video: true, audio: true, width: 1280, height: 720, videoDeviceId: Blank(cameraId), audioDeviceId: Blank(micId));
        }
        catch (Exception)
        {
            try
            {
                Local = await MediaDevices.GetUserMediaAsync(video: false, audio: true, audioDeviceId: Blank(micId));
                MediaProblem = "No camera here, so the others see your initials. Your microphone works.";
            }
            catch (Exception)
            {
                MediaProblem = "This browser gave no camera or microphone. You can still join, watch, listen and chat.";
            }
        }
        old?.Dispose();
        HasCamera = Local?.HasVideo ?? false;
        HasMic = Local?.HasAudio ?? false;
        Local?.SetAudioEnabled(Mic);
        Local?.SetVideoEnabled(Cam);
        foreach (var p in Peers.Values)
        {
            _ = p.Pc.ReplaceTrackAsync("audio", Local?.Handle ?? 0);
            if (!Sharing) _ = p.Pc.ReplaceTrackAsync("video", Local?.Handle ?? 0);
        }
        Changed?.Invoke();
    }

    private static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public static void ReleaseMedia()
    {
        Local?.Dispose();
        Local = null;
        HasCamera = HasMic = false;
    }

    // ── joining and leaving ──────────────────────────────────────────────

    public static void Join(Desk desk)
    {
        Room = desk;
        Since = Now;
        Story = 0;
        StoryAt = 0;
        Hand = 0;
        EndedBy = "";
        Chat.Clear();
        Speaker = "";
        AnnouncePresence();
        Post(new Wire { T = "hello", Room = desk.Id });
        Changed?.Invoke();
    }

    public static async Task LeaveAsync(bool endForEveryone = false)
    {
        if (Room is null) return;
        if (endForEveryone) SendAll(new Wire { T = "end", Name = Name });
        if (Recording) await StopRecordingAsync();
        Post(new Wire { T = "bye", Room = Room.Id });
        foreach (var p in Peers.Values) p.Pc.Dispose();
        Peers.Clear();
        StopShare(notify: false);
        Reactions.Clear();
        Room = null;
        Hand = 0;
        Speaker = "";
        ReleaseMedia();
        Changed?.Invoke();
    }

    // ── controls ─────────────────────────────────────────────────────────

    public static void ToggleMic()
    {
        Mic = !Mic;
        Local?.SetAudioEnabled(Mic);
        SendStateToAll();
    }

    public static void ToggleCam()
    {
        Cam = !Cam;
        Local?.SetVideoEnabled(Cam);
        SendStateToAll();
    }

    /// <summary>Before joining: the switches on the pre-join screen.</summary>
    public static void SetStartState(bool mic, bool cam)
    {
        Mic = mic;
        Cam = cam;
        Local?.SetAudioEnabled(Mic);
        Local?.SetVideoEnabled(Cam);
        Changed?.Invoke();
    }

    public static void ToggleHand()
    {
        Hand = Hand > 0 ? 0 : Now;
        SendStateToAll();
    }

    public static void LowerHandOf(string peerId)
    {
        if (peerId == MyId) { Hand = 0; SendStateToAll(); return; }
        if (Peers.TryGetValue(peerId, out var p)) p.Pc.SendData(Json.Serialize(new Wire { T = "lower" }));
    }

    public static async Task ShareScreenAsync()
    {
        if (Sharing) { StopShare(); return; }
        try
        {
            Screen = await MediaDevices.GetDisplayMediaAsync();
        }
        catch (Exception)
        {
            Toasts.Show("Screen sharing was cancelled.", "info");
            return;
        }
        Screen.OnEnded(() => StopShare());
        Sharing = true;
        foreach (var p in Peers.Values) _ = p.Pc.ReplaceTrackAsync("video", Screen.Handle);
        SendStateToAll();
    }

    public static void StopShare(bool notify = true)
    {
        if (!Sharing && Screen is null) return;
        Sharing = false;
        foreach (var p in Peers.Values) _ = p.Pc.ReplaceTrackAsync("video", Local?.Handle ?? 0);
        Screen?.Dispose();
        Screen = null;
        if (notify) SendStateToAll();
    }

    public static void React(string emoji)
    {
        AddReaction(MyId, emoji);
        SendAll(new Wire { T = "react", Text = emoji });
        Changed?.Invoke();
    }

    public static void SendChat(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        var line = new ChatLine { Id = MyId + "-" + Chat.Count + "-" + (long)Now, From = MyId, Name = Name, Bureau = Bureau, Text = text, At = Now };
        Chat.Add(line);
        SendAll(new Wire { T = "chat", Id = line.Id, Text = text, At = line.At, Name = Name, Bureau = Bureau });
        Changed?.Invoke();
    }

    /// <summary>Moves the whole call to a story on the rundown.</summary>
    public static void GoToStory(int index)
    {
        if (Room is null) return;
        Story = Math.Clamp(index, 0, Room.Rundown.Length - 1);
        StoryAt = Now;
        SendAll(new Wire { T = "story", Story = Story, StoryAt = StoryAt });
        Changed?.Invoke();
    }

    // ── recording ────────────────────────────────────────────────────────

    public static void StartRecording()
    {
        if (Recording || Room is null) return;
        _mixer = new MediaMixer(1280, 720, 24);
        UpdateMix();
        _recorder = new MediaRecorder(_mixer.Output.Handle);
        _recorder.Start();
        _recStarted = DateTime.Now;
        SendStateToAll();
    }

    public static async Task StopRecordingAsync()
    {
        if (_recorder is null || Room is null) return;
        var rec = _recorder;
        _recorder = null;
        var done = await rec.FinishAsync();
        _mixer?.Dispose();
        _mixer = null;
        Recordings.Insert(0, new SavedRecording(done.Url, done.Bytes, Room.Id, _recStarted, DateTime.Now - _recStarted, Peers.Count + 1));
        SendStateToAll();
        Toasts.Show("Recording saved. Find it under Recordings.");
    }

    private static void UpdateMix()
    {
        if (_mixer is null) return;
        var sources = new List<MixSource>
        {
            new(Sharing && Screen is not null ? Screen.Handle : Local?.Handle ?? 0, Name + " · " + Newsroom.BureauOf(Bureau).City, Newsroom.Initials(Name), Sharing || (Cam && HasCamera)),
        };
        foreach (var p in Peers.Values)
            sources.Add(new(p.Stream?.Handle ?? 0, p.State.Name + " · " + Newsroom.BureauOf(p.State.Bureau).City, Newsroom.Initials(p.State.Name), p.State.Sharing || (p.State.Cam && p.State.HasCamera)));
        _mixer.SetSources(sources, "#0c1115", "#e8edf1");
    }

    // ── reading the call ─────────────────────────────────────────────────

    /// <summary>When the call started: the earliest join among the people in it.</summary>
    public static double CallSince => Peers.Values.Select(p => p.State.Since).Where(s => s > 0).Append(Since).Min();

    public static IEnumerable<Presence> InRoom(string room) => Lobby.Values.Where(p => p.Room == room).OrderBy(p => p.Since);

    public static Peer? Sharer => Peers.Values.FirstOrDefault(p => p.State.Sharing);

    public static IEnumerable<Reaction> ReactionsOf(string who) => Reactions.Where(r => r.Who == who);

    // ── the BroadcastChannel ─────────────────────────────────────────────

    private static void Post(Wire w)
    {
        w.From = MyId;
        _channel?.Post(Json.Serialize(w));
    }

    private static void AnnouncePresence()
    {
        if (Room is null) return;
        Post(new Wire { T = "presence", Room = Room.Id, Name = Name, Bureau = Bureau, Since = Since });
    }

    private static void Heartbeat()
    {
        AnnouncePresence();
        var now = Now;
        var changed = false;
        foreach (var stale in Lobby.Values.Where(p => now - p.Seen > 5000).Select(p => p.Id).ToList())
        {
            Lobby.Remove(stale);
            changed = true;
        }
        foreach (var gone in Peers.Values.Where(p => p.DisconnectedAt > 0 && now - p.DisconnectedAt > 5000).Select(p => p.Id).ToList())
        {
            RemovePeer(gone);
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    private static void Receive(string text)
    {
        Wire w;
        try { w = Json.Deserialize<Wire>(text); }
        catch (Exception) { return; }
        if (w.From == MyId) return;
        switch (w.T)
        {
            case "hello":
                AnnouncePresence();
                break;
            case "presence":
                var known = Lobby.ContainsKey(w.From);
                Lobby[w.From] = new Presence { Id = w.From, Name = w.Name, Bureau = w.Bureau, Room = w.Room, Since = w.Since, Seen = Now };
                if (Room is not null && w.Room == Room.Id && !Peers.ContainsKey(w.From) && string.CompareOrdinal(MyId, w.From) < 0)
                    CreatePeer(w.From, w.Name, w.Bureau, caller: true);
                if (!known) Changed?.Invoke();
                break;
            case "signal":
                if (w.To != MyId || Room is null || w.Room != Room.Id) return;
                if (!Peers.TryGetValue(w.From, out var peer)) peer = CreatePeer(w.From, w.Name, w.Bureau, caller: false);
                _ = Deliver(peer, w.Data);
                break;
            case "bye":
                Lobby.Remove(w.From);
                RemovePeer(w.From);
                Changed?.Invoke();
                break;
        }
    }

    private static async Task Deliver(Peer peer, string data)
    {
        try { await peer.Pc.ReceiveSignalAsync(data); }
        catch (Exception e) { Console.WriteLine("signal from " + peer.State.Name + " failed: " + e.Message); }
    }

    private static Peer CreatePeer(string id, string name, string bureau, bool caller)
    {
        var pc = new PeerConnection();
        var peer = new Peer(id, pc);
        peer.State.Name = name;
        peer.State.Bureau = bureau;
        Peers[id] = peer;
        var room = Room!.Id;
        pc.OnSignal += data => Post(new Wire { T = "signal", Room = room, To = id, Name = Name, Bureau = Bureau, Data = data });
        pc.OnRemoteStream += _ => { UpdateMix(); Changed?.Invoke(); };
        pc.OnDataOpen += () => SendState(peer);
        pc.OnData += text => OnPeerData(peer, text);
        pc.OnStateChange += state =>
        {
            if (state == "connected") { peer.Connected = true; peer.DisconnectedAt = 0; }
            else if (state == "disconnected") peer.DisconnectedAt = Now;
            else if (state is "failed" or "closed") RemovePeer(id);
            Changed?.Invoke();
        };
        if (Local is not null) pc.AddStream(Local.Handle);
        if (Sharing && Screen is not null) _ = pc.ReplaceTrackAsync("video", Screen.Handle);
        if (caller) _ = pc.CallAsync();
        Changed?.Invoke();
        return peer;
    }

    private static void RemovePeer(string id)
    {
        if (!Peers.TryGetValue(id, out var p)) return;
        Peers.Remove(id);
        p.Pc.Dispose();
        if (Speaker == id) Speaker = "";
        UpdateMix();
    }

    // ── the data channels ────────────────────────────────────────────────

    private static Wire StateWire() => new()
    {
        T = "state", Name = Name, Bureau = Bureau, Mic = Mic, Cam = Cam, HasCamera = HasCamera, Sharing = Sharing,
        Hand = Hand, Since = Since, Story = Story, StoryAt = StoryAt, Recording = Recording,
    };

    private static void SendState(Peer peer) => peer.Pc.SendData(Json.Serialize(StateWire()));

    private static void SendStateToAll()
    {
        SendAll(StateWire());
        UpdateMix();
        Changed?.Invoke();
    }

    private static void SendAll(Wire w)
    {
        w.From = MyId;
        var text = Json.Serialize(w);
        foreach (var p in Peers.Values) p.Pc.SendData(text);
    }

    private static void OnPeerData(Peer peer, string text)
    {
        Wire w;
        try { w = Json.Deserialize<Wire>(text); }
        catch (Exception) { return; }
        switch (w.T)
        {
            case "state":
                var s = peer.State;
                s.Name = w.Name;
                s.Bureau = w.Bureau;
                s.Mic = w.Mic;
                s.Cam = w.Cam;
                s.HasCamera = w.HasCamera;
                s.Sharing = w.Sharing;
                s.Hand = w.Hand;
                s.Since = w.Since;
                peer.Recording = w.Recording;
                if (w.StoryAt > StoryAt) { Story = w.Story; StoryAt = w.StoryAt; }
                UpdateMix();
                break;
            case "chat":
                if (Chat.Any(c => c.Id == w.Id)) return;
                Chat.Add(new ChatLine { Id = w.Id, From = peer.Id, Name = w.Name, Bureau = w.Bureau, Text = w.Text, At = w.At });
                break;
            case "react":
                AddReaction(peer.Id, w.Text);
                break;
            case "story":
                if (w.StoryAt > StoryAt) { Story = w.Story; StoryAt = w.StoryAt; }
                break;
            case "lower":
                Hand = 0;
                SendStateToAll();
                Toasts.Show("Your hand was lowered.", "info");
                return;
            case "end":
                EndedBy = w.Name;
                _ = LeaveAsync();
                return;
        }
        Changed?.Invoke();
    }

    private static void AddReaction(string who, string emoji) => Reactions.Add(new Reaction(++_reactionId, who, emoji, Now + 2600));

    // ── who is talking ───────────────────────────────────────────────────

    private static void MeasureLevels()
    {
        if (Room is null) return;
        var now = Now;
        var changed = Reactions.RemoveAll(r => r.Until < now) > 0;

        var mine = Mic && HasMic ? Step(Local?.Level() ?? 0) : 0;
        if (mine != MyLevel) { MyLevel = mine; changed = true; }
        foreach (var p in Peers.Values)
        {
            var level = p.State.Mic ? Step(p.Stream?.Level() ?? 0) : 0;
            if (level != p.Level) { p.Level = level; changed = true; }
        }

        // The loudest voice above a floor takes the tally light; it holds it
        // for at least a second so the light does not flicker between people.
        var loudest = Peers.Values.Select(p => (p.Id, p.Level)).Append((MyId, MyLevel)).OrderByDescending(x => x.Item2).First();
        var current = Speaker == MyId ? MyLevel : Peers.TryGetValue(Speaker, out var sp) ? sp.Level : 0;
        if (current >= 2) _heardAt = now;
        if (loudest.Item2 >= 2 && loudest.Item1 != Speaker && (current < 2 || (now - _speakerSince > 1000 && loudest.Item2 > current + 1)))
        {
            Speaker = loudest.Item1;
            _speakerSince = _heardAt = now;
            changed = true;
        }
        else if (Speaker.Length > 0 && now - _heardAt > 2000)
        {
            Speaker = "";
            changed = true;
        }

        if (changed) Levels?.Invoke();
    }

    /// <summary>Loudness 0..1 as one of nine meter steps.</summary>
    private static int Step(double level) => (int)Math.Round(Math.Clamp(level, 0, 1) * 8);
}
