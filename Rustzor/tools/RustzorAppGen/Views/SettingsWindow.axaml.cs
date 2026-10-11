using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RustzorAppGen.Services;

namespace RustzorAppGen.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AssistantService? _assistant;
    // Edits are kept per provider until Save, so switching providers does not lose them.
    private readonly Dictionary<string, ProviderSettings> _draft = new();
    private string? _current;

    public SettingsWindow() : this(AppSettings.Load(), null) { }

    public SettingsWindow(AppSettings settings, AssistantService? assistant)
    {
        _settings = settings;
        _assistant = assistant;
        InitializeComponent();
        ConfigPath.Text = $"Saved to {settings.ConfigPath}";
        foreach (var p in settings.Providers.Values)
            _draft[p.Name] = new ProviderSettings { Name = p.Name, Models = p.Models.ToList(), ApiKey = p.ApiKey, Endpoint = p.Endpoint, WorkspaceId = p.WorkspaceId };
        ProviderBox.ItemsSource = AppSettings.ProviderNames;
        ProviderBox.SelectedItem = settings.Resolve(settings.ActiveModel).Provider.Name;

        TemperatureBox.Text = settings.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "";
        MaxTokensBox.Value = settings.MaxOutputTokens;
        PromptBox.Text = string.IsNullOrWhiteSpace(settings.SystemPrompt) ? "" : settings.SystemPrompt;
        PromptBox.Watermark = "Jack's default prompt is used.";
        TavilyBox.Text = settings.TavilyApiKey;
        ToolList.ItemsSource = assistant?.ToolNames.ToList() ?? new List<string>();
        SourceRef.IsChecked = settings.RustzorReference != "NuGet";
        NuGetRef.IsChecked = settings.RustzorReference == "NuGet";
        RootBox.Text = settings.RustzorRoot;
        VersionBox.Text = settings.RustzorVersion;
        ProjectsBox.Text = settings.ProjectsFolder;
        DeployBox.Text = settings.DeployFolder;
        PortBox.Value = settings.ServePort;
        DarkTheme.IsChecked = settings.Theme != "Light";
        LightTheme.IsChecked = settings.Theme == "Light";
        FontSizeBox.Value = (decimal)settings.EditorFontSize;
        LineNumbersBox.IsChecked = settings.ShowLineNumbers;
        WrapBox.IsChecked = settings.WordWrap;
    }

    private void StoreCurrent()
    {
        if (_current is null || !_draft.TryGetValue(_current, out var p)) return;
        p.Models = (ModelsBox.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        p.ApiKey = KeyBox.Text?.Trim() ?? "";
        p.Endpoint = EndpointBox.Text?.Trim() ?? "";
        p.WorkspaceId = WorkspaceBox.Text?.Trim() ?? "";
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        StoreCurrent();
        _current = ProviderBox.SelectedItem as string;
        if (_current is null) return;
        var p = _draft[_current];
        ModelsBox.Text = string.Join(", ", p.Models);
        KeyBox.Text = p.ApiKey;
        EndpointBox.Text = p.Endpoint;
        WorkspaceBox.Text = p.WorkspaceId;
        WorkspacePanel.IsVisible = _current == "Claude";
        EndpointHelp.Text = _current switch
        {
            "AzureOpenAI" => "Your resource, e.g. https://my-resource.openai.azure.com/ (the v1 path is added for you).",
            "Claude" => "Normally https://api.anthropic.com.",
            "Gemini" => "Google's OpenAI-compatible endpoint.",
            "Ollama" => "A local Ollama server's OpenAI-compatible endpoint. No key needed.",
            _ => "Change it for any OpenAI-compatible server (DeepSeek, Groq, LM Studio, vLLM…).",
        };
        TestResult.Text = "";
    }

    private void OnShowKey(object? sender, RoutedEventArgs e) =>
        KeyBox.RevealPassword = (sender as CheckBox)?.IsChecked == true;

    private void OnResetPrompt(object? sender, RoutedEventArgs e) => PromptBox.Text = RustzorKnowledge.DefaultSystemPrompt;

    private async void OnTest(object? sender, RoutedEventArgs e)
    {
        StoreCurrent();
        if (_assistant is null || _current is null) return;
        var p = _draft[_current];
        if (p.Models.Count == 0)
        {
            TestResult.Text = "Add at least one model.";
            return;
        }
        // Test with the edited values, then restore what was saved.
        var saved = _settings.Providers[_current];
        var activeBefore = _settings.ActiveModel;
        _settings.Providers[_current] = p;
        _settings.ActiveModel = $"{p.Name}|{p.Models[0]}";
        TestButton.IsEnabled = false;
        TestResult.Text = "Testing…";
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var reply = await Task.Run(() => _assistant.PingAsync(cancel.Token));
            TestResult.Text = $"Connected to {p.Models[0]}: “{reply}”";
            TestResult.Foreground = (Avalonia.Media.IBrush?)this.FindResource("Patina");
        }
        catch (Exception ex)
        {
            TestResult.Text = ex.InnerException?.Message ?? ex.Message;
            TestResult.Foreground = (Avalonia.Media.IBrush?)this.FindResource("Danger");
        }
        finally
        {
            _settings.Providers[_current] = saved;
            _settings.ActiveModel = activeBefore;
            TestButton.IsEnabled = true;
        }
    }

    private async Task<string?> PickFolder(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void OnBrowseRoot(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Rustzor source folder") is { } p) RootBox.Text = p;
    }

    private async void OnBrowseProjects(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Projects folder") is { } p) ProjectsBox.Text = p;
    }

    private async void OnBrowseDeploy(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Deploy folder") is { } p) DeployBox.Text = p;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        StoreCurrent();
        var temperatureText = TemperatureBox.Text?.Trim() ?? "";
        double? temperature = null;
        if (temperatureText.Length > 0)
        {
            if (!double.TryParse(temperatureText, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) || t < 0 || t > 2)
            {
                await Dialogs.MessageAsync(this, "Check the temperature", "Use a number from 0 to 2, or leave it empty.");
                return;
            }
            temperature = t;
        }
        var root = RootBox.Text?.Trim() ?? "";
        if (SourceRef.IsChecked == true && root.Length > 0 && !File.Exists(Path.Combine(root, "Rustzor.slnx")))
        {
            await Dialogs.MessageAsync(this, "Check the Rustzor folder", $"{root} has no Rustzor.slnx. Pick the folder of your Rustzor checkout.");
            return;
        }
        foreach (var p in _draft.Values) _settings.Providers[p.Name] = p;
        _settings.Temperature = temperature;
        _settings.MaxOutputTokens = (int)(MaxTokensBox.Value ?? 8000);
        _settings.SystemPrompt = PromptBox.Text?.Trim() == RustzorKnowledge.DefaultSystemPrompt.Trim() ? "" : PromptBox.Text?.Trim() ?? "";
        _settings.TavilyApiKey = TavilyBox.Text?.Trim() ?? "";
        _settings.RustzorReference = NuGetRef.IsChecked == true ? "NuGet" : "Source";
        _settings.RustzorRoot = root;
        _settings.RustzorVersion = string.IsNullOrWhiteSpace(VersionBox.Text) ? "0.1.0" : VersionBox.Text.Trim();
        _settings.ProjectsFolder = ProjectsBox.Text?.Trim() ?? _settings.ProjectsFolder;
        _settings.DeployFolder = DeployBox.Text?.Trim() ?? "";
        _settings.ServePort = (int)(PortBox.Value ?? 5170);
        _settings.Theme = LightTheme.IsChecked == true ? "Light" : "Dark";
        _settings.EditorFontSize = (double)(FontSizeBox.Value ?? 14);
        _settings.ShowLineNumbers = LineNumbersBox.IsChecked == true;
        _settings.WordWrap = WrapBox.IsChecked == true;
        Close(true);
    }
}
