using System.Text;
using Rustzor.AI;
using Rustzor.Browser.Storage;
using Rustzor.Interop;

namespace Loket.Model;

public sealed class Doc
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Lang { get; set; } = "id";
    public string Text { get; set; } = "";
    public bool Seeded { get; set; }
    public DateTime Added { get; set; }
}

public sealed class Chunk
{
    public string DocId = "";
    public string DocTitle = "";
    public string Section = "";
    public int Index;
    public string Text = "";
    public Dictionary<string, int> Terms = new();
    public int Length;
}

public sealed record Hit(Chunk Chunk, double Score);

/// <summary>
/// The documents the desk answers from, cut into passages per section and searched with BM25.
/// BM25 needs no model and no network, works the same in Indonesian and English, and is fast
/// enough on a few hundred passages to run on every question.
/// </summary>
public static class Knowledge
{
    private const string UploadsKey = "loket.docs";

    public static List<Doc> Docs { get; } = new();
    public static List<Chunk> Chunks { get; } = new();
    private static readonly Dictionary<string, int> DocFreq = new();
    private static double _avgLength = 1;

    public static void Load()
    {
        Docs.Clear();
        foreach (var (id, title, lang, text) in Seed.Documents)
            Docs.Add(new Doc { Id = id, Title = title, Lang = lang, Text = text.Trim(), Seeded = true });
        Docs.AddRange(WebStorage.Local.GetList<Doc>(UploadsKey));
        Rebuild();
    }

    public static Doc Add(string title, string text)
    {
        var doc = new Doc { Id = "doc-" + Desk.NewId(), Title = title, Text = text.Trim(), Added = DateTime.UtcNow, Lang = GuessEnglish(text) ? "en" : "id" };
        Docs.Add(doc);
        SaveUploads();
        Rebuild();
        return doc;
    }

    public static void Remove(Doc doc)
    {
        if (doc.Seeded) return;
        Docs.Remove(doc);
        SaveUploads();
        Rebuild();
    }

    public static void ResetUploads()
    {
        WebStorage.Local.Remove(UploadsKey);
        Load();
    }

    private static void SaveUploads() => WebStorage.Local.SetJson(UploadsKey, Docs.Where(d => !d.Seeded).ToList());

    public static int ChunkCount(string docId) => Chunks.Count(c => c.DocId == docId);

    private static void Rebuild()
    {
        Chunks.Clear();
        DocFreq.Clear();
        foreach (var doc in Docs)
        {
            var index = 0;
            foreach (var (section, body) in Sections(doc.Text))
            {
                foreach (var piece in TextChunker.Split(body, 700, 80))
                {
                    var chunk = new Chunk { DocId = doc.Id, DocTitle = doc.Title, Section = section, Index = index++, Text = piece };
                    // The document title and the section heading count as words of the passage,
                    // so "biaya" finds the fees section and "KTP-el" the KTP-el procedure.
                    foreach (var term in Terms(doc.Title + " " + section + " " + piece))
                    {
                        chunk.Terms[term] = chunk.Terms.TryGetValue(term, out var n) ? n + 1 : 1;
                        chunk.Length++;
                    }
                    foreach (var term in chunk.Terms.Keys) DocFreq[term] = DocFreq.TryGetValue(term, out var d) ? d + 1 : 1;
                    Chunks.Add(chunk);
                }
            }
        }
        _avgLength = Chunks.Count == 0 ? 1 : Math.Max(1, Chunks.Average(c => c.Length));
    }

    /// <summary>Splits Markdown-ish text at "## " headings; text before any heading is its own section.</summary>
    public static List<(string Section, string Body)> Sections(string text)
    {
        var result = new List<(string, string)>();
        var section = "";
        var body = new StringBuilder();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# ")) continue;
            if (line.StartsWith("## "))
            {
                if (body.ToString().Trim().Length > 0) result.Add((section, body.ToString().Trim()));
                section = line[3..].Trim();
                body.Clear();
                continue;
            }
            body.Append(line).Append('\n');
        }
        if (body.ToString().Trim().Length > 0) result.Add((section, body.ToString().Trim()));
        return result;
    }

    public static List<Hit> Search(string query, int top = 4)
    {
        const double k1 = 1.4, b = 0.75;
        var terms = Terms(query).Distinct().ToList();
        var hits = new List<Hit>();
        if (terms.Count == 0) return hits;
        var n = Chunks.Count;
        foreach (var chunk in Chunks)
        {
            double score = 0;
            foreach (var t in terms)
            {
                if (!chunk.Terms.TryGetValue(t, out var f)) continue;
                var df = DocFreq[t];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * f * (k1 + 1) / (f + k1 * (1 - b + b * chunk.Length / _avgLength));
            }
            if (score > 0) hits.Add(new Hit(chunk, score));
        }
        hits.Sort((x, y) => y.Score.CompareTo(x.Score));
        return hits.Count > top ? hits.GetRange(0, top) : hits;
    }

    private static readonly HashSet<string> Stop = new(
        ("yang dan di ke dari untuk dengan pada ini itu atau juga akan bisa dapat ada tidak saya kami kita anda " +
         "apa apakah bagaimana berapa kapan mana siapa mengapa kenapa harus perlu mau ingin tolong mohon " +
         "the a an and or of to in on for with is are be can do does what how when where which who my i you " +
         "your me we our it this that there as at by from if not please need want").Split(' '));

    public static List<string> Terms(string text)
    {
        var terms = new List<string>();
        var word = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant() + " ")
        {
            if (char.IsLetterOrDigit(ch))
            {
                word.Append(ch);
                continue;
            }
            if (word.Length > 0)
            {
                var w = Stem(word.ToString());
                word.Clear();
                if (w.Length >= 2 && !Stop.Contains(w)) terms.Add(w);
            }
        }
        return terms;
    }

    private static readonly string[] Particles = { "nya", "lah", "kah" };
    private static readonly string[] Suffixes = { "kan", "an" };
    // Longest first; "meny"/"peny" stand for a dropped s (menyimpan -> simpan).
    private static readonly (string Prefix, string Restore)[] Prefixes =
    {
        ("meng", ""), ("meny", "s"), ("mem", "p"), ("men", ""), ("me", ""),
        ("peng", ""), ("peny", "s"), ("pem", "p"), ("pen", ""), ("per", ""), ("pe", ""),
        ("ber", ""), ("ter", ""), ("di", ""), ("ke", ""),
    };

    /// <summary>
    /// A light Indonesian stemmer: particles (-nya, -lah, -kah), then one prefix and one
    /// suffix, so "dijaga" and "menjaga" both become "jaga". It is crude, but the same rules
    /// run on the question and on the documents, so a word only has to stem consistently.
    /// English plurals lose their s.
    /// </summary>
    private static string Stem(string w)
    {
        foreach (var p in Particles)
            if (w.Length > p.Length + 3 && w.EndsWith(p)) { w = w[..^p.Length]; break; }
        if (w.Length > 5)
        {
            foreach (var (prefix, restore) in Prefixes)
            {
                if (w.StartsWith(prefix) && w.Length - prefix.Length >= 4)
                {
                    // per- before a vowel is pe- + r...: perekaman is pe-rekam-an, persyaratan is per-syarat-an.
                    if (prefix == "per" && "aeiou".Contains(w[3])) w = w[2..];
                    else w = restore + w[prefix.Length..];
                    break;
                }
            }
        }
        foreach (var s in Suffixes)
            if (w.Length > s.Length + 4 && w.EndsWith(s)) { w = w[..^s.Length]; break; }
        if (w.Length > 4 && w.EndsWith("s") && !w.EndsWith("ss")) return w[..^1];
        return w;
    }

    public static bool GuessEnglish(string text)
    {
        var en = 0;
        var id = 0;
        foreach (var w in text.ToLowerInvariant().Split(' ', '\n', ',', '.', '?', '!'))
        {
            if (w is "the" or "what" or "how" or "is" or "are" or "do" or "i" or "my" or "need" or "where" or "when" or "can" or "you" or "to" or "for" or "of") en++;
            if (w is "yang" or "apa" or "bagaimana" or "saya" or "di" or "ke" or "untuk" or "berapa" or "kapan" or "bisa" or "dan" or "syarat" or "mau" or "tolong") id++;
        }
        return en > id;
    }
}
