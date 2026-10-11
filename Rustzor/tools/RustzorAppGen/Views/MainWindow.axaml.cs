using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.TextMate;
using RustzorAppGen.Models;
using RustzorAppGen.Services;
using TextMateSharp.Grammars;

namespace RustzorAppGen.Views;

public partial class MainWindow : Window, IWorkbench
{
    private readonly AppSettings _settings;
    private readonly MainViewModel _vm = new();
    private readonly AssistantService _assistant;
    private readonly BuildService _build;
    private readonly TemplateService _templates;
    private readonly RegistryOptions _registry;
    private readonly TextMate.Installation _textMate;
    private readonly string? _startPath;
    private readonly string? _autoPrompt;
    private CancellationTokenSource? _jackCancel;
    private bool _closingConfirmed;
    private bool _explorerVisible = true;
    private bool _chatVisible;
    private bool _logsVisible;
    private DispatcherTimer? _scrollTimer;

    // The designer needs a parameterless constructor.
    public MainWindow() : this(AppSettings.Load(), null, null) { }

    public MainWindow(AppSettings settings, string? startPath, string? autoPrompt = null)
    {
        _settings = settings;
        _startPath = startPath;
        _autoPrompt = autoPrompt;
        InitializeComponent();
        DataContext = _vm;

        _build = new BuildService(settings, Log);
        _templates = new TemplateService(settings);
        _assistant = new AssistantService(settings, this);

        _registry = new RegistryOptions(settings.Theme == "Light" ? ThemeName.LightPlus : ThemeName.DarkPlus);
        _textMate = Editor.InstallTextMate(_registry);
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();
        ApplyEditorSettings();

        _vm.ShowLineNumbers = settings.ShowLineNumbers;
        _vm.WordWrap = settings.WordWrap;
        _explorerVisible = true;
        _chatVisible = settings.ChatPanelVisible;
        _logsVisible = settings.LogsVisible;
        ApplyPanels();

        LoadModels();
        LoadRecent();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedModel) && _vm.SelectedModel is { } m && _settings.ActiveModel != m.Key)
            {
                _settings.ActiveModel = m.Key;
                SaveSettings();
                Log($"Jack now uses {m.Model} ({m.ProviderLabel})", LogKind.Info);
            }
        };
        _vm.Chat.CollectionChanged += (_, _) => ChatIntro.IsVisible = _vm.Chat.Count == 0;
        _vm.Attachments.CollectionChanged += (_, _) => _vm.NotifyCanSend();
        _vm.Problems.CollectionChanged += (_, _) => _vm.NotifyProblems();

        ChatBox.AddHandler(KeyDownEvent, OnChatKeyDown, RoutingStrategies.Tunnel);
        Composer.AddHandler(DragDrop.DropEvent, OnComposerDrop);
        Opened += OnOpened;
        Closing += OnClosing;

        Log("Rustzor App Generator ready. Built by Gravicode Studios, led by Kang Fadhil.", LogKind.Success);
        Log($"Settings: {_settings.ConfigPath}", LogKind.Info);
    }

    // -- startup, shutdown -----------------------------------------------------

    private async void OnOpened(object? sender, EventArgs e)
    {
        var target = _startPath;
        if (target is not null && Directory.Exists(target)) await OpenProjectAsync(target);
        else if (target is not null && File.Exists(target)) OpenFile(target);
        if (string.IsNullOrWhiteSpace(_settings.RustzorRoot) && _settings.RustzorReference == "Source")
            Log("Set the Rustzor source folder in Settings → Rustzor so new projects can build.", LogKind.Warning);
        if (!string.IsNullOrWhiteSpace(_autoPrompt))
        {
            _vm.ChatInput = _autoPrompt;
            await SendToJackAsync();
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed) return;
        var dirty = _vm.Documents.Where(d => d.IsDirty).ToList();
        SaveLayout();
        if (dirty.Count == 0)
        {
            _build.Stop();
            return;
        }
        e.Cancel = true;
        var choice = await Dialogs.ChooseAsync(this, "Save changes?", $"{dirty.Count} file(s) have unsaved changes: {string.Join(", ", dirty.Select(d => d.Title))}.", "Save all", "Don't save");
        if (choice is null) return;
        if (choice == true) foreach (var d in dirty) SaveDocument(d);
        _build.Stop();
        _closingConfirmed = true;
        Close();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log($"Could not save settings: {ex.Message}", LogKind.Warning);
        }
    }

    private void SaveLayout()
    {
        if (_explorerVisible && Workspace.ColumnDefinitions[0].ActualWidth > 80) _settings.ExplorerWidth = Workspace.ColumnDefinitions[0].ActualWidth;
        if (_chatVisible && Workspace.ColumnDefinitions[4].ActualWidth > 200) _settings.ChatPanelWidth = Workspace.ColumnDefinitions[4].ActualWidth;
        _settings.ChatPanelVisible = _chatVisible;
        _settings.LogsVisible = _logsVisible;
        _settings.ShowLineNumbers = _vm.ShowLineNumbers;
        _settings.WordWrap = _vm.WordWrap;
        SaveSettings();
    }

    private void LoadModels()
    {
        _vm.Models.Clear();
        foreach (var key in _settings.AllModels)
        {
            var (provider, model) = _settings.Resolve(key);
            _vm.Models.Add(new ModelChoice(provider.Name, model, provider.IsConfigured));
        }
        _vm.SelectedModel = _vm.Models.FirstOrDefault(m => m.Key == _settings.ActiveModel) ?? _vm.Models.FirstOrDefault(m => m.Configured) ?? _vm.Models.FirstOrDefault();
    }

    private void LoadRecent()
    {
        _vm.RecentProjects.Clear();
        foreach (var p in _settings.RecentProjects.Where(Directory.Exists).Take(8)) _vm.RecentProjects.Add(p);
        RecentMenu.Items.Clear();
        foreach (var p in _vm.RecentProjects)
        {
            var item = new MenuItem { Header = p, Tag = p };
            item.Click += OnOpenRecent;
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = _vm.RecentProjects.Count > 0;
    }

    // -- IWorkbench --------------------------------------------------------------

    public string? ProjectRoot => _vm.ProjectRoot;

    public void Log(string text, LogKind kind = LogKind.Info)
    {
        void Add()
        {
            _vm.Logs.Add(new LogLine(text, kind));
            while (_vm.Logs.Count > 4000) _vm.Logs.RemoveAt(0);
            if (kind is LogKind.Success or LogKind.Error or LogKind.Ai) _vm.StatusText = text.Length > 120 ? text[..120] + "…" : text;
            LogList.ScrollIntoView(_vm.Logs.Count - 1);
        }
        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }

    public void FileChanged(string path) => Dispatcher.UIThread.Post(() =>
    {
        RefreshTree();
        var doc = _vm.Documents.FirstOrDefault(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (doc is null) return;
        if (!File.Exists(path))
        {
            CloseDocument(doc, force: true);
            return;
        }
        if (doc.IsDirty)
        {
            Log($"{doc.Title} changed on disk while you had unsaved edits; your version is kept in the editor.", LogKind.Warning);
            return;
        }
        var text = File.ReadAllText(path);
        if (doc.Document.Text != text)
        {
            doc.Document.Text = text;
            doc.IsDirty = false;
        }
    });

    public void OpenFile(string path, int line = 0) => Dispatcher.UIThread.Post(() => OpenDocumentAt(path, line));

    public Task OpenProjectAsync(string folder) => Dispatcher.UIThread.InvokeAsync(() => OpenProjectCore(folder)).GetTask();

    public Task<BuildResult> BuildAsync(CancellationToken cancel = default) =>
        Dispatcher.UIThread.InvokeAsync(() => BuildCoreAsync(cancel));

    public Task<string> RunAsync(CancellationToken cancel = default) =>
        Dispatcher.UIThread.InvokeAsync(() => RunCoreAsync(cancel));

    // -- projects ------------------------------------------------------------------

    private void OpenProjectCore(string folder)
    {
        folder = Path.GetFullPath(folder);
        foreach (var d in _vm.Documents.ToList())
            if (!d.FullPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) CloseDocument(d, force: !d.IsDirty);
        _vm.ProjectRoot = folder;
        _vm.Problems.Clear();
        RefreshTree();
        _settings.RememberProject(folder);
        SaveSettings();
        LoadRecent();
        Log($"Opened {folder}", LogKind.Success);
        var start = new[] { "Pages/Home.razor", "Pages/Index.razor", "MainLayout.razor", "Program.cs", "README.md" }
            .Select(p => Path.Combine(folder, p)).FirstOrDefault(File.Exists);
        if (start is not null && _vm.Documents.Count == 0) OpenDocumentAt(start, 0);
    }

    private void RefreshTree()
    {
        if (_vm.ProjectRoot is null)
        {
            _vm.Tree.Clear();
            return;
        }
        var expanded = _vm.Tree.SelectMany(n => n.SelfAndDescendants()).Where(n => n.IsDirectory && n.IsExpanded).Select(n => n.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = FileNode.Load(_vm.ProjectRoot);
        foreach (var n in root.SelfAndDescendants().Where(n => n.IsDirectory))
            if (expanded.Contains(n.FullPath)) n.IsExpanded = true;
        _vm.Tree.Clear();
        foreach (var c in root.Children) _vm.Tree.Add(c);
    }

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectWindow(_templates, _settings);
        var folder = await dialog.ShowDialog<string?>(this);
        if (folder is null) return;
        SaveSettings();
        OpenProjectCore(folder);
        if (dialog.FirstPrompt is { Length: > 0 } prompt)
        {
            _vm.ChatInput = prompt;
            await SendToJackAsync();
        }
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open a project folder", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) OpenProjectCore(path);
    }

    private void OnOpenRecent(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string path && Directory.Exists(path)) OpenProjectCore(path);
    }

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open a file", AllowMultiple = true });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } p) OpenDocumentAt(p, 0);
    }

    private async void OnCloseProject(object? sender, RoutedEventArgs e)
    {
        if (_vm.ProjectRoot is null) return;
        var dirty = _vm.Documents.Where(d => d.IsDirty).ToList();
        if (dirty.Count > 0)
        {
            var choice = await Dialogs.ChooseAsync(this, "Save changes?", $"Save {dirty.Count} changed file(s) before closing the project?", "Save all", "Don't save");
            if (choice is null) return;
            if (choice == true) foreach (var d in dirty) SaveDocument(d);
        }
        foreach (var d in _vm.Documents.ToList()) CloseDocument(d, force: true);
        _build.Stop();
        _vm.ServingUrl = null;
        _vm.ProjectRoot = null;
        _vm.Tree.Clear();
        _vm.Problems.Clear();
        LoadRecent();
        Log("Closed the project", LogKind.Info);
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    // -- documents -------------------------------------------------------------------

    private void OpenDocumentAt(string path, int line)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            Log($"No such file: {path}", LogKind.Warning);
            return;
        }
        var doc = _vm.Documents.FirstOrDefault(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (doc is null)
        {
            if (new FileInfo(path).Length > 8_000_000)
            {
                Log($"{Path.GetFileName(path)} is too large to edit here.", LogKind.Warning);
                return;
            }
            if (IsBinary(path))
            {
                Log($"{Path.GetFileName(path)} is not a text file.", LogKind.Info);
                return;
            }
            doc = new OpenDocument(path, File.ReadAllText(path));
            doc.IsDirty = false;
            _vm.Documents.Add(doc);
        }
        Activate(doc);
        if (line > 0)
        {
            var l = Math.Min(line, Editor.Document.LineCount);
            Editor.TextArea.Caret.Line = l;
            Editor.TextArea.Caret.Column = 1;
            Editor.ScrollToLine(l);
            Editor.TextArea.Caret.BringCaretToView();
        }
        Editor.Focus();
    }

    private static bool IsBinary(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico" or ".dll" or ".exe" or ".wasm" or ".zip" or ".pdf" or ".woff" or ".woff2" or ".ttf") return true;
        using var s = File.OpenRead(path);
        var buffer = new byte[Math.Min(4096, s.Length)];
        var n = s.Read(buffer, 0, buffer.Length);
        return buffer.Take(n).Contains((byte)0);
    }

    private void Activate(OpenDocument doc)
    {
        foreach (var d in _vm.Documents) d.IsActive = d == doc;
        _vm.ActiveDocument = doc;
        Editor.Document = doc.Document;
        SetGrammar(doc.Extension);
        UpdateCaret();
    }

    private void SetGrammar(string extension)
    {
        var candidates = extension switch
        {
            ".razor" or ".cshtml" => new[] { ".razor", ".cshtml", ".html" },
            ".csproj" or ".props" or ".targets" or ".config" or ".axaml" or ".xaml" => new[] { ".xml" },
            ".mjs" => new[] { ".js" },
            _ => new[] { extension },
        };
        foreach (var ext in candidates)
        {
            var language = _registry.GetLanguageByExtension(ext);
            if (language is null) continue;
            _textMate.SetGrammar(_registry.GetScopeByLanguageId(language.Id));
            return;
        }
        _textMate.SetGrammar(null);
    }

    private void UpdateCaret()
    {
        if (_vm.ActiveDocument is null)
        {
            _vm.CaretText = "";
            return;
        }
        _vm.CaretText = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";
    }

    private void SaveDocument(OpenDocument doc)
    {
        try
        {
            File.WriteAllText(doc.FullPath, doc.Document.Text);
            doc.IsDirty = false;
            Log($"Saved {doc.Title}", LogKind.Info);
        }
        catch (Exception ex)
        {
            Log($"Could not save {doc.Title}: {ex.Message}", LogKind.Error);
        }
    }

    private void SaveAll()
    {
        foreach (var d in _vm.Documents.Where(d => d.IsDirty)) SaveDocument(d);
    }

    private async void CloseDocument(OpenDocument doc, bool force = false)
    {
        if (doc.IsDirty && !force)
        {
            var choice = await Dialogs.ChooseAsync(this, "Save changes?", $"{doc.Title} has unsaved changes.", "Save", "Don't save");
            if (choice is null) return;
            if (choice == true) SaveDocument(doc);
        }
        var index = _vm.Documents.IndexOf(doc);
        _vm.Documents.Remove(doc);
        if (_vm.ActiveDocument == doc)
        {
            if (_vm.Documents.Count > 0) Activate(_vm.Documents[Math.Clamp(index - 1, 0, _vm.Documents.Count - 1)]);
            else
            {
                _vm.ActiveDocument = null;
                _vm.CaretText = "";
            }
        }
    }

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is not OpenDocument doc) return;
        if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) CloseDocument(doc);
        else Activate(doc);
    }

    private void OnTabClose(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is OpenDocument doc) CloseDocument(doc);
        e.Handled = true;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_vm.ActiveDocument is { } d) SaveDocument(d);
    }

    private void OnSaveAll(object? sender, RoutedEventArgs e) => SaveAll();

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (_vm.ActiveDocument is { } d) CloseDocument(d);
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => Editor.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => Editor.Redo();

    private async void OnGoToLine(object? sender, RoutedEventArgs e)
    {
        if (_vm.ActiveDocument is null) return;
        var answer = await Dialogs.PromptAsync(this, "Go to line", $"Line number (1–{Editor.Document.LineCount}), optionally :column.", Editor.TextArea.Caret.Line.ToString(), "Go");
        if (answer is null) return;
        var parts = answer.Split(':', ',');
        if (!int.TryParse(parts[0], out var line)) return;
        var column = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 1;
        line = Math.Clamp(line, 1, Editor.Document.LineCount);
        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.Column = Math.Max(1, column);
        Editor.ScrollToLine(line);
        Editor.TextArea.Caret.BringCaretToView();
        Editor.Focus();
    }

    private void OnFormat(object? sender, RoutedEventArgs e)
    {
        if (_vm.ActiveDocument is not { } doc) return;
        try
        {
            var formatted = CodeFormatter.Format(doc.FullPath, doc.Document.Text);
            if (formatted == doc.Document.Text)
            {
                Log($"{doc.Title} is already formatted", LogKind.Info);
                return;
            }
            var caret = Editor.TextArea.Caret.Line;
            doc.Document.Replace(0, doc.Document.TextLength, formatted);
            Editor.TextArea.Caret.Line = Math.Min(caret, doc.Document.LineCount);
            Log($"Formatted {doc.Title}", LogKind.Success);
        }
        catch (FormatException ex)
        {
            Log(ex.Message, LogKind.Warning);
        }
    }

    // -- explorer ---------------------------------------------------------------------

    private FileNode? SelectedNode => ProjectTree.SelectedItem as FileNode;

    private void OnTreeSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (SelectedNode is { IsDirectory: false } n) OpenDocumentAt(n.FullPath, 0);
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SelectedNode is { IsDirectory: true } n) n.IsExpanded = !n.IsExpanded;
    }

    private void OnTreeOpen(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is { IsDirectory: false } n) OpenDocumentAt(n.FullPath, 0);
    }

    private string TargetFolder() =>
        SelectedNode is { } n ? (n.IsDirectory ? n.FullPath : Path.GetDirectoryName(n.FullPath)!) : _vm.ProjectRoot!;

    private async void OnNewFile(object? sender, RoutedEventArgs e)
    {
        if (_vm.ProjectRoot is null) return;
        var folder = TargetFolder();
        var name = await Dialogs.PromptAsync(this, "New file", $"In {Path.GetRelativePath(_vm.ProjectRoot, folder)}. Use .razor for a component or page.", "NewPage.razor", "Create");
        if (name is null) return;
        var path = Path.Combine(folder, name);
        if (File.Exists(path))
        {
            Log($"{name} already exists", LogKind.Warning);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Starter(path));
        RefreshTree();
        OpenDocumentAt(path, 0);
    }

    private string Starter(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        // The project's root namespace is its .csproj name (TemplateService.ProjectNamespace).
        var project = _vm.ProjectRoot is { } root ? Directory.EnumerateFiles(root, "*.csproj").FirstOrDefault() : null;
        var ns = project is null ? "App" : Path.GetFileNameWithoutExtension(project);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".razor" => $"@page \"/{name.ToLowerInvariant()}\"\n\n<PageHeader Title=\"{name}\" />\n\n<Card>\n    <p>Start here.</p>\n</Card>\n\n@code {{\n}}\n",
            ".cs" => $"namespace {ns};\n\npublic sealed class {TemplateService.ToIdentifier(name)}\n{{\n}}\n",
            _ => "",
        };
    }

    private async void OnNewFolder(object? sender, RoutedEventArgs e)
    {
        if (_vm.ProjectRoot is null) return;
        var name = await Dialogs.PromptAsync(this, "New folder", "Folder name", "Components", "Create");
        if (name is null) return;
        Directory.CreateDirectory(Path.Combine(TargetFolder(), name));
        RefreshTree();
    }

    private async void OnTreeRename(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } n) return;
        var name = await Dialogs.PromptAsync(this, "Rename", n.FullPath, n.Name, "Rename");
        if (name is null || name == n.Name) return;
        var target = Path.Combine(Path.GetDirectoryName(n.FullPath)!, name);
        try
        {
            if (n.IsDirectory) Directory.Move(n.FullPath, target);
            else File.Move(n.FullPath, target);
            foreach (var d in _vm.Documents.Where(d => d.FullPath.StartsWith(n.FullPath, StringComparison.OrdinalIgnoreCase)).ToList()) CloseDocument(d, force: !d.IsDirty);
            RefreshTree();
        }
        catch (Exception ex)
        {
            Log($"Could not rename: {ex.Message}", LogKind.Error);
        }
    }

    private async void OnTreeDelete(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } n) return;
        if (!await Dialogs.ConfirmAsync(this, $"Delete {n.Name}?", n.IsDirectory ? "The folder and everything in it will be deleted." : "The file will be deleted.", "Delete", danger: true)) return;
        try
        {
            if (n.IsDirectory) Directory.Delete(n.FullPath, true);
            else File.Delete(n.FullPath);
            foreach (var d in _vm.Documents.Where(d => d.FullPath.StartsWith(n.FullPath, StringComparison.OrdinalIgnoreCase)).ToList()) CloseDocument(d, force: true);
            RefreshTree();
            Log($"Deleted {n.Name}", LogKind.Info);
        }
        catch (Exception ex)
        {
            Log($"Could not delete: {ex.Message}", LogKind.Error);
        }
    }

    private void OnTreeReveal(object? sender, RoutedEventArgs e)
    {
        var path = SelectedNode?.FullPath ?? _vm.ProjectRoot;
        if (path is null) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"");
            else Process.Start(new ProcessStartInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log(ex.Message, LogKind.Warning);
        }
    }

    private void OnRefreshTree(object? sender, RoutedEventArgs e) => RefreshTree();

    // -- build, run, deploy ---------------------------------------------------------

    private async Task<BuildResult> BuildCoreAsync(CancellationToken cancel)
    {
        if (_vm.ProjectRoot is null) throw new InvalidOperationException("No project is open.");
        SaveAll();
        ShowLogs(0);
        _vm.BusyText = "Building…";
        try
        {
            var result = await Task.Run(() => _build.BuildAsync(_vm.ProjectRoot, "Debug", cancel), cancel);
            _vm.Problems.Clear();
            foreach (var p in result.Problems.OrderBy(p => p.Severity == "error" ? 0 : 1)) _vm.Problems.Add(p);
            if (result.Problems.Any(p => p.Severity == "error")) ShowLogs(1);
            return result;
        }
        finally
        {
            _vm.BusyText = "";
        }
    }

    private async Task<string> RunCoreAsync(CancellationToken cancel)
    {
        if (_vm.ProjectRoot is null) throw new InvalidOperationException("No project is open.");
        SaveAll();
        ShowLogs(0);
        _vm.BusyText = "Building and starting…";
        try
        {
            var url = await Task.Run(() => _build.RunAsync(_vm.ProjectRoot, cancel), cancel);
            _vm.ServingUrl = url;
            return url;
        }
        catch
        {
            var problems = BuildService.ParseProblems(string.Join("\n", _vm.Logs.Select(l => l.Text)));
            if (problems.Count > 0)
            {
                _vm.Problems.Clear();
                foreach (var p in problems) _vm.Problems.Add(p);
                ShowLogs(1);
            }
            throw;
        }
        finally
        {
            _vm.BusyText = "";
        }
    }

    private async void OnBuild(object? sender, RoutedEventArgs e)
    {
        try
        {
            await BuildCoreAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log(ex.Message, LogKind.Error);
        }
    }

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        try
        {
            await RunCoreAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log(ex.Message, LogKind.Error);
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        _build.Stop();
        _vm.ServingUrl = null;
    }

    private void OnOpenServing(object? sender, RoutedEventArgs e)
    {
        if (_vm.ServingUrl is { } url) BuildService.OpenBrowser(url);
    }

    private async void OnDeploy(object? sender, RoutedEventArgs e)
    {
        if (_vm.ProjectRoot is null) return;
        SaveAll();
        ShowLogs(0);
        _vm.BusyText = "Publishing…";
        try
        {
            var target = await Task.Run(() => _build.DeployAsync(_vm.ProjectRoot));
            await Dialogs.MessageAsync(this, "Deployed", $"The Release build of the site is ready:\n{target}\n\nUpload the folder (or unzip the archive) to any static web host. No server-side code is needed.");
        }
        catch (Exception ex)
        {
            Log(ex.Message, LogKind.Error);
        }
        finally
        {
            _vm.BusyText = "";
        }
    }

    private void OnProblemOpen(object? sender, TappedEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is not BuildProblem p) return;
        var path = Path.IsPathRooted(p.File) ? p.File : Path.Combine(_vm.ProjectRoot ?? "", p.File);
        OpenDocumentAt(path, p.Line);
        if (_vm.ActiveDocument is not null) Editor.TextArea.Caret.Column = Math.Max(1, p.Column);
    }

    // -- panels, view -------------------------------------------------------------------

    private void ApplyEditorSettings()
    {
        Editor.FontSize = _settings.EditorFontSize;
        Editor.ShowLineNumbers = _vm.ShowLineNumbers;
        Editor.WordWrap = _vm.WordWrap;
        _textMate.SetTheme(_registry.LoadTheme(_settings.Theme == "Light" ? ThemeName.LightPlus : ThemeName.DarkPlus));
        if (this.TryFindResource("Bg2", ActualThemeVariant, out var bg) && bg is IBrush brush) Editor.Background = brush;
        if (this.TryFindResource("Faint", ActualThemeVariant, out var faint) && faint is IBrush f) Editor.LineNumbersForeground = f;
    }

    private void ApplyPanels()
    {
        var cols = Workspace.ColumnDefinitions;
        cols[0].Width = _explorerVisible ? new GridLength(Math.Max(180, _settings.ExplorerWidth)) : new GridLength(0);
        cols[1].Width = new GridLength(_explorerVisible ? 1 : 0);
        ExplorerSplitter.IsVisible = _explorerVisible;
        cols[4].Width = _chatVisible ? new GridLength(Math.Max(320, _settings.ChatPanelWidth)) : new GridLength(0);
        cols[3].Width = new GridLength(_chatVisible ? 1 : 0);
        ChatSplitter.IsVisible = _chatVisible;
        ChatPanel.IsVisible = _chatVisible;
        LogsPanel.IsVisible = _logsVisible;
        LogsSplitter.IsVisible = _logsVisible;
    }

    private void ShowLogs(int tab)
    {
        _logsVisible = true;
        ApplyPanels();
        _vm.LogsTab = tab;
        OutputTab.IsChecked = tab == 0;
        ProblemsTab.IsChecked = tab == 1;
    }

    private void OnLogsTab(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string t) ShowLogs(int.Parse(t));
    }

    private void OnClearLogs(object? sender, RoutedEventArgs e)
    {
        if (_vm.LogsTab == 0) _vm.Logs.Clear();
        else _vm.Problems.Clear();
    }

    private void OnSplitterDragged(object? sender, VectorEventArgs e) => SaveLayout();

    private void OnToggleExplorer(object? sender, RoutedEventArgs e)
    {
        if (_explorerVisible) _settings.ExplorerWidth = Workspace.ColumnDefinitions[0].ActualWidth;
        _explorerVisible = !_explorerVisible;
        ApplyPanels();
    }

    private void OnToggleChat(object? sender, RoutedEventArgs e)
    {
        if (_chatVisible) _settings.ChatPanelWidth = Workspace.ColumnDefinitions[4].ActualWidth;
        _chatVisible = !_chatVisible;
        ApplyPanels();
        SaveLayout();
        if (_chatVisible) ChatBox.Focus();
    }

    private void OnToggleLogs(object? sender, RoutedEventArgs e)
    {
        _logsVisible = !_logsVisible;
        ApplyPanels();
        SaveLayout();
    }

    private void OnToggleLineNumbers(object? sender, RoutedEventArgs e)
    {
        _vm.ShowLineNumbers = !_vm.ShowLineNumbers;
        Editor.ShowLineNumbers = _vm.ShowLineNumbers;
        SaveLayout();
    }

    private void OnToggleWordWrap(object? sender, RoutedEventArgs e)
    {
        _vm.WordWrap = !_vm.WordWrap;
        Editor.WordWrap = _vm.WordWrap;
        SaveLayout();
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        _settings.Theme = _settings.Theme == "Light" ? "Dark" : "Light";
        App.ApplyTheme(_settings.Theme);
        Dispatcher.UIThread.Post(ApplyEditorSettings, DispatcherPriority.Background);
        SaveSettings();
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, _assistant);
        var saved = await dialog.ShowDialog<bool>(this);
        if (!saved) return;
        SaveSettings();
        App.ApplyTheme(_settings.Theme);
        LoadModels();
        _vm.ShowLineNumbers = _settings.ShowLineNumbers;
        _vm.WordWrap = _settings.WordWrap;
        Dispatcher.UIThread.Post(ApplyEditorSettings, DispatcherPriority.Background);
        Log("Settings saved", LogKind.Success);
    }

    private void OnOpenRepo(object? sender, RoutedEventArgs e) => BuildService.OpenBrowser("https://github.com/DotNetVibeCoderz/Vibe_Framework/tree/main/Rustzor");

    private async void OnAbout(object? sender, RoutedEventArgs e) => await Dialogs.MessageAsync(this, "Rustzor App Generator",
        "Version 0.1.0\n\nAn editor with an AI assistant, Jack - The Code Bender, that builds web apps with Rustzor: C# Razor components running in the browser on RustNetRuntime (Rust + WebAssembly).\n\n" +
        "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.\nBuilt by Gravicode Studios, led by Kang Fadhil.\n\nFonts: Bricolage Grotesque, IBM Plex Sans, JetBrains Mono (SIL Open Font License).");

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        RoutedEventArgs r = new();
        var handled = true;
        switch (e.Key)
        {
            case Key.N when ctrl && shift: OnNewProject(this, r); break;
            case Key.O when ctrl && shift: OnOpenProject(this, r); break;
            case Key.O when ctrl: OnOpenFile(this, r); break;
            case Key.S when ctrl && shift: OnSaveAll(this, r); break;
            case Key.S when ctrl: OnSave(this, r); break;
            case Key.W when ctrl: OnCloseTab(this, r); break;
            case Key.G when ctrl: OnGoToLine(this, r); break;
            case Key.F when shift && alt: OnFormat(this, r); break;
            case Key.B when ctrl && shift: OnBuild(this, r); break;
            case Key.B when ctrl: OnToggleExplorer(this, r); break;
            case Key.F5 when shift: OnStop(this, r); break;
            case Key.F5: OnRun(this, r); break;
            case Key.J when ctrl: OnToggleLogs(this, r); break;
            case Key.L when ctrl && shift: OnToggleChat(this, r); break;
            case Key.L when ctrl:
                if (!_chatVisible) OnToggleChat(this, r);
                ChatBox.Focus();
                break;
            case Key.OemComma when ctrl: OnSettings(this, r); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
        else base.OnKeyDown(e);
    }

    // -- Jack ---------------------------------------------------------------------------

    private void OnChatKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            if (_vm.CanSend) _ = SendToJackAsync();
        }
    }

    private async void OnSend(object? sender, RoutedEventArgs e) => await SendToJackAsync();

    private async void OnExamplePrompt(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string prompt) return;
        _vm.ChatInput = prompt;
        await SendToJackAsync();
    }

    private void OnStopJack(object? sender, RoutedEventArgs e) => _jackCancel?.Cancel();

    private void OnClearChat(object? sender, RoutedEventArgs e)
    {
        _jackCancel?.Cancel();
        _vm.Chat.Clear();
        _assistant.Clear();
        Log("Started a new conversation with Jack", LogKind.Info);
    }

    private sealed class ChatSink(MainWindow window, ChatItem item) : IAssistantSink
    {
        public void Text(string piece) => Dispatcher.UIThread.Post(() =>
        {
            item.Append(piece);
            window.ScheduleScroll();
        });

        public void ToolCall(string callId, string name, string arguments) => Dispatcher.UIThread.Post(() =>
        {
            if (item.Strikes.Any(s => s.CallId == callId)) return;
            item.Strikes.Add(new ToolStrike { CallId = callId, Name = name.Replace('-', '.').Replace('_', '_'), Arguments = arguments });
            item.NotifyStrikes();
            window.Log($"Jack → {name}({arguments})", LogKind.Ai);
            window.ScheduleScroll();
        });

        public void ToolResult(string callId, string summary) => Dispatcher.UIThread.Post(() =>
        {
            var strike = item.Strikes.FirstOrDefault(s => s.CallId == callId);
            if (strike is null) return;
            strike.Result = summary;
            strike.Done = true;
        });
    }

    private void ScheduleScroll()
    {
        _scrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) =>
        {
            _scrollTimer!.Stop();
            ChatScroll.ScrollToEnd();
        });
        if (!_scrollTimer.IsEnabled) _scrollTimer.Start();
    }

    private async Task SendToJackAsync()
    {
        var text = _vm.ChatInput.Trim();
        if (_vm.IsJackWorking || (text.Length == 0 && _vm.Attachments.Count == 0)) return;
        if (text.Length == 0) text = "Here is an image.";
        if (!_chatVisible) OnToggleChat(this, new RoutedEventArgs());

        var images = _vm.Attachments.Select(a => (a.Data, a.MediaType)).ToList();
        _vm.Chat.Add(new ChatItem { Role = "user", Text = text, Images = _vm.Attachments.Select(a => a.Thumbnail).ToList() });
        _vm.ChatInput = "";
        _vm.Attachments.Clear();
        var reply = new ChatItem { Role = "jack", IsStreaming = true };
        _vm.Chat.Add(reply);
        ScheduleScroll();

        _vm.IsJackWorking = true;
        _jackCancel = new CancellationTokenSource();
        var sink = new ChatSink(this, reply);
        try
        {
            var context = _vm.ActiveDocument is { } d && _vm.ProjectRoot is { } root && d.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? $"\n\n(The user has {Path.GetRelativePath(root, d.FullPath).Replace('\\', '/')} open in the editor, caret at line {Editor.TextArea.Caret.Line}.)"
                : "";
            await Task.Run(() => _assistant.SendAsync(text + context, images, sink, _jackCancel.Token));
            if (string.IsNullOrWhiteSpace(reply.Text) && reply.Strikes.Count == 0) reply.Text = "(no reply)";
        }
        catch (OperationCanceledException)
        {
            reply.Append(reply.Text.Length > 0 ? "\n\n— stopped." : "Stopped.");
        }
        catch (Exception ex)
        {
            if (reply.Text.Length == 0 && reply.Strikes.Count == 0) _vm.Chat.Remove(reply);
            _vm.Chat.Add(new ChatItem { Role = "error", Text = Explain(ex) });
            Log($"Jack: {ex.Message}", LogKind.Error);
        }
        finally
        {
            reply.IsStreaming = false;
            _vm.IsJackWorking = false;
            _jackCancel = null;
            ScheduleScroll();
        }
    }

    private static string Explain(Exception ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        if (message.Contains("401") || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return $"The provider rejected the API key. Check it in Settings → AI providers.\n\n{message}";
        if (message.Contains("404")) return $"The model or endpoint was not found. Check the model name and endpoint in Settings.\n\n{message}";
        if (message.Contains("429")) return $"The provider is rate-limiting requests. Wait a moment and try again.\n\n{message}";
        return message;
    }

    private void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string code) _ = Clipboard?.SetTextAsync(code);
        Log("Copied code to the clipboard", LogKind.Info);
    }

    private void OnInsertCode(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string code) return;
        if (_vm.ActiveDocument is null)
        {
            Log("Open a file first, then insert the code at the cursor.", LogKind.Warning);
            return;
        }
        Editor.Document.Insert(Editor.CaretOffset, code);
        Editor.Focus();
    }

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach images",
            AllowMultiple = true,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } p) AddAttachment(p);
    }

    private void OnComposerDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } p) AddAttachment(p);
    }

    private void AddAttachment(string path)
    {
        var mediaType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp",
            _ => null,
        };
        if (mediaType is null)
        {
            Log($"{Path.GetFileName(path)} is not an image Jack can read (PNG, JPEG, GIF, WebP).", LogKind.Warning);
            return;
        }
        var data = File.ReadAllBytes(path);
        if (data.Length > 15_000_000)
        {
            Log($"{Path.GetFileName(path)} is larger than 15 MB.", LogKind.Warning);
            return;
        }
        using var stream = new MemoryStream(data);
        var thumb = Bitmap.DecodeToWidth(stream, 160);
        _vm.Attachments.Add(new Attachment { Name = Path.GetFileName(path), Data = data, MediaType = mediaType, Thumbnail = thumb });
    }

    private void OnRemoveAttachment(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is Attachment a) _vm.Attachments.Remove(a);
    }
}
