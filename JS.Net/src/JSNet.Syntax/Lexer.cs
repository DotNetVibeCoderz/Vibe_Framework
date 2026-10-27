using System.Text;

namespace JSNet.Syntax;

/// <summary>
/// Single-pass, allocation-light lexer. <c>&gt;</c> is always emitted as a single-character token so that
/// nested generics (<c>List&lt;List&lt;int&gt;&gt;</c>) parse naturally; the parser re-joins adjacent
/// <c>&gt;</c>/<c>=</c> tokens into shift and comparison operators in expression context.
/// </summary>
public sealed class Lexer
{
    private static readonly string[] Punctuators =
    [
        "...", "===", "!==", "**=", "<<=", "&&=", "||=", "??=",
        "=>", "==", "!=", "<=", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
        "&&", "||", "??", "?.", "**", "<<",
        "{", "}", "(", ")", "[", "]", ";", ",", "<", ">", "+", "-", "*", "/", "%", "&", "|", "^",
        "!", "~", "?", ":", "=", ".", "@",
    ];

    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics;
    private readonly int _end;
    private int _pos;
    private bool _newLine;

    public Lexer(SourceText source, DiagnosticBag diagnostics, int start = 0, int end = -1)
    {
        _source = source;
        _diagnostics = diagnostics;
        _pos = start;
        _end = end < 0 ? source.Length : end;
    }

    public static List<Token> Tokenize(SourceText source, DiagnosticBag diagnostics, int start = 0, int end = -1)
    {
        var lexer = new Lexer(source, diagnostics, start, end);
        var tokens = new List<Token>(Math.Max(16, (end < 0 ? source.Length : end - start) / 4));
        Token t;
        do
        {
            t = lexer.Next();
            tokens.Add(t);
        } while (t.Kind != TokenKind.EndOfFile);
        return tokens;
    }

    private char Cur => _pos < _end ? _source.Text[_pos] : '\0';
    private char Peek(int n = 1) => _pos + n < _end ? _source.Text[_pos + n] : '\0';

    public Token Next()
    {
        _newLine = false;
        SkipTrivia();
        int start = _pos;
        if (_pos >= _end) return new Token(TokenKind.EndOfFile, string.Empty, new TextSpan(_end, 0), true);

        char c = Cur;
        if (IsIdentStart(c) || (c == '#' && IsIdentStart(Peek())))
        {
            _pos++;
            while (IsIdentPart(Cur)) _pos++;
            string text = _source.Text[start.._pos];
            var kind = Keywords.Reserved.Contains(text) ? TokenKind.Keyword : TokenKind.Identifier;
            return new Token(kind, text, TextSpan.FromBounds(start, _pos), _newLine);
        }

        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek()))) return LexNumber(start);
        if (c is '"' or '\'') return LexString(start, c);
        if (c == '`') return LexTemplate(start);

        foreach (var p in Punctuators)
        {
            if (Matches(p))
            {
                _pos += p.Length;
                return new Token(TokenKind.Punctuator, p, new TextSpan(start, p.Length), _newLine);
            }
        }

        _pos++;
        _diagnostics.Error(DiagnosticCodes.UnexpectedCharacter, $"Unexpected character '{c}'.", _source, new TextSpan(start, 1));
        return new Token(TokenKind.Bad, c.ToString(), new TextSpan(start, 1), _newLine);
    }

    private bool Matches(string p)
    {
        if (_pos + p.Length > _end) return false;
        return string.CompareOrdinal(_source.Text, _pos, p, 0, p.Length) == 0;
    }

    private void SkipTrivia()
    {
        while (_pos < _end)
        {
            char c = Cur;
            if (c == '\n') { _newLine = true; _pos++; }
            else if (char.IsWhiteSpace(c)) _pos++;
            else if (c == '/' && Peek() == '/')
            {
                while (_pos < _end && Cur != '\n') _pos++;
            }
            else if (c == '/' && Peek() == '*')
            {
                int start = _pos;
                _pos += 2;
                while (_pos < _end && !(Cur == '*' && Peek() == '/'))
                {
                    if (Cur == '\n') _newLine = true;
                    _pos++;
                }
                if (_pos >= _end)
                    _diagnostics.Error(DiagnosticCodes.UnterminatedComment, "Unterminated block comment.", _source, TextSpan.FromBounds(start, _end));
                else _pos += 2;
            }
            else break;
        }
    }

    private Token LexNumber(int start)
    {
        if (Cur == '0' && (Peek() is 'x' or 'X' or 'b' or 'B' or 'o' or 'O'))
        {
            _pos += 2;
            while (char.IsAsciiHexDigit(Cur) || Cur == '_') _pos++;
        }
        else
        {
            while (char.IsAsciiDigit(Cur) || Cur == '_') _pos++;
            if (Cur == '.' && char.IsAsciiDigit(Peek()))
            {
                _pos++;
                while (char.IsAsciiDigit(Cur) || Cur == '_') _pos++;
            }
            if (Cur is 'e' or 'E' && (char.IsAsciiDigit(Peek()) || (Peek() is '+' or '-' && char.IsAsciiDigit(Peek(2)))))
            {
                _pos += 2;
                while (char.IsAsciiDigit(Cur)) _pos++;
            }
        }
        // .NET numeric suffixes: L (long), m (decimal), f (float), d (double), u/ul (unsigned).
        while (Cur is 'L' or 'l' or 'm' or 'M' or 'f' or 'F' or 'd' or 'D' or 'u' or 'U') _pos++;
        return new Token(TokenKind.Number, _source.Text[start.._pos], TextSpan.FromBounds(start, _pos), _newLine);
    }

    private Token LexString(int start, char quote)
    {
        _pos++;
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _end || Cur == '\n')
            {
                _diagnostics.Error(DiagnosticCodes.UnterminatedString, "Unterminated string literal.", _source, TextSpan.FromBounds(start, _pos));
                break;
            }
            char c = Cur;
            if (c == quote) { _pos++; break; }
            if (c == '\\') { ReadEscape(sb); continue; }
            sb.Append(c);
            _pos++;
        }
        return new Token(TokenKind.String, _source.Text[start.._pos], TextSpan.FromBounds(start, _pos), _newLine, sb.ToString());
    }

    private void ReadEscape(StringBuilder sb)
    {
        _pos++; // backslash
        char e = Cur;
        _pos++;
        switch (e)
        {
            case 'n': sb.Append('\n'); break;
            case 't': sb.Append('\t'); break;
            case 'r': sb.Append('\r'); break;
            case '0': sb.Append('\0'); break;
            case 'b': sb.Append('\b'); break;
            case 'f': sb.Append('\f'); break;
            case 'v': sb.Append('\v'); break;
            case 'u':
                if (Cur == '{')
                {
                    int close = _source.Text.IndexOf('}', _pos);
                    if (close > 0 && int.TryParse(_source.Text.AsSpan(_pos + 1, close - _pos - 1), System.Globalization.NumberStyles.HexNumber, null, out int cp))
                    {
                        sb.Append(char.ConvertFromUtf32(cp));
                        _pos = close + 1;
                    }
                }
                else if (_pos + 4 <= _end && int.TryParse(_source.Text.AsSpan(_pos, 4), System.Globalization.NumberStyles.HexNumber, null, out int u))
                {
                    sb.Append((char)u);
                    _pos += 4;
                }
                break;
            case 'x':
                if (_pos + 2 <= _end && int.TryParse(_source.Text.AsSpan(_pos, 2), System.Globalization.NumberStyles.HexNumber, null, out int x))
                {
                    sb.Append((char)x);
                    _pos += 2;
                }
                break;
            case '\r':
                if (Cur == '\n') _pos++;
                break;
            case '\n':
                break;
            default: sb.Append(e); break;
        }
    }

    /// <summary>Templates keep their raw body in <see cref="Token.Value"/>; the parser splits out <c>${...}</c> holes.</summary>
    private Token LexTemplate(int start)
    {
        _pos++;
        int depth = 0;
        while (true)
        {
            if (_pos >= _end)
            {
                _diagnostics.Error(DiagnosticCodes.UnterminatedString, "Unterminated template literal.", _source, TextSpan.FromBounds(start, _pos));
                break;
            }
            char c = Cur;
            if (depth == 0)
            {
                if (c == '`') { _pos++; break; }
                if (c == '\\') { _pos += 2; continue; }
                if (c == '$' && Peek() == '{') { depth = 1; _pos += 2; continue; }
                _pos++;
            }
            else
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c is '"' or '\'')
                {
                    SkipQuoted(c);
                    continue;
                }
                else if (c == '`')
                {
                    SkipNestedTemplate();
                    continue;
                }
                _pos++;
            }
        }
        int bodyEnd = Math.Max(start + 1, _pos - 1);
        return new Token(TokenKind.Template, _source.Text[start.._pos], TextSpan.FromBounds(start, _pos), _newLine,
            _source.Text[(start + 1)..Math.Min(bodyEnd, _source.Length)]);
    }

    private void SkipQuoted(char quote)
    {
        _pos++;
        while (_pos < _end && Cur != quote && Cur != '\n')
        {
            if (Cur == '\\') _pos++;
            _pos++;
        }
        _pos++;
    }

    private void SkipNestedTemplate()
    {
        _pos++;
        int depth = 0;
        while (_pos < _end)
        {
            char c = Cur;
            if (depth == 0 && c == '`') { _pos++; return; }
            if (c == '\\') { _pos += 2; continue; }
            if (c == '$' && Peek() == '{') { depth++; _pos += 2; continue; }
            if (depth > 0 && c == '}') depth--;
            _pos++;
        }
    }

    public static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '$';
    public static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
