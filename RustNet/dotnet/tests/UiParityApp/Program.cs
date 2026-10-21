using RustNet.Resources;
using RustNet.UI;

namespace UiParityApp;

public static class Program
{
    public static void Main()
    {
        UiElement root = Ui.LoadXml(Resource.GetString("parity.xml"));
        Ui.Render(root);
        Console.WriteLine("ui parity rendered");
    }
}
