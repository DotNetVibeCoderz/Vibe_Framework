using Rustzor.Browser.AI;
using Rustzor.Interop;

namespace Rustzor.AI;

/// <summary>
/// Inference inside the browser, with no server and no key: transformers.js
/// models (downloaded once, then cached) for embeddings, classification and
/// small text generation, plus Chrome's built-in model where available.
/// </summary>
public static class LocalAi
{
    /// <summary>Sentence embeddings, e.g. "Xenova/all-MiniLM-L6-v2" (384 dimensions, about 25 MB).</summary>
    public static async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts, string model = "Xenova/all-MiniLM-L6-v2")
    {
        var r = await Js.CallAsync("ai.local.embed", Js.Args("model", model, "texts", texts));
        return r.Items.Select(v => v.Items.Select(x => (float)x.AsDouble()).ToArray()).ToList();
    }

    /// <summary>Labels with scores, e.g. sentiment with "Xenova/distilbert-base-uncased-finetuned-sst-2-english".</summary>
    public static async Task<List<(string Label, double Score)>> ClassifyAsync(string text, string model = "Xenova/distilbert-base-uncased-finetuned-sst-2-english")
    {
        var r = await Js.CallAsync("ai.local.classify", Js.Args("model", model, "text", text));
        return r.Items.Select(x => (x["label"].AsString(), x["score"].AsDouble())).ToList();
    }

    /// <summary>Text generation with a small model, e.g. "Xenova/Qwen1.5-0.5B-Chat".</summary>
    public static async Task<string> GenerateAsync(string prompt, string model = "onnx-community/Qwen2.5-0.5B-Instruct", int maxNewTokens = 128) =>
        (await Js.CallAsync("ai.local.generate", Js.Args("model", model, "prompt", prompt, "max", maxNewTokens))).AsString();

    /// <summary>Whether Chrome's built-in language model can run here.</summary>
    public static async Task<bool> BuiltInAvailableAsync()
    {
        try
        {
            return await BuiltInAi.AvailabilityAsync() is "available" or "downloadable" or "readily";
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Local embeddings as an <see cref="IEmbedder"/>, so a RAG pipeline can run entirely offline.</summary>
public sealed class LocalEmbedder(string model = "Xenova/all-MiniLM-L6-v2") : IEmbedder
{
    public Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts) => LocalAi.EmbedAsync(texts, model);
}

/// <summary>Chrome's built-in model as an <see cref="IChatModel"/>.</summary>
public sealed class BuiltInChat : IChatModel
{
    public string Model => "chrome-built-in";

    public async Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null)
    {
        using var session = await BuiltInAi.CreateSessionAsync(SystemOf(messages), options?.Temperature ?? 0.7);
        return new ChatReply(await session.PromptAsync(Transcript(messages)));
    }

    public async Task<string> StreamAsync(IReadOnlyList<ChatMessage> messages, Action<string> onText, ChatOptions? options = null)
    {
        using var session = await BuiltInAi.CreateSessionAsync(SystemOf(messages), options?.Temperature ?? 0.7);
        var all = new System.Text.StringBuilder();
        await session.PromptStreamingAsync(Transcript(messages), piece =>
        {
            all.Append(piece);
            onText(piece);
        });
        return all.ToString();
    }

    private static string? SystemOf(IReadOnlyList<ChatMessage> m) =>
        m.FirstOrDefault(x => x.Role == "system")?.Content;

    private static string Transcript(IReadOnlyList<ChatMessage> m) =>
        string.Join("\n\n", m.Where(x => x.Role != "system").Select(x => $"{x.Role}: {x.Content}")) + "\n\nassistant:";
}
