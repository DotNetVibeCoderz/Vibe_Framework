using Avalonia;
using RustNet.Designer.Assistant;

namespace RustNet.Designer.Avalonia;

/// <summary>
/// Entry point, and the headless commands that never open a window.
/// </summary>
/// <remarks>
/// The headless paths are answered before Avalonia starts, not from inside
/// the application lifetime as the WPF version did from <c>OnStartup</c>.
/// That is the difference that makes them usable where it matters: starting a
/// desktop toolkit needs a display, so a build server running
/// <c>--selftest</c> over SSH would fail before reaching the check. Nothing
/// below <c>--ask</c> or <c>--selftest</c> touches a window.
/// </remarks>
internal static class Program
{
    [System.STAThread]
    public static int Main(string[] args)
    {
        if (Has(args, "--selftest"))
        {
            return AssistantSelfTest.Run(System.Console.Out) ? 0 : 1;
        }
        if (Has(args, "--ask"))
        {
            return HeadlessAsk.Run(args, System.Console.Out);
        }
        if (Has(args, "--export"))
        {
            return Export(args);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Round-trip a layout through the model's own load/save.</summary>
    private static int Export(string[] args)
    {
        int i = System.Array.IndexOf(args, "--export");
        string? outPath = i + 1 < args.Length ? args[i + 1] : null;
        if (outPath is null)
        {
            System.Console.Error.WriteLine("usage: --export <out.xml> [in.xml]");
            return 2;
        }
        string? inPath = i + 2 < args.Length ? args[i + 2] : null;
        string src = inPath is not null && System.IO.File.Exists(inPath)
            ? System.IO.File.ReadAllText(inPath)
            : SampleLayout.Xml;
        System.IO.File.WriteAllText(outPath, UI.Ui.ToXml(UI.Ui.LoadXml(src)));
        System.Console.WriteLine("exported " + outPath);
        return 0;
    }

    private static bool Has(string[] args, string flag) =>
        System.Array.IndexOf(args, flag) >= 0;

    // Referenced by the Avalonia XAML previewer as well as by Main.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
