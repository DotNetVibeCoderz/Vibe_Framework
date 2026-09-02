using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RustNet.Designer.Assistant;

namespace RustNet.Designer.Avalonia;

/// <summary>
/// The assistant panel: session list, provider and model pickers, the
/// transcript, and a composer that can carry uploads.
/// </summary>
/// <remarks>
/// The WPF panel drew its transcript in a WebView2 — an HTML page written to
/// disk, served from a virtual host, and updated during streaming by calling
/// JavaScript across the bridge. None of that survives here, and none of it is
/// missed: Markdown.Avalonia renders markdown into ordinary Avalonia controls,
/// so there is no browser to install, no second theme in CSS to keep in step
/// with this one, and no HTML escaping between the model's words and the
/// screen. It also removes the reason the drawers had to hide the transcript
/// rather than cover it — a WebView2 is a native child window that nothing can
/// be drawn on top of, and a normal control is not.
/// </remarks>
public partial class ChatPanel : UserControl
{
    private readonly ObservableCollection<ChatSession> _sessions = new();
    private readonly List<ChatAttachment> _pending = new();

    private AssistantOptions _options = null!;
    private SessionStore _store = null!;
    private AttachmentStore _attachments = null!;
    private AssistantService _service = null!;
    private IDesignerBridge _designer = null!;

    private ChatSession? _current;
    private CancellationTokenSource? _turn;
    private bool _ready;
    private bool _suppressPickers;

    // Streaming arrives token by token; redrawing per token would spend the
    // whole turn re-parsing markdown, so deltas are collected and flushed.
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly System.Text.StringBuilder _streamed = new();
    private readonly List<string> _turnTools = new();
    private bool _dirty;
    private bool _live;

    public ChatPanel()
    {
        InitializeComponent();
        _flush.Tick += (_, _) => FlushStream();
    }

    /// <summary>Raised when the person closes the panel from its own header.</summary>
    public event EventHandler? HideRequested;

    /// <summary>Status text for the window's readout strip.</summary>
    public event EventHandler<string>? Status;

    // ---- lifetime ------------------------------------------------------

    /// <summary>Wire the panel to the Designer. Safe to call once.</summary>
    public void Initialize(IDesignerBridge designer)
    {
        _designer = designer;
        _options = AssistantOptions.Load();
        _store = new SessionStore(_options.DataDirectory);
        _attachments = new AttachmentStore(_store, _options);
        _service = new AssistantService(_options, _store, _attachments, designer);

        LoadPickers();
        LoadSettingsFields();
        BuildPromptGallery();

        SessionList.ItemsSource = _sessions;
        foreach (ChatSession s in _store.LoadAll())
        {
            _sessions.Add(s);
        }
        if (_sessions.Count == 0)
        {
            _sessions.Add(NewSession());
        }

        _ready = true;
        SelectSession(_sessions[0]);
    }

    // ---- sessions ------------------------------------------------------

    private ChatSession NewSession()
    {
        var session = new ChatSession
        {
            Provider = _options.Provider.ToString(),
            Model = _options.Current.Model,
        };
        _store.Save(session);
        return session;
    }

    private void SelectSession(ChatSession session)
    {
        _current = session;
        SessionList.SelectedItem = session;
        ClearPending();
        RenderTranscript();
    }

    private void OnSessionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SessionList.SelectedItem is not ChatSession s || ReferenceEquals(s, _current))
        {
            return;
        }
        SelectSession(s);
        SessionsToggle.IsChecked = false;
    }

    private void OnNewSession(object? sender, RoutedEventArgs e)
    {
        ChatSession session = NewSession();
        _sessions.Insert(0, session);
        SelectSession(session);
        SessionsToggle.IsChecked = false;
        Composer.Focus();
    }

    private async void OnResetSession(object? sender, RoutedEventArgs e)
    {
        if (_current == null || Owner() is not { } owner)
        {
            return;
        }
        if (!await Dialogs.Confirm(owner, "Reset session",
                $"Empty \"{_current.Title}\"? Its messages and uploads are deleted; "
                + "the session stays in the list."))
        {
            return;
        }
        _store.Reset(_current);
        RefreshSessionRow(_current);
        RenderTranscript();
        Report("Session reset.");
    }

    private async void OnDeleteSession(object? sender, RoutedEventArgs e)
    {
        if (_current == null || Owner() is not { } owner)
        {
            return;
        }
        if (!await Dialogs.Confirm(owner, "Delete session",
                $"Delete \"{_current.Title}\" and its uploads? This cannot be undone."))
        {
            return;
        }
        ChatSession gone = _current;
        _store.Delete(gone);
        _sessions.Remove(gone);
        if (_sessions.Count == 0)
        {
            _sessions.Add(NewSession());
        }
        SelectSession(_sessions[0]);
        Report("Session deleted.");
    }

    /// <summary>Re-seat a session in the list so its message count and title refresh.</summary>
    private void RefreshSessionRow(ChatSession session)
    {
        int i = _sessions.IndexOf(session);
        if (i >= 0)
        {
            _sessions.RemoveAt(i);
            _sessions.Insert(i, session);
            SessionList.SelectedItem = session;
        }
    }

    // ---- transcript ----------------------------------------------------

    private void RenderTranscript()
    {
        if (_current == null)
        {
            return;
        }
        string document = _current.Messages.Count == 0 && !_live
            ? TranscriptMarkdown.EmptyState(
                _options.Provider.ToString(),
                _options.Current.Model,
                _options.IsProviderConfigured(_options.Provider))
            : TranscriptMarkdown.Document(
                _current.Messages,
                _live ? _streamed.ToString() : null,
                _live ? _turnTools : null);

        Transcript.Markdown = document;
        ScrollToNewest();
    }

    /// <summary>
    /// Keep the newest line in view.
    /// </summary>
    /// <remarks>
    /// Setting <c>Markdown</c> rebuilds the whole document, and a rebuilt
    /// document starts at the top — so without this the reply you just asked
    /// for appears off-screen and the panel looks like it did nothing. The
    /// WPF version got this free: it appended to a live HTML page rather than
    /// replacing one.
    ///
    /// Posted at Background priority because the scrollable extent is not
    /// known until the new content has been measured; scrolling in the same
    /// pass scrolls to the end of the *old* document.
    /// </remarks>
    private void ScrollToNewest() => Dispatcher.UIThread.Post(
        () => Transcript.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd(),
        DispatcherPriority.Background);

    // ---- the turn ------------------------------------------------------

    private async void OnSend(object? sender, RoutedEventArgs e) => await SendAsync();

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        _turn?.Cancel();
        Report("Stopped.");
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private async Task SendAsync()
    {
        if (_current == null || _turn != null)
        {
            return;
        }
        string text = (Composer.Text ?? "").Trim();
        if (text.Length == 0 && _pending.Count == 0)
        {
            return;
        }

        var userMessage = new ChatMessage { Role = ChatRole.User, Text = text };
        userMessage.Attachments.AddRange(_pending);
        _current.Messages.Add(userMessage);
        if (_current.Messages.Count == 1)
        {
            _current.RetitleFromFirstMessage();
        }
        _current.Provider = _options.Provider.ToString();
        _current.Model = _options.Current.Model;
        _store.Save(_current);

        Composer.Text = "";
        ClearPending();
        RefreshSessionRow(_current);

        var live = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Model = $"{_options.Provider}/{_options.Current.Model}",
        };
        _streamed.Clear();
        _turnTools.Clear();
        _live = true;
        RenderTranscript();
        SetBusy(true);

        _turn = new CancellationTokenSource();
        try
        {
            ChatMessage reply = await _service.SendAsync(
                _current, userMessage,
                onDelta: piece => Dispatcher.UIThread.Post(() => { _streamed.Append(piece); _dirty = true; }),
                onTool: name => Dispatcher.UIThread.Post(() => AddToolChip(name)),
                _turn.Token);

            reply.ToolCalls.AddRange(_turnTools);
            _current.Messages.Add(reply);
            _store.Save(_current);
            _live = false;
            RenderTranscript();
            Report(reply.ToolCalls.Count > 0
                ? $"Answered using {reply.ToolCalls.Count} function call(s)."
                : "Answered.");
        }
        catch (OperationCanceledException)
        {
            FinishPartial(live, "_(stopped)_");
        }
        catch (Exception ex)
        {
            FinishPartial(live, "**That turn failed.**\n\n" + ex.Message, isError: true);
            Report("Turn failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
            _turn?.Dispose();
            _turn = null;
            if (_current != null)
            {
                RefreshSessionRow(_current);
            }
        }
    }

    /// <summary>
    /// Keep whatever streamed before the failure — a half answer with the error
    /// under it is more use than an empty bubble.
    /// </summary>
    private void FinishPartial(ChatMessage live, string note, bool isError = false)
    {
        live.Text = _streamed.Length > 0 ? _streamed + "\n\n" + note : note;
        live.IsError = isError;
        live.ToolCalls.AddRange(_turnTools);
        if (_current != null)
        {
            _current.Messages.Add(live);
            _store.Save(_current);
        }
        _live = false;
        RenderTranscript();
    }

    private void AddToolChip(string name)
    {
        // The same call can be reported by both the SK filter and the
        // IChatClient tap; only the first sighting in a row is shown.
        if (_turnTools.Count > 0 && _turnTools[^1] == name)
        {
            return;
        }
        _turnTools.Add(name);
        _dirty = true;
        Report("Calling " + name + "...");
    }

    private void FlushStream()
    {
        if (!_dirty)
        {
            return;
        }
        _dirty = false;
        RenderTranscript();
    }

    private void SetBusy(bool busy)
    {
        SendButton.IsVisible = !busy;
        StopButton.IsVisible = busy;
        Composer.IsEnabled = !busy;
        SignalMark.Background = Brush(busy ? "Cyan" : "CyanDim");
        if (busy)
        {
            _flush.Start();
        }
        else
        {
            _flush.Stop();
            FlushStream();
        }
    }

    // ---- attachments ---------------------------------------------------

    private void OnAttachImage(object? sender, RoutedEventArgs e) => _ = AttachAsync(
        new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] });

    private void OnAttachDocument(object? sender, RoutedEventArgs e) => _ = AttachAsync(
        new FilePickerFileType("Documents")
        {
            Patterns = ["*.md", "*.txt", "*.cs", "*.xml", "*.json", "*.csv", "*.log",
                        "*.yaml", "*.toml", "*.rs", "*.pdf"],
        });

    private async Task AttachAsync(FilePickerFileType kind)
    {
        if (_current == null || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Attach",
                AllowMultiple = true,
                FileTypeFilter = [kind, FilePickerFileTypes.All],
            });

        foreach (IStorageFile f in picked)
        {
            if (f.TryGetLocalPath() is not { } path)
            {
                continue;
            }
            try
            {
                _pending.Add(_attachments.Add(_current, path));
            }
            catch (Exception ex) when (Owner() is not null)
            {
                await Dialogs.Message(Owner()!, "Attach", ex.Message);
            }
        }
        RenderPending();
    }

    private void RenderPending()
    {
        var chips = new List<Control>();
        foreach (ChatAttachment a in _pending)
        {
            var chip = new Border
            {
                Background = Brush("Sunken"),
                BorderBrush = Brush(a.Kind == AttachmentKind.Image ? "AmberDim" : "Rail"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(6, 2, 3, 2),
                Margin = new Thickness(0, 0, 4, 4),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new TextBlock
            {
                Text = $"{a.FileName}  {a.SizeBytes / 1024} KB",
                FontSize = 10,
                Foreground = Brush("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.Classes.Add("readout");
            row.Children.Add(label);

            var remove = new Button
            {
                Content = "x",
                FontSize = 9,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(4, 0, 0, 0),
                Tag = a,
            };
            remove.Classes.Add("ghost");
            ToolTip.SetTip(remove, "Remove this upload from the message");
            remove.Click += (s, _) =>
            {
                if (((Button)s!).Tag is ChatAttachment target)
                {
                    _pending.Remove(target);
                    TryDelete(target.StoredPath);
                    RenderPending();
                }
            };
            row.Children.Add(remove);
            chip.Child = row;
            chips.Add(chip);
        }
        AttachmentStrip.ItemsSource = chips;
    }

    private void ClearPending()
    {
        _pending.Clear();
        AttachmentStrip.ItemsSource = null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // An upload left behind is harmless; it goes with the session.
        }
    }

    // ---- pickers and settings ------------------------------------------

    private void LoadPickers()
    {
        _suppressPickers = true;
        var providers = new List<string>();
        foreach (AiProvider p in Enum.GetValues<AiProvider>())
        {
            providers.Add(p.ToString());
        }
        ProviderBox.ItemsSource = providers;
        ProviderBox.SelectedItem = _options.Provider.ToString();
        LoadModels();
        ToolsToggle.IsChecked = _options.ToolsEnabled;
        _suppressPickers = false;
    }

    private void LoadModels()
    {
        ModelBox.ItemsSource = new List<string>(_options.Current.Models);
        if (_options.Current.Model.Length > 0)
        {
            ModelBox.SelectedItem = _options.Current.Model;
        }
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressPickers || ProviderBox.SelectedItem is not string name)
        {
            return;
        }
        _options.Provider = AssistantOptions.ParseProvider(name, _options.Provider);
        _suppressPickers = true;
        LoadModels();
        _suppressPickers = false;
        _service?.Invalidate();
        _options.Save();
        LoadSettingsFields();
        Report($"Provider is {_options.Provider}.");
    }

    private void OnModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressPickers || ModelBox.SelectedItem is not string model)
        {
            return;
        }
        _options.Current.Model = model;
        _service?.Invalidate();
        _options.Save();
        Report("Model is " + model + ".");
    }

    private void OnToolsToggled(object? sender, RoutedEventArgs e)
    {
        if (_suppressPickers || _options == null)
        {
            return;
        }
        _options.ToolsEnabled = ToolsToggle.IsChecked == true;
        _service?.Invalidate();
        _options.Save();
        LoadSettingsFields();
        Report(_options.ToolsEnabled ? "Functions enabled." : "Functions disabled.");
    }

    private void LoadSettingsFields()
    {
        PersonaBox.Text = _options.Persona;
        TemperatureBox.Text = _options.Temperature.ToString("0.##", CultureInfo.InvariantCulture);
        TopPBox.Text = _options.TopP.ToString("0.##", CultureInfo.InvariantCulture);
        MaxTokensBox.Text = _options.MaxTokens.ToString(CultureInfo.InvariantCulture);
        HistoryBox.Text = _options.HistoryTurns.ToString(CultureInfo.InvariantCulture);
        StreamingCheck.IsChecked = _options.Streaming;
        ToolListText.Text = _options.ToolsEnabled
            ? string.Join("  ", _service?.ToolNames ?? Array.Empty<string>())
            : "(functions are off)";
        KeyStatusText.Text = string.Join("\n", new[]
        {
            Line(AiProvider.OpenAI), Line(AiProvider.Anthropic), Line(AiProvider.Gemini), Line(AiProvider.Ollama),
            "tavily      " + (_options.TavilyApiKey.Length > 0 ? "set" : "not set - search is unavailable"),
        });

        string Line(AiProvider p) =>
            $"{_options.For(p).Name,-11} {(_options.IsProviderConfigured(p) ? "ready" : "not configured")}";
    }

    private void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        _options.Persona = PersonaBox.Text ?? "";
        if (double.TryParse(TemperatureBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
        {
            _options.Temperature = Math.Clamp(t, 0, 2);
        }
        if (double.TryParse(TopPBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
        {
            _options.TopP = Math.Clamp(p, 0, 1);
        }
        if (int.TryParse(MaxTokensBox.Text, out int max))
        {
            _options.MaxTokens = Math.Clamp(max, 256, 200_000);
        }
        if (int.TryParse(HistoryBox.Text, out int turns))
        {
            _options.HistoryTurns = Math.Max(0, turns);
        }
        _options.Streaming = StreamingCheck.IsChecked == true;
        _options.Save();
        _service.Invalidate();
        LoadSettingsFields();
        Report("Settings saved to app.config.");
    }

    private void OnResetPersona(object? sender, RoutedEventArgs e)
        => PersonaBox.Text = PromptLibrary.DefaultPersona;

    // ---- drawers -------------------------------------------------------

    private bool _promptsOpen;

    /// <summary>
    /// Show at most one drawer.
    /// </summary>
    /// <remarks>
    /// The WPF version also had to *hide* the transcript while a drawer was
    /// open: WebView2 is a native child window, and nothing can be drawn over
    /// one. A Markdown.Avalonia transcript is an ordinary control, so a drawer
    /// simply covers it — the panel keeps its place underneath.
    /// </remarks>
    private void SyncDrawers()
    {
        Border? open =
            _promptsOpen ? PromptDrawer
            : SettingsToggle.IsChecked == true ? SettingsDrawer
            : SessionsToggle.IsChecked == true ? SessionsDrawer
            : null;

        SessionsDrawer.IsVisible = ReferenceEquals(SessionsDrawer, open);
        SettingsDrawer.IsVisible = ReferenceEquals(SettingsDrawer, open);
        PromptDrawer.IsVisible = ReferenceEquals(PromptDrawer, open);
    }

    private void OnSessionsToggled(object? sender, RoutedEventArgs e)
    {
        if (SessionsToggle.IsChecked == true)
        {
            SettingsToggle.IsChecked = false;
            _promptsOpen = false;
        }
        SyncDrawers();
    }

    private void OnSettingsToggled(object? sender, RoutedEventArgs e)
    {
        if (SettingsToggle.IsChecked == true)
        {
            SessionsToggle.IsChecked = false;
            _promptsOpen = false;
            LoadSettingsFields();
        }
        SyncDrawers();
    }

    private void OnShowPrompts(object? sender, RoutedEventArgs e)
    {
        _promptsOpen = true;
        SessionsToggle.IsChecked = false;
        SettingsToggle.IsChecked = false;
        SyncDrawers();
    }

    private void OnClosePrompts(object? sender, RoutedEventArgs e)
    {
        _promptsOpen = false;
        SyncDrawers();
    }

    private void OnHide(object? sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private void BuildPromptGallery()
    {
        string? lastCategory = null;
        foreach (PromptTemplate template in PromptLibrary.All)
        {
            if (template.Category != lastCategory)
            {
                lastCategory = template.Category;
                var heading = new TextBlock
                {
                    Text = template.Category.ToUpperInvariant(),
                    Margin = new Thickness(10, 12, 10, 4),
                };
                heading.Classes.Add("heading");
                PromptList.Children.Add(heading);
            }
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(6, 0, 6, 1),
                Padding = new Thickness(6, 4, 6, 4),
                Foreground = Brush("Ink"),
                Content = new TextBlock { Text = template.Title, TextWrapping = TextWrapping.Wrap },
                Tag = template,
            };
            button.Classes.Add("ghost");
            ToolTip.SetTip(button, new TextBlock
            {
                Text = template.Text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            });
            button.Click += (s, _) =>
            {
                if (((Button)s!).Tag is PromptTemplate chosen)
                {
                    Composer.Text = chosen.Text;
                    Composer.CaretIndex = Composer.Text.Length;
                    _promptsOpen = false;
                    SyncDrawers();
                    Composer.Focus();
                }
            };
            PromptList.Children.Add(button);
        }
    }

    // ---- misc ----------------------------------------------------------

    /// <summary>Put the caret in the composer — what Ctrl+J should do.</summary>
    public void FocusComposer() => Composer.Focus();

    /// <summary>
    /// Drop text into the composer without sending it, so the person can edit
    /// the request first. Used by "Send to assistant" in the code pane.
    /// </summary>
    public void Compose(string text)
    {
        Composer.Text = text;
        Composer.CaretIndex = Composer.Text.Length;
        _promptsOpen = false;
        SessionsToggle.IsChecked = false;
        SettingsToggle.IsChecked = false;
        SyncDrawers();
        Composer.Focus();
    }

    private Window? Owner() => TopLevel.GetTopLevel(this) as Window;

    private IBrush Brush(string key)
        => this.TryFindResource(key, out object? value) && value is IBrush brush ? brush : Brushes.Gray;

    private void Report(string message) => Status?.Invoke(this, message);
}
