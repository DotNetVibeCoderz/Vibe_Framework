using Rustzor.Browser.Storage;
using Rustzor.Interop;

namespace {{Namespace}};

public enum Phase { Focus, ShortBreak, LongBreak }

public sealed class TaskItem
{
    public string Title { get; set; } = "";
    public int Estimate { get; set; } = 1;
    public int Done { get; set; }
    public bool Finished { get; set; }
}

public sealed class Settings
{
    public int FocusMinutes { get; set; } = 25;
    public int ShortMinutes { get; set; } = 5;
    public int LongMinutes { get; set; } = 15;
    public int LongEvery { get; set; } = 4;
    public bool Sound { get; set; } = true;
}

/// <summary>Everything that survives a reload: tasks, settings and today's tally.</summary>
public sealed class Journal
{
    public List<TaskItem> Tasks { get; set; } = new();
    public Settings Settings { get; set; } = new();
    public string Day { get; set; } = "";
    public int FocusBlocks { get; set; }
    public int FocusMinutes { get; set; }

    private const string Key = "pomodoro";

    public static Journal Load()
    {
        var j = WebStorage.Local.GetObject<Journal>(Key) ?? new Journal();
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (j.Day != today)
        {
            j.Day = today;
            j.FocusBlocks = 0;
            j.FocusMinutes = 0;
        }
        if (j.Tasks.Count == 0)
        {
            j.Tasks.Add(new TaskItem { Title = "Draft the release notes", Estimate = 2 });
            j.Tasks.Add(new TaskItem { Title = "Review two pull requests", Estimate = 1 });
            j.Tasks.Add(new TaskItem { Title = "Plan next sprint", Estimate = 3 });
        }
        return j;
    }

    public void Save() => WebStorage.Local.SetJson(Key, this);
}
