using Rustzor.AI;
using Rustzor.Browser.Storage;

namespace {{Namespace}};

public sealed class AiSettings
{
    public string Provider { get; set; } = "demo";
    public string Model { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public double Temperature { get; set; } = 0.7;
    public string Persona { get; set; } = "assistant";
}

public sealed record Persona(string Id, string Name, string Prompt);

public sealed record Provider(string Id, string Name, string DefaultModel, bool NeedsKey, bool NeedsEndpoint, string Help);

/// <summary>Settings, the model they describe, and the saved conversation.</summary>
public static class Assistant
{
    public static readonly Provider[] Providers =
    {
        new("demo", "Offline demo", "demo", false, false, "Answers from a script so the app works with no network. Pick a real provider to chat."),
        new("openai", "OpenAI", "gpt-4o-mini", true, false, "Key from platform.openai.com."),
        new("azure", "Azure OpenAI", "your-deployment", true, true, "Endpoint like https://your-resource.openai.azure.com; model is the deployment name."),
        new("claude", "Claude", "claude-sonnet-4-5", true, false, "Key from console.anthropic.com. Browser calls need the key's CORS access enabled."),
        new("ollama", "Ollama (local)", "llama3.2", false, true, "Runs on your machine. Start Ollama with OLLAMA_ORIGINS set to this site's origin."),
        new("builtin", "Chrome built-in", "chrome-built-in", false, false, "Gemini Nano inside Chrome, where available. No key, no network."),
    };

    public static readonly Persona[] Personas =
    {
        new("assistant", "Helpful assistant", "You are a concise, friendly assistant. Answer directly and use Markdown when it helps."),
        new("reviewer", "Code reviewer", "You review code. Point out bugs first, then risks, then style. Quote the lines you mean."),
        new("translator", "Translator (EN ⇄ ID)", "Translate between English and Bahasa Indonesia. Detect the language of each message and answer only with the translation."),
        new("tutor", "Patient tutor", "You are a tutor. Explain step by step, check understanding with one short question at the end."),
    };

    public static AiSettings Settings { get; private set; } = WebStorage.Local.GetObject<AiSettings>("ai-settings") ?? new AiSettings();

    public static void Save(AiSettings s)
    {
        Settings = s;
        WebStorage.Local.SetJson("ai-settings", s);
    }

    public static Provider CurrentProvider => Providers.FirstOrDefault(p => p.Id == Settings.Provider) ?? Providers[0];

    public static Persona CurrentPersona => Personas.FirstOrDefault(p => p.Id == Settings.Persona) ?? Personas[0];

    public static IChatModel CreateModel()
    {
        var s = Settings;
        var model = s.Model.Trim().Length > 0 ? s.Model.Trim() : CurrentProvider.DefaultModel;
        return s.Provider switch
        {
            "openai" => OpenAiChat.OpenAI(s.ApiKey, model),
            "azure" => OpenAiChat.Azure(s.Endpoint, s.ApiKey, model),
            "claude" => new ClaudeChat(s.ApiKey, model),
            "ollama" => OpenAiChat.Ollama(model, s.Endpoint.Trim().Length > 0 ? s.Endpoint : "http://localhost:11434/v1"),
            "builtin" => new BuiltInChat(),
            _ => new DemoChat(),
        };
    }

    // ChatMessage is a positional record; the saved form is a plain class.
    public static List<ChatMessage> LoadHistory() =>
        WebStorage.Local.GetList<Turn>("ai-history").Select(t => new ChatMessage(t.Role, t.Content)).ToList();

    public static void SaveHistory(IEnumerable<ChatMessage> messages) =>
        WebStorage.Local.SetJson("ai-history", messages.Where(m => m.Role != "system").Select(m => new Turn { Role = m.Role, Content = m.Content }).ToList());
}

public sealed class Turn
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>A stand-in model: streams a scripted answer word by word.</summary>
public sealed class DemoChat : IChatModel
{
    public string Model => "demo";

    public Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null) =>
        Task.FromResult(new ChatReply(Answer(messages)));

    public async Task<string> StreamAsync(IReadOnlyList<ChatMessage> messages, Action<string> onText, ChatOptions? options = null)
    {
        var text = Answer(messages);
        foreach (var word in text.Split(' '))
        {
            onText(word + " ");
            await Task.Delay(18);
        }
        return text;
    }

    private static string Answer(IReadOnlyList<ChatMessage> messages)
    {
        var question = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        return $"""
            This is the **offline demo model**, so I can't really answer *"{(question.Length > 60 ? question[..60] + "…" : question)}"*.

            To chat for real:

            1. Open **Settings**.
            2. Pick OpenAI, Azure OpenAI, Claude, Ollama or Chrome's built-in model.
            3. Paste a key if the provider needs one, and come back here.

            Replies stream in as they are written, and render Markdown, `code` included.
            """;
    }
}
