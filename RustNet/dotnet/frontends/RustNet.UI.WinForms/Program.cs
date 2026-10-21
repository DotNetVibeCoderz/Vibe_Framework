namespace RustNet.UI.WinForms;

internal static class Program
{
    /// <summary>RustNet.UI.WinForms.exe layout.xml [--zoom N]</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            MessageBox.Show("usage: RustNet.UI.WinForms layout.xml [--zoom N]", "RustNet.UI");
            return 2;
        }
        int zoom = 2;
        int z = Array.IndexOf(args, "--zoom");
        if (z >= 0 && z + 1 < args.Length)
        {
            int.TryParse(args[z + 1], out zoom);
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new UiForm(File.ReadAllText(args[0]), zoom) { Text = Path.GetFileName(args[0]) + " — RustNet.UI" });
        return 0;
    }
}
