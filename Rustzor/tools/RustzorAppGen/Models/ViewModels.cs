using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using RustzorAppGen.Services;

namespace RustzorAppGen.Models;

/// <summary>A file or folder in the explorer.</summary>
public sealed partial class FileNode : ObservableObject
{
    public FileNode(string fullPath, bool isDirectory)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
    }

    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string Name => Path.GetFileName(FullPath.TrimEnd(Path.DirectorySeparatorChar));
    public ObservableCollection<FileNode> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;

    /// <summary>A short type tag shown before the name, coloured by kind.</summary>
    public string Tag => IsDirectory ? "" : Path.GetExtension(FullPath).ToLowerInvariant() switch
    {
        ".razor" => "RZ", ".cs" => "C#", ".css" => "CSS", ".js" or ".mjs" => "JS", ".html" or ".htm" => "HTM",
        ".json" => "{}", ".csproj" => "PRJ", ".md" => "MD", ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".ico" => "IMG",
        ".rs" => "RS", ".toml" => "TML", ".xml" or ".config" => "XML", _ => "··",
    };

    public IBrush TagBrush => Tag switch
    {
        "RZ" => new SolidColorBrush(Color.Parse("#E8692A")),
        "C#" => new SolidColorBrush(Color.Parse("#6FA8DC")),
        "CSS" => new SolidColorBrush(Color.Parse("#3FB8A5")),
        "JS" => new SolidColorBrush(Color.Parse("#C9A15A")),
        "HTM" => new SolidColorBrush(Color.Parse("#E5484D")),
        "PRJ" => new SolidColorBrush(Color.Parse("#A78BFA")),
        _ => new SolidColorBrush(Color.Parse("#8B9199")),
    };

    public static FileNode Load(string path, int depth = 0)
    {
        var node = new FileNode(path, true) { IsExpanded = depth == 0 };
        if (depth > 12) return node;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name is "bin" or "obj" or ".vs" or ".git" or "node_modules" or "publish" || name.StartsWith('.')) continue;
                node.Children.Add(Load(dir, depth + 1));
            }
            foreach (var file in Directory.EnumerateFiles(path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                node.Children.Add(new FileNode(file, false));
        }
        catch (UnauthorizedAccessException)
        {
        }
        return node;
    }

    public IEnumerable<FileNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants()) yield return d;
    }
}

/// <summary>A file open in the editor.</summary>
public sealed partial class OpenDocument : ObservableObject
{
    public OpenDocument(string fullPath, string text)
    {
        FullPath = fullPath;
        Document = new TextDocument(text);
        Document.TextChanged += (_, _) => IsDirty = true;
    }

    public string FullPath { get; }
    public TextDocument Document { get; }
    public string Title => Path.GetFileName(FullPath);
    public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isActive;

    public string Language => Extension switch
    {
        ".razor" => "Razor", ".cs" => "C#", ".css" => "CSS", ".js" or ".mjs" => "JavaScript", ".html" or ".htm" => "HTML",
        ".json" => "JSON", ".csproj" or ".xml" or ".config" or ".props" or ".targets" => "XML", ".md" => "Markdown",
        ".rs" => "Rust", ".toml" => "TOML", ".ts" => "TypeScript", _ => "Plain text",
    };
}

/// <summary>A tool call Jack made, shown as a chip under his message.</summary>
public sealed partial class ToolStrike : ObservableObject
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    public string Arguments { get; init; } = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private bool _done;
    public string Display => string.IsNullOrEmpty(Arguments) ? Name : $"{Name}({Arguments})";
}

/// <summary>A run of a message: prose, or a fenced code block.</summary>
public sealed record ChatSegment(bool IsCode, string Text, string Language)
{
    public bool IsText => !IsCode;
}

/// <summary>One message in the chat panel.</summary>
public sealed partial class ChatItem : ObservableObject
{
    public required string Role { get; init; }   // "user", "jack", "error"
    public DateTime Time { get; } = DateTime.Now;
    public ObservableCollection<ToolStrike> Strikes { get; } = new();
    public ObservableCollection<ChatSegment> Segments { get; } = new();
    public List<Bitmap> Images { get; init; } = new();

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming;

    public bool IsUser => Role == "user";
    public bool IsJack => Role == "jack";
    public bool IsError => Role == "error";
    public bool HasImages => Images.Count > 0;
    public bool HasStrikes => Strikes.Count > 0;
    public string Stamp => Time.ToString("HH:mm");

    partial void OnTextChanged(string value) => Reparse();

    public void Append(string piece) => Text += piece;

    /// <summary>Splits on ``` fences so code renders in its own block with copy and insert.</summary>
    public void Reparse()
    {
        var parsed = new List<ChatSegment>();
        var text = Text.Replace("\r\n", "\n");
        var pos = 0;
        while (pos < text.Length)
        {
            var open = text.IndexOf("```", pos, StringComparison.Ordinal);
            if (open < 0)
            {
                Add(parsed, false, text[pos..], "");
                break;
            }
            Add(parsed, false, text[pos..open], "");
            var langEnd = text.IndexOf('\n', open + 3);
            if (langEnd < 0)
            {
                Add(parsed, true, "", text[(open + 3)..].Trim());
                break;
            }
            var lang = text[(open + 3)..langEnd].Trim();
            var close = text.IndexOf("\n```", langEnd, StringComparison.Ordinal);
            if (close < 0)
            {
                // Still streaming: show the code so far.
                Add(parsed, true, text[(langEnd + 1)..], lang);
                break;
            }
            Add(parsed, true, text[(langEnd + 1)..close], lang);
            pos = close + 4;
            var after = text.IndexOf('\n', pos);
            pos = after < 0 ? text.Length : after + 1;
        }

        // Update in place where possible, so streaming does not rebuild every block.
        for (var i = 0; i < parsed.Count; i++)
        {
            if (i < Segments.Count)
            {
                if (Segments[i] != parsed[i]) Segments[i] = parsed[i];
            }
            else Segments.Add(parsed[i]);
        }
        while (Segments.Count > parsed.Count) Segments.RemoveAt(Segments.Count - 1);
    }

    private static void Add(List<ChatSegment> list, bool code, string text, string lang)
    {
        if (!code) text = text.Trim('\n');
        if (!code && string.IsNullOrWhiteSpace(text)) return;
        list.Add(new ChatSegment(code, code ? text.TrimEnd('\n') : text, lang));
    }

    public void NotifyStrikes() => OnPropertyChanged(nameof(HasStrikes));
}

/// <summary>A line in the Output panel.</summary>
public sealed class LogLine
{
    public LogLine(string text, LogKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public DateTime Time { get; } = DateTime.Now;
    public string Text { get; }
    public LogKind Kind { get; }
    public string Stamp => Time.ToString("HH:mm:ss");

    public IBrush Brush => Kind switch
    {
        LogKind.Error => new SolidColorBrush(Color.Parse("#E5484D")),
        LogKind.Warning => new SolidColorBrush(Color.Parse("#C9A15A")),
        LogKind.Success => new SolidColorBrush(Color.Parse("#3FB8A5")),
        LogKind.Ai => new SolidColorBrush(Color.Parse("#F3A26F")),
        LogKind.Command => new SolidColorBrush(Color.Parse("#6FA8DC")),
        _ => new SolidColorBrush(Color.Parse("#9AA0A8")),
    };
}

/// <summary>A model entry for the picker at the top of the chat.</summary>
public sealed record ModelChoice(string Provider, string Model, bool Configured)
{
    public string Key => $"{Provider}|{Model}";
    public string Display => $"{Model}";
    public string ProviderLabel => Provider switch { "AzureOpenAI" => "Azure OpenAI", _ => Provider } + (Configured ? "" : " · no key");
}
