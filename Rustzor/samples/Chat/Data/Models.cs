namespace Chat.Data;

public sealed class Person
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Color { get; set; } = "#6b3fa0";
    /// <summary>A simulated teammate who answers when mentioned or messaged.</summary>
    public bool Bot { get; set; }
    public string First => Name.Split(' ')[0];
}

public sealed class Room
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Topic { get; set; } = "";
    /// <summary>"channel" or "dm".</summary>
    public string Kind { get; set; } = "channel";
    /// <summary>For a direct message: the other person's id.</summary>
    public string With { get; set; } = "";
}

public sealed class Message
{
    public string Id { get; set; } = "";
    public string Room { get; set; } = "";
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Local time, "yyyy-MM-ddTHH:mm:ss".</summary>
    public string Time { get; set; } = "";
    /// <summary>The message this one replies to in a thread, or empty.</summary>
    public string Parent { get; set; } = "";
    /// <summary>Emoji to the ids of the people who reacted with it.</summary>
    public Dictionary<string, List<string>> Reactions { get; set; } = new();
    /// <summary>An attached image as a data URL, or empty.</summary>
    public string Image { get; set; } = "";
    public bool Edited { get; set; }
    public bool Deleted { get; set; }
    public bool Pinned { get; set; }

    public DateTime At => DateTime.Parse(Time);
}

/// <summary>Who has read a room up to when, kept per person on this device.</summary>
public sealed class ReadMark
{
    public string Id { get; set; } = "";
    public string Time { get; set; } = "";
}
