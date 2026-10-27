namespace JSNet.Syntax;

public enum TokenKind : byte
{
    EndOfFile,
    Identifier,
    Keyword,
    Number,
    String,
    Template,
    Regex,
    Punctuator,
    Bad,
}

/// <summary>
/// A lexical token. <see cref="Value"/> holds the decoded value for strings and the raw body for templates.
/// <see cref="NewLineBefore"/> drives automatic semicolon insertion.
/// </summary>
public readonly record struct Token(TokenKind Kind, string Text, TextSpan Span, bool NewLineBefore, string? Value = null)
{
    public bool Is(string text) => (Kind is TokenKind.Punctuator or TokenKind.Keyword or TokenKind.Identifier) && Text == text;
    public bool IsKeyword(string text) => Kind == TokenKind.Keyword && Text == text;
    public bool IsIdentifierLike => Kind is TokenKind.Identifier or TokenKind.Keyword;
    public override string ToString() => $"{Kind} '{Text}' @{Span.Start}";
}

public static class Keywords
{
    /// <summary>Reserved words: never valid as plain identifiers.</summary>
    public static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "default", "delete", "do", "else", "enum",
        "export", "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof",
        "new", "null", "return", "super", "switch", "this", "throw", "true", "try", "typeof", "var",
        "void", "while", "let", "yield", "interface", "implements", "undefined",
    };
}
