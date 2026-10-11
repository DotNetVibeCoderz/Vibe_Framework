// The API Playground's server. One small ASP.NET Core app speaks the four
// ways a browser talks to a backend, so the Rustzor client can try each:
//   REST        /api/books, /api/echo, /api/slow, /api/status/{code}
//   WebSocket   /ws         (echo, plus a tick every two seconds)
//   SignalR     /hubs/chat  (rooms, an RPC with a result, a stream, an error)
//   gRPC-Web    playground.Inventory (unary and server streaming)
// It also serves the built client, so one URL runs everything.
// Built by Gravicode Studios, led by Kang Fadhil.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ApiPlayground.Server;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.Services.AddGrpc();
builder.Services.AddSingleton<Books>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
    .WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding", "X-Request-Id", "X-Elapsed-Ms")));

var app = builder.Build();
app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

// Every response says how long it took and carries an id, which the client shows.
app.Use(async (context, next) =>
{
    var started = DateTime.UtcNow;
    var id = Guid.NewGuid().ToString("N")[..8];
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Request-Id"] = id;
        context.Response.Headers["X-Elapsed-Ms"] = ((int)(DateTime.UtcNow - started).TotalMilliseconds).ToString();
        return Task.CompletedTask;
    });
    await next();
});

// ── REST ───────────────────────────────────────────────────────────────────

var api = app.MapGroup("/api");
api.MapGet("/books", (Books books, string? q, string? sort) => books.Find(q, sort));
api.MapGet("/books/{id:int}", (Books books, int id) => books.Get(id) is { } b ? Results.Ok(b) : Results.Problem($"No book has id {id}.", statusCode: 404, title: "Not found"));
api.MapPost("/books", (Books books, Book input) =>
{
    if (string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Author))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["A book needs a title and an author."] });
    var created = books.Add(input);
    return Results.Created($"/api/books/{created.Id}", created);
});
api.MapPut("/books/{id:int}", (Books books, int id, Book input) => books.Update(id, input) is { } b ? Results.Ok(b) : Results.NotFound());
api.MapDelete("/books/{id:int}", (Books books, int id) => books.Delete(id) ? Results.NoContent() : Results.NotFound());
api.MapPost("/books/reset", (Books books) => { books.Reset(); return Results.NoContent(); });
api.MapMethods("/echo", ["GET", "POST", "PUT", "PATCH", "DELETE"], async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Ok(new
    {
        method = request.Method,
        path = request.Path.Value,
        query = request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()),
        headers = request.Headers.Where(h => !h.Key.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase)).ToDictionary(h => h.Key, h => h.Value.ToString()),
        body,
        receivedAt = DateTime.UtcNow,
    });
});
api.MapGet("/slow", async (int? ms) =>
{
    var wait = Math.Clamp(ms ?? 1200, 0, 10_000);
    await Task.Delay(wait);
    return Results.Ok(new { waitedMs = wait });
});
api.MapGet("/status/{code:int}", (int code) => Results.Problem($"You asked for HTTP {code}.", statusCode: Math.Clamp(code, 400, 599)));

// ── WebSocket ──────────────────────────────────────────────────────────────

var sockets = new SocketHub();
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Connect with a WebSocket.");
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await sockets.RunAsync(socket, context.RequestAborted);
});

// ── SignalR and gRPC ───────────────────────────────────────────────────────

app.MapHub<ChatHub>("/hubs/chat");
app.MapGrpcService<InventoryService>();

// ── the client ─────────────────────────────────────────────────────────────

var clientRoot = Path.GetFullPath(app.Configuration["ClientRoot"] ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Client", "bin", "Debug", "net10.0", "wwwroot"));
if (Directory.Exists(clientRoot))
{
    var files = new PhysicalFileProvider(clientRoot);
    var types = new FileExtensionContentTypeProvider();
    types.Mappings[".dll"] = "application/octet-stream";
    types.Mappings[".pdb"] = "application/octet-stream";
    types.Mappings[".wasm"] = "application/wasm";
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types, ServeUnknownFileTypes = true });
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
    app.Logger.LogInformation("Serving the client from {Root}", clientRoot);
}
else
{
    app.MapGet("/", () => Results.Text($"Build the client first (dotnet build samples/ApiPlayground/Client), or set ClientRoot. Looked in {clientRoot}."));
}

app.Run();

/// <summary>The raw WebSocket endpoint: echoes, answers ping, and ticks to everyone.</summary>
sealed class SocketHub
{
    private readonly List<WebSocket> _open = new();
    private readonly Timer _timer;
    private int _tick;

    public SocketHub() => _timer = new Timer(_ => _ = Broadcast(), null, 2000, 2000);

    public async Task RunAsync(WebSocket socket, CancellationToken aborted)
    {
        lock (_open) _open.Add(socket);
        await Send(socket, new { type = "welcome", text = "Connected to the API Playground. Send anything and it comes back; send \"ping\" for a pong.", clients = _open.Count });
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, aborted);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                object reply = text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase)
                    ? new { type = "pong", at = DateTime.UtcNow }
                    : new { type = "echo", text, length = text.Length, reversed = new string(text.Reverse().ToArray()), at = DateTime.UtcNow };
                await Send(socket, reply);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            lock (_open) _open.Remove(socket);
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    }

    private async Task Broadcast()
    {
        WebSocket[] targets;
        lock (_open) targets = _open.ToArray();
        var tick = new { type = "tick", n = ++_tick, at = DateTime.UtcNow, clients = targets.Length, load = Math.Round(0.4 + 0.3 * Math.Sin(_tick / 5.0) + Random.Shared.NextDouble() * 0.1, 2) };
        foreach (var s in targets)
        {
            try { await Send(s, tick); } catch { /* the reader loop cleans up */ }
        }
    }

    private static Task Send(WebSocket socket, object message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web), WebSocketMessageType.Text, true, CancellationToken.None);
}
