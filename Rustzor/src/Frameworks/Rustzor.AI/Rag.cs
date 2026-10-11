using System.Text;
using Rustzor.Interop;

namespace Rustzor.AI;

/// <summary>Turns texts into vectors.</summary>
public interface IEmbedder
{
    Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts);
}

/// <summary>Embeddings from an OpenAI-compatible endpoint.</summary>
public sealed class RemoteEmbedder : IEmbedder
{
    private readonly OpenAiChat _client;
    private readonly string _model;

    public RemoteEmbedder(OpenAiChat client, string model = "text-embedding-3-small")
    {
        _client = client;
        _model = model;
    }

    public Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts) => _client.EmbedAsync(texts, _model);
}

public sealed record VectorRecord(string Id, string Text, float[] Vector, Dictionary<string, string> Metadata);

public sealed record SearchHit(VectorRecord Record, double Score);

/// <summary>
/// An in-memory vector index with cosine similarity. Vectors are normalised on
/// insert so a search is one dot product per record. Serialises to JSON for
/// storage in IndexedDB or localStorage.
/// </summary>
public sealed class VectorStore
{
    private readonly List<VectorRecord> _records = new();

    public int Count => _records.Count;
    public IReadOnlyList<VectorRecord> Records => _records;

    public void Add(string id, string text, float[] vector, Dictionary<string, string>? metadata = null)
    {
        _records.RemoveAll(r => r.Id == id);
        _records.Add(new VectorRecord(id, text, Normalize(vector), metadata ?? new()));
    }

    public void Remove(string id) => _records.RemoveAll(r => r.Id == id);
    public void Clear() => _records.Clear();

    public List<SearchHit> Search(float[] query, int top = 4, Func<VectorRecord, bool>? filter = null)
    {
        var q = Normalize(query);
        var hits = new List<SearchHit>();
        foreach (var r in _records)
        {
            if (filter is not null && !filter(r)) continue;
            hits.Add(new SearchHit(r, Dot(q, r.Vector)));
        }
        hits.Sort((a, b) => b.Score.CompareTo(a.Score));
        return hits.Count > top ? hits.GetRange(0, top) : hits;
    }

    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += v[i] * v[i];
        var len = Math.Sqrt(sum);
        if (len < 1e-12) return v;
        var o = new float[v.Length];
        for (var i = 0; i < v.Length; i++) o[i] = (float)(v[i] / len);
        return o;
    }

    public static double Dot(float[] a, float[] b)
    {
        double s = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public string ToJson()
    {
        var w = new JsonWriter().BeginArray();
        foreach (var r in _records)
        {
            w.BeginObject().Prop("id", r.Id).Prop("text", r.Text).Name("v").BeginArray();
            foreach (var x in r.Vector) w.Number((double)x);
            w.EndArray().Name("meta").BeginObject();
            foreach (var kv in r.Metadata) w.Prop(kv.Key, kv.Value);
            w.EndObject().EndObject();
        }
        return w.EndArray().ToString();
    }

    public static VectorStore FromJson(string json)
    {
        var store = new VectorStore();
        foreach (var r in JsonValue.Parse(json).Items)
        {
            var meta = new Dictionary<string, string>();
            foreach (var k in r["meta"].Keys) meta[k] = r["meta"][k].AsString();
            store._records.Add(new VectorRecord(r["id"].AsString(), r["text"].AsString(), r["v"].Items.Select(x => (float)x.AsDouble()).ToArray(), meta));
        }
        return store;
    }
}

/// <summary>Splits text into overlapping chunks on paragraph and sentence boundaries.</summary>
public static class TextChunker
{
    public static List<string> Split(string text, int maxChars = 800, int overlap = 120)
    {
        var chunks = new List<string>();
        var sentences = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in text.Replace("\r", ""))
        {
            sb.Append(c);
            if (c is '.' or '!' or '?' or '\n')
            {
                var s = sb.ToString().Trim();
                if (s.Length > 0) sentences.Add(s);
                sb.Clear();
            }
        }
        if (sb.ToString().Trim().Length > 0) sentences.Add(sb.ToString().Trim());

        var current = new StringBuilder();
        foreach (var s in sentences)
        {
            if (current.Length + s.Length + 1 > maxChars && current.Length > 0)
            {
                var chunk = current.ToString();
                chunks.Add(chunk);
                current.Clear();
                if (overlap > 0) current.Append(chunk[Math.Max(0, chunk.Length - overlap)..]).Append(' ');
            }
            // A single sentence longer than a chunk is cut hard.
            var rest = s;
            while (rest.Length > maxChars)
            {
                chunks.Add(rest[..maxChars]);
                rest = rest[(maxChars - overlap)..];
            }
            current.Append(rest).Append(' ');
        }
        if (current.ToString().Trim().Length > 0) chunks.Add(current.ToString().Trim());
        return chunks;
    }
}

public sealed record RagAnswer(string Text, List<SearchHit> Sources);

/// <summary>
/// Retrieval-augmented generation: index documents, then answer questions
/// from the most relevant chunks, citing them.
/// </summary>
public sealed class RagPipeline
{
    private readonly IEmbedder _embedder;
    private readonly IChatModel _chat;

    public RagPipeline(IEmbedder embedder, IChatModel chat, VectorStore? store = null)
    {
        _embedder = embedder;
        _chat = chat;
        Store = store ?? new VectorStore();
    }

    public VectorStore Store { get; }
    public int TopK { get; set; } = 4;
    public string Instructions { get; set; } =
        "Answer using only the numbered context. Cite sources like [1]. If the context does not contain the answer, say you do not know.";

    public async Task<int> IndexAsync(string documentId, string text, Dictionary<string, string>? metadata = null)
    {
        var chunks = TextChunker.Split(text);
        for (var start = 0; start < chunks.Count; start += 64)
        {
            var batch = chunks.Skip(start).Take(64).ToList();
            var vectors = await _embedder.EmbedAsync(batch);
            for (var i = 0; i < batch.Count; i++)
            {
                var meta = new Dictionary<string, string>(metadata ?? new()) { ["doc"] = documentId };
                Store.Add($"{documentId}#{start + i}", batch[i], vectors[i], meta);
            }
        }
        return chunks.Count;
    }

    public async Task<RagAnswer> AskAsync(string question, Action<string>? onText = null)
    {
        var q = (await _embedder.EmbedAsync(new[] { question }))[0];
        var hits = Store.Search(q, TopK);
        var context = new StringBuilder();
        for (var i = 0; i < hits.Count; i++) context.AppendLine($"[{i + 1}] {hits[i].Record.Text}");
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(Instructions),
            ChatMessage.User($"Context:\n{context}\nQuestion: {question}"),
        };
        var text = onText is null ? (await _chat.CompleteAsync(messages)).Text : await _chat.StreamAsync(messages, onText);
        return new RagAnswer(text, hits);
    }
}
