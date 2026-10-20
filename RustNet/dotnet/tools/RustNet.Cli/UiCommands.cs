using RustNet.UI;
using RustNet.UI.AsciiConsole;

namespace RustNet.Cli;

/// <summary>
/// <c>rustnet ui preview layout.xml</c>: show a RustNet.UI layout in the
/// terminal through the AsciiConsole frontend — the device's own layout code,
/// rendered as text. <c>--interactive</c> drives it from the keyboard.
/// </summary>
internal static class UiCommands
{
    public static int Dispatch(string[] args)
    {
        if (args.ElementAtOrDefault(0) != "preview" || args.Length < 2)
        {
            Console.Error.WriteLine("usage: rustnet ui preview <layout.xml> [--width W] [--height H] [--ascii] [--interactive]");
            return 2;
        }
        string path = args[1];
        UiElement root = Ui.LoadXml(File.ReadAllText(path));
        if (int.TryParse(Cli.Opt(args, "--width"), out int w))
        {
            root.Width = w;
        }
        if (int.TryParse(Cli.Opt(args, "--height"), out int h))
        {
            root.Height = h;
        }
        var renderer = new AsciiRenderer { Unicode = !Cli.Flag(args, "--ascii") };
        if (Cli.Flag(args, "--interactive"))
        {
            new AsciiConsoleHost(root, renderer).Run();
            return 0;
        }
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Write(renderer.Render(root));
        return 0;
    }
}
