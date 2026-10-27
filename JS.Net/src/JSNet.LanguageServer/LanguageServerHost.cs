using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JSNet.Compiler;
using JSNet.Syntax;

namespace JSNet.LanguageServer;

/// <summary>
/// A dependency-free Language Server Protocol host for JS.Net (JSON-RPC over stdio). Each change re-runs the
/// front end (parse + bind) over all open documents, which takes a few milliseconds for typical projects.
/// </summary>
public sealed class LanguageServerHost
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ConcurrentDictionary<string, string> _documents = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _shutdown;

    private LanguageServerHost(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public static async Task<int> RunAsync(Stream input, Stream output)
    {
        var host = new LanguageServerHost(input, output);
        return await host.LoopAsync().ConfigureAwait(false);
    }

    private async Task<int> LoopAsync()
    {
        while (true)
        {
            var message = await ReadMessageAsync().ConfigureAwait(false);
            if (message is null) return 0;
            string? method = message["method"]?.GetValue<string>();
            var id = message["id"];
            var @params = message["params"];
            try
            {
                switch (method)
                {
                    case "initialize": await RespondAsync(id, Capabilities()).ConfigureAwait(false); break;
                    case "initialized": break;
                    case "shutdown":
                        _shutdown = true;
                        await RespondAsync(id, null).ConfigureAwait(false);
                        break;
                    case "exit": return _shutdown ? 0 : 1;
                    case "textDocument/didOpen":
                    {
                        string uri = @params!["textDocument"]!["uri"]!.GetValue<string>();
                        _documents[uri] = @params["textDocument"]!["text"]!.GetValue<string>();
                        await PublishAllDiagnosticsAsync().ConfigureAwait(false);
                        break;
                    }
                    case "textDocument/didChange":
                    {
                        string uri = @params!["textDocument"]!["uri"]!.GetValue<string>();
                        var changes = @params["contentChanges"]!.AsArray();
                        if (changes.Count > 0) _documents[uri] = changes[^1]!["text"]!.GetValue<string>();
                        await PublishAllDiagnosticsAsync().ConfigureAwait(false);
                        break;
                    }
                    case "textDocument/didClose":
                    {
                        string uri = @params!["textDocument"]!["uri"]!.GetValue<string>();
                        _documents.TryRemove(uri, out _);
                        await NotifyAsync("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = new JsonArray() }).ConfigureAwait(false);
                        break;
                    }
                    case "textDocument/completion": await RespondAsync(id, Completion(@params!)).ConfigureAwait(false); break;
                    case "textDocument/hover": await RespondAsync(id, Hover(@params!)).ConfigureAwait(false); break;
                    case "textDocument/documentSymbol": await RespondAsync(id, DocumentSymbols(@params!)).ConfigureAwait(false); break;
                    case "textDocument/definition": await RespondAsync(id, Definition(@params!)).ConfigureAwait(false); break;
                    case "textDocument/formatting": await RespondAsync(id, Formatting(@params!)).ConfigureAwait(false); break;
                    case "textDocument/references": await RespondAsync(id, References(@params!)).ConfigureAwait(false); break;
                    case "textDocument/semanticTokens/full":
                    {
                        string stUri = @params!["textDocument"]!["uri"]!.GetValue<string>();
                        var data = _documents.TryGetValue(stUri, out var stText) ? SemanticTokens.Encode(stText) : [];
                        await RespondAsync(id, new JsonObject { ["data"] = new JsonArray(data.Select(v => (JsonNode)v).ToArray()) }).ConfigureAwait(false);
                        break;
                    }
                    case "textDocument/prepareRename": await RespondAsync(id, PrepareRename(@params!)).ConfigureAwait(false); break;
                    case "textDocument/rename": await RespondAsync(id, Rename(@params!)).ConfigureAwait(false); break;
                    case "textDocument/codeAction": await RespondAsync(id, CodeActions(@params!)).ConfigureAwait(false); break;
                    default:
                        if (id is not null) await RespondErrorAsync(id, -32601, $"Method '{method}' is not supported.").ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                if (id is not null) await RespondErrorAsync(id, -32603, ex.Message).ConfigureAwait(false);
            }
        }
    }

    private static JsonObject Capabilities() => new()
    {
        ["capabilities"] = new JsonObject
        {
            ["textDocumentSync"] = 1,
            ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".", "@") },
            ["hoverProvider"] = true,
            ["documentSymbolProvider"] = true,
            ["definitionProvider"] = true,
            ["referencesProvider"] = true,
            ["semanticTokensProvider"] = new JsonObject
            {
                ["legend"] = new JsonObject
                {
                    ["tokenTypes"] = new JsonArray(SemanticTokens.Legend.Select(t => (JsonNode)t).ToArray()),
                    ["tokenModifiers"] = new JsonArray(),
                },
                ["full"] = true,
            },
            ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
            ["codeActionProvider"] = new JsonObject { ["codeActionKinds"] = new JsonArray("quickfix") },
            ["documentFormattingProvider"] = true,
        },
        ["serverInfo"] = new JsonObject { ["name"] = "jsnet-language-server", ["version"] = "0.1.0" },
    };

    // ------------------------------------------------------------------------------- analysis

    private sealed record Analysis(Dictionary<string, CompilationUnit> Units, IReadOnlyList<Diagnostic> Diagnostics, JsNetCompilationResult Result);

    private Analysis Analyze()
    {
        var sources = _documents.Select(kv => new SourceText(kv.Value, UriToPath(kv.Key))).ToList();
        var result = JsNetCompiler.Compile(sources, new JsNetCompilationOptions { EmitLineDirectives = false, IsLibrary = false });
        var units = new Dictionary<string, CompilationUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in result.Units) units[u.Source.Path] = u;
        // Multiple open files each containing statements is normal while editing; don't flag it in the IDE.
        var diags = result.Diagnostics.Where(d => d.Code != DiagnosticCodes.MultipleEntryPoints).ToList();
        return new Analysis(units, diags, result);
    }

    private async Task PublishAllDiagnosticsAsync()
    {
        var analysis = Analyze();
        foreach (var (uri, _) in _documents)
        {
            string path = UriToPath(uri);
            var items = new JsonArray();
            foreach (var d in analysis.Diagnostics.Where(d => string.Equals(d.Source?.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                var (l1, c1) = d.Source!.GetLineColumn(d.Span.Start);
                var (l2, c2) = d.Source.GetLineColumn(d.Span.End);
                items.Add(new JsonObject
                {
                    ["range"] = Range(l1 - 1, c1 - 1, l2 - 1, Math.Max(c2 - 1, c1)),
                    ["severity"] = d.Severity switch { DiagnosticSeverity.Error => 1, DiagnosticSeverity.Warning => 2, _ => 3 },
                    ["code"] = d.Code,
                    ["source"] = "jsnet",
                    ["message"] = d.Message,
                });
            }
            await NotifyAsync("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = items }).ConfigureAwait(false);
        }
    }

    private static readonly string[] KeywordList =
    [
        "let", "const", "var", "dynamic", "function", "async", "await", "class", "interface", "enum", "struct", "record", "extends", "implements",
        "import", "export", "from", "return", "if", "else", "for", "of", "in", "while", "do", "switch", "case", "default", "break", "continue",
        "try", "catch", "finally", "throw", "new", "this", "super", "typeof", "instanceof", "true", "false", "null", "undefined", "static",
        "public", "private", "protected", "readonly", "abstract", "override", "get", "set", "constructor", "yield", "namespace", "type",
        "ai", "prompt", "using", "number", "int", "long", "string", "boolean", "bool", "any", "void", "object",
    ];

    private static readonly Dictionary<string, string[]> MemberLists = new(StringComparer.Ordinal)
    {
        ["console"] = ["log", "info", "warn", "error", "debug", "table", "time", "timeEnd", "group", "groupEnd", "assert", "count", "clear"],
        ["Math"] = ["abs", "floor", "ceil", "round", "trunc", "sign", "sqrt", "cbrt", "pow", "exp", "log", "log2", "log10", "sin", "cos", "tan", "atan2", "hypot", "random", "max", "min", "clamp", "PI", "E"],
        ["JSON"] = ["stringify", "parse"],
        ["Promise"] = ["all", "allSettled", "race", "any", "resolve", "reject", "delay"],
        ["Object"] = ["keys", "values", "entries", "assign", "freeze", "fromEntries"],
        ["Array"] = ["isArray", "from", "of", "range"],
        ["Number"] = ["isInteger", "isFinite", "isNaN", "isSafeInteger", "parseFloat", "parseInt", "MAX_SAFE_INTEGER", "EPSILON"],
        ["Date"] = ["now", "parse"],
        ["process"] = ["argv", "env", "platform", "cwd", "exit", "pid", "version"],
    };

    private static readonly string[] CommonMembers =
    [
        "length", "map", "filter", "reduce", "forEach", "find", "findIndex", "some", "every", "includes", "indexOf", "join", "push", "pop",
        "shift", "unshift", "slice", "splice", "sort", "reverse", "concat", "flatMap", "where", "select", "orderBy", "groupBy", "first",
        "count", "sum", "average", "toList", "toUpperCase", "toLowerCase", "trim", "split", "startsWith", "endsWith", "replace", "padStart",
        "toFixed", "toString", "get", "set", "has", "delete", "size", "keys", "values", "entries",
    ];

    private JsonNode Completion(JsonNode p)
    {
        string uri = p["textDocument"]!["uri"]!.GetValue<string>();
        int line = p["position"]!["line"]!.GetValue<int>();
        int character = p["position"]!["character"]!.GetValue<int>();
        var items = new JsonArray();
        _documents.TryGetValue(uri, out var text);
        text ??= string.Empty;
        var source = new SourceText(text);
        int offset = source.GetOffset(line + 1, character + 1);

        // Member completion after "receiver."
        int dot = offset - 1;
        while (dot >= 0 && dot < text.Length && Lexer.IsIdentPart(text[dot])) dot--;
        if (dot >= 0 && dot < text.Length && text[dot] == '.')
        {
            string receiver = ReceiverBefore(text, dot);
            if (MemberLists.TryGetValue(receiver, out var known))
            {
                foreach (var m in known) items.Add(new JsonObject { ["label"] = m, ["kind"] = 2 });
                return items;
            }
            var analysis0 = Analyze();
            if (receiver.Length > 0 && analysis0.Units.TryGetValue(UriToPath(uri), out var unit0))
            {
                try
                {
                    var members = DotNetMembers.Complete(analysis0.Result, unit0, receiver, dot);
                    foreach (var m in members) items.Add(new JsonObject { ["label"] = m.Name, ["kind"] = m.Kind, ["detail"] = m.Detail });
                }
                catch (Exception) { /* fall back to the JavaScript member list */ }
            }
            if (items.Count == 0)
                foreach (var m in CommonMembers) items.Add(new JsonObject { ["label"] = m, ["kind"] = 2 });
            return items;
        }

        foreach (var k in KeywordList) items.Add(new JsonObject { ["label"] = k, ["kind"] = 14 });
        foreach (var g in MemberLists.Keys) items.Add(new JsonObject { ["label"] = g, ["kind"] = 9 });
        foreach (var g in new[] { "parseInt", "parseFloat", "isNaN", "setTimeout", "setInterval", "clearTimeout", "delay", "fetch" })
            items.Add(new JsonObject { ["label"] = g, ["kind"] = 3 });

        var analysis = Analyze();
        foreach (var unit in analysis.Units.Values)
            foreach (var (name, kind, _) in Declarations(unit))
                items.Add(new JsonObject { ["label"] = name, ["kind"] = kind, ["detail"] = Path.GetFileName(unit.Source.Path) });

        items.Add(Snippet("fn", "function ${1:name}(${2:params}): ${3:void} {\n\t$0\n}", "function declaration"));
        items.Add(Snippet("cls", "class ${1:Name} {\n\tconstructor(${2}) {\n\t\t$0\n\t}\n}", "class declaration"));
        items.Add(Snippet("forof", "for (const ${1:item} of ${2:items}) {\n\t$0\n}", "for...of loop"));
        items.Add(Snippet("log", "console.log(${1});", "console.log"));
        items.Add(Snippet("afn", "async function ${1:name}(): Promise<${2:void}> {\n\t$0\n}", "async function"));
        return items;
    }

    private static JsonObject Snippet(string label, string body, string detail) => new()
    {
        ["label"] = label,
        ["kind"] = 15,
        ["insertTextFormat"] = 2,
        ["insertText"] = body,
        ["detail"] = detail,
    };

    /// <summary>(name, LSP CompletionItemKind, node) for top-level and class-member declarations.</summary>
    private static IEnumerable<(string Name, int Kind, Node Node)> Declarations(CompilationUnit unit)
    {
        foreach (var s in unit.Statements)
        {
            switch (s)
            {
                case FunctionDeclaration f: yield return (f.Name, 3, f); break;
                case ClassDeclaration c:
                    yield return (c.Name, 7, c);
                    foreach (var m in c.Members.Where(m => m.Kind != MemberKind.Constructor))
                        yield return (m.Name, m.Kind == MemberKind.Field ? 5 : 2, m);
                    break;
                case InterfaceDeclaration i: yield return (i.Name, 8, i); break;
                case EnumDeclaration e:
                    yield return (e.Name, 13, e);
                    break;
                case PromptDeclaration pr: yield return (pr.Name, 7, pr); break;
                case VariableStatement v:
                    foreach (var d in v.Declarators)
                        if (d.Target is IdentifierPattern ip) yield return (ip.Name, 6, d);
                    break;
            }
        }
    }

    private (CompilationUnit? Unit, string? Word, int Offset) WordAt(JsonNode p)
    {
        string uri = p["textDocument"]!["uri"]!.GetValue<string>();
        int line = p["position"]!["line"]!.GetValue<int>();
        int character = p["position"]!["character"]!.GetValue<int>();
        if (!_documents.TryGetValue(uri, out var text)) return (null, null, 0);
        var analysis = Analyze();
        analysis.Units.TryGetValue(UriToPath(uri), out var unit);
        var src = new SourceText(text);
        int offset = src.GetOffset(line + 1, character + 1);
        int s = offset, e = offset;
        while (s > 0 && Lexer.IsIdentPart(text[s - 1])) s--;
        while (e < text.Length && Lexer.IsIdentPart(text[e])) e++;
        return (unit, e > s ? text[s..e] : null, offset);
    }

    /// <summary>The dotted receiver chain ending just before <paramref name="dot"/> (<c>this.items</c>, <c>sb</c>, <c>System.IO</c>).</summary>
    private static string ReceiverBefore(string text, int dot)
    {
        int start = dot;
        while (start > 0 && (Lexer.IsIdentPart(text[start - 1]) || text[start - 1] == '.')) start--;
        return text[start..dot].Trim('.');
    }

    private JsonNode? Hover(JsonNode p)
    {
        var (unit, word, wordOffset) = WordAt(p);
        if (word is null) return null;
        var analysis = Analyze();
        // Member of a .NET (or JS.Net) type: `sb.Append`
        string uri0 = p["textDocument"]!["uri"]!.GetValue<string>();
        if (_documents.TryGetValue(uri0, out var docText) && unit is not null)
        {
            int s0 = wordOffset;
            while (s0 > 0 && Lexer.IsIdentPart(docText[s0 - 1])) s0--;
            if (s0 > 0 && docText[s0 - 1] == '.')
            {
                string receiver = ReceiverBefore(docText, s0 - 1);
                string? sig = null;
                try { sig = receiver.Length > 0 ? DotNetMembers.Describe(analysis.Result, unit, receiver, word, s0 - 1) : null; }
                catch (Exception) { }
                if (sig is not null)
                    return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = $"```csharp\n{sig}\n```" } };
            }
        }
        foreach (var u in analysis.Units.Values)
        {
            foreach (var (name, _, node) in Declarations(u))
            {
                if (name != word) continue;
                string sig = Describe(node, u.Source);
                return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = $"```jsnet\n{sig}\n```\n*{Path.GetFileName(u.Source.Path)}*" } };
            }
        }
        if (MemberLists.ContainsKey(word))
            return new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = $"**{word}** — JS.Net runtime global (JSNet.Runtime)" } };
        return null;
    }

    private static string Describe(Node node, SourceText source)
    {
        // The first line of the declaration is a readable signature.
        var (line, _) = source.GetLineColumn(node.Span.Start);
        string text = source.GetLine(line).Trim();
        int brace = text.IndexOf('{');
        return brace > 0 ? text[..brace].TrimEnd() : text;
    }

    private JsonNode? Definition(JsonNode p)
    {
        var (_, word, _) = WordAt(p);
        if (word is null) return null;
        var analysis = Analyze();
        foreach (var u in analysis.Units.Values)
        {
            foreach (var (name, _, node) in Declarations(u))
            {
                if (name != word) continue;
                var (l1, c1) = u.Source.GetLineColumn(node.Span.Start);
                return new JsonObject
                {
                    ["uri"] = PathToUri(u.Source.Path),
                    ["range"] = Range(l1 - 1, c1 - 1, l1 - 1, c1 - 1 + name.Length),
                };
            }
        }
        return null;
    }

    private JsonNode DocumentSymbols(JsonNode p)
    {
        string uri = p["textDocument"]!["uri"]!.GetValue<string>();
        var result = new JsonArray();
        var analysis = Analyze();
        if (!analysis.Units.TryGetValue(UriToPath(uri), out var unit)) return result;
        foreach (var s in unit.Statements)
        {
            JsonObject? sym = s switch
            {
                FunctionDeclaration f => Symbol(f.Name, 12, f, unit.Source),
                ClassDeclaration c => Symbol(c.Name, 5, c, unit.Source, c.Members.Select(m => Symbol(m.Name, m.Kind switch
                {
                    MemberKind.Constructor => 9,
                    MemberKind.Field => 8,
                    MemberKind.Getter or MemberKind.Setter => 7,
                    _ => 6,
                }, m, unit.Source))),
                InterfaceDeclaration i => Symbol(i.Name, 11, i, unit.Source),
                EnumDeclaration e => Symbol(e.Name, 10, e, unit.Source),
                PromptDeclaration pr => Symbol(pr.Name, 5, pr, unit.Source),
                VariableStatement v when v.Declarators.FirstOrDefault()?.Target is IdentifierPattern ip => Symbol(ip.Name, v.Kind == VariableKind.Const ? 14 : 13, v, unit.Source),
                _ => null,
            };
            if (sym is not null) result.Add(sym);
        }
        return result;
    }

    private static JsonObject Symbol(string name, int kind, Node node, SourceText source, IEnumerable<JsonObject>? children = null)
    {
        var (l1, c1) = source.GetLineColumn(node.Span.Start);
        var (l2, c2) = source.GetLineColumn(node.Span.End);
        var o = new JsonObject
        {
            ["name"] = name,
            ["kind"] = kind,
            ["range"] = Range(l1 - 1, c1 - 1, l2 - 1, c2 - 1),
            ["selectionRange"] = Range(l1 - 1, c1 - 1, l1 - 1, c1 - 1),
        };
        if (children is not null) o["children"] = new JsonArray(children.Select(JsonNode (c) => c).ToArray());
        return o;
    }

    // ------------------------------------------------------------------ references, rename, code actions

    /// <summary>Every identifier token named <paramref name="name"/> in the open documents.</summary>
    private List<(string Uri, int Line, int Column, int Length)> Occurrences(string name)
    {
        var list = new List<(string, int, int, int)>();
        foreach (var (uri, text) in _documents)
        {
            var source = new SourceText(text);
            foreach (var t in Lexer.Tokenize(source, new DiagnosticBag()))
            {
                if (t.Kind != TokenKind.Identifier || t.Text != name) continue;
                var (l, c) = source.GetLineColumn(t.Span.Start);
                list.Add((uri, l - 1, c - 1, t.Span.Length));
            }
        }
        return list;
    }

    private JsonNode References(JsonNode p)
    {
        var (_, word, _) = WordAt(p);
        var result = new JsonArray();
        if (word is null) return result;
        foreach (var (uri, line, col, len) in Occurrences(word))
            result.Add(new JsonObject { ["uri"] = uri, ["range"] = Range(line, col, line, col + len) });
        return result;
    }

    /// <summary>Only names declared in JS.Net code can be renamed (not .NET or runtime APIs).</summary>
    private bool IsDeclaredInProject(string name, Analysis analysis) =>
        analysis.Units.Values.Any(u => AstWalker.DescendantsAndSelf(u).Any(n => n switch
        {
            FunctionDeclaration f => f.Name == name,
            ClassDeclaration c => c.Name == name,
            InterfaceDeclaration i => i.Name == name,
            EnumDeclaration e => e.Name == name,
            ClassMember m => m.Name == name,
            Parameter pa => pa.Name == name,
            VariableDeclarator { Target: IdentifierPattern ip } => ip.Name == name,
            _ => false,
        }));

    private JsonNode? PrepareRename(JsonNode p)
    {
        var (_, word, offset) = WordAt(p);
        if (word is null || !IsDeclaredInProject(word, Analyze())) return null;
        string uri = p["textDocument"]!["uri"]!.GetValue<string>();
        var source = new SourceText(_documents[uri]);
        int s = offset;
        while (s > 0 && Lexer.IsIdentPart(source.Text[s - 1])) s--;
        var (l, c) = source.GetLineColumn(s);
        return new JsonObject { ["range"] = Range(l - 1, c - 1, l - 1, c - 1 + word.Length), ["placeholder"] = word };
    }

    private JsonNode? Rename(JsonNode p)
    {
        var (_, word, _) = WordAt(p);
        string newName = p["newName"]!.GetValue<string>();
        if (word is null || !IsDeclaredInProject(word, Analyze())) return null;
        var changes = new JsonObject();
        foreach (var group in Occurrences(word).GroupBy(o => o.Uri))
        {
            var edits = new JsonArray();
            foreach (var (_, line, col, len) in group)
                edits.Add(new JsonObject { ["range"] = Range(line, col, line, col + len), ["newText"] = newName });
            changes[group.Key] = edits;
        }
        return new JsonObject { ["changes"] = changes };
    }

    /// <summary>Quick fixes for JS.Net diagnostics: const→let (JSN1001), prefix unused variables (JSN2001), fractional division (JSN2003).</summary>
    private JsonNode CodeActions(JsonNode p)
    {
        string uri = p["textDocument"]!["uri"]!.GetValue<string>();
        var actions = new JsonArray();
        if (!_documents.TryGetValue(uri, out var text)) return actions;
        var source = new SourceText(text);
        foreach (var d in p["context"]?["diagnostics"]?.AsArray() ?? [])
        {
            string? code = d?["code"]?.GetValue<string>();
            var range = d!["range"]!;
            int line = range["start"]!["line"]!.GetValue<int>();
            int col = range["start"]!["character"]!.GetValue<int>();
            int offset = source.GetOffset(line + 1, col + 1);
            switch (code)
            {
                case DiagnosticCodes.ConstReassignment:
                {
                    string message = d["message"]?.GetValue<string>() ?? string.Empty;
                    int q1 = message.IndexOf('\''), q2 = message.IndexOf('\'', q1 + 1);
                    if (q1 < 0 || q2 < 0) break;
                    string name = message[(q1 + 1)..q2];
                    var match = System.Text.RegularExpressions.Regex.Match(text, $@"\bconst(\s+){System.Text.RegularExpressions.Regex.Escape(name)}\b");
                    if (!match.Success) break;
                    var (l, c) = source.GetLineColumn(match.Index);
                    actions.Add(QuickFix($"Change '{name}' to let", uri, d, Range(l - 1, c - 1, l - 1, c + 4), "let"));
                    break;
                }
                case DiagnosticCodes.UnusedVariable:
                {
                    int end = offset;
                    while (end < text.Length && Lexer.IsIdentPart(text[end])) end++;
                    string name = text[offset..end];
                    if (name.Length > 0) actions.Add(QuickFix($"Prefix '{name}' with '_'", uri, d, Range(line, col, line, col), "_"));
                    break;
                }
                case DiagnosticCodes.IntegerDivision:
                {
                    // Make the right-hand integer literal fractional: `a / 2` → `a / 2.0`.
                    var endPos = range["end"]!;
                    int endOffset = source.GetOffset(endPos["line"]!.GetValue<int>() + 1, endPos["character"]!.GetValue<int>() + 1);
                    int k = Math.Min(endOffset, text.Length);
                    while (k > offset && !char.IsAsciiDigit(text[k - 1])) k--;
                    int digitsEnd = k;
                    while (k > offset && char.IsAsciiDigit(text[k - 1])) k--;
                    if (digitsEnd > k && (k == 0 || !Lexer.IsIdentPart(text[k - 1])) && (digitsEnd >= text.Length || text[digitsEnd] != '.'))
                    {
                        var (l, c) = source.GetLineColumn(digitsEnd);
                        actions.Add(QuickFix("Use fractional division", uri, d, Range(l - 1, c - 1, l - 1, c - 1), ".0"));
                    }
                    break;
                }
            }
        }
        return actions;
    }

    private static JsonObject QuickFix(string title, string uri, JsonNode diagnostic, JsonObject range, string newText) => new()
    {
        ["title"] = title,
        ["kind"] = "quickfix",
        ["diagnostics"] = new JsonArray(diagnostic.DeepClone()),
        ["edit"] = new JsonObject
        {
            ["changes"] = new JsonObject { [uri] = new JsonArray(new JsonObject { ["range"] = range, ["newText"] = newText }) },
        },
    };

    /// <summary>Formatting delegates to the native Rust formatter when it is installed.</summary>
    private JsonNode Formatting(JsonNode p)
    {
        string uri = p["textDocument"]!["uri"]!.GetValue<string>();
        if (!_documents.TryGetValue(uri, out var text)) return new JsonArray();
        string? tool = Environment.GetEnvironmentVariable("JSNET_TOOLS_PATH");
        if (string.IsNullOrEmpty(tool))
        {
            string exe = OperatingSystem.IsWindows() ? "jsnet-tools.exe" : "jsnet-tools";
            tool = new[] { Path.Combine(AppContext.BaseDirectory, exe), Path.Combine(AppContext.BaseDirectory, "native", exe) }.FirstOrDefault(File.Exists);
        }
        if (tool is null || !File.Exists(tool)) return new JsonArray();
        var psi = new ProcessStartInfo(tool, "fmt --stdin") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(text);
        proc.StandardInput.Close();
        string formatted = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0 || formatted == text) return new JsonArray();
        var src = new SourceText(text);
        return new JsonArray(new JsonObject
        {
            ["range"] = Range(0, 0, src.LineCount, 0),
            ["newText"] = formatted,
        });
    }

    // ----------------------------------------------------------------------------- transport

    private static JsonObject Range(int l1, int c1, int l2, int c2) => new()
    {
        ["start"] = new JsonObject { ["line"] = l1, ["character"] = c1 },
        ["end"] = new JsonObject { ["line"] = l2, ["character"] = c2 },
    };

    public static string UriToPath(string uri)
    {
        if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return uri;
        return Path.GetFullPath(new Uri(uri).LocalPath);
    }

    public static string PathToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private async Task<JsonNode?> ReadMessageAsync()
    {
        int contentLength = -1;
        var header = new StringBuilder();
        while (true)
        {
            int b = _input.ReadByte();
            if (b < 0) return null;
            header.Append((char)b);
            if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") break;
        }
        foreach (var line in header.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line[(colon + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (contentLength < 0) return null;
        var buffer = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = await _input.ReadAsync(buffer.AsMemory(read, contentLength - read)).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return JsonNode.Parse(buffer);
    }

    private Task RespondAsync(JsonNode? id, JsonNode? result) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

    private Task RespondErrorAsync(JsonNode id, int code, string message) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    private Task NotifyAsync(string method, JsonNode @params) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params });

    private async Task WriteAsync(JsonObject message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        byte[] head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(head).ConfigureAwait(false);
            await _output.WriteAsync(body).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
