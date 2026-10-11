using Rustzor.Interop;

namespace Rustzor.Browser.AI;

/// <summary>
/// The AI built into the browser (Chrome built-in AI): a language model for
/// prompts, and summarising, translating and language detection — on the
/// device, with no server and no key.
/// </summary>
/// <remarks>
/// Availability depends on the browser and the device; check
/// <see cref="AvailabilityAsync"/> and fall back to a cloud model
/// (<c>Rustzor.AI</c>) when it is "unavailable".
/// </remarks>
public static class BuiltInAi
{
    /// <summary>
    /// "available", "downloadable", "downloading" or "unavailable" for one
    /// capability: "LanguageModel", "Summarizer", "Translator", "LanguageDetector".
    /// </summary>
    public static async Task<string> AvailabilityAsync(string capability = "LanguageModel") =>
        (await Js.CallAsync("bai.availability", Js.Args("api", capability))).AsString();

    /// <summary>Starts a chat session with an optional system prompt.</summary>
    public static async Task<AiSession> CreateSessionAsync(string? systemPrompt = null, double temperature = 0.7, int topK = 3) =>
        new((await Js.CallAsync("bai.session", Js.Args("system", systemPrompt, "temperature", temperature, "topK", topK))).AsInt());

    /// <summary>"tldr", "key-points", "teaser" or "headline"; "short", "medium" or "long".</summary>
    public static async Task<string> SummarizeAsync(string text, string type = "key-points", string length = "medium") =>
        (await Js.CallAsync("bai.summarize", Js.Args("text", text, "type", type, "length", length))).AsString();

    public static async Task<string> TranslateAsync(string text, string from, string to) =>
        (await Js.CallAsync("bai.translate", Js.Args("text", text, "from", from, "to", to))).AsString();

    /// <summary>The most likely language (BCP 47 code) and its confidence.</summary>
    public static async Task<(string Language, double Confidence)> DetectLanguageAsync(string text)
    {
        var v = await Js.CallAsync("bai.detect", Js.Args("text", text));
        return (v["language"].AsString(), v["confidence"].AsDouble());
    }
}

/// <summary>A conversation with the built-in model; it remembers earlier turns.</summary>
public sealed class AiSession : IDisposable
{
    private readonly int _handle;

    internal AiSession(int handle) => _handle = handle;

    public async Task<string> PromptAsync(string message) => (await Js.CallAsync("bai.prompt", Js.Args("h", _handle, "text", message))).AsString();

    /// <summary>Streams the reply; <paramref name="onText"/> receives the text so far.</summary>
    public Task PromptStreamingAsync(string message, Action<string> onText)
    {
        var tcs = new TaskCompletionSource<bool>();
        var id = 0;
        id = Callbacks.Register(v =>
        {
            if (v.Has("done"))
            {
                Callbacks.Unregister(id);
                if (v.Has("error")) tcs.TrySetException(new JsException("bai.stream", v["error"].AsString()));
                else tcs.TrySetResult(true);
                return;
            }
            onText(v["text"].AsString());
        });
        Js.Send("bai.stream", Js.Args("h", _handle, "text", message, "id", id));
        return tcs.Task;
    }

    public void Dispose() => Js.Send("bai.destroy", Js.Args("h", _handle));
}
