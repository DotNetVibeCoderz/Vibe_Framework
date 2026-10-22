using System.Text;
using Rustzor.AI;
using Rustzor.Interop;

namespace Loket.Model;

/// <summary>
/// The built-in model: no key, no network. It reads the resident's message, picks a tool the
/// way a real model would (arithmetic → calculator, "now"/"open" → current_time, "note" →
/// create_note, a service question → search_documents), and writes its answer from what the
/// tools returned, word by word, so streaming, stopping, tool cards and citations all work
/// offline exactly as they do with a real provider.
/// </summary>
public sealed class DemoModel : IToolChatModel
{
    public string Model => "loket-demo";

    /// <summary>Milliseconds between words.</summary>
    public int Pace { get; set; } = 22;

    public async Task<ChatReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null)
    {
        var turn = await StreamTurnAsync(messages, Array.Empty<ChatTool>(), _ => { }, options);
        return new ChatReply(turn.Text, turn.Usage);
    }

    public async Task<string> StreamAsync(IReadOnlyList<ChatMessage> messages, Action<string> onText, ChatOptions? options = null) =>
        (await StreamTurnAsync(messages, Array.Empty<ChatTool>(), onText, options)).Text;

    public async Task<ChatTurn> StreamTurnAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
        Action<string> onText, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var input = messages.Sum(m => m.Content.Length) / 4 + 1;
        var last = messages[^1];
        var userText = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var english = Knowledge.GuessEnglish(userText);
        string reply;

        if (last.Role == "tool")
        {
            reply = FromToolResults(messages, english);
        }
        else
        {
            var call = ChooseTool(userText, tools);
            if (call is not null)
            {
                await Scheduler.Delay(120);
                cancellationToken.ThrowIfCancellationRequested();
                return new ChatTurn("", new[] { call }, new ChatUsage(input, 12));
            }
            reply = Talk(userText, last.ImageDataUrl, english);
        }

        var written = new StringBuilder();
        foreach (var piece in Pieces(reply))
        {
            await Scheduler.Delay(Pace);
            cancellationToken.ThrowIfCancellationRequested();
            written.Append(piece);
            onText(piece);
        }
        return new ChatTurn(written.ToString(), Array.Empty<ToolCall>(), new ChatUsage(input, written.Length / 4 + 1));
    }

    private static ToolCall? ChooseTool(string text, IReadOnlyList<ChatTool> tools)
    {
        bool Has(string name) => tools.Any(t => t.Name == name);
        var lower = text.ToLowerInvariant();
        var id = "demo_" + Desk.NewId();

        if (Has("calculator") && Expression(text) is { } expr)
            return new ToolCall(id, "calculator", "{\"expression\":" + DeskTools.Quote(expr) + "}");

        if (Has("create_note") && StartsWithAny(lower, "catat", "ingatkan", "tolong catat", "note:", "note ", "remind", "buat catatan", "add a note"))
        {
            var title = text;
            var colon = text.IndexOf(':');
            if (colon >= 0 && colon < 30) title = text[(colon + 1)..];
            title = title.Trim().TrimEnd('.', '!');
            if (title.Length > 0) title = char.ToUpperInvariant(title[0]) + title[1..];
            return new ToolCall(id, "create_note", "{\"title\":" + DeskTools.Quote(title) + "}");
        }

        if (Has("current_time") && ContainsAny(lower, "jam berapa", "hari ini", "sekarang", "buka", "tutup", "what time", "today", "open now", "is the office open", "tanggal"))
            return new ToolCall(id, "current_time", "{}");

        if (Has("search_documents") && Knowledge.Chunks.Count > 0 && Knowledge.Search(text, 1).Count > 0 && !ContainsAny(lower, "halo", "hello", "terima kasih", "thank"))
            return new ToolCall(id, "search_documents", "{\"query\":" + DeskTools.Quote(text.Trim()) + "}");

        return null;
    }

    /// <summary>The longest run of number-and-operator characters with two numbers and an operator in it.</summary>
    public static string? Expression(string text)
    {
        var lower = text.ToLowerInvariant().Replace(" dari ", " * ").Replace(" of ", " * ").Replace(" kali ", " * ").Replace(" times ", " * ")
            .Replace(" dibagi ", " / ").Replace(" divided by ", " / ").Replace(" tambah ", " + ").Replace(" plus ", " + ").Replace(" kurang ", " - ").Replace(" minus ", " - ");
        string? best = null;
        var run = new StringBuilder();
        foreach (var ch in lower + "\n")
        {
            if (char.IsDigit(ch) || " .,+-*/x×÷:%^()".Contains(ch))
            {
                run.Append(ch);
                continue;
            }
            Consider(run.ToString());
            run.Clear();
        }
        return best;

        void Consider(string candidate)
        {
            var c = candidate.Trim().Trim(':', ',', '.', 'x').Trim();
            var digits = c.Count(char.IsDigit);
            var ops = c.Count(ch => "+-*/×÷^%".Contains(ch) || ch == 'x');
            if (digits < 2 || ops < 1) return;
            // 0812-3000-4455 is a phone number, not a sum: dashes alone, with no spaces, are not arithmetic.
            if (!c.Any(ch => "+*/×÷^%".Contains(ch) || ch == 'x') && !c.Contains(" - ")) return;
            // Two numbers on either side of an operator.
            var numbers = 0;
            var inNumber = false;
            foreach (var ch in c)
            {
                var isNum = char.IsDigit(ch) || ch == '.' || ch == ',';
                if (isNum && !inNumber) numbers++;
                inNumber = isNum;
            }
            if (numbers < 2 && !c.Contains('%')) return;
            try
            {
                Calc.Evaluate(c);
            }
            catch (Exception)
            {
                return;
            }
            if (best is null || c.Length > best.Length) best = c;
        }
    }

    private static string FromToolResults(IReadOnlyList<ChatMessage> messages, bool english)
    {
        var calls = messages.LastOrDefault(m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 })?.ToolCalls ?? Array.Empty<ToolCall>();
        var results = new Dictionary<string, string>();
        foreach (var m in messages.Where(m => m.Role == "tool" && m.ToolCallId is not null)) results[m.ToolCallId!] = m.Content;
        var sb = new StringBuilder();
        foreach (var call in calls)
        {
            var result = results.TryGetValue(call.Id, out var r) ? r : "";
            if (result.StartsWith("Error"))
            {
                sb.Append(english ? $"I could not use {call.Name}: {result[6..].Trim()}." : $"Saya tidak bisa memakai {call.Name}: {result[6..].Trim()}.");
                continue;
            }
            switch (call.Name)
            {
                case "calculator":
                    var v = JsonValue.Parse(result);
                    sb.Append(english
                        ? $"The result is **{Grouped(v["result"].AsString(), true)}**.\n\nWorked out exactly as `{v["expression"].AsString()}`."
                        : $"Hasilnya **{Grouped(v["result"].AsString(), false)}**.\n\nDihitung tepat sebagai `{v["expression"].AsString()}`.");
                    break;
                case "current_time":
                    var t = JsonValue.Parse(result);
                    var open = t["office_open"].AsBool();
                    sb.Append(english
                        ? $"It is **{t["time"].AsString()}**, {t["date"].AsString()}. The office is {(open ? "**open**" : "**closed**")}: {t["office"].AsString()}."
                        : $"Sekarang pukul **{t["time"].AsString()}**, {t["date"].AsString()}. Kantor sedang {(open ? "**buka**" : "**tutup**")}: {Office(t["office"].AsString())}.");
                    break;
                case "create_note":
                    var n = JsonValue.Parse(result);
                    sb.Append(english
                        ? $"Noted for the staff: **{n["title"].AsString()}**. You will find it under Notes."
                        : $"Sudah saya catat untuk petugas: **{n["title"].AsString()}**. Catatan ini ada di menu Catatan.");
                    break;
                case "search_documents":
                    sb.Append(FromPassages(result, english));
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>An answer from search results: the most relevant sentences, each with its citation.</summary>
    private static string FromPassages(string result, bool english)
    {
        if (!result.StartsWith("["))
            return english
                ? "The office documents do not cover this. Please ask at the counter, or call 0812-3000-4455."
                : "Dokumen kantor tidak membahas hal ini. Silakan tanyakan langsung di loket, atau hubungi 0812-3000-4455.";
        var passages = result.Split("\n\n");
        var sb = new StringBuilder(english ? "From the office documents:\n\n" : "Menurut dokumen kantor:\n\n");
        string? firstDoc = null;
        foreach (var p in passages.Take(2))
        {
            var close = p.IndexOf(']');
            var colon = p.IndexOf(": ", close);
            if (close < 1 || colon < 0) continue;
            // A second passage is quoted only from the same document: the demo has no judgement
            // of relevance beyond the ranking, and a neighbouring procedure reads as a wrong answer.
            var doc = p[(close + 2)..colon].Split(" — ")[0];
            if (firstDoc is not null && doc != firstDoc) continue;
            firstDoc ??= doc;
            var n = p[1..close];
            var sentences = p[(colon + 2)..].Split(". ").Take(2).Select(s => s.Trim().TrimEnd('.')).Where(s => s.Length > 0).ToList();
            if (sentences.Count == 0) continue;
            sb.Append("- ").Append(string.Join(". ", sentences)).Append(". [").Append(n).Append("]\n");
        }
        sb.Append(english
            ? "\nThis is the demo model quoting the documents; connect a provider in Settings for answers in your own words."
            : "\nIni model demo yang mengutip dokumen; hubungkan penyedia model di Pengaturan untuk jawaban yang lebih luwes.");
        return sb.ToString();
    }

    private static string Talk(string text, string? image, bool english)
    {
        var lower = text.ToLowerInvariant();
        if (image is not null)
            return english
                ? "I received your picture, but the demo model cannot look at images. Choose Azure OpenAI, OpenAI or Claude in Settings to ask about it."
                : "Gambar Anda sudah saya terima, tetapi model demo tidak bisa melihat gambar. Pilih Azure OpenAI, OpenAI, atau Claude di Pengaturan untuk menanyakannya.";
        if (ContainsAny(lower, "markdown", "contoh format", "format example"))
            return """
Berikut contoh format yang bisa saya tulis:

### Daftar yang perlu dibawa
1. **Kartu Keluarga** asli
2. KTP-el dua orang saksi
3. Surat keterangan lahir

| Layanan | Waktu | Biaya |
| --- | --- | --- |
| KTP-el | 7 hari kerja | Gratis |
| Akta kelahiran | Hari yang sama | Gratis |

```json
{ "layanan": "akta-kelahiran", "biaya": 0 }
```

Kode di atas bisa disalin dengan tombol **Salin**.
""";
        if (ContainsAny(lower, "terima kasih", "makasih", "thank"))
            return english ? "You are welcome. Is there anything else the desk can help with?" : "Sama-sama. Ada lagi yang bisa dibantu di loket?";
        var echo = text.Length > 0 && (text.Contains('<') || text.Contains('>'))
            ? (english ? $"You wrote: {text}\n\n" : $"Anda menulis: {text}\n\n")
            : "";
        return echo + (english
            ? "Hello, this is the Kemuning Raya front desk. I can:\n\n- answer questions about **KTP-el, family cards, birth certificates and letters**, from the office documents, with sources;\n- **calculate**, for example `15% of 2,400,000`;\n- tell you the **time** and whether the office is open;\n- write a **note** for the staff, for example `note: call the RT about my domicile letter`."
            : "Halo, ini meja layanan Kelurahan Kemuning Raya. Saya bisa:\n\n- menjawab pertanyaan tentang **KTP-el, KK, akta kelahiran, dan surat keterangan** dari dokumen kantor, lengkap dengan sumbernya;\n- **menghitung**, misalnya `15% dari 2.400.000`;\n- memberi tahu **jam** dan apakah kantor sedang buka;\n- membuat **catatan** untuk petugas, misalnya `catat: telepon Pak RT soal surat domisili`.");
    }

    private static string Office(string note) => note
        .Replace("closed at weekends; opens Monday 08.00 WIB", "tutup di akhir pekan; buka lagi Senin pukul 08.00 WIB")
        .Replace("opens at 08.00 WIB today; queue numbers from 07.45", "buka pukul 08.00 WIB hari ini; nomor antrean mulai 07.45")
        .Replace("closed for today; opens Monday 08.00 WIB", "sudah tutup untuk hari ini; buka lagi Senin pukul 08.00 WIB")
        .Replace("closed for today; opens tomorrow 08.00 WIB", "sudah tutup untuk hari ini; buka lagi besok pukul 08.00 WIB")
        .Replace("lunch break until 13.00 WIB", "istirahat sampai pukul 13.00 WIB")
        .Replace("open until", "buka sampai");

    /// <summary>2400000 → 2.400.000 (Indonesian) or 2,400,000 (English).</summary>
    public static string Grouped(string number, bool english)
    {
        var negative = number.StartsWith("-");
        var digits = negative ? number[1..] : number;
        var dot = digits.IndexOf('.');
        var whole = dot < 0 ? digits : digits[..dot];
        var frac = dot < 0 ? "" : digits[(dot + 1)..];
        var sb = new StringBuilder();
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0) sb.Append(english ? ',' : '.');
            sb.Append(whole[i]);
        }
        if (frac.Length > 0) sb.Append(english ? '.' : ',').Append(frac);
        return (negative ? "-" : "") + sb;
    }

    private static bool ContainsAny(string s, params string[] words) => words.Any(s.Contains);

    private static bool StartsWithAny(string s, params string[] words) => words.Any(w => s.TrimStart().StartsWith(w));

    /// <summary>Words with their following space, so the stream reads naturally.</summary>
    private static List<string> Pieces(string text)
    {
        var pieces = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            sb.Append(ch);
            if (ch == ' ' || ch == '\n')
            {
                pieces.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0) pieces.Add(sb.ToString());
        return pieces;
    }
}
