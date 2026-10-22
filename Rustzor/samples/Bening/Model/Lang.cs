using Rustzor.Browser.Storage;

namespace Bening.Model;

/// <summary>Indonesian first, English on a toggle; the choice is kept in this browser.</summary>
public static class L
{
    public static bool English { get; private set; }

    public static event Action? Changed;

    public static string T(string id, string en) => English ? en : id;

    public static void Load()
    {
        try { English = WebStorage.Local.Get("bening.lang") == "en"; }
        catch { English = false; }
    }

    public static void Set(bool english)
    {
        if (English == english) return;
        English = english;
        WebStorage.Local.Set("bening.lang", english ? "en" : "id");
        Changed?.Invoke();
    }
}
