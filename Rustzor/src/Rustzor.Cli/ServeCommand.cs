using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Rustzor.Cli;

/// <summary>
/// rustzor serve: build the app, serve its site, and while it runs rebuild on
/// every save and tell the open pages to reload — or show the build errors
/// over the page when the build fails.
/// </summary>
public static class ServeCommand
{
    private const string DevPath = "/_rustzor-dev";

    public static async Task<int> RunAsync(Options o)
    {
        var project = Project.Find(o.Positional.FirstOrDefault());
        var port = int.TryParse(o.Get("p", "port"), out var p) ? p : 5080;
        var watch = !o.Has("no-watch");

        Out.Step($"Building {Path.GetFileName(project)}");
        var (ok, log) = await BuildAsync(project);
        if (!ok)
        {
            Console.WriteLine(log);
            throw new CliException("The build failed; fix the errors above and run rustzor serve again.");
        }
        var site = await Project.SiteAsync(project);
        if (!File.Exists(Path.Combine(site, "index.html")))
            throw new CliException($"The build produced no site at {site}. Is this a Rustzor app (<Project Sdk=\"Rustzor.Sdk/...\">)?");

        var reload = new Broadcaster();
        var app = CreateServer(site, port, reload);
        await app.StartAsync();
        var url = $"http://localhost:{port}/";
        Out.Ok($"Serving {Out.Bold(url)}  {Out.Dim(site)}");

        using var watcher = watch ? new Rebuilder(project, reload) : null;
        if (watch) Out.Step("Watching for changes. Press Ctrl+C to stop.");
        if (o.Has("open")) Proc.OpenBrowser(url);

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        await stop.Task;
        await app.StopAsync();
        return 0;
    }

    /// <summary>`dotnet build`, quiet; the log is returned for showing when it fails.</summary>
    public static async Task<(bool Ok, string Log)> BuildAsync(string project)
    {
        var (code, output) = await Proc.CaptureAsync("dotnet", $"build \"{project}\" -nologo -v q -clp:NoSummary");
        return (code == 0, output.Trim());
    }

    private static WebApplication CreateServer(string site, int port, Broadcaster reload)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = site });
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        var files = new PhysicalFileProvider(site);
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".dll"] = "application/octet-stream";
        types.Mappings[".pdb"] = "application/octet-stream";
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".dat"] = "application/octet-stream";
        var script = ReadResource("livereload.js");

        app.Use(async (context, next) =>
        {
            // During development nothing is cached: every reload sees the new build.
            context.Response.Headers.CacheControl = "no-store";
            await next();
        });

        app.Map(DevPath + "/livereload.js", (HttpContext c) => Results.Text(script, "text/javascript"));
        app.Map(DevPath + "/events", async (HttpContext c) =>
        {
            c.Response.Headers.ContentType = "text/event-stream";
            var reader = reload.Subscribe();
            try
            {
                await c.Response.WriteAsync("event: hello\ndata: {}\n\n", c.RequestAborted);
                await c.Response.Body.FlushAsync(c.RequestAborted);
                await foreach (var message in reader.ReadAllAsync(c.RequestAborted))
                {
                    await c.Response.WriteAsync(message, c.RequestAborted);
                    await c.Response.Body.FlushAsync(c.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                reload.Unsubscribe(reader);
            }
        });

        // index.html (for "/" and for any route the client handles) gets the live-reload script.
        async Task SendIndex(HttpContext c)
        {
            var html = await File.ReadAllTextAsync(Path.Combine(site, "index.html"));
            var tag = $"<script src=\"{DevPath}/livereload.js\"></script>";
            html = html.Contains("</body>", StringComparison.OrdinalIgnoreCase)
                ? html.Replace("</body>", tag + "</body>", StringComparison.OrdinalIgnoreCase)
                : html + tag;
            c.Response.ContentType = "text/html; charset=utf-8";
            await c.Response.WriteAsync(html);
        }

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "/";
            if (path == "/" || path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase))
            {
                await SendIndex(context);
                return;
            }
            await next();
        });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types, ServeUnknownFileTypes = true });
        // A path that is not a file is a client-side route: answer with the app.
        app.MapFallback(async c =>
        {
            if (Path.HasExtension(c.Request.Path.Value)) { c.Response.StatusCode = 404; return; }
            await SendIndex(c);
        });
        return app;
    }

    private static string ReadResource(string name)
    {
        using var s = typeof(ServeCommand).Assembly.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

/// <summary>Sends server-sent events to every open page.</summary>
public sealed class Broadcaster
{
    private readonly List<Channel<string>> _clients = new();

    public ChannelReader<string> Subscribe()
    {
        var c = Channel.CreateUnbounded<string>();
        lock (_clients) _clients.Add(c);
        return c.Reader;
    }

    public void Unsubscribe(ChannelReader<string> reader)
    {
        lock (_clients) _clients.RemoveAll(c => c.Reader == reader);
    }

    public int Send(string eventName, string json)
    {
        var data = new StringBuilder($"event: {eventName}\n");
        foreach (var line in json.Split('\n')) data.Append("data: ").Append(line).Append('\n');
        data.Append('\n');
        lock (_clients)
        {
            foreach (var c in _clients) c.Writer.TryWrite(data.ToString());
            return _clients.Count;
        }
    }
}

/// <summary>Watches the project's sources and rebuilds after a burst of saves settles.</summary>
public sealed class Rebuilder : IDisposable
{
    private static readonly string[] Watched = { ".razor", ".cs", ".css", ".html", ".js", ".json", ".csproj", ".png", ".svg", ".jpg", ".webp", ".ico", ".woff2" };

    private readonly string _project;
    private readonly Broadcaster _reload;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly SemaphoreSlim _building = new(1, 1);
    private bool _pending;

    public Rebuilder(string project, Broadcaster reload)
    {
        _project = project;
        _reload = reload;
        _debounce = new Timer(_ => _ = RebuildAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(project)!)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
        };
        _watcher.Changed += OnChange;
        _watcher.Created += OnChange;
        _watcher.Deleted += OnChange;
        _watcher.Renamed += OnChange;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChange(object sender, FileSystemEventArgs e)
    {
        var rel = Path.GetRelativePath(Path.GetDirectoryName(_project)!, e.FullPath).Replace('\\', '/');
        if (rel.StartsWith("bin/") || rel.StartsWith("obj/") || rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith(".")) return;
        if (!Watched.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase)) return;
        _debounce.Change(300, Timeout.Infinite);
    }

    private async Task RebuildAsync()
    {
        if (!await _building.WaitAsync(0))
        {
            _pending = true;
            return;
        }
        try
        {
            do
            {
                _pending = false;
                var started = DateTime.Now;
                Out.Step("Change detected, rebuilding");
                _reload.Send("building", "{}");
                var (ok, log) = await ServeCommand.BuildAsync(_project);
                var ms = (int)(DateTime.Now - started).TotalMilliseconds;
                if (ok)
                {
                    var pages = _reload.Send("reload", "{}");
                    Out.Ok($"Rebuilt in {ms} ms{(pages > 0 ? $", reloaded {pages} page{(pages == 1 ? "" : "s")}" : "")}");
                }
                else
                {
                    var errors = string.Join("\n", log.Split('\n').Where(l => l.Contains(": error ")).Select(Tidy).Distinct().Take(20));
                    if (errors.Length == 0) errors = log;
                    Out.Error("Build failed:\n" + errors);
                    _reload.Send("build-error", System.Text.Json.JsonSerializer.Serialize(new { text = errors }));
                }
            }
            while (_pending);
        }
        finally
        {
            _building.Release();
        }
    }

    /// <summary>"C:\…\App\Pages\Home.razor(20,22): error CS1525: … [C:\…\App.csproj]" becomes "Pages/Home.razor(20,22): error CS1525: …".</summary>
    private string Tidy(string line)
    {
        line = line.Trim();
        var suffix = line.LastIndexOf(" [", StringComparison.Ordinal);
        if (suffix > 0 && line.EndsWith(".csproj]", StringComparison.OrdinalIgnoreCase)) line = line[..suffix];
        var root = Path.GetDirectoryName(_project)! + Path.DirectorySeparatorChar;
        if (line.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var paren = line.IndexOf('(');
            var path = paren > 0 ? line[root.Length..paren] : line[root.Length..];
            line = path.Replace('\\', '/') + (paren > 0 ? line[paren..] : "");
        }
        return line;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
