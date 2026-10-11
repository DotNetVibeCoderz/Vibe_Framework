using System.Text;
using Rustzor.Browser.Fetch;
using Rustzor.Interop;

namespace Rustzor.AI;

/// <summary>A chat turn. <see cref="ImageDataUrl"/> attaches an image (data:image/png;base64,...) for vision models.</summary>
public sealed record ChatMessage(string Role, string Content, string? ImageDataUrl = null)
{
    public static ChatMessage System(string text) => new("system", text);
    public static ChatMessage User(string text, string? imageDataUrl = null) => new("user", text, imageDataUrl);
    public static ChatMessage Assistant(string text) => new("assistant", text);
}

public sealed class ChatOptions
{
    /// <summary>Sampling temperature; null leaves it to the model (some models, such as
    /// OpenAI's reasoning models, accept only their default).</summary>
    public double? Temperature { get; set; }
    public int MaxTokens { get; set; } = 1024;
    /// <summary>Ask for a JSON object reply (OpenAI-compatible endpoints).</summary>
    public bool JsonMode { get; set; }
}

public sealed record ChatUsage(int InputTokens, int OutputTokens);

public sealed record ChatReply(string Text, ChatUsage? Usage = null);

/// <summary>A chat model behind some HTTP API.</summary>
public interface IChatModel
{
    string Model { get; }
    Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null);
    /// <summary>Calls <paramref name="onText"/> with each piece of text as it streams; returns the whole reply.</summary>
    Task<string> StreamAsync(IReadOnlyList<ChatMessage> messages, Action<string> onText, ChatOptions? options = null);
}

/// <summary>
/// Any OpenAI-compatible chat endpoint: OpenAI, Azure OpenAI (v1 API),
/// Ollama, DeepSeek, Groq, LM Studio, vLLM.
/// <para>
/// A key in browser code is visible to every visitor. For public apps, put a
/// small proxy in front of the provider and point <c>endpoint</c> at it.
/// </para>
/// </summary>
public sealed class OpenAiChat : IChatModel
{
    private readonly FetchClient _http = new();
    private readonly string _endpoint;

    /// <param name="endpoint">Base URL ending before /chat/completions, e.g. https://api.openai.com/v1.</param>
    /// <param name="apiKeyHeader">"Authorization" (Bearer) for OpenAI; "api-key" for Azure.</param>
    public OpenAiChat(string endpoint, string? apiKey, string model, string apiKeyHeader = "Authorization")
    {
        _endpoint = endpoint.TrimEnd('/');
        Model = model;
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultHeaders[apiKeyHeader] = apiKeyHeader == "Authorization" ? "Bearer " + apiKey : apiKey;
    }

    public string Model { get; }

    public static OpenAiChat OpenAI(string apiKey, string model = "gpt-4o-mini") => new("https://api.openai.com/v1", apiKey, model);

    /// <summary>Azure OpenAI through its OpenAI-compatible v1 surface.</summary>
    public static OpenAiChat Azure(string resourceEndpoint, string apiKey, string deployment) =>
        new(resourceEndpoint.TrimEnd('/') + "/openai/v1", apiKey, deployment, "api-key");

    public static OpenAiChat Ollama(string model = "llama3.2", string endpoint = "http://localhost:11434/v1") => new(endpoint, null, model);

    private string Body(IReadOnlyList<ChatMessage> messages, ChatOptions o, bool stream)
    {
        var w = new JsonWriter().BeginObject().Prop("model", Model).Prop("stream", stream);
        if (o.Temperature is { } temperature) w.Prop("temperature", temperature);
        // Newer OpenAI models accept only max_completion_tokens; Ollama and
        // most compatible servers accept it too.
        w.Prop("max_completion_tokens", o.MaxTokens);
        if (o.JsonMode) w.Name("response_format").BeginObject().Prop("type", "json_object").EndObject();
        if (stream) w.Name("stream_options").BeginObject().Prop("include_usage", true).EndObject();
        w.Name("messages").BeginArray();
        foreach (var m in messages)
        {
            w.BeginObject().Prop("role", m.Role);
            if (m.ImageDataUrl is null) w.Prop("content", m.Content);
            else
            {
                w.Name("content").BeginArray()
                    .BeginObject().Prop("type", "text").Prop("text", m.Content).EndObject()
                    .BeginObject().Prop("type", "image_url").Name("image_url").BeginObject().Prop("url", m.ImageDataUrl).EndObject().EndObject()
                    .EndArray();
            }
            w.EndObject();
        }
        return w.EndArray().EndObject().ToString();
    }

    public async Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null)
    {
        var request = new FetchRequest(_endpoint + "/chat/completions", "POST") { Body = Body(messages, options ?? new(), false) }
            .WithHeader("Content-Type", "application/json");
        var r = (await _http.SendAsync(request)).EnsureSuccess().Json();
        var usage = r["usage"];
        return new ChatReply(r["choices"][0]["message"]["content"].AsString(),
            usage.IsNull ? null : new ChatUsage(usage["prompt_tokens"].AsInt(), usage["completion_tokens"].AsInt()));
    }

    public async Task<string> StreamAsync(IReadOnlyList<ChatMessage> messages, Action<string> onText, ChatOptions? options = null)
    {
        var all = new StringBuilder();
        var sse = new SseReader(data =>
        {
            if (data == "[DONE]") return;
            var v = JsonValue.Parse(data);
            var piece = v["choices"][0]["delta"]["content"].AsStringOrNull();
            if (string.IsNullOrEmpty(piece)) return;
            all.Append(piece);
            onText(piece);
        });
        var request = new FetchRequest(_endpoint + "/chat/completions", "POST") { Body = Body(messages, options ?? new(), true) }
            .WithHeader("Content-Type", "application/json");
        await _http.StreamAsync(request, sse.Push);
        return all.ToString();
    }

    /// <summary>Embeddings from /embeddings, e.g. text-embedding-3-small.</summary>
    public async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts, string model = "text-embedding-3-small")
    {
        var body = new JsonWriter().BeginObject().Prop("model", model).Name("input").Any(texts).EndObject().ToString();
        var request = new FetchRequest(_endpoint + "/embeddings", "POST") { Body = body }.WithHeader("Content-Type", "application/json");
        var r = (await _http.SendAsync(request)).EnsureSuccess().Json();
        return r["data"].Items.Select(d => d["embedding"].Items.Select(x => (float)x.AsDouble()).ToArray()).ToList();
    }
}

/// <summary>
/// Anthropic's Messages API (Claude). Calls straight from a browser send the
/// <c>anthropic-dangerous-direct-browser-access</c> header; prefer a proxy
/// for public apps so the key stays on a server.
/// </summary>
public sealed class ClaudeChat : IChatModel
{
    private readonly FetchClient _http = new();
    private readonly string _endpoint;

    /// <param name="workspaceId">For a key not scoped to a workspace: the workspace to bill and use.</param>
    public ClaudeChat(string apiKey, string model = "claude-sonnet-4-5", string endpoint = "https://api.anthropic.com", string? workspaceId = null)
    {
        _endpoint = endpoint.TrimEnd('/');
        Model = model;
        _http.DefaultHeaders["x-api-key"] = apiKey;
        if (!string.IsNullOrEmpty(workspaceId)) _http.DefaultHeaders["anthropic-workspace-id"] = workspaceId;
        _http.DefaultHeaders["anthropic-version"] = "2023-06-01";
        _http.DefaultHeaders["anthropic-dangerous-direct-browser-access"] = "true";
    }

    public string Model { get; }

    private string Body(IReadOnlyList<ChatMessage> messages, ChatOptions o, bool stream)
    {
        var w = new JsonWriter().BeginObject().Prop("model", Model).Prop("max_tokens", o.MaxTokens).Prop("stream", stream);
        if (o.Temperature is { } temperature) w.Prop("temperature", temperature);
        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        if (system.Length > 0) w.Prop("system", system);
        w.Name("messages").BeginArray();
        foreach (var m in messages.Where(m => m.Role != "system"))
        {
            w.BeginObject().Prop("role", m.Role).Name("content").BeginArray();
            if (m.ImageDataUrl is { } url && url.StartsWith("data:"))
            {
                var comma = url.IndexOf(',');
                var mediaType = url[5..url.IndexOf(';')];
                w.BeginObject().Prop("type", "image").Name("source").BeginObject()
                    .Prop("type", "base64").Prop("media_type", mediaType).Prop("data", url[(comma + 1)..])
                    .EndObject().EndObject();
            }
            w.BeginObject().Prop("type", "text").Prop("text", m.Content).EndObject();
            w.EndArray().EndObject();
        }
        return w.EndArray().EndObject().ToString();
    }

    public async Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null)
    {
        var request = new FetchRequest(_endpoint + "/v1/messages", "POST") { Body = Body(messages, options ?? new(), false) }
            .WithHeader("Content-Type", "application/json");
        var r = (await _http.SendAsync(request)).EnsureSuccess().Json();
        var text = string.Concat(r["content"].Items.Where(c => c["type"].AsString() == "text").Select(c => c["text"].AsString()));
        return new ChatReply(text, new ChatUsage(r["usage"]["input_tokens"].AsInt(), r["usage"]["output_tokens"].AsInt()));
    }

    public async Task<string> StreamAsync(IReadOnlyList<ChatMessage> messages, Action<string> onText, ChatOptions? options = null)
    {
        var all = new StringBuilder();
        var sse = new SseReader(data =>
        {
            var v = JsonValue.Parse(data);
            if (v["type"].AsString() != "content_block_delta") return;
            var piece = v["delta"]["text"].AsStringOrNull();
            if (string.IsNullOrEmpty(piece)) return;
            all.Append(piece);
            onText(piece);
        });
        var request = new FetchRequest(_endpoint + "/v1/messages", "POST") { Body = Body(messages, options ?? new(), true) }
            .WithHeader("Content-Type", "application/json");
        await _http.StreamAsync(request, sse.Push);
        return all.ToString();
    }
}

/// <summary>Splits a server-sent-events byte stream, delivered in arbitrary chunks, into data payloads.</summary>
public sealed class SseReader
{
    private readonly StringBuilder _buffer = new();
    private readonly Action<string> _onData;

    public SseReader(Action<string> onData) => _onData = onData;

    public void Push(string chunk)
    {
        _buffer.Append(chunk);
        while (true)
        {
            var text = _buffer.ToString();
            var nl = text.IndexOf('\n');
            if (nl < 0) return;
            var line = text[..nl].TrimEnd('\r');
            _buffer.Remove(0, nl + 1);
            if (line.StartsWith("data:")) _onData(line[5..].TrimStart());
        }
    }
}

/// <summary>A running conversation: keeps history and an optional system prompt.</summary>
public sealed class Conversation
{
    private readonly IChatModel _model;

    public Conversation(IChatModel model, string? systemPrompt = null)
    {
        _model = model;
        if (systemPrompt is not null) Messages.Add(ChatMessage.System(systemPrompt));
    }

    public List<ChatMessage> Messages { get; } = new();
    public ChatOptions Options { get; } = new();
    /// <summary>Oldest turns are dropped beyond this many (the system prompt stays).</summary>
    public int MaxTurns { get; set; } = 40;

    public async Task<string> SendAsync(string text, Action<string>? onText = null, string? imageDataUrl = null)
    {
        Messages.Add(ChatMessage.User(text, imageDataUrl));
        Trim();
        var reply = onText is null
            ? (await _model.CompleteAsync(Messages, Options)).Text
            : await _model.StreamAsync(Messages, onText, Options);
        Messages.Add(ChatMessage.Assistant(reply));
        return reply;
    }

    public void Clear() => Messages.RemoveAll(m => m.Role != "system");

    private void Trim()
    {
        var turns = Messages.Where(m => m.Role != "system").ToList();
        while (turns.Count > MaxTurns)
        {
            Messages.Remove(turns[0]);
            turns.RemoveAt(0);
        }
    }
}
