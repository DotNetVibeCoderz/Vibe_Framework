using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using RustzorAppGen.Services;
using RustzorAppGen.Views;

namespace RustzorAppGen;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = AppSettings.Load();
        ApplyTheme(Settings.Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // RustzorAppGen [folder-or-file] [--ask "prompt"] [--model "Provider|model"]
            var args = desktop.Args ?? Array.Empty<string>();
            string? path = null, ask = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--ask" && i + 1 < args.Length) ask = args[++i];
                else if (args[i] == "--model" && i + 1 < args.Length) Settings.ActiveModel = args[++i];
                else path ??= args[i];
            }
            desktop.MainWindow = new MainWindow(Settings, path, ask);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is not null)
            Current.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
    }
}
