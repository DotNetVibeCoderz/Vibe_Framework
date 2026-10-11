using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RustzorAppGen.Services;

/// <summary>Formats a document by its file type.</summary>
public static class CodeFormatter
{
    public static string Format(string path, string text)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".cs" => CSharp(text),
                ".json" => Json(text),
                ".csproj" or ".props" or ".targets" or ".xml" or ".axaml" or ".xaml" or ".config" or ".svg" => Xml(text),
                ".razor" or ".html" or ".htm" or ".cshtml" => Markup(text),
                ".css" or ".js" or ".mjs" or ".ts" => Braces(text),
                _ => text,
            };
        }
        catch (Exception e)
        {
            throw new FormatException($"Could not format: {e.Message}", e);
        }
    }

    private static string CSharp(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) throw new FormatException($"the file has syntax errors (line {errors[0].Location.GetLineSpan().StartLinePosition.Line + 1}: {errors[0].GetMessage()})");
        var formatted = tree.GetRoot().NormalizeWhitespace("    ", "\n").ToFullString();
        // NormalizeWhitespace packs members together; give top-level members and methods a blank line between them.
        var lines = formatted.Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]).Append('\n');
            var trimmed = lines[i].Trim();
            var next = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
            if (trimmed == "}" && next.Length > 0 && next != "}" && !next.StartsWith("else") && !next.StartsWith("catch") && !next.StartsWith("finally") && !next.StartsWith(")") && !next.StartsWith("while") && !next.StartsWith(";") && !next.StartsWith(","))
                sb.Append('\n');
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Json(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static string Xml(string text)
    {
        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var declaration = doc.Declaration is null ? "" : doc.Declaration + "\n";
        var clean = XDocument.Parse(doc.ToString(), LoadOptions.None);
        return declaration + clean.ToString() + "\n";
    }

    /// <summary>Re-indents Razor/HTML by tag and brace nesting, four spaces per level.</summary>
    private static string Markup(string text)
    {
        var sb = new StringBuilder();
        var depth = 0;
        var inPre = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (inPre)
            {
                sb.Append(raw).Append('\n');
                if (line.Contains("</pre>") || line.Contains("</textarea>")) inPre = false;
                continue;
            }
            if (line.Length == 0)
            {
                sb.Append('\n');
                continue;
            }
            var closesFirst = line.StartsWith("</") || line.StartsWith("}") || line.StartsWith(")");
            if (closesFirst) depth = Math.Max(0, depth - 1);
            sb.Append(new string(' ', depth * 4)).Append(line).Append('\n');
            var delta = Opens(line) - Closes(line) + (closesFirst ? 1 : 0);
            depth = Math.Max(0, depth + delta);
            if ((line.Contains("<pre") && !line.Contains("</pre>")) || (line.Contains("<textarea") && !line.Contains("</textarea>"))) inPre = true;
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr", "!doctype" };

    private static int Opens(string line)
    {
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '{' && !InString(line, i)) count++;
            if (line[i] == '<' && i + 1 < line.Length && (char.IsLetter(line[i + 1]) || line[i + 1] == '!'))
            {
                var end = line.IndexOf('>', i);
                if (end < 0) { count++; break; }
                var tag = new string(line[(i + 1)..].TakeWhile(c => char.IsLetterOrDigit(c) || c == '!' || c == '-').ToArray());
                if (line[end - 1] != '/' && !VoidTags.Contains(tag)) count++;
                i = end;
            }
        }
        return count;
    }

    private static int Closes(string line)
    {
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '}' && !InString(line, i)) count++;
            if (line[i] == '<' && i + 1 < line.Length && line[i + 1] == '/') count++;
        }
        return count;
    }

    private static bool InString(string line, int index)
    {
        var quotes = 0;
        for (var i = 0; i < index; i++) if (line[i] == '"') quotes++;
        return quotes % 2 == 1;
    }

    /// <summary>Re-indents CSS/JavaScript by brace nesting.</summary>
    private static string Braces(string text)
    {
        var sb = new StringBuilder();
        var depth = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                sb.Append('\n');
                continue;
            }
            var opens = line.Count(c => c is '{' or '[' or '(');
            var closes = line.Count(c => c is '}' or ']' or ')');
            var leading = line.TakeWhile(c => c is '}' or ']' or ')').Count();
            sb.Append(new string(' ', Math.Max(0, depth - leading) * 2)).Append(line).Append('\n');
            depth = Math.Max(0, depth + opens - closes);
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}
