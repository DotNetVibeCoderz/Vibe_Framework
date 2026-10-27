using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace JSNet.AI;

/// <summary>
/// Runtime behind JS.Net <c>ai function</c>, <c>ai function*</c> (streaming) and <c>prompt</c> declarations. Configure it
/// once with any <see cref="IChatClient"/> (OpenAI, Azure OpenAI, Ollama, or a Semantic Kernel chat service via
/// <c>AsChatClient()</c>). Without configuration an offline <see cref="EchoChatClient"/> is used so samples and tests run
/// anywhere. Methods marked <c>@AiTool</c> on objects registered with <see cref="useTools"/> are offered to the model as
/// callable functions (automatic function invocation).
/// </summary>
public static class AiRuntime
{
    private static IChatClient _inner = new EchoChatClient();
    private static IChatClient _client = Wrap(_inner);
    private static readonly List<AITool> Tools = [];
    private static readonly Lock Gate = new();

    public static IChatClient client => _inner;

    public static ChatOptions? options { get; set; }

    /// <summary>Optional system prompt prepended to every call.</summary>
    public static string? systemPrompt { get; set; } = "You are a precise assistant used from JS.Net code. Answer concisely.";

    public static void configure(IChatClient client, ChatOptions? chatOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (Gate)
        {
            _inner = client;
            _client = Wrap(client);
            options = chatOptions;
        }
    }

    private static IChatClient Wrap(IChatClient client) => new ChatClientBuilder(client).UseFunctionInvocation().Build();

    /// <summary>Registers every <c>@AiTool</c> method of <paramref name="target"/> (or its static methods when a Type is passed).</summary>
    public static int useTools(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var type = target as Type ?? target.GetType();
        object? instance = target is Type ? null : target;
        var flags = BindingFlags.Public | BindingFlags.Static | (instance is null ? 0 : BindingFlags.Instance);
        int added = 0;
        lock (Gate)
        {
            foreach (var method in type.GetMethods(flags))
            {
                if (method.GetCustomAttribute<AiToolAttribute>() is not { } attr) continue;
                Tools.Add(AIFunctionFactory.Create(method, method.IsStatic ? null : instance,
                    new AIFunctionFactoryOptions { Name = attr.Name ?? method.Name, Description = attr.Description }));
                added++;
            }
        }
        return added;
    }

    public static void clearTools()
    {
        lock (Gate) Tools.Clear();
    }

    public static IReadOnlyList<string> toolNames
    {
        get { lock (Gate) return Tools.Select(t => t.Name).ToList(); }
    }

    private static ChatOptions BuildOptions(Type? jsonResult)
    {
        var o = options?.Clone() ?? new ChatOptions();
        lock (Gate)
        {
            if (Tools.Count > 0) o.Tools = [.. (o.Tools ?? []), .. Tools];
        }
        if (jsonResult is not null)
            o.ResponseFormat = ChatResponseFormat.ForJsonSchema(AIJsonUtilities.CreateJsonSchema(jsonResult), jsonResult.Name);
        return o;
    }

    private static List<ChatMessage> Messages(string prompt)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        messages.Add(new ChatMessage(ChatRole.User, prompt));
        return messages;
    }

    /// <summary>
    /// Invoked by generated code. A string result is returned as-is; any other <typeparamref name="T"/> is requested as
    /// structured output (JSON schema derived from the type) and deserialized.
    /// </summary>
    public static async Task<T> invokeAsync<T>(string functionName, string? template, IReadOnlyDictionary<string, object?>? args, CancellationToken cancellationToken = default)
    {
        string prompt = template ?? DefaultPrompt(functionName, args);
        bool wantsText = typeof(T) == typeof(string) || typeof(T) == typeof(object);
        if (!wantsText)
            prompt += $"\n\nRespond ONLY with JSON that matches the .NET type '{typeof(T).Name}'. No prose, no markdown fences.";

        var response = await _client.GetResponseAsync(Messages(prompt), BuildOptions(wantsText ? null : typeof(T)), cancellationToken).ConfigureAwait(false);
        string text = response.Text?.Trim() ?? string.Empty;
        if (wantsText) return (T)(object)text;
        return JsonSerializer.Deserialize<T>(StripFences(text), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>Backs <c>ai function*</c>: yields text chunks as the model streams them.</summary>
    public static async IAsyncEnumerable<string> streamAsync(string functionName, string? template, IReadOnlyDictionary<string, object?>? args,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string prompt = template ?? DefaultPrompt(functionName, args);
        await foreach (var update in _client.GetStreamingResponseAsync(Messages(prompt), BuildOptions(null), cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text)) yield return update.Text;
        }
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

/// <summary><c>@AiTool("Gets the weather")</c>: exposes a method to the model via <see cref="AiRuntime.useTools"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AiToolAttribute(string? description = null) : Attribute
{
    public string? Description { get; } = description;
    public string? Name { get; init; }
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
/// Deterministic offline <see cref="IChatClient"/>: echoes a short summary of the prompt (streamed word by word).
/// Used by default so AI-native samples run without network access or API keys.
/// </summary>
public sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var user = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        string reply;
        if (options?.ResponseFormat is ChatResponseFormatJson || user.Contains("Respond ONLY with JSON", StringComparison.Ordinal)) reply = "{}";
        else
        {
            string firstLine = user.Split('\n')[0];
            reply = $"[offline echo] {(firstLine.Length > 160 ? firstLine[..160] + "…" : firstLine)}";
        }
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var words = response.Text.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, i == 0 ? words[i] : " " + words[i]);
            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
