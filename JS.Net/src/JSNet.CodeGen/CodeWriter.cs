using System.Globalization;
using System.Text;

namespace JSNet.CodeGen;

/// <summary>Indentation-aware string builder for generated C#.</summary>
internal sealed class CodeWriter(int indent = 0)
{
    private readonly StringBuilder _sb = new(4096);

    public int Indent { get; set; } = indent;
    public int IndentColumns => Indent * 4;

    public void WriteLine(string line = "")
    {
        if (line.Length == 0)
        {
            _sb.Append('\n');
            return;
        }
        _sb.Append(' ', Indent * 4).Append(line).Append('\n');
    }

    /// <summary>Preprocessor directives always start at column 0.</summary>
    public void Directive(string directive) => _sb.Append(directive).Append('\n');

    public void Open(string header)
    {
        WriteLine(header);
        WriteLine("{");
        Indent++;
    }

    public void Close(string suffix = "")
    {
        Indent--;
        WriteLine("}" + suffix);
    }

    public override string ToString() => _sb.ToString();
}

/// <summary>C# naming and literal helpers.</summary>
public static class CSharpNames
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };

    private static readonly HashSet<string> PredefinedTypes = new(StringComparer.Ordinal)
    {
        "string", "int", "object", "double", "bool", "long", "decimal", "char", "byte", "float", "short", "uint", "ulong", "ushort", "sbyte",
    };

    /// <summary>Escapes an identifier for use as a declaration or member name.</summary>
    public static string Id(string name)
    {
        if (name.StartsWith('#')) return "__" + name[1..];
        return Keywords.Contains(name) ? "@" + name : name;
    }

    /// <summary>Escapes an identifier in expression position (predefined type keywords stay usable, e.g. <c>string.Join</c>).</summary>
    public static string ExprId(string name) => PredefinedTypes.Contains(name) ? name : Id(name);

    public static string StringLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        AppendEscaped(sb, value, interpolated: false);
        sb.Append('"');
        return sb.ToString();
    }

    public static void AppendEscaped(StringBuilder sb, string value, bool interpolated)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                case '{' when interpolated: sb.Append("{{"); break;
                case '}' when interpolated: sb.Append("}}"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
    }

    /// <summary>Decodes JavaScript escape sequences in a raw template chunk.</summary>
    public static string DecodeTemplateChunk(string raw)
    {
        if (raw.IndexOf('\\') < 0) return raw;
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length)
            {
                sb.Append(c);
                continue;
            }
            char e = raw[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '0': sb.Append('\0'); break;
                case 'u' when i + 4 < raw.Length && int.TryParse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, null, out int u):
                    sb.Append((char)u);
                    i += 4;
                    break;
                case '\n': break;
                default: sb.Append(e); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Converts a JS numeric literal to valid C# (octal and separators handled).</summary>
    public static string NumberLiteral(string text)
    {
        if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            var digits = text[2..].Replace("_", "", StringComparison.Ordinal);
            return Convert.ToInt64(digits, 8).ToString(CultureInfo.InvariantCulture);
        }
        if (text.StartsWith('.')) return "0" + text;
        if (text.EndsWith('_')) return text.TrimEnd('_');
        return text;
    }

    public static string PascalCase(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool upper = true;
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upper = true;
                continue;
            }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }
}
