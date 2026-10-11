using Avalonia;

namespace RustzorAppGen;

/// <summary>Rustzor App Generator. Built by Gravicode Studios, led by Kang Fadhil.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // RustzorAppGen --new <templateId> <name> <parentFolder>: create a project and exit (scripts, CI).
        if (args.Length >= 4 && args[0] == "--new") return CreateProject(args[1], args[2], args[3]);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int CreateProject(string templateId, string name, string parent)
    {
        var templates = new Services.TemplateService(Services.AppSettings.Load());
        var template = templates.Find(templateId);
        if (template is null)
        {
            Console.Error.WriteLine($"No template '{templateId}'. Available: {string.Join(", ", templates.All().Select(t => t.Id))}");
            return 1;
        }
        try
        {
            Console.WriteLine(templates.Create(template, name, parent));
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
