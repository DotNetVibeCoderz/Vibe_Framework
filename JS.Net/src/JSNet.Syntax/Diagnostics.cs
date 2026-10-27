namespace JSNet.Syntax;

public enum DiagnosticSeverity { Info, Warning, Error }

/// <summary>A compiler message. Codes: JSN0xxx syntax, JSN1xxx semantics, JSN2xxx analyzers, JSN3xxx driver.</summary>
public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message, SourceText? Source, TextSpan Span)
{
    public (int Line, int Column) Position => Source?.GetLineColumn(Span.Start) ?? (1, 1);

    /// <summary>MSBuild canonical format, e.g. <c>file.jsn(3,7): error JSN0001: message</c>.</summary>
    public override string ToString()
    {
        var (line, col) = Position;
        string sev = Severity switch { DiagnosticSeverity.Error => "error", DiagnosticSeverity.Warning => "warning", _ => "info" };
        string file = Source?.Path ?? "jsnet";
        return $"{file}({line},{col}): {sev} {Code}: {Message}";
    }
}

public sealed class DiagnosticBag : List<Diagnostic>
{
    public bool HasErrors => this.Any(static d => d.Severity == DiagnosticSeverity.Error);

    public void Error(string code, string message, SourceText? source, TextSpan span) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Error, message, source, span));

    public void Warning(string code, string message, SourceText? source, TextSpan span) =>
        Add(new Diagnostic(code, DiagnosticSeverity.Warning, message, source, span));
}

/// <summary>Well-known diagnostic ids, kept in one place so docs and tooling stay in sync.</summary>
public static class DiagnosticCodes
{
    public const string UnexpectedCharacter = "JSN0001";
    public const string UnterminatedString = "JSN0002";
    public const string UnterminatedComment = "JSN0003";
    public const string UnexpectedToken = "JSN0010";
    public const string ExpectedToken = "JSN0011";
    public const string InvalidDeclaration = "JSN0012";

    public const string ConstReassignment = "JSN1001";
    public const string DuplicateDeclaration = "JSN1002";
    public const string AwaitOutsideAsync = "JSN1003";
    public const string BreakOutsideLoop = "JSN1004";
    public const string SuperOutsideDerived = "JSN1005";
    public const string SuperCallPosition = "JSN1006";
    public const string MultipleEntryPoints = "JSN1007";
    public const string StatementsInLibrary = "JSN1008";
    public const string UnsupportedFeature = "JSN1009";

    public const string UnusedVariable = "JSN2001";
    public const string DynamicFallback = "JSN2002";
    public const string IntegerDivision = "JSN2003";
    public const string UnreachableCode = "JSN2004";
    public const string LooseUnion = "JSN2005";
    public const string UnhandledUnionMember = "JSN2006";

    public const string FileNotFound = "JSN3001";
}
