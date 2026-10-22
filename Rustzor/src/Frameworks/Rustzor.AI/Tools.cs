using System.Text;
using Rustzor.Interop;

namespace Rustzor.AI;

/// <summary>A function the model may call: a name, what it does, and its parameters as a JSON Schema object.</summary>
public sealed record ChatTool(string Name, string Description, string ParametersSchema)
{
    /// <summary>A tool with no parameters.</summary>
    public static ChatTool NoArguments(string name, string description) =>
        new(name, description, "{\"type\":\"object\",\"properties\":{}}");
}

/// <summary>A call the model asked for; <see cref="Arguments"/> is the JSON object it wrote.</summary>
public sealed record ToolCall(string Id, string Name, string Arguments)
{
    /// <summary>The arguments parsed; an empty object when the model sent none or invalid JSON.</summary>
    public JsonValue Args
    {
        get
        {
            try
            {
                var v = JsonValue.Parse(string.IsNullOrWhiteSpace(Arguments) ? "{}" : Arguments);
                return v.Kind == JsonKind.Object ? v : JsonValue.Parse("{}");
            }
            catch (Exception)
            {
                return JsonValue.Parse("{}");
            }
        }
    }
}

/// <summary>One model turn: the text it wrote and the tools it wants called (empty when it is done).</summary>
public sealed record ChatTurn(string Text, IReadOnlyList<ToolCall> ToolCalls, ChatUsage? Usage)
{
    /// <summary>Why the model stopped, as the provider says it: "stop", "tool_calls", "length" (OpenAI), "end_turn", "max_tokens" (Anthropic).</summary>
    public string? StopReason { get; init; }

    /// <summary>The model ran out of output tokens: a reasoning model can spend them all before writing a word.</summary>
    public bool CutOff => StopReason is "length" or "max_tokens";
}

/// <summary>A chat model that can call tools and be cancelled mid-stream.</summary>
public interface IToolChatModel : IChatModel
{
    /// <summary>
    /// Streams one turn. Text arrives through <paramref name="onText"/>; tool calls come back
    /// in the result for the caller to run and answer with <see cref="ChatMessage.Tool"/>.
    /// </summary>
    Task<ChatTurn> StreamTurnAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
        Action<string> onText, ChatOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// The tool loop: ask the model, run the tools it calls, give it the results, repeat until it
/// answers in text or <see cref="MaxSteps"/> is reached. The conversation list is extended in
/// place with the assistant and tool messages, so it can be saved and continued.
/// </summary>
public sealed class ToolAgent
{
    private readonly IToolChatModel _model;
    private readonly Dictionary<string, Func<ToolCall, Task<string>>> _handlers = new();

    public ToolAgent(IToolChatModel model) => _model = model;

    public List<ChatTool> Tools { get; } = new();
    public int MaxSteps { get; set; } = 5;

    /// <summary>Text as it streams, from every step.</summary>
    public Action<string>? OnText { get; set; }
    /// <summary>A tool is about to run.</summary>
    public Action<ToolCall>? OnToolCall { get; set; }
    /// <summary>A tool finished: the call, its result, and whether it failed.</summary>
    public Action<ToolCall, string, bool>? OnToolResult { get; set; }
    /// <summary>A step ended (its text is complete), before any tools of that step run.</summary>
    public Action<ChatTurn>? OnStep { get; set; }

    public ToolAgent Register(ChatTool tool, Func<ToolCall, Task<string>> handler)
    {
        Tools.RemoveAll(t => t.Name == tool.Name);
        Tools.Add(tool);
        _handlers[tool.Name] = handler;
        return this;
    }

    public ToolAgent Register(ChatTool tool, Func<ToolCall, string> handler) =>
        Register(tool, call => Task.FromResult(handler(call)));

    /// <summary>Runs to the final answer. Usage is summed over every step.</summary>
    public async Task<ChatTurn> RunAsync(List<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var input = 0;
        var output = 0;
        var anyUsage = false;
        var text = new StringBuilder();
        for (var step = 0; step < MaxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastStep = step == MaxSteps - 1;
            var turn = await _model.StreamTurnAsync(messages, lastStep ? Array.Empty<ChatTool>() : Tools,
                piece => OnText?.Invoke(piece), options, cancellationToken);
            if (turn.Usage is { } u)
            {
                anyUsage = true;
                input += u.InputTokens;
                output += u.OutputTokens;
            }
            text.Append(turn.Text);
            OnStep?.Invoke(turn);
            if (turn.ToolCalls.Count == 0)
            {
                messages.Add(ChatMessage.Assistant(turn.Text));
                return new ChatTurn(text.ToString(), Array.Empty<ToolCall>(), anyUsage ? new ChatUsage(input, output) : null) { StopReason = turn.StopReason };
            }

            messages.Add(ChatMessage.AssistantCalls(turn.Text, turn.ToolCalls));
            foreach (var call in turn.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OnToolCall?.Invoke(call);
                string result;
                var failed = false;
                if (_handlers.TryGetValue(call.Name, out var handler))
                {
                    try
                    {
                        result = await handler(call);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        result = "Error: " + e.Message;
                        failed = true;
                    }
                }
                else
                {
                    result = $"Error: there is no tool named {call.Name}.";
                    failed = true;
                }
                OnToolResult?.Invoke(call, result, failed);
                messages.Add(ChatMessage.Tool(call.Id, result));
            }
        }
        return new ChatTurn(text.ToString(), Array.Empty<ToolCall>(), anyUsage ? new ChatUsage(input, output) : null);
    }
}

/// <summary>Collects a streamed tool call, whose id, name and arguments arrive in pieces.</summary>
internal sealed class ToolCallBuilder
{
    public string Id = "";
    public string Name = "";
    public readonly StringBuilder Arguments = new();

    public ToolCall Build(int index) =>
        new(Id.Length > 0 ? Id : "call_" + index, Name, Arguments.Length > 0 ? Arguments.ToString() : "{}");
}
