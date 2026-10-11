using System.ComponentModel;
using System.Text;
using Microsoft.SemanticKernel;
using RustzorAppGen.Services;

namespace RustzorAppGen.Plugins;

/// <summary>Reading and writing the files of the open project. Paths are relative to the project folder.</summary>
public sealed class ProjectPlugin(IWorkbench workbench)
{
    private static readonly string[] Skipped = { "bin", "obj", ".vs", ".git", "node_modules" };

    private string Root => workbench.ProjectRoot ?? throw new InvalidOperationException(
        "No project is open. Create one with rustzor.create_project first, or ask the user to open a folder.");

    /// <summary>Resolves a project-relative path and refuses anything outside the project.</summary>
    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative.Replace('\\', '/').TrimStart('/')));
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(full + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{relative}' is outside the project folder.");
        return full;
    }

    private string Relative(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');

    [KernelFunction("get_project_overview")]
    [Description("Returns the open project's folder tree (without bin/obj) and its .csproj, Program.cs and _Imports.razor. Call this first to learn the project before changing it.")]
    public string GetProjectOverview()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Project folder: {Path.GetFileName(Root)}");
        sb.AppendLine("Files:");
        foreach (var f in Files(Root).Take(400)) sb.AppendLine("  " + Relative(f));
        foreach (var name in new[] { "*.csproj", "Program.cs", "_Imports.razor" })
        {
            foreach (var f in Directory.EnumerateFiles(Root, name, SearchOption.TopDirectoryOnly))
            {
                sb.AppendLine($"\n--- {Relative(f)} ---");
                sb.AppendLine(File.ReadAllText(f));
            }
        }
        return sb.ToString();
    }

    [KernelFunction("list_files")]
    [Description("Lists files under a folder of the project, recursively.")]
    public string ListFiles([Description("Folder relative to the project, empty for all")] string folder = "")
    {
        var dir = Resolve(folder);
        if (!Directory.Exists(dir)) return $"No folder '{folder}'.";
        var files = Files(dir).Select(Relative).ToList();
        return files.Count == 0 ? "(empty)" : string.Join("\n", files);
    }

    [KernelFunction("read_file")]
    [Description("Reads a project file. Optionally a line range (1-based, inclusive).")]
    public string ReadFile(
        [Description("Path relative to the project, e.g. Pages/Home.razor")] string path,
        [Description("First line to return, 0 for the start")] int startLine = 0,
        [Description("Last line to return, 0 for the end")] int endLine = 0)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) return $"No file '{path}'. Use list_files to see what exists.";
        var lines = File.ReadAllLines(full);
        var from = Math.Max(1, startLine);
        var to = endLine <= 0 ? lines.Length : Math.Min(lines.Length, endLine);
        var text = string.Join("\n", lines.Skip(from - 1).Take(to - from + 1));
        return text.Length > 120_000 ? text[..120_000] + "\n… (truncated; read a line range)" : text;
    }

    [KernelFunction("write_file")]
    [Description("Creates or overwrites a project file with the complete content given. Folders are created as needed.")]
    public string WriteFile(
        [Description("Path relative to the project")] string path,
        [Description("The full new content of the file")] string content)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var existed = File.Exists(full);
        File.WriteAllText(full, content.Replace("\r\n", "\n"));
        workbench.FileChanged(full);
        workbench.Log($"{(existed ? "Updated" : "Created")} {Relative(full)}", LogKind.Ai);
        return $"{(existed ? "Updated" : "Created")} {Relative(full)} ({content.Split('\n').Length} lines).";
    }

    [KernelFunction("edit_file")]
    [Description("Replaces one exact, unique piece of text in a project file. Prefer this to write_file for small changes to large files.")]
    public string EditFile(
        [Description("Path relative to the project")] string path,
        [Description("Text to find; must occur exactly once")] string oldText,
        [Description("Replacement text")] string newText)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) return $"No file '{path}'.";
        var text = File.ReadAllText(full).Replace("\r\n", "\n");
        var find = oldText.Replace("\r\n", "\n");
        var first = text.IndexOf(find, StringComparison.Ordinal);
        if (first < 0) return "The text to replace was not found. Read the file again and copy the text exactly.";
        if (text.IndexOf(find, first + 1, StringComparison.Ordinal) >= 0) return "The text occurs more than once; include more surrounding lines so it is unique.";
        File.WriteAllText(full, text[..first] + newText.Replace("\r\n", "\n") + text[(first + find.Length)..]);
        workbench.FileChanged(full);
        workbench.Log($"Edited {Relative(full)}", LogKind.Ai);
        return $"Edited {Relative(full)}.";
    }

    [KernelFunction("delete_path")]
    [Description("Deletes a file or folder of the project.")]
    public string DeletePath([Description("Path relative to the project")] string path)
    {
        var full = Resolve(path);
        if (full.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return "Refusing to delete the project folder itself.";
        if (File.Exists(full)) File.Delete(full);
        else if (Directory.Exists(full)) Directory.Delete(full, true);
        else return $"Nothing at '{path}'.";
        workbench.FileChanged(full);
        workbench.Log($"Deleted {Relative(full)}", LogKind.Ai);
        return $"Deleted {path}.";
    }

    [KernelFunction("search_text")]
    [Description("Searches the project's text files for a string and returns matching lines as path:line: text.")]
    public string SearchText(
        [Description("Text to look for (case-insensitive)")] string query,
        [Description("File name pattern, e.g. *.razor")] string pattern = "*")
    {
        var hits = new List<string>();
        foreach (var f in Files(Root).Where(f => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(f))))
        {
            if (new FileInfo(f).Length > 2_000_000) continue;
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length && hits.Count < 150; i++)
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) hits.Add($"{Relative(f)}:{i + 1}: {lines[i].Trim()}");
        }
        return hits.Count == 0 ? "No matches." : string.Join("\n", hits);
    }

    [KernelFunction("open_in_editor")]
    [Description("Opens a project file in the editor for the user, optionally at a line.")]
    public string OpenInEditor([Description("Path relative to the project")] string path, int line = 0)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) return $"No file '{path}'.";
        workbench.OpenFile(full, line);
        return $"Opened {path}.";
    }

    private static IEnumerable<string> Files(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) yield return f;
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (Skipped.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var f in Files(d)) yield return f;
        }
    }
}
