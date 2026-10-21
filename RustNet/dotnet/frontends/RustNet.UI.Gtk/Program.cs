using Gtk;
using RustNet.UI.Gtk;

// RustNet.UI.Gtk layout.xml [--zoom N]
if (args.Length == 0 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: RustNet.UI.Gtk <layout.xml> [--zoom N]");
    return 2;
}
int zoom = 2;
int z = Array.IndexOf(args, "--zoom");
if (z >= 0 && z + 1 < args.Length)
{
    int.TryParse(args[z + 1], out zoom);
}
Application.Init();
var window = new UiWindow(File.ReadAllText(args[0]), zoom, Path.GetFileName(args[0]) + " — RustNet.UI");
window.ShowAll();
Application.Run();
return 0;
