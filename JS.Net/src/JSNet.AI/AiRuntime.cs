using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace JSNet.AI;

/// <summary>
/// Runtime behind JS.Net <c>ai function</c> and <c>prompt</c> declarations. Configure it once with any
/// <see cref="IChatClient"/> (OpenAI, Azure OpenAI, Ollama, or a Semantic Kernel chat service via
/// <c>AsChatClient()</c>). Without configuration an offline <see cref="EchoChatClient"/> is used so samples
/// and tests run anywhere.
/// </summary>
public static class AiRuntime
{
    private static IChatClient _client = new EchoChatClient();

    public static IChatClient client => _client;

    public static ChatOptions? options { get; set; }

    /// <summary>Optional system prompt prepended to every call.</summary>
    public static string? systemPrompt { get; set; } = "You are a precise assistant used from JS.Net code. Answer concisely.";

    public static void configure(IChatClient client, ChatOptions? chatOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        options = chatOptions;
    }

    /// <summary>
    /// Invoked by generated code. A string result is returned as-is; any other <typeparamref name="T"/> is
    /// requested as JSON and deserialized.
    /// </summary>
    public static async Task<T> invokeAsync<T>(string functionName, string? template, IReadOnlyDictionary<string, object?>? args, CancellationToken cancellationToken = default)
    {
        string prompt = template ?? DefaultPrompt(functionName, args);
        bool wantsText = typeof(T) == typeof(string) || typeof(T) == typeof(object);
        if (!wantsText)
            prompt += $"\n\nRespond ONLY with JSON that matches the .NET type '{typeof(T).Name}'. No prose, no markdown fences.";

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        messages.Add(new ChatMessage(ChatRole.User, prompt));

        var response = await _client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        string text = response.Text?.Trim() ?? string.Empty;
        if (wantsText) return (T)(object)text;
        return JsonSerializer.Deserialize<T>(StripFences(text), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static string DefaultPrompt(string functionName, IReadOnlyDictionary<string, object?>? args)
    {
        var sb = new StringBuilder();
        sb.Append("Perform the task '").Append(Humanize(functionName)).Append("'.");
        if (args is { Count: > 0 })
        {
            sb.Append("\nInputs:");
            foreach (var (k, v) in args) sb.Append("\n- ").Append(k).Append(": ").Append(JSNet.Runtime.JS.str(v));
        }
        return sb.ToString();
    }

    private static string Humanize(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string StripFences(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        int firstNewLine = text.IndexOf('\n');
        int last = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine > 0 && last > firstNewLine ? text[(firstNewLine + 1)..last] : text;
    }
}

/// <summary>Base class for <c>prompt</c> declarations.</summary>
public abstract class Prompt<TOutput>
{
    public abstract string render();

    public Task<TOutput> invokeAsync(CancellationToken cancellationToken = default) =>
        AiRuntime.invokeAsync<TOutput>(GetType().Name, render(), null, cancellationToken);

    public override string ToString() => render();
}

/// <summary>
/// Deterministic offline <see cref="IChatClient"/>: echoes a short summary of the prompt. Used by default so
/// AI-native samples run without network access or API keys.
/// </summary>
public sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var user = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        string reply;
        if (user.Contains("Respond ONLY with JSON", StringComparison.Ordinal)) reply = "{}";
        else
        {
            string firstLine = user.Split('\n')[0];
            reply = $"[offline echo] {(firstLine.Length > 160 ? firstLine[..160] + "…" : firstLine)}";
        }
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
