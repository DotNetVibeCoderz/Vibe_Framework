namespace Chat.Data;

/// <summary>
/// Three simulated teammates, so the studio feels lived in with one tab open.
/// They answer a direct message or an @mention after "typing" for a moment,
/// in the thread the question was asked in. Replies come from a small table
/// of what each of them cares about; nothing leaves the browser.
/// </summary>
public static class Bots
{
    private static readonly Dictionary<string, (string[] Keys, string Reply)[]> Topics = new()
    {
        ["dewi"] = new[]
        {
            (new[] { "warna", "color", "colour", "palette" }, "Stick to the tokens in the design system: accent for actions only, and check contrast in dark mode before you ship it."),
            (new[] { "font", "type", "typography" }, "Plus Jakarta Sans for UI, 15 px body. If a heading needs more presence, go up the scale, don't go bold."),
            (new[] { "review", "feedback", "critique", "cek" }, "Send me the link and I'll leave comments before 15:00. Screens in context, please, not single components."),
            (new[] { "icon", "ikon", "logo" }, "Icons are 1.8 px stroke on a 24 grid. The new logo files are pinned in #desain."),
        },
        ["raka"] = new[]
        {
            (new[] { "bug", "error", "crash", "500" }, "Can you paste the request id from the response header? I'll pull the logs."),
            (new[] { "deploy", "rilis", "release", "ship" }, "Staging is green. The production window is Thursday 10:00, and I'll post in #rilis when it's out."),
            (new[] { "api", "endpoint" }, "The endpoint is documented in the API reference; pagination is cursor-based, 50 per page."),
            (new[] { "database", "db", "migration" }, "The migration ran on staging last night without locks. Production gets it with Thursday's deploy."),
        },
        ["sari"] = new[]
        {
            (new[] { "deadline", "kapan", "when", "tanggal" }, "We're aiming for Thursday. If anything is at risk, flag it in #rilis today so we can cut scope, not quality."),
            (new[] { "scope", "fitur", "feature" }, "Let's keep 2.4 to the checkout flow. Everything else goes on the board for 2.5."),
            (new[] { "meeting", "rapat", "sync", "call" }, "Booked 15 minutes at 14:00; the link is in the calendar invite."),
            (new[] { "customer", "pelanggan", "user" }, "Three customers asked for this last week. I'll share the interview notes in the thread."),
        },
    };

    private static readonly Dictionary<string, string[]> Fallbacks = new()
    {
        ["dewi"] = new[] { "Looks promising. Can you show it next to the screen before and after?", "Noted, I'll take a look this afternoon.", "Good question. Let me check with the team and come back to you." },
        ["raka"] = new[] { "On it. I'll update here when I know more.", "Works on my machine, which means nothing. Checking staging.", "Give me ten minutes." },
        ["sari"] = new[] { "Thanks! Adding it to the board.", "Makes sense. Who owns it?", "Let's discuss at stand-up tomorrow." },
    };

    private static int _turn;

    /// <summary>Called for every message this tab sends; replies if a teammate was addressed.</summary>
    public static void Consider(Message m)
    {
        var room = ChatStore.RoomOf(m.Room);
        var bots = ChatStore.People.Where(p => p.Bot)
            .Where(p => (room?.Kind == "dm" && room.With == p.Id) || m.Text.Contains("@" + p.First, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var delay = 0;
        foreach (var bot in bots)
        {
            Reply(bot, m, 700 + delay);
            delay += 1800;
        }

        var lower = m.Text.ToLowerInvariant();
        if (lower.Contains("terima kasih") || lower.Contains("thanks") || lower.Contains("makasih"))
            React(m, "🙏", "sari", 1200);
        if (lower.Contains("shipped") || lower.Contains("sudah rilis") || lower.Contains("released"))
            React(m, "🎉", "raka", 900);
    }

    private static async void Reply(Person bot, Message to, int wait)
    {
        await Task.Delay(wait);
        var text = Answer(bot, to.Text);
        ChatStore.ShowTyping(to.Room, bot.Id, 8);
        await Task.Delay(Math.Clamp(text.Length * 18, 1200, 2800));
        var parent = to.Parent.Length > 0 ? to.Parent : ChatStore.RoomOf(to.Room)?.Kind == "dm" ? "" : to.Id;
        await ChatStore.SendAsync(to.Room, text, parent, author: bot);
    }

    private static async void React(Message m, string emoji, string who, int wait)
    {
        await Task.Delay(wait);
        if (m.Reactions.TryGetValue(emoji, out var list) && list.Contains(who)) return;
        await ChatStore.ReactAsync(m, emoji, who);
    }

    public static string Answer(Person bot, string question)
    {
        var lower = question.ToLowerInvariant();
        foreach (var (keys, reply) in Topics[bot.Id])
            if (keys.Any(lower.Contains)) return reply;
        var name = ChatStore.Me?.First;
        var pool = Fallbacks[bot.Id];
        var line = pool[_turn++ % pool.Length];
        return name is null ? line : $"@{name} {line}";
    }
}
