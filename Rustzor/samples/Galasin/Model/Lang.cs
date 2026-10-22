using Rustzor.Browser.Storage;

namespace Galasin.Model;

/// <summary>Indonesian first, English on a toggle; the choice is kept in this browser.</summary>
public static class L
{
    public static bool English { get; private set; }

    public static event Action? Changed;

    public static string T(string id, string en) => English ? en : id;

    public static void Load()
    {
        try { English = WebStorage.Local.Get("galasin.lang") == "en"; }
        catch { English = false; }
    }

    public static void Set(bool english)
    {
        English = english;
        WebStorage.Local.Set("galasin.lang", english ? "en" : "id");
        Changed?.Invoke();
    }

    public static string Team(char team) => team == 'm' ? T("Merah", "Red") : T("Putih", "White");
}
