using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RustNet.Deploy;
using RustNet.MetadataProcessor;

namespace RustNet.Debugger;

/// <summary>
/// A Debug Adapter Protocol (DAP) server for RustNet apps. VSCode launches this
/// over stdio; it compiles the app, flashes it to the device, and bridges DAP
/// requests (breakpoints, stepping, stack, variables) to the on-device
/// interpreter debugger via RNDP. Source lines map to (method, IL offset) sites
/// through the RNX debug section (<see cref="RnxDebugInfo"/>).
/// </summary>
public static class Program
{
    public static int Main()
    {
        var dap = new DapProtocol(Console.OpenStandardInput(), Console.OpenStandardOutput());
        var session = new DebugSession(dap);
        try
        {
            while (dap.Read() is { } msg)
            {
                if (msg["type"]?.GetValue<string>() == "request")
                {
                    session.Handle(msg);
                    if (session.Done)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rustnet-debugger: {ex.Message}");
            return 1;
        }
        session.Shutdown();
        return 0;
    }
}

public sealed partial class DebugSession(DapProtocol dap)
{
    private RndpClient? _client;
    private RnxDebugInfo? _debug;
    private string _appName = "app";
    private string? _sourcePath;
    private readonly List<(uint Method, uint Il)> _breakpoints = new();
    private Thread? _poller;
    private volatile bool _running;
    private (uint Method, uint Il)? _lastStop;
    public bool Done { get; private set; }

    [GeneratedRegex(@"^(?<name>.+?) @IL_(?<il>[0-9a-fA-F]+)(?: \(line (?<line>\d+)\))?")]
    private static partial Regex StackLine();

    public void Handle(JsonObject request)
    {
        string command = request["command"]?.GetValue<string>() ?? "";
        try
        {
            switch (command)
            {
                case "initialize": Initialize(request); break;
                case "launch": Launch(request); break;
                case "setBreakpoints": SetBreakpoints(request); break;
                case "configurationDone": ConfigurationDone(request); break;
                case "threads": Threads(request); break;
                case "stackTrace": StackTrace(request); break;
                case "scopes": Scopes(request); break;
                case "variables": Variables(request); break;
                case "continue": Continue(request); break;
                case "next" or "stepIn" or "stepOut": Step(request); break;
                case "pause": Pause(request); break;
                case "disconnect" or "terminate": Disconnect(request); break;
                default: dap.SendResponse(request); break; // ack unknown requests
            }
        }
        catch (Exception ex)
        {
            dap.SendResponse(request, success: false, message: ex.Message);
        }
    }

    private void Initialize(JsonObject request)
    {
        dap.SendResponse(request, new JsonObject
        {
            ["supportsConfigurationDoneRequest"] = true,
            ["supportsTerminateRequest"] = true,
        });
    }

    private void Launch(JsonObject request)
    {
        JsonObject args = request["arguments"] as JsonObject ?? new JsonObject();
        string program = Str(args, "program") ?? throw new ArgumentException("launch: 'program' (app dll) is required");
        string device = Str(args, "device") ?? "tcp:127.0.0.1:7878";
        string keyPath = Str(args, "key") ?? throw new ArgumentException("launch: 'key' (signing key) is required");
        _appName = Str(args, "name") ?? Path.GetFileNameWithoutExtension(program).ToLowerInvariant();
        var chip = Signing.ParseChip(Str(args, "chip") ?? "host-sim");

        byte[] rnx = program.EndsWith(".rnx", StringComparison.OrdinalIgnoreCase)
            ? File.ReadAllBytes(program)
            : RnxCompiler.Compile(program, out _);
        _debug = RnxDebugInfo.Parse(rnx);

        // The device must already be provisioned with the matching public key
        // (`rustnet provision`); the adapter only flashes a signed image.
        _client = RndpClient.Connect(device);
        byte[] sealedApp = Signing.Seal(ImageKind.App, chip, rnx, File.ReadAllBytes(keyPath));
        _client.FlashApp(_appName, sealedApp);
        dap.Output($"flashed '{_appName}' to {device}\n");

        dap.SendResponse(request);
        dap.SendEvent("initialized");
    }

    private void SetBreakpoints(JsonObject request)
    {
        JsonObject args = request["arguments"] as JsonObject ?? new JsonObject();
        _sourcePath ??= (args["source"] as JsonObject)?["path"]?.GetValue<string>();

        // Clear the previous set, then apply the new one.
        if (_client is not null)
        {
            foreach (var (m, il) in _breakpoints)
            {
                TryClear(m, il);
            }
        }
        _breakpoints.Clear();

        var verified = new JsonArray();
        var lines = args["breakpoints"] as JsonArray;
        if (lines is not null && _debug is not null)
        {
            foreach (var bpNode in lines)
            {
                int line = (bpNode as JsonObject)?["line"]?.GetValue<int>() ?? 0;
                var site = _debug.SiteForLine(line);
                var bp = new JsonObject { ["verified"] = false, ["line"] = line };
                if (site is { } s)
                {
                    _breakpoints.Add(s);
                    _client?.DebugSetBreakpoint(s.Method, s.Il);
                    bp["verified"] = true;
                }
                verified.Add(bp);
            }
        }
        dap.SendResponse(request, new JsonObject { ["breakpoints"] = verified });
    }

    private void ConfigurationDone(JsonObject request)
    {
        dap.SendResponse(request);
        _client?.StartApp(_appName);
        _running = true;
        _poller = new Thread(PollLoop) { IsBackground = true };
        _poller.Start();
    }

    private void Threads(JsonObject request)
    {
        dap.SendResponse(request, new JsonObject
        {
            ["threads"] = new JsonArray { new JsonObject { ["id"] = 1, ["name"] = "app" } },
        });
    }

    private void StackTrace(JsonObject request)
    {
        var frames = new JsonArray();
        if (_client is not null)
        {
            string raw;
            try { raw = _client.DebugStack(); }
            catch { raw = ""; }
            int id = 0;
            foreach (string lineText in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var m = StackLine().Match(lineText);
                if (!m.Success)
                {
                    continue;
                }
                string full = m.Groups["name"].Value;
                int c = full.LastIndexOf("::", StringComparison.Ordinal);
                string name = c >= 0 ? full[(c + 2)..] : full;
                var frame = new JsonObject
                {
                    ["id"] = id++,
                    ["name"] = name,
                    ["line"] = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : 0,
                    ["column"] = 1,
                };
                if (_sourcePath is not null)
                {
                    frame["source"] = new JsonObject
                    {
                        ["name"] = Path.GetFileName(_sourcePath),
                        ["path"] = _sourcePath,
                    };
                }
                frames.Add(frame);
            }
        }
        dap.SendResponse(request, new JsonObject
        {
            ["stackFrames"] = frames,
            ["totalFrames"] = frames.Count,
        });
    }

    private void Scopes(JsonObject request)
    {
        dap.SendResponse(request, new JsonObject
        {
            ["scopes"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "Locals",
                    ["variablesReference"] = 1,
                    ["expensive"] = false,
                },
            },
        });
    }

    private void Variables(JsonObject request)
    {
        var vars = new JsonArray();
        if (_client is not null)
        {
            string raw;
            try { raw = _client.DebugLocals(); }
            catch { raw = ""; }
            foreach (string lineText in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = lineText.IndexOf('=');
                string name = eq >= 0 ? lineText[..eq].Trim() : lineText.Trim();
                string value = eq >= 0 ? lineText[(eq + 1)..].Trim() : "";
                vars.Add(new JsonObject
                {
                    ["name"] = name,
                    ["value"] = value,
                    ["variablesReference"] = 0,
                });
            }
        }
        dap.SendResponse(request, new JsonObject { ["variables"] = vars });
    }

    private void Continue(JsonObject request)
    {
        _lastStop = null;
        TryDebug(() => _client?.DebugContinue());
        _running = true;
        dap.SendResponse(request, new JsonObject { ["allThreadsContinued"] = true });
    }

    private void Step(JsonObject request)
    {
        _lastStop = null;
        TryDebug(() => _client?.DebugStep());
        _running = true;
        dap.SendResponse(request);
    }

    private void Pause(JsonObject request)
    {
        // The interpreter pauses only at breakpoints/steps; acknowledge.
        dap.SendResponse(request);
    }

    private void Disconnect(JsonObject request)
    {
        dap.SendResponse(request);
        Shutdown();
        Done = true;
    }

    private void PollLoop()
    {
        while (_running)
        {
            Thread.Sleep(50);
            (uint Method, uint Il)? state;
            try { state = _client?.DebugState(); }
            catch { break; }

            if (state is { } s)
            {
                if (_lastStop != s)
                {
                    _lastStop = s;
                    dap.SendEvent("stopped", new JsonObject
                    {
                        ["reason"] = "breakpoint",
                        ["threadId"] = 1,
                        ["allThreadsStopped"] = true,
                    });
                }
            }
            else if (AppFinished())
            {
                dap.SendEvent("terminated");
                dap.SendEvent("exited", new JsonObject { ["exitCode"] = 0 });
                _running = false;
                break;
            }
        }
    }

    private bool AppFinished()
    {
        try
        {
            string logs = _client?.GetLogs(50) ?? "";
            return logs.Contains($"app '{_appName}' exited")
                || logs.Contains($"app '{_appName}' crashed");
        }
        catch
        {
            return false;
        }
    }

    private void TryClear(uint method, uint il) => TryDebug(() => _client?.DebugClearBreakpoint(method, il));

    private static void TryDebug(Action a)
    {
        try { a(); }
        catch { /* device may be running/paused transitions */ }
    }

    public void Shutdown()
    {
        _running = false;
        try { _client?.StopApp(); }
        catch { /* ignore */ }
        _client?.Dispose();
        _client = null;
    }

    private static string? Str(JsonObject o, string key) => o[key]?.GetValue<string>();
}
