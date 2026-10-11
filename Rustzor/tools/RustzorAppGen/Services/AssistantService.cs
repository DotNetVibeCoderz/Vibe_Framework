using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using OpenAI;
using RustzorAppGen.Plugins;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using TextContent = Microsoft.Extensions.AI.TextContent;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace RustzorAppGen.Services;

/// <summary>What the assistant reports while it works, for the chat panel.</summary>
public interface IAssistantSink
{
    void Text(string piece);
    void ToolCall(string callId, string name, string arguments);
    void ToolResult(string callId, string summary);
}

/// <summary>
/// Jack, the Code Bender: a chat with one of the configured LLMs that can act
/// on the open project through Semantic Kernel plugins. Every provider is
/// reached through <see cref="IChatClient"/>, and kernel functions become its
/// tools, so tool calling works the same on OpenAI, Azure, Claude, Gemini and
/// Ollama.
/// </summary>
public sealed class AssistantService
{
    private readonly AppSettings _settings;
    private readonly Kernel _kernel;
    private readonly List<AIChatMessage> _history = new();

    public AssistantService(AppSettings settings, IWorkbench workbench)
    {
        _settings = settings;
        var builder = Kernel.CreateBuilder();
        builder.Plugins.AddFromObject(new ProjectPlugin(workbench), "project");
        builder.Plugins.AddFromObject(new RustzorPlugin(workbench, settings), "rustzor");
        builder.Plugins.AddFromObject(new BuildPlugin(workbench), "build");
        builder.Plugins.AddFromObject(new WebPlugin(settings), "web");
        builder.Plugins.AddFromObject(new MathPlugin(), "math");
        builder.Plugins.AddFromObject(new TimePlugin(), "time");
        builder.Plugins.AddFromObject(new UtilityPlugin(), "util");
        _kernel = builder.Build();
    }

    public Kernel Kernel => _kernel;
    public int TurnCount => _history.Count(m => m.Role == ChatRole.User);

    public void Clear() => _history.Clear();

    public IEnumerable<string> ToolNames => _kernel.Plugins.SelectMany(p => p.Select(f => $"{p.Name}.{f.Name}"));

    /// <summary>Sends one user turn and streams Jack's reply; returns the reply text.</summary>
    public async Task<string> SendAsync(string text, IReadOnlyList<(byte[] Data, string MediaType)> images, IAssistantSink sink, CancellationToken cancel)
    {
        var (provider, model) = _settings.Resolve(_settings.ActiveModel);
        if (!provider.IsConfigured)
            throw new InvalidOperationException($"{provider.Name} has no API key. Add one in Settings → AI providers.");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"Pick a {provider.Name} model at the top of the chat panel.");

        var contents = new List<AIContent> { new TextContent(text) };
        foreach (var (data, mediaType) in images) contents.Add(new DataContent(data, mediaType));
        _history.Add(new AIChatMessage(ChatRole.User, contents));

        using var client = new ChatClientBuilder(CreateClient(provider, model))
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 40)
            .Build();

        var messages = new List<AIChatMessage> { new(ChatRole.System, RustzorKnowledge.SystemPrompt(_settings)) };
        messages.AddRange(_history);
        var options = new ChatOptions
        {
            ModelId = model,
            MaxOutputTokens = _settings.MaxOutputTokens,
            Temperature = (float?)_settings.Temperature,
            Tools = _kernel.Plugins.SelectMany(p => p.Select(f => (AITool)f.AsAIFunction(_kernel))).ToList(),
            ToolMode = ChatToolMode.Auto,
        };

        var reply = new System.Text.StringBuilder();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancel))
        {
            updates.Add(update);
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent t when !string.IsNullOrEmpty(t.Text):
                        reply.Append(t.Text);
                        sink.Text(t.Text);
                        break;
                    case FunctionCallContent call:
                        sink.ToolCall(call.CallId, call.Name, Arguments(call.Arguments));
                        break;
                    case FunctionResultContent result:
                        sink.ToolResult(result.CallId, Summarize(result.Result));
                        break;
                }
            }
        }
        // Keep the whole exchange, tool calls included, so the next turn has context.
        _history.AddRange(updates.ToChatResponse().Messages);
        return reply.ToString();
    }

    /// <summary>A one-line health check of the active model.</summary>
    public async Task<string> PingAsync(CancellationToken cancel)
    {
        var (provider, model) = _settings.Resolve(_settings.ActiveModel);
        using var client = CreateClient(provider, model);
        var response = await client.GetResponseAsync(
            new[] { new AIChatMessage(ChatRole.User, "Reply with the single word: ready") },
            new ChatOptions { ModelId = model, MaxOutputTokens = 200 }, cancel);
        return response.Text.Trim();
    }

    private IChatClient CreateClient(ProviderSettings provider, string model)
    {
        switch (provider.Name)
        {
            case "Claude":
            {
                var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                if (!string.IsNullOrWhiteSpace(provider.WorkspaceId))
                    http.DefaultRequestHeaders.Add("anthropic-workspace-id", provider.WorkspaceId);
                var claude = new AnthropicClient { ApiKey = provider.ApiKey, HttpClient = http };
                if (!string.IsNullOrWhiteSpace(provider.Endpoint)) claude = (AnthropicClient)claude.WithOptions(o => o with { BaseUrl = provider.Endpoint.TrimEnd('/') });
                return claude.AsIChatClient(model, _settings.MaxOutputTokens);
            }
            case "AzureOpenAI":
            {
                // Azure's v1 surface speaks the OpenAI protocol; it reads the key
                // from an `api-key` header.
                var endpoint = provider.Endpoint.TrimEnd('/');
                if (!endpoint.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase)) endpoint += "/openai/v1";
                var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint), NetworkTimeout = TimeSpan.FromMinutes(10) };
                options.AddPolicy(new HeaderPolicy("api-key", provider.ApiKey), PipelinePosition.PerCall);
                return new OpenAIClient(new ApiKeyCredential(provider.ApiKey), options).GetChatClient(model).AsIChatClient();
            }
            default:
            {
                // OpenAI itself, and the OpenAI-compatible endpoints of Gemini and Ollama.
                var options = new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(10) };
                if (!string.IsNullOrWhiteSpace(provider.Endpoint)) options.Endpoint = new Uri(provider.Endpoint.TrimEnd('/'));
                var key = string.IsNullOrWhiteSpace(provider.ApiKey) ? "ollama" : provider.ApiKey;
                return new OpenAIClient(new ApiKeyCredential(key), options).GetChatClient(model).AsIChatClient();
            }
        }
    }

    private static string Arguments(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return "";
        return string.Join(", ", args.Select(kv => $"{kv.Key}: {Shorten(kv.Value?.ToString() ?? "null", 60)}"));
    }

    private static string Summarize(object? result) => Shorten(result?.ToString() ?? "done", 140);

    private static string Shorten(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }

    private sealed class HeaderPolicy(string name, string value) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Set(name, value);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Set(name, value);
            await ProcessNextAsync(message, pipeline, currentIndex);
        }
    }
}
