namespace RustzorAppGen.Services;

public enum LogKind { Info, Success, Warning, Error, Ai, Command }

public sealed record BuildProblem(string File, int Line, int Column, string Severity, string Code, string Message)
{
    public override string ToString() => $"{Path.GetFileName(File)}({Line},{Column}): {Severity} {Code}: {Message}";
}

public sealed record BuildResult(bool Succeeded, IReadOnlyList<BuildProblem> Problems, string Output)
{
    public string Summary(int max = 30)
    {
        if (Succeeded && Problems.Count == 0) return "Build succeeded with no warnings.";
        var errors = Problems.Where(p => p.Severity == "error").ToList();
        var warnings = Problems.Count - errors.Count;
        var head = Succeeded ? $"Build succeeded with {warnings} warning(s)." : $"Build failed: {errors.Count} error(s), {warnings} warning(s).";
        var lines = Problems.OrderBy(p => p.Severity == "error" ? 0 : 1).Take(max).Select(p => $"- {p}");
        var text = head + "\n" + string.Join("\n", lines);
        if (!Succeeded && errors.Count == 0)
        {
            // MSBuild failed without compiler errors: the tail of the log says why.
            var tail = Output.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).TakeLast(10);
            text += "\n" + string.Join("\n", tail);
        }
        return text;
    }
}

/// <summary>
/// What the assistant's plugins can do to the editor around them. The main
/// window implements it, marshalling onto the UI thread.
/// </summary>
public interface IWorkbench
{
    string? ProjectRoot { get; }
    void Log(string text, LogKind kind = LogKind.Info);
    /// <summary>A file was created or changed on disk; refresh the explorer and any open editor.</summary>
    void FileChanged(string path);
    void OpenFile(string path, int line = 0);
    Task OpenProjectAsync(string folder);
    Task<BuildResult> BuildAsync(CancellationToken cancel = default);
    /// <summary>Builds, serves and opens the app; returns its URL.</summary>
    Task<string> RunAsync(CancellationToken cancel = default);
}
