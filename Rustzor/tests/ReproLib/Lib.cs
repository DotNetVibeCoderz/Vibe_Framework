namespace ReproLib;
public interface IEmbedder { Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts); }
public static class Runner
{
    public static async Task<int> Run(IEmbedder e) => (await e.EmbedAsync(new[] { "abc" })).Count;
}
public sealed class LibEx : Exception { public LibEx(string op) : base(op + ": failed") { } }
