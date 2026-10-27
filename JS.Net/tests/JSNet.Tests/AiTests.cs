using System.Runtime.CompilerServices;
using JSNet.AI;
using JSNet.Scripting;
using Microsoft.Extensions.AI;

namespace JSNet.Tests;

/// <summary>AI-native features. One class so the tests run sequentially (AiRuntime is process-wide).</summary>
public class AiTests : IDisposable
{
    public AiTests()
    {
        AiRuntime.configure(new EchoChatClient());
        AiRuntime.clearTools();
    }

    public void Dispose()
    {
        AiRuntime.configure(new EchoChatClient());
        AiRuntime.clearTools();
    }

    private static async Task<string> Run(string code, params System.Reflection.Assembly[] extra)
    {
        var refs = new List<System.Reflection.Assembly> { typeof(AiRuntime).Assembly };
        refs.AddRange(extra);
        var result = await JsNetScript.RunAsync(code, "ai.jsn", new ScriptOptions { CaptureOutput = true, AdditionalReferences = refs });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + result.Exception + "\n--- C# ---\n" +
            string.Join("\n", result.GeneratedFiles.Select(f => f.Text)));
        return result.Output.Replace("\r\n", "\n").TrimEnd();
    }

    [Fact]
    public async Task AiFunctionsUseOfflineClientByDefault()
    {
        string output = await Run("""
            ai function summarize(text: string): Promise<string> { `Summarize: ${text}` }
            console.log(await summarize("JS.Net is a language"));
            """);
        Assert.Contains("Summarize: JS.Net is a language", output);
    }

    [Fact]
    public async Task StreamingAiFunctionsYieldChunks()
    {
        string output = await Run("""
            ai function* tell(topic: string): AsyncIterable<string> { `Tell me about ${topic}` }
            const parts = new Array<string>();
            for await (const chunk of tell("CoreCLR")) parts.push(chunk);
            console.log(parts.length > 3, parts.join(""));
            """);
        Assert.Equal("true [offline echo] Tell me about CoreCLR", output);
    }

    [Fact]
    public async Task StructuredOutputDeserializesIntoInterfaces()
    {
        AiRuntime.configure(new ScriptedClient(_ => new ChatMessage(ChatRole.Assistant, "{\"city\":\"Bandung\",\"celsius\":24.5}")));
        string output = await Run("""
            interface Weather { city: string; celsius: number; }
            ai function forecast(city: string): Promise<Weather> { `Weather for ${city}` }
            const w = await forecast("Bandung");
            console.log(w.city, w.celsius);
            """);
        Assert.Equal("Bandung 24.5", output);
    }

    [Fact]
    public async Task AiToolsAreCalledByTheModel()
    {
        // First turn: the "model" asks for the tool; second turn: it answers with the tool result.
        AiRuntime.configure(new ScriptedClient(messages =>
        {
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().FirstOrDefault();
            return result is null
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call1", "getWeather", new Dictionary<string, object?> { ["city"] = "Bandung" })])
                : new ChatMessage(ChatRole.Assistant, $"Forecast: {result.Result}");
        }));
        string output = await Run("""
            import JSNet.AI;
            class WeatherTools {
                calls: int = 0;
                @AiTool("Current weather for a city")
                getWeather(city: string): string { this.calls++; return `${city} 24°C, light rain`; }
            }
            const tools = new WeatherTools();
            AiRuntime.useTools(tools);
            ai function ask(question: string): Promise<string> { question }
            console.log(await ask("Weather in Bandung?"), tools.calls);
            """);
        Assert.Equal("Forecast: Bandung 24°C, light rain 1", output);
    }

    [Fact]
    public async Task JsNetClassesAreSemanticKernelPlugins()
    {
        string output = await Run("""
            import Microsoft.SemanticKernel;
            import System.ComponentModel;
            class MathPlugin {
                @KernelFunction
                @Description("Adds two numbers")
                add(a: number, b: number): number { return a + b; }
            }
            const plugin = KernelPluginFactory.CreateFromObject(new MathPlugin(), "math");
            const fn = plugin.first();
            console.log(fn.Name, fn.Description, plugin.FunctionCount);
            """, typeof(Microsoft.SemanticKernel.Kernel).Assembly, typeof(Microsoft.SemanticKernel.KernelFunction).Assembly);
        Assert.Equal("add Adds two numbers 1", output);
    }

    private sealed class ScriptedClient(Func<IList<ChatMessage>, ChatMessage> reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(reply(messages.ToList())));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
