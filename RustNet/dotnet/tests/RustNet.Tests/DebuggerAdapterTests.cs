using System.Text;
using System.Text.Json.Nodes;
using RustNet.Debugger;
using RustNet.Deploy;
using RustNet.MetadataProcessor;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// Unit tests for the DAP adapter's pure pieces: the Content-Length message
/// framing and the RNX debug-info source/IL mapping. The full launch/flash flow
/// is covered on-device by <see cref="EndToEndTests.DebuggerBreakpointCycle"/>.
/// </summary>
public class DebuggerAdapterTests
{
    [Fact]
    public void DapProtocolFramesAndParsesMessages()
    {
        // A request framed the way VSCode sends it.
        var request = new JsonObject
        {
            ["seq"] = 1,
            ["type"] = "request",
            ["command"] = "initialize",
        };
        byte[] body = Encoding.UTF8.GetBytes(request.ToJsonString());
        byte[] framed = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n")
            .Concat(body).ToArray();

        using var input = new MemoryStream(framed);
        using var output = new MemoryStream();
        var dap = new DapProtocol(input, output);

        JsonObject? read = dap.Read();
        Assert.NotNull(read);
        Assert.Equal("initialize", read!["command"]!.GetValue<string>());

        // A response round-trips back through the same framing.
        dap.SendResponse(read, new JsonObject { ["supportsConfigurationDoneRequest"] = true });
        string wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.StartsWith("Content-Length:", wire);
        int split = wire.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var resp = JsonNode.Parse(wire[(split + 4)..]) as JsonObject;
        Assert.NotNull(resp);
        Assert.Equal("response", resp!["type"]!.GetValue<string>());
        Assert.Equal("initialize", resp["command"]!.GetValue<string>());
        Assert.True(resp["success"]!.GetValue<bool>());
        Assert.True(resp["body"]!["supportsConfigurationDoneRequest"]!.GetValue<bool>());
    }

    [Fact]
    public void RnxDebugInfoMapsSourceLinesToSites()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "SampleApp.dll");
        byte[] rnx = RnxCompiler.Compile(dll, out _);
        var di = RnxDebugInfo.Parse(rnx);

        Assert.NotNull(di.EntryMethod);
        // The compiler emitted sequence points for the entry method.
        var entry = di.Methods[(int)di.EntryMethod!.Value];
        Assert.NotEmpty(entry.Points);

        // Every executable line round-trips: line -> site -> line.
        (uint il, uint line) = entry.Points[0];
        var site = di.SiteForLine((int)line);
        Assert.NotNull(site);
        Assert.Equal(line, di.LineAt(site!.Value.Method, site.Value.Il));
        Assert.Contains((int)line, di.ExecutableLines());
    }
}
