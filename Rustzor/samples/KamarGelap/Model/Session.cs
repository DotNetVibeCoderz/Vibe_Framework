namespace KamarGelap.Model;

/// <summary>What the editor page should open when it arrives, chosen on the start page.</summary>
public static class Session
{
    public static Editor Editor { get; } = new();

    /// <summary>"sample", "new", "image", "project", "continue", or null to keep what is open.</summary>
    public static string? Pending { get; set; }
    public static int NewW { get; set; } = 2000;
    public static int NewH { get; set; } = 1500;
    public static string NewBackground { get; set; } = "#ffffff";
    public static int FileHandle { get; set; }
    public static string FileName { get; set; } = "";
}
