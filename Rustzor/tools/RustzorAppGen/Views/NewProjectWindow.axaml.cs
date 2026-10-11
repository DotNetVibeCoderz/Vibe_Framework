using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RustzorAppGen.Services;

namespace RustzorAppGen.Views;

public partial class NewProjectWindow : Window
{
    private readonly TemplateService _templates;
    private readonly AppSettings _settings;
    private readonly List<ProjectTemplate> _all;
    private ProjectTemplate? _picked;
    private Border? _pickedCard;

    public NewProjectWindow() : this(new TemplateService(AppSettings.Load()), AppSettings.Load()) { }

    public NewProjectWindow(TemplateService templates, AppSettings settings)
    {
        _templates = templates;
        _settings = settings;
        InitializeComponent();
        _all = templates.All();
        var categories = new List<string> { "All" };
        categories.AddRange(_all.Where(t => t.Id != "blank").Select(t => t.Category).Distinct().OrderBy(c => c));
        Categories.ItemsSource = categories;
        Categories.SelectedIndex = 0;
        LocationBox.Text = settings.ProjectsFolder;
        Note.Text = settings.RustzorReference == "NuGet"
            ? $"The project will reference Rustzor {settings.RustzorVersion} packages from NuGet."
            : string.IsNullOrWhiteSpace(settings.RustzorRoot)
                ? "Set the Rustzor source folder in Settings → Rustzor first, or the project will not build."
                : $"The project will build against the Rustzor source at {settings.RustzorRoot}.";
        Opened += (_, _) => NameBox.Focus();
    }

    /// <summary>What to ask Jack once the project is open.</summary>
    public string? FirstPrompt { get; private set; }

    private void OnKindChanged(object? sender, RoutedEventArgs e)
    {
        var blank = BlankChoice.IsChecked == true;
        Categories.IsEnabled = !blank;
        Gallery.Opacity = blank ? 0.4 : 1;
        Gallery.IsHitTestVisible = !blank;
    }

    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        var category = Categories.SelectedItem as string ?? "All";
        Gallery.ItemsSource = _all.Where(t => t.Id != "blank" && (category == "All" || t.Category == category)).ToList();
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border card || card.Tag is not ProjectTemplate t) return;
        _pickedCard?.Classes.Remove("picked");
        card.Classes.Add("picked");
        _pickedCard = card;
        _picked = t;
        TemplateChoice.IsChecked = true;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Watermark = t.Name;
    }

    private void OnCardDoubleTapped(object? sender, TappedEventArgs e) => OnCreate(sender, new RoutedEventArgs());

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where to create the project", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } p) LocationBox.Text = p;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        var template = BlankChoice.IsChecked == true ? _templates.Find("blank") : _picked;
        if (template is null)
        {
            Note.Text = "Pick a template, or choose Blank app.";
            return;
        }
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? (BlankChoice.IsChecked == true ? "My Rustzor App" : template.Name) : NameBox.Text.Trim();
        var location = string.IsNullOrWhiteSpace(LocationBox.Text) ? _settings.ProjectsFolder : LocationBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(location);
            var folder = _templates.Create(template, name, location);
            _settings.ProjectsFolder = location;
            FirstPrompt = PromptBox.Text?.Trim();
            Close(folder);
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync(this, "Could not create the project", ex.Message);
        }
    }
}
