using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using JSNet.LanguageServer;

namespace JSNet.Tests;

/// <summary>Drives the language server in-process over pipes, like an editor would.</summary>
public sealed class LanguageServerTests : IAsyncLifetime
{
    private AnonymousPipeServerStream _toServer = null!;
    private AnonymousPipeClientStream _serverIn = null!;
    private AnonymousPipeServerStream _fromServer = null!;
    private AnonymousPipeClientStream _clientIn = null!;
    private Task<int> _server = null!;
    private int _nextId;
    private const string Uri = "file:///C:/jsnet-lsp-test/demo.jsn";

    public async Task InitializeAsync()
    {
        _toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        _serverIn = new AnonymousPipeClientStream(PipeDirection.In, _toServer.ClientSafePipeHandle);
        _fromServer = new AnonymousPipeServerStream(PipeDirection.Out);
        _clientIn = new AnonymousPipeClientStream(PipeDirection.In, _fromServer.ClientSafePipeHandle);
        _server = Task.Run(() => LanguageServerHost.RunAsync(_serverIn, _fromServer));
        await Request("initialize", new JsonObject());
    }

    public async Task DisposeAsync()
    {
        await Request("shutdown", null);
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "exit" });
        await _server.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var s in new Stream[] { _toServer, _serverIn, _fromServer, _clientIn }) await s.DisposeAsync();
    }

    private async Task Send(JsonObject message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _toServer.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        await _toServer.WriteAsync(body);
        await _toServer.FlushAsync();
    }

    private async Task<JsonNode> Read()
    {
        var header = new StringBuilder();
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int b = _clientIn.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            header.Append((char)b);
        }
        int length = int.Parse(header.ToString().Split(':')[1].Trim());
        var buffer = new byte[length];
        int read = 0;
        while (read < length) read += await _clientIn.ReadAsync(buffer.AsMemory(read));
        return JsonNode.Parse(buffer)!;
    }

    private async Task<JsonNode?> Request(string method, JsonNode? @params)
    {
        int id = ++_nextId;
        await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        while (true)
        {
            var msg = await Read().WaitAsync(TimeSpan.FromSeconds(60));
            if (msg["id"]?.GetValue<int>() == id) return msg["result"];
        }
    }

    private Task Open(string text) => Send(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "textDocument/didOpen",
        ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri, ["languageId"] = "jsnet", ["version"] = 1, ["text"] = text } },
    });

    private static JsonObject Position(int line, int character) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = Uri },
        ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
    };

    private static HashSet<string> Labels(JsonNode? items) =>
        items!.AsArray().Select(i => i!["label"]!.GetValue<string>()).ToHashSet();

    [Fact]
    public async Task CompletesDotNetMembersOfInferredTypes()
    {
        await Open("import System.Text;\nimport System.IO;\nconst sb = new StringBuilder();\nsb.\nconst xs = [1, 2, 3];\nxs.\nFile.\n");
        var sb = Labels(await Request("textDocument/completion", Position(3, 3)));
        Assert.Contains("Append", sb);
        Assert.Contains("AppendLine", sb);
        var xs = Labels(await Request("textDocument/completion", Position(5, 3)));
        Assert.Contains("Add", xs);       // List<int> (CLR)
        Assert.Contains("push", xs);      // JS.Net runtime extension member
        Assert.Contains("length", xs);
        var file = Labels(await Request("textDocument/completion", Position(6, 5)));
        Assert.Contains("ReadAllText", file);
    }

    [Fact]
    public async Task HoverShowsDotNetSignatures()
    {
        await Open("const sb = new System.Text.StringBuilder();\nsb.AppendLine(\"x\");\n");
        var hover = await Request("textDocument/hover", Position(1, 5));
        Assert.Contains("AppendLine", hover!["contents"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task FindsReferencesAndRenames()
    {
        await Open("function total(n: int): int { return n * 2; }\nconsole.log(total(2), total(3));\n");
        var refs = await Request("textDocument/references", Position(1, 14));
        Assert.Equal(3, refs!.AsArray().Count);
        var p = Position(0, 10);
        p["newName"] = "double2";
        var edit = await Request("textDocument/rename", p);
        Assert.Equal(3, edit!["changes"]![Uri]!.AsArray().Count);
        var notRenameable = Position(1, 3);  // console is a runtime global
        Assert.Null(await Request("textDocument/prepareRename", notRenameable));
    }

    [Fact]
    public async Task ProvidesSemanticTokens()
    {
        await Open("// note\nconst n: number = 42;\nconsole.log(`x ${n}`);\n");
        var result = await Request("textDocument/semanticTokens/full", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri } });
        var data = result!["data"]!.AsArray().Select(v => v!.GetValue<int>()).ToList();
        Assert.Equal(0, data.Count % 5);
        var types = Enumerable.Range(0, data.Count / 5).Select(i => SemanticTokens.Legend[data[i * 5 + 3]]).ToList();
        Assert.Equal("comment", types[0]);    // // note
        Assert.Equal("keyword", types[1]);    // const
        Assert.Contains("type", types);        // number
        Assert.Contains("number", types);      // 42
        Assert.Contains("function", types);    // log(
        Assert.Contains("string", types);      // template
    }

    [Fact]
    public async Task OffersQuickFixes()
    {
        await Open("const limit = 1;\nlimit = 2;\nconsole.log(limit);\n");
        var p = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 1, ["character"] = 0 }, ["end"] = new JsonObject { ["line"] = 1, ["character"] = 9 } },
            ["context"] = new JsonObject
            {
                ["diagnostics"] = new JsonArray(new JsonObject
                {
                    ["code"] = "JSN1001",
                    ["message"] = "Cannot assign to 'limit' because it is a constant.",
                    ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 1, ["character"] = 0 }, ["end"] = new JsonObject { ["line"] = 1, ["character"] = 9 } },
                }),
            },
        };
        var actions = await Request("textDocument/codeAction", p);
        var action = Assert.Single(actions!.AsArray());
        Assert.Equal("Change 'limit' to let", action!["title"]!.GetValue<string>());
        Assert.Equal("let", action["edit"]!["changes"]![Uri]![0]!["newText"]!.GetValue<string>());
    }
}
