namespace Meet.Data;

/// <summary>A newsroom bureau of the Kabar Pesisir cooperative, with the colour of its strap tab.</summary>
public sealed record Bureau(string Id, string City, string Color);

/// <summary>One story on a meeting's rundown: its slug, who files it, and the time it gets.</summary>
public sealed record RundownItem(string Slug, string Desk, int Seconds, string Note);

/// <summary>A standing call room: the morning meeting, a bureau desk, a special-coverage desk.</summary>
public sealed record Desk(string Id, string Name, string Kind, string Schedule, string Purpose, RundownItem[] Rundown);

/// <summary>A message in the call's chat, sent over each peer's data channel.</summary>
public sealed class ChatLine
{
    public string Id { get; set; } = "";
    public string From { get; set; } = "";
    public string Name { get; set; } = "";
    public string Bureau { get; set; } = "";
    public string Text { get; set; } = "";
    public double At { get; set; }
}

/// <summary>An emoji floating up over someone's tile for a moment.</summary>
public sealed record Reaction(int Id, string Who, string Emoji, double Until);

/// <summary>A call recorded in this tab: a blob URL that lives until the tab closes.</summary>
public sealed record SavedRecording(string Url, long Bytes, string Desk, DateTime Started, TimeSpan Length, int People)
{
    public string FileName => $"kabar-pesisir-{Desk}-{Started:yyyyMMdd-HHmm}.webm";
}

/// <summary>Someone in another tab, seen on the lobby's presence channel.</summary>
public sealed class Presence
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Bureau { get; set; } = "";
    public string Room { get; set; } = "";
    public double Since { get; set; }
    public double Seen { get; set; }
}

/// <summary>What a peer tells the others about itself over the data channel.</summary>
public sealed class PeerState
{
    public string Name { get; set; } = "";
    public string Bureau { get; set; } = "";
    public bool Mic { get; set; } = true;
    public bool Cam { get; set; } = true;
    public bool HasCamera { get; set; } = true;
    public bool Sharing { get; set; }
    public double Hand { get; set; }
    public double Since { get; set; }
    public int Story { get; set; }
    public double StoryAt { get; set; }
}
