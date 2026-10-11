using System.Configuration;
using System.Globalization;

namespace RustzorAppGen.Services;

/// <summary>One LLM provider's connection settings.</summary>
public sealed class ProviderSettings
{
    public required string Name { get; init; }
    public List<string> Models { get; set; } = new();
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "";
    /// <summary>Claude only: the workspace for keys not scoped to one.</summary>
    public string WorkspaceId { get; set; } = "";
    /// <summary>The key came from an environment variable; it is never written to app.config.</summary>
    public bool KeyFromEnvironment { get; set; }
    public bool IsConfigured => Name == "Ollama" || !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Settings kept in the app's <c>app.config</c> (RustzorAppGen.dll.config next
/// to the executable), read at start and written back on Save.
/// </summary>
public sealed class AppSettings
{
    public static readonly string[] ProviderNames = { "OpenAI", "AzureOpenAI", "Claude", "Gemini", "Ollama" };

    public Dictionary<string, ProviderSettings> Providers { get; } = new();
    public string ActiveModel { get; set; } = "";
    public double? Temperature { get; set; }
    public int MaxOutputTokens { get; set; } = 8000;
    public string SystemPrompt { get; set; } = "";
    public string TavilyApiKey { get; set; } = "";
    private string? TavilyFromEnvironment { get; set; }
    public string RustzorReference { get; set; } = "Source";
    public string RustzorRoot { get; set; } = "";
    public string RustzorVersion { get; set; } = "0.1.0";
    public string ProjectsFolder { get; set; } = "";
    public string DeployFolder { get; set; } = "";
    public int ServePort { get; set; } = 5170;
    public string Theme { get; set; } = "Dark";
    public double EditorFontSize { get; set; } = 14;
    public bool ShowLineNumbers { get; set; } = true;
    public bool WordWrap { get; set; }
    public double ChatPanelWidth { get; set; } = 420;
    public bool ChatPanelVisible { get; set; } = true;
    public double ExplorerWidth { get; set; } = 260;
    public bool LogsVisible { get; set; } = true;
    public List<string> RecentProjects { get; set; } = new();

    public string ConfigPath { get; private set; } = "";

    /// <summary>Every model of every provider, as "Provider|model", configured providers first.</summary>
    public IEnumerable<string> AllModels =>
        Providers.Values.OrderByDescending(p => p.IsConfigured)
            .SelectMany(p => p.Models.Select(m => $"{p.Name}|{m}"));

    public static AppSettings Load()
    {
        var config = Open();
        var s = new AppSettings { ConfigPath = config.FilePath };
        string Get(string key, string fallback = "") => config.AppSettings.Settings[key]?.Value ?? fallback;
        foreach (var name in ProviderNames)
        {
            s.Providers[name] = new ProviderSettings
            {
                Name = name,
                Models = Split(Get($"{name}.Models")),
                ApiKey = Get($"{name}.ApiKey"),
                Endpoint = Get($"{name}.Endpoint"),
                WorkspaceId = Get($"{name}.WorkspaceId"),
            };
            // RUSTZOR_APPGEN_<PROVIDER>_APIKEY / _ENDPOINT override the file, for CI and for keeping keys out of it.
            var envKey = Environment.GetEnvironmentVariable($"RUSTZOR_APPGEN_{name.ToUpperInvariant()}_APIKEY");
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                s.Providers[name].ApiKey = envKey;
                s.Providers[name].KeyFromEnvironment = true;
            }
            var envEndpoint = Environment.GetEnvironmentVariable($"RUSTZOR_APPGEN_{name.ToUpperInvariant()}_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(envEndpoint)) s.Providers[name].Endpoint = envEndpoint;
        }
        if (Environment.GetEnvironmentVariable("RUSTZOR_APPGEN_TAVILY_APIKEY") is { Length: > 0 } tavily) s.TavilyFromEnvironment = tavily;
        s.ActiveModel = Get("ActiveModel", "AzureOpenAI|gpt-5-mini");
        s.Temperature = double.TryParse(Get("Temperature"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : null;
        s.MaxOutputTokens = int.TryParse(Get("MaxOutputTokens"), out var mt) ? mt : 8000;
        s.SystemPrompt = Get("SystemPrompt");
        s.TavilyApiKey = s.TavilyFromEnvironment ?? Get("TavilyApiKey");
        s.RustzorReference = Get("RustzorReference", "Source");
        s.RustzorRoot = Get("RustzorRoot");
        s.RustzorVersion = Get("RustzorVersion", "0.1.0");
        s.ProjectsFolder = Get("ProjectsFolder");
        s.DeployFolder = Get("DeployFolder");
        s.ServePort = int.TryParse(Get("ServePort"), out var port) ? port : 5170;
        s.Theme = Get("Theme", "Dark");
        s.EditorFontSize = double.TryParse(Get("EditorFontSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fs) ? fs : 14;
        s.ShowLineNumbers = Get("ShowLineNumbers", "true") != "false";
        s.WordWrap = Get("WordWrap") == "true";
        s.ChatPanelWidth = double.TryParse(Get("ChatPanelWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cw) ? cw : 420;
        s.ChatPanelVisible = Get("ChatPanelVisible", "true") != "false";
        s.ExplorerWidth = double.TryParse(Get("ExplorerWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ew) ? ew : 260;
        s.LogsVisible = Get("LogsVisible", "true") != "false";
        s.RecentProjects = Split(Get("RecentProjects"), '|');
        if (string.IsNullOrWhiteSpace(s.RustzorRoot)) s.RustzorRoot = GuessRustzorRoot();
        if (string.IsNullOrWhiteSpace(s.ProjectsFolder))
            s.ProjectsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rustzor Projects");
        return s;
    }

    public void Save()
    {
        var config = Open();
        var app = config.AppSettings.Settings;
        void Set(string key, string value)
        {
            if (app[key] is null) app.Add(key, value);
            else app[key].Value = value;
        }
        foreach (var p in Providers.Values)
        {
            Set($"{p.Name}.Models", string.Join(",", p.Models));
            if (!p.KeyFromEnvironment) Set($"{p.Name}.ApiKey", p.ApiKey);
            Set($"{p.Name}.Endpoint", p.Endpoint);
            if (p.Name == "Claude") Set($"{p.Name}.WorkspaceId", p.WorkspaceId);
        }
        Set("ActiveModel", ActiveModel);
        Set("Temperature", Temperature?.ToString(CultureInfo.InvariantCulture) ?? "");
        Set("MaxOutputTokens", MaxOutputTokens.ToString(CultureInfo.InvariantCulture));
        Set("SystemPrompt", SystemPrompt);
        if (TavilyFromEnvironment is null) Set("TavilyApiKey", TavilyApiKey);
        Set("RustzorReference", RustzorReference);
        Set("RustzorRoot", RustzorRoot);
        Set("RustzorVersion", RustzorVersion);
        Set("ProjectsFolder", ProjectsFolder);
        Set("DeployFolder", DeployFolder);
        Set("ServePort", ServePort.ToString(CultureInfo.InvariantCulture));
        Set("Theme", Theme);
        Set("EditorFontSize", EditorFontSize.ToString(CultureInfo.InvariantCulture));
        Set("ShowLineNumbers", ShowLineNumbers ? "true" : "false");
        Set("WordWrap", WordWrap ? "true" : "false");
        Set("ChatPanelWidth", Math.Round(ChatPanelWidth).ToString(CultureInfo.InvariantCulture));
        Set("ChatPanelVisible", ChatPanelVisible ? "true" : "false");
        Set("ExplorerWidth", Math.Round(ExplorerWidth).ToString(CultureInfo.InvariantCulture));
        Set("LogsVisible", LogsVisible ? "true" : "false");
        Set("RecentProjects", string.Join("|", RecentProjects.Take(10)));
        config.Save(ConfigurationSaveMode.Modified);
        ConfigurationManager.RefreshSection("appSettings");
    }

    public void RememberProject(string folder)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, folder);
    }

    public (ProviderSettings Provider, string Model) Resolve(string providerAndModel)
    {
        var parts = providerAndModel.Split('|', 2);
        var provider = Providers.TryGetValue(parts[0], out var p) ? p : Providers["OpenAI"];
        var model = parts.Length > 1 ? parts[1] : provider.Models.FirstOrDefault() ?? "";
        return (provider, model);
    }

    private static Configuration Open() => ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);

    private static List<string> Split(string value, char separator = ',') =>
        value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The Rustzor repository this tool lives in, when run from a checkout.</summary>
    private static string GuessRustzorRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rustzor.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return "";
    }
}
