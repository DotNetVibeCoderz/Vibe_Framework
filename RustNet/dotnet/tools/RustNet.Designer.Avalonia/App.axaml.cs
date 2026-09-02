using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RustNet.Designer.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var win = new MainWindow();
            string[] args = desktop.Args ?? [];
            if (args.Length >= 1 && System.IO.File.Exists(args[0]))
            {
                win.OpenFile(args[0]);
            }
            desktop.MainWindow = win;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
