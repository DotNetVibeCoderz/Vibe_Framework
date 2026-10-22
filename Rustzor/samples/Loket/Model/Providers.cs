using Rustzor.AI;

namespace Loket.Model;

/// <summary>Who the assistant is: the system prompt, editable in the settings.</summary>
public static class Personas
{
    public const string Desk = """
You are the front-desk assistant of Kelurahan Kemuning Raya, the neighbourhood office of Kota Sentosa, Indonesia.
Residents ask about identity cards (KTP-el), family cards, birth certificates, letters of domicile and business, and opening hours.

- Reply in the resident's language: Indonesian when they write Indonesian, English when they write English.
- Before answering a question about a service, call search_documents and answer only from the passages it returns. Cite them as [1], [2] right after the sentence they support. If the documents do not say, say so and suggest asking at the counter.
- Never invent requirements, fees or deadlines. Every service at this office is free; say so when fees come up.
- Use calculator for any arithmetic and current_time for "today", "now" or whether the office is open.
- When a resident needs to come back, bring something, or wants a reminder, offer to write a note for the staff with create_note.
- Be brief and concrete: short paragraphs, lists for requirements, bold for what to bring.
""";

    public const string Concise = """
You are the front-desk assistant of Kelurahan Kemuning Raya. Answer in at most three sentences, in the resident's language, from search_documents only, citing [n]. Use calculator and current_time when needed.
""";

    public const string Guide = """
You are an English-speaking guide at Kelurahan Kemuning Raya for foreign residents and visitors. Always reply in English, explain Indonesian terms (KTP-el, Kartu Keluarga, RT/RW, KITAS) the first time you use them, answer from search_documents with citations [n], and use calculator and current_time when needed.
""";
}

/// <summary>The providers the settings offer, and the models they make.</summary>
public static class Providers
{
    public static readonly (string Id, string Name, string DefaultModel, string Hint)[] All =
    {
        ("demo", "Demo (offline)", "loket-demo", "Built in: works without a key or network, with the same tools."),
        ("azure", "Azure OpenAI", "gpt-5-mini", "Endpoint like https://<resource>.openai.azure.com; the model is the deployment name."),
        ("openai", "OpenAI", "gpt-4o-mini", "Uses api.openai.com."),
        ("anthropic", "Anthropic Claude", "claude-sonnet-4-5", "A key not scoped to a workspace needs the workspace id."),
        ("compatible", "OpenAI-compatible", "llama3.2", "Ollama, LM Studio, vLLM, DeepSeek, Groq: the base URL before /chat/completions."),
    };

    public static string NameOf(string id) => All.FirstOrDefault(p => p.Id == id).Name ?? id;

    public static string ModelOf(Settings s) =>
        string.IsNullOrWhiteSpace(s.Model) ? All.FirstOrDefault(p => p.Id == s.Provider).DefaultModel ?? "" : s.Model.Trim();

    /// <summary>Whether the provider takes images in a message.</summary>
    public static bool SeesImages(Settings s) => s.Provider is "openai" or "azure" or "anthropic";

    public static IToolChatModel Create(Settings s)
    {
        var model = ModelOf(s);
        var key = s.ApiKey.Trim();
        switch (s.Provider)
        {
            case "demo":
                return new DemoModel();
            case "openai":
                Require(key, "an OpenAI API key");
                return OpenAiChat.OpenAI(key, model);
            case "azure":
                Require(key, "an Azure OpenAI key");
                Require(s.Endpoint, "the Azure endpoint");
                return OpenAiChat.Azure(s.Endpoint.Trim(), key, model);
            case "anthropic":
                Require(key, "an Anthropic API key");
                return new ClaudeChat(key, model, workspaceId: string.IsNullOrWhiteSpace(s.WorkspaceId) ? null : s.WorkspaceId.Trim());
            default:
                Require(s.Endpoint, "the endpoint URL");
                return new OpenAiChat(s.Endpoint.Trim(), key.Length == 0 ? null : key, model);
        }
    }

    private static void Require(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Settings need {what}.");
    }

    public static double Cost(Settings s, int input, int output) =>
        s.Provider == "demo" ? 0 : (input * s.PriceIn + output * s.PriceOut) / 1_000_000.0;
}
