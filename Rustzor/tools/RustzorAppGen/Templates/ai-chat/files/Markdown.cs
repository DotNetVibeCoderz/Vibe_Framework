using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace {{Namespace}};

/// <summary>
/// A small CommonMark subset: headings, paragraphs, emphasis, inline code,
/// fenced code, block quotes, ordered and bullet lists, task lists, links,
/// horizontal rules and tables. Input is HTML-escaped first, so notes cannot
/// inject markup.
/// </summary>
public static class Markdown
{
    public static string ToHtml(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var html = new StringBuilder();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { i++; continue; }

            if (line.StartsWith("```"))
            {
                var lang = line.Substring(3).Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```")) code.Append(lines[i++]).Append('\n');
                i++;
                html.Append("<pre><code").Append(lang.Length > 0 ? $" class=\"lang-{Esc(lang)}\"" : "").Append('>')
                    .Append(Esc(code.ToString().TrimEnd('\n'))).Append("</code></pre>");
                continue;
            }
            var heading = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                html.Append($"<h{level}>").Append(Inline(heading.Groups[2].Value)).Append($"</h{level}>");
                i++;
                continue;
            }
            if (Regex.IsMatch(line, @"^\s*([-*_])(\s*\1){2,}\s*$"))
            {
                html.Append("<hr>");
                i++;
                continue;
            }
            if (line.StartsWith(">"))
            {
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].StartsWith(">")) quote.Append(lines[i++].TrimStart('>').TrimStart()).Append('\n');
                html.Append("<blockquote>").Append(ToHtml(quote.ToString())).Append("</blockquote>");
                continue;
            }
            if (line.Contains('|') && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$"))
            {
                html.Append("<table><thead><tr>");
                foreach (var cell in Cells(line)) html.Append("<th>").Append(Inline(cell)).Append("</th>");
                html.Append("</tr></thead><tbody>");
                i += 2;
                while (i < lines.Length && lines[i].Contains('|'))
                {
                    html.Append("<tr>");
                    foreach (var cell in Cells(lines[i++])) html.Append("<td>").Append(Inline(cell)).Append("</td>");
                    html.Append("</tr>");
                }
                html.Append("</tbody></table>");
                continue;
            }
            var bullet = Regex.Match(line, @"^\s*([-*+]|\d+[.)])\s+");
            if (bullet.Success)
            {
                var ordered = char.IsDigit(bullet.Groups[1].Value[0]);
                html.Append(ordered ? "<ol>" : "<ul>");
                while (i < lines.Length && Regex.Match(lines[i], @"^\s*([-*+]|\d+[.)])\s+") is { Success: true } m && char.IsDigit(m.Groups[1].Value[0]) == ordered)
                {
                    var item = lines[i].Substring(m.Length);
                    var task = Regex.Match(item, @"^\[([ xX])\]\s+");
                    if (task.Success)
                    {
                        var done = task.Groups[1].Value != " ";
                        html.Append($"<li class=\"task{(done ? " done" : "")}\"><input type=\"checkbox\" disabled{(done ? " checked" : "")}> ")
                            .Append(Inline(item.Substring(task.Length))).Append("</li>");
                    }
                    else
                    {
                        html.Append("<li>").Append(Inline(item)).Append("</li>");
                    }
                    i++;
                }
                html.Append(ordered ? "</ol>" : "</ul>");
                continue;
            }
            // A paragraph runs until a blank line or another block starts.
            var para = new StringBuilder(line.Trim());
            i++;
            while (i < lines.Length && lines[i].Trim().Length > 0 && !Regex.IsMatch(lines[i], @"^(#{1,6}\s|```|>|\s*([-*+]|\d+[.)])\s)"))
                para.Append(' ').Append(lines[i++].Trim());
            html.Append("<p>").Append(Inline(para.ToString())).Append("</p>");
        }
        return html.ToString();
    }

    private static IEnumerable<string> Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim());

    private static string Inline(string text)
    {
        var s = Esc(text);
        // Code spans first, so their contents are not formatted.
        var codes = new List<string>();
        s = Regex.Replace(s, "`([^`]+)`", m => { codes.Add(m.Groups[1].Value); return $"\u0001{codes.Count - 1}\u0001"; });
        s = Regex.Replace(s, @"\[([^\]]+)\]\((https?://[^\s)]+|mailto:[^\s)]+|#[^\s)]*)\)", "<a href=\"$2\" target=\"_blank\" rel=\"noopener\">$1</a>");
        s = Regex.Replace(s, @"\*\*([^*]+)\*\*|__([^_]+)__", m => $"<strong>{m.Groups[1].Value}{m.Groups[2].Value}</strong>");
        s = Regex.Replace(s, @"(?<![\w*])\*([^*]+)\*(?!\*)|(?<!\w)_([^_]+)_(?!\w)", m => $"<em>{m.Groups[1].Value}{m.Groups[2].Value}</em>");
        s = Regex.Replace(s, @"~~([^~]+)~~", "<del>$1</del>");
        s = Regex.Replace(s, "\u0001(\\d+)\u0001", m => $"<code>{codes[int.Parse(m.Groups[1].Value)]}</code>");
        return s;
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s);

    public static int Words(string s) => Regex.Matches(s, @"[\p{L}\p{N}']+").Count;
}
