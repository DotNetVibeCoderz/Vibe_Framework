using JSNet.Syntax;

namespace JSNet.LanguageServer;

/// <summary>
/// LSP semantic tokens (syntax colouring for editors without a TextMate grammar, such as Visual Studio and
/// JetBrains IDEs through LSP4IJ). Built on the real JS.Net lexer; comments come from the trivia between tokens.
/// </summary>
public static class SemanticTokens
{
    public static readonly string[] Legend =
        ["keyword", "string", "number", "comment", "type", "function", "decorator", "operator", "variable", "property", "regexp"];

    private const int Keyword = 0, String = 1, Number = 2, Comment = 3, Type = 4, Function = 5, Decorator = 6, Operator = 7, Variable = 8, Property = 9, Regexp = 10;

    private static readonly HashSet<string> Contextual = new(StringComparer.Ordinal)
    {
        "async", "await", "of", "as", "from", "get", "set", "static", "public", "private", "protected", "readonly", "abstract",
        "override", "type", "namespace", "ai", "prompt", "rule", "dynamic", "using", "partial", "declare", "is", "out", "ref",
    };

    private static readonly HashSet<string> TypeNames = new(StringComparer.Ordinal)
    {
        "number", "int", "long", "float", "double", "decimal", "string", "bool", "boolean", "any", "object", "void", "never", "unknown",
    };

    /// <summary>Returns the LSP relative encoding: [deltaLine, deltaStart, length, tokenType, modifiers]*.</summary>
    public static List<int> Encode(string text)
    {
        var source = new SourceText(text);
        var tokens = Lexer.Tokenize(source, new DiagnosticBag());
        var spans = new List<(int Start, int Length, int Type)>();
        int pos = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.EndOfFile) break;
            AddComments(text, pos, t.Span.Start, spans);
            var prev = i > 0 ? tokens[i - 1] : default;
            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
            int? type = t.Kind switch
            {
                TokenKind.Keyword => t.Text is "true" or "false" or "null" or "undefined" ? Keyword : Keyword,
                TokenKind.String or TokenKind.Template => String,
                TokenKind.Number => Number,
                TokenKind.Regex => Regexp,
                TokenKind.Punctuator => t.Text == "@" ? Decorator : t.Text is "=>" or "===" or "!==" or "&&" or "||" or "??" ? Operator : null,
                TokenKind.Identifier when prev.Text == "@" && prev.Kind == TokenKind.Punctuator => Decorator,
                TokenKind.Identifier when Contextual.Contains(t.Text) && next.Kind is TokenKind.Identifier or TokenKind.Keyword => Keyword,
                TokenKind.Identifier when TypeNames.Contains(t.Text) => Type,
                TokenKind.Identifier when next.Text == "(" => Function,
                TokenKind.Identifier when prev.Text is "." or "?." => Property,
                TokenKind.Identifier when t.Text.Length > 0 && char.IsUpper(t.Text[0]) => Type,
                TokenKind.Identifier => Variable,
                _ => null,
            };
            if (type is { } ty) spans.Add((t.Span.Start, t.Span.Length, ty));
            pos = t.Span.End;
        }
        AddComments(text, pos, text.Length, spans);

        // LSP tokens may not span lines: split, then delta-encode.
        var data = new List<int>(spans.Count * 5);
        int lastLine = 0, lastCol = 0;
        foreach (var (start, length, type) in spans.OrderBy(s => s.Start))
        {
            int offset = start, end = start + length;
            while (offset < end)
            {
                int lineEnd = text.IndexOf('\n', offset, end - offset);
                int pieceEnd = lineEnd < 0 ? end : lineEnd;
                int pieceLength = pieceEnd - offset;
                if (pieceLength > 0 && !(pieceLength == 1 && text[offset] == '\r'))
                {
                    var (line, col) = source.GetLineColumn(offset);
                    line--; col--;
                    data.Add(line - lastLine);
                    data.Add(line == lastLine ? col - lastCol : col);
                    data.Add(text[pieceEnd - 1] == '\r' ? pieceLength - 1 : pieceLength);
                    data.Add(type);
                    data.Add(0);
                    lastLine = line;
                    lastCol = col;
                }
                offset = lineEnd < 0 ? end : lineEnd + 1;
            }
        }
        return data;
    }

    private static void AddComments(string text, int from, int to, List<(int, int, int)> spans)
    {
        int i = from;
        while (i < to - 1)
        {
            if (text[i] == '/' && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i);
                if (end < 0 || end > to) end = to;
                spans.Add((i, end - i, Comment));
                i = end;
            }
            else if (text[i] == '/' && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? to : Math.Min(end + 2, to);
                spans.Add((i, end - i, Comment));
                i = end;
            }
            else i++;
        }
    }
}
