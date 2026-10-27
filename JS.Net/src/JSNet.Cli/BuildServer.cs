using System.IO.Pipes;
using JSNet.Sdk.Tasks;

namespace JSNet.Cli;

/// <summary>
/// <c>jsnet server</c> — the JS.Net build server. JSNet.Sdk's <see cref="JsNetCompile"/> task sends <c>compile</c>
/// requests over a named pipe (current user only); they run in this process, which stays loaded and JIT-compiled
/// between builds, several at a time. The server exits after <c>JSNET_SERVER_IDLE_SECONDS</c> (default 600) without
/// requests, or on <c>jsnet server --stop</c>.
/// </summary>
internal static class BuildServer
{
    public static async Task<int> RunCommand(string[] args)
    {
        string? pipe = null;
        bool stop = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pipe") pipe = args[++i];
            else if (args[i] == BuildServerProtocol.StopCommand) stop = true;
        }
        if (stop)
        {
            bool stopped = pipe is null ? BuildServerClient.Stop(typeof(BuildServer).Assembly.Location) : StopPipe(pipe);
            Console.WriteLine(stopped ? "jsnet: build server stopped" : "jsnet: no build server running");
            return 0;
        }
        pipe ??= BuildServerProtocol.PipeName(typeof(BuildServer).Assembly.Location);
        int idle = int.TryParse(Environment.GetEnvironmentVariable("JSNET_SERVER_IDLE_SECONDS"), out int s) && s > 0 ? s : 600;
        return await RunAsync(pipe, TimeSpan.FromSeconds(idle));
    }

    private static bool StopPipe(string pipe)
    {
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
        try { client.Connect(500); }
        catch (TimeoutException) { return false; }
        try
        {
            BuildServerProtocol.WriteRequest(client, Directory.GetCurrentDirectory(), [BuildServerProtocol.StopCommand]);
            BuildServerProtocol.ReadResponse(client);
            return true;
        }
        catch (IOException)
        {
            return false;   // a server that was already shutting down
        }
    }

    public static async Task<int> RunAsync(string pipe, TimeSpan idleTimeout)
    {
        using var shutdown = new CancellationTokenSource();
        int active = 0;
        long lastActivity = Environment.TickCount64;
        bool first = true;

        _ = Task.Run(async () =>
        {
            while (!shutdown.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, idleTimeout.TotalSeconds)), CancellationToken.None);
                if (Volatile.Read(ref active) == 0 && Environment.TickCount64 - Interlocked.Read(ref lastActivity) > idleTimeout.TotalMilliseconds)
                    shutdown.Cancel();
            }
        });

        while (!shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                server = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, options);
            }
            catch (Exception ex) when (first && ex is IOException or UnauthorizedAccessException)
            {
                return 0;   // another server already owns this pipe
            }
            first = false;
            try
            {
                await server.WaitForConnectionAsync(shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }
            Interlocked.Increment(ref active);
            _ = Task.Run(() =>
            {
                try { Serve(server, shutdown); }
                finally
                {
                    server.Dispose();
                    Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
                    Interlocked.Decrement(ref active);
                }
            });
        }
        // Let requests that are still compiling finish.
        while (Volatile.Read(ref active) > 0) await Task.Delay(50);
        return 0;
    }

    private static void Serve(Stream stream, CancellationTokenSource shutdown)
    {
        int exitCode;
        var output = new StringWriter();
        var error = new StringWriter();
        try
        {
            var (version, cwd, args) = BuildServerProtocol.ReadRequest(stream);
            if (version != BuildServerProtocol.Version)
            {
                error.WriteLine($"jsnet: error JSN3000: build server protocol mismatch ('{version}').");
                exitCode = 2;
            }
            else if (args is [BuildServerProtocol.StopCommand])
            {
                shutdown.Cancel();
                exitCode = 0;
            }
            else if (args is ["compile", .. var rest])
            {
                exitCode = Program.Compile([.. rest], cwd, output, error);
            }
            else
            {
                error.WriteLine("jsnet: error JSN3000: the build server only runs 'compile'.");
                exitCode = 2;
            }
        }
        catch (Exception ex) when (ex is not (IOException or EndOfStreamException))
        {
            error.WriteLine($"jsnet: error JSN3000: {ex.Message}");
            exitCode = 2;
        }
        catch (Exception)
        {
            return;   // the client went away
        }

        var lines = Lines(output, false).Concat(Lines(error, true)).ToList();
        try { BuildServerProtocol.WriteResponse(stream, exitCode, lines); }
        catch (IOException) { /* the client went away */ }
    }

    private static IEnumerable<OutputLine> Lines(StringWriter writer, bool isError) =>
        writer.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Select(l => new OutputLine(isError, l));
}
