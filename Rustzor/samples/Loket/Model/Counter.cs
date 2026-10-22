using Rustzor.AI;
using Rustzor.Browser.Audio;
using Rustzor.Interop;

namespace Loket.Model;

/// <summary>
/// The counter at work: takes a resident's message, runs the model with the tools, and
/// streams the answer into the conversation. One answer at a time; <see cref="Stop"/>
/// cancels it, keeping what was written so far.
/// </summary>
public static class Counter
{
    private static CancellationTokenSource? _cts;

    public static bool Busy => _cts is not null;
    public static string? BusyConv { get; private set; }

    /// <summary>A redraw is due (throttled while text streams).</summary>
    public static event Action? Tick;

    public static void Stop() => _cts?.Cancel();

    public static async Task AskAsync(Conv conv, string text, string? image)
    {
        if (Busy) return;
        var settings = Desk.Settings;
        var user = new Item { Role = "user", Text = text, Image = image, At = DateTime.UtcNow };
        conv.Items.Add(user);
        if (conv.Title.Length == 0) conv.Title = TitleFrom(text);
        var answer = new Item { Role = "assistant", At = DateTime.UtcNow };
        conv.Items.Add(answer);
        Desk.Save(conv);

        _cts = new CancellationTokenSource();
        BusyConv = conv.Id;
        var lastDraw = 0.0;
        void Draw(bool now)
        {
            var t = Environment.TickCount64;
            if (!now && t - lastDraw < 60) return;
            lastDraw = t;
            Tick?.Invoke();
        }

        try
        {
            var model = Providers.Create(settings);
            answer.Model = model.Model;
            var messages = History(conv, settings);
            var agent = DeskTools.Agent(model, answer.Sources, conv.Id);
            agent.OnText = piece =>
            {
                answer.Text += piece;
                Draw(false);
            };
            agent.OnToolCall = call =>
            {
                // A step's text before its calls (often empty) stays in the bubble.
                answer.Tools.Add(new ToolRun { Id = call.Id, Name = call.Name, Args = call.Arguments, Running = true });
                Draw(true);
            };
            agent.OnToolResult = (call, result, failed) =>
            {
                var run = answer.Tools.LastOrDefault(r => r.Id == call.Id);
                if (run is not null)
                {
                    run.Result = result;
                    run.Failed = failed;
                    run.Running = false;
                }
                if (!answer.Text.EndsWith("\n") && answer.Text.Length > 0) answer.Text += "\n\n";
                Draw(true);
            };
            // Reasoning models (gpt-5-mini, o-series) count their hidden reasoning against this
            // budget; with 1200 a long answer could be spent entirely on thinking.
            var turn = await agent.RunAsync(messages, new ChatOptions { MaxTokens = 4096 }, _cts.Token);
            if (turn.CutOff)
                answer.Error = answer.Text.Trim().Length == 0
                    ? L.T("Model kehabisan token sebelum sempat menjawab (pada model penalaran, berpikir pun memakai token). Coba pertanyaan yang lebih sempit.", "The model ran out of tokens before it could answer (reasoning models spend tokens on thinking too). Try a narrower question.")
                    : L.T("Jawaban terpotong: model mencapai batas token.", "The answer was cut short: the model reached its token limit.");
            var input = turn.Usage?.InputTokens ?? messages.Sum(m => m.Content.Length) / 4;
            var output = turn.Usage?.OutputTokens ?? answer.Text.Length / 4;
            answer.In = input;
            answer.Out = output;
            answer.Cost = Providers.Cost(settings, input, output);
            if (answer.Text.Trim().Length == 0 && answer.Tools.Count == 0) answer.Text = L.T("(tidak ada jawaban)", "(no answer)");
            if (settings.Speak) Speak(answer.Text, Knowledge.GuessEnglish(text));
        }
        catch (OperationCanceledException)
        {
            answer.Stopped = true;
            foreach (var r in answer.Tools.Where(r => r.Running)) r.Running = false;
        }
        catch (Exception e)
        {
            answer.Error = Describe(e);
            foreach (var r in answer.Tools.Where(r => r.Running)) r.Running = false;
        }
        finally
        {
            _cts = null;
            BusyConv = null;
            answer.Text = answer.Text.TrimEnd();
            Desk.Save(conv);
            Tick?.Invoke();
        }
    }

    /// <summary>The conversation as the model sees it: persona and context, then each turn with its tool calls and results.</summary>
    public static List<ChatMessage> History(Conv conv, Settings settings)
    {
        var context = $"\n\nToday is {DeskTools.Clock()}. Office documents available: {string.Join("; ", Knowledge.Docs.Select(d => d.Title))}.";
        var messages = new List<ChatMessage> { ChatMessage.System(settings.Persona.Trim() + context) };
        var items = conv.Items.Take(conv.Items.Count - 1).ToList(); // the empty answer being written
        var lastUser = items.FindLastIndex(i => i.Role == "user");
        for (var k = 0; k < items.Count; k++)
        {
            var item = items[k];
            if (item.Role == "user")
            {
                // Only the newest picture is sent; older ones would cost tokens on every turn.
                var image = k == lastUser && Providers.SeesImages(settings) || k == lastUser && settings.Provider == "demo" ? item.Image : null;
                messages.Add(ChatMessage.User(item.Text, image));
                continue;
            }
            if (item.Error.Length > 0 && item.Text.Length == 0) continue;
            var done = item.Tools.Where(t => !t.Running && t.Result.Length > 0).ToList();
            if (done.Count > 0)
            {
                messages.Add(ChatMessage.AssistantCalls("", done.Select(t => new ToolCall(t.Id, t.Name, t.Args)).ToList()));
                foreach (var t in done) messages.Add(ChatMessage.Tool(t.Id, t.Result));
            }
            if (item.Text.Length > 0) messages.Add(ChatMessage.Assistant(item.Text));
        }
        return messages;
    }

    private static string TitleFrom(string text)
    {
        var t = text.Replace("\n", " ").Trim();
        return t.Length <= 48 ? t : t[..46].TrimEnd() + "…";
    }

    /// <summary>A provider error in plain words, without anything secret.</summary>
    public static string Describe(Exception e)
    {
        var message = e.Message;
        var cut = message.IndexOf("HTTP ", StringComparison.Ordinal);
        if (cut >= 0) message = message[cut..];
        if (message.Length > 300) message = message[..300] + "…";
        if (message.Contains("401") || message.Contains("403"))
            return L.T("Penyedia menolak kunci API (", "The provider refused the API key (") + message + L.T("). Periksa kunci dan model di Pengaturan.", "). Check the key and model in Settings.");
        if (message.Contains("Failed to fetch") || message.Contains("NetworkError"))
            return L.T("Tidak bisa menghubungi penyedia: periksa jaringan dan alamat endpoint.", "Could not reach the provider: check the network and the endpoint address.");
        return message;
    }

    public static void Speak(string text, bool english)
    {
        var plain = new string(text.Where(c => c != '*' && c != '`' && c != '#').ToArray());
        var cut = plain.IndexOf("\nIni model demo", StringComparison.Ordinal);
        if (cut > 0) plain = plain[..cut];
        cut = plain.IndexOf("\nThis is the demo model", StringComparison.Ordinal);
        if (cut > 0) plain = plain[..cut];
        // Citation numbers read aloud are noise.
        var sb = new System.Text.StringBuilder();
        var skip = false;
        foreach (var c in plain)
        {
            if (c == '[') skip = true;
            if (!skip) sb.Append(c);
            if (c == ']') skip = false;
        }
        Js.Send("lk.speakStop", Js.Args());
        AudioEngine.Speak(sb.ToString(), english ? "en-US" : "id-ID");
    }
}
