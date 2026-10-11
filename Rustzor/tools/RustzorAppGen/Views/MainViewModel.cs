using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RustzorAppGen.Models;
using RustzorAppGen.Services;

namespace RustzorAppGen.Views;

public sealed class Attachment
{
    public required string Name { get; init; }
    public required byte[] Data { get; init; }
    public required string MediaType { get; init; }
    public required Bitmap Thumbnail { get; init; }
}

/// <summary>Bindable state of the main window; actions live in its code-behind.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public ObservableCollection<FileNode> Tree { get; } = new();
    public ObservableCollection<OpenDocument> Documents { get; } = new();
    public ObservableCollection<ChatItem> Chat { get; } = new();
    public ObservableCollection<LogLine> Logs { get; } = new();
    public ObservableCollection<BuildProblem> Problems { get; } = new();
    public ObservableCollection<ModelChoice> Models { get; } = new();
    public ObservableCollection<Attachment> Attachments { get; } = new();
    public ObservableCollection<string> RecentProjects { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProject), nameof(NoProject), nameof(WindowTitle))] private string? _projectRoot;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDocument), nameof(NoDocument), nameof(LanguageText))] private OpenDocument? _activeDocument;
    [ObservableProperty] private ModelChoice? _selectedModel;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSend))] private string _chatInput = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSend), nameof(JackStatus))] private bool _isJackWorking;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _caretText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBusy))] private string _busyText = "";
    [ObservableProperty] private string? _servingUrl;
    [ObservableProperty] private int _logsTab;
    [ObservableProperty] private bool _showLineNumbers = true;
    [ObservableProperty] private bool _wordWrap;

    public bool HasProject => ProjectRoot is not null;
    public bool NoProject => ProjectRoot is null;
    public bool HasDocument => ActiveDocument is not null;
    public bool NoDocument => ActiveDocument is null;
    public bool IsBusy => BusyText.Length > 0;
    public bool CanSend => !IsJackWorking && (ChatInput.Trim().Length > 0 || Attachments.Count > 0);
    public string ProjectName => ProjectRoot is null ? "No project" : Path.GetFileName(ProjectRoot.TrimEnd(Path.DirectorySeparatorChar));
    public string WindowTitle => ProjectRoot is null ? "Rustzor App Generator" : $"{ProjectName} — Rustzor App Generator";
    public string LanguageText => ActiveDocument?.Language ?? "";
    public string JackStatus => IsJackWorking ? "Jack is working…" : "Jack is ready";
    public string ProblemsHeader => Problems.Count == 0 ? "Problems" : $"Problems ({Problems.Count})";

    partial void OnProjectRootChanged(string? value) => OnPropertyChanged(nameof(ProjectName));

    public void NotifyProblems() => OnPropertyChanged(nameof(ProblemsHeader));
    public void NotifyCanSend() => OnPropertyChanged(nameof(CanSend));
}
