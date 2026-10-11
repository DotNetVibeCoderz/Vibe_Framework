using System.IO.Compression;

namespace Rustzor.Cli;

/// <summary>rustzor publish: dotnet publish in Release, then a report of what will be downloaded.</summary>
public static class PublishCommand
{
    public static async Task<int> RunAsync(Options o)
    {
        var project = Project.Find(o.Positional.FirstOrDefault());
        var aot = o.Has("aot");
        var output = o.Get("o", "output");

        var args = $"publish \"{project}\" -c Release -nologo -v q";
        if (aot) args += " -p:RustzorAot=true";
        if (output is not null) args += $" -o \"{Path.GetFullPath(output)}\"";

        Out.Step($"Publishing {Path.GetFileName(project)}{(aot ? " with AOT" : "")}");
        var (code, log) = await Proc.CaptureAsync("dotnet", args);
        if (code != 0)
        {
            Console.WriteLine(log.Trim());
            throw new CliException("The publish failed.");
        }

        var publishDir = output is not null
            ? Path.GetFullPath(output)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, (await Project.PropertyAsync(project, "PublishDir", "Release"))));
        var site = Path.Combine(publishDir, "wwwroot");
        if (!Directory.Exists(site)) throw new CliException($"The publish finished but there is no site at {site}.");

        Report(site);
        Out.Ok($"Ready to upload: {Out.Bold(site)}");
        Console.WriteLine(Out.Dim("  Any static host works (GitHub Pages, Azure Static Web Apps, Netlify, Cloudflare Pages, S3, nginx)."));
        Console.WriteLine(Out.Dim("  Serve index.html for unknown paths so client-side routes survive a reload."));
        return 0;
    }

    /// <summary>Sizes by kind, raw and gzipped: the gzipped figure is close to what a browser downloads.</summary>
    private static void Report(string site)
    {
        var rows = Directory.GetFiles(site, "*", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetExtension(f).ToLowerInvariant() switch
            {
                ".wasm" => "WebAssembly",
                ".dll" => "assemblies",
                ".js" => "JavaScript",
                ".css" => "styles",
                ".html" => "pages",
                _ => "other",
            })
            .Select(g => (Kind: g.Key, Count: g.Count(), Raw: g.Sum(f => new FileInfo(f).Length), Gz: g.Sum(Gzipped)))
            .OrderByDescending(r => r.Raw)
            .ToList();
        Console.WriteLine();
        foreach (var r in rows)
            Console.WriteLine($"  {r.Kind,-12} {r.Count,4} files  {Out.Size(r.Raw),10}  {Out.Dim($"{Out.Size(r.Gz)} gzipped")}");
        Console.WriteLine($"  {"total",-12} {rows.Sum(r => r.Count),4} files  {Out.Size(rows.Sum(r => r.Raw)),10}  {Out.Dim($"{Out.Size(rows.Sum(r => r.Gz))} gzipped")}");
        Console.WriteLine();
    }

    private static long Gzipped(string file)
    {
        using var input = File.OpenRead(file);
        using var counter = new CountingStream();
        using (var gz = new GZipStream(counter, CompressionLevel.Optimal, leaveOpen: true)) input.CopyTo(gz);
        return counter.Length;
    }

    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
    }
}
