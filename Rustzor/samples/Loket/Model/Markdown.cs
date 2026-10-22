using System.Text;

namespace Loket.Model;

/// <summary>
/// Markdown for chat replies: headings, paragraphs, lists, quotes, tables, fenced code with a
/// copy button, inline code, bold, italic, links and citations [n]. Every character of the
/// input is HTML-escaped first; the only tags in the output are the ones written here, links
/// are kept only for http, https and mailto, so a model (or a resident) cannot inject markup.
/// </summary>
public static class Markdown
{
    /// <param name="sources">How many citations exist: [n] becomes a button only for 1..sources.</param>
    /// <param name="message">The message index, carried on citation buttons.</param>
    public static string Render(string text, int sources = 0, int message = -1)
    {
        var html = new StringBuilder();
        var lines = text.Replace("\r", "").Split('\n');
        var i = 0;
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>").Append(string.Join("<br>", paragraph.Select(l => Inline(l, sources, message)))).Append("</p>");
            paragraph.Clear();
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```"))
            {
                Flush();
                var lang = Escape(trimmed[3..].Trim());
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```"))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                i++; // the closing fence (or the end, while streaming)
                html.Append("<div class=\"lk-code\"><div class=\"lk-code-bar\"><span>").Append(lang.Length > 0 ? lang : "text")
                    .Append("</span><button type=\"button\" class=\"lk-copy\">Salin</button></div><pre><code>")
                    .Append(Escape(code.ToString())).Append("</code></pre></div>");
                continue;
            }

            if (trimmed.Length == 0)
            {
                Flush();
                i++;
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                if (level <= 6 && trimmed.Length > level && trimmed[level] == ' ')
                {
                    Flush();
                    var tag = "h" + Math.Min(6, level + 2); // h3..h6 inside a bubble
                    html.Append('<').Append(tag).Append('>').Append(Inline(trimmed[(level + 1)..], sources, message)).Append("</").Append(tag).Append('>');
                    i++;
                    continue;
                }
            }

            if (trimmed.StartsWith("> ") || trimmed == ">")
            {
                Flush();
                var quote = new List<string>();
                while (i < lines.Length && (lines[i].Trim().StartsWith("> ") || lines[i].Trim() == ">"))
                {
                    quote.Add(lines[i].Trim().TrimStart('>').Trim());
                    i++;
                }
                html.Append("<blockquote>").Append(string.Join("<br>", quote.Select(q => Inline(q, sources, message)))).Append("</blockquote>");
                continue;
            }

            if (IsTableRow(trimmed) && i + 1 < lines.Length && IsDivider(lines[i + 1].Trim()))
            {
                Flush();
                html.Append("<div class=\"lk-table\"><table><thead><tr>");
                foreach (var cell in Cells(trimmed)) html.Append("<th>").Append(Inline(cell, sources, message)).Append("</th>");
                html.Append("</tr></thead><tbody>");
                i += 2;
                while (i < lines.Length && IsTableRow(lines[i].Trim()))
                {
                    html.Append("<tr>");
                    foreach (var cell in Cells(lines[i].Trim())) html.Append("<td>").Append(Inline(cell, sources, message)).Append("</td>");
                    html.Append("</tr>");
                    i++;
                }
                html.Append("</tbody></table></div>");
                continue;
            }

            if (ListItem(trimmed, out var ordered, out _))
            {
                Flush();
                var tag = ordered ? "ol" : "ul";
                html.Append('<').Append(tag).Append('>');
                while (i < lines.Length && ListItem(lines[i].Trim(), out var o2, out var body) && o2 == ordered)
                {
                    html.Append("<li>").Append(Inline(body, sources, message)).Append("</li>");
                    i++;
                }
                html.Append("</").Append(tag).Append('>');
                continue;
            }

            paragraph.Add(trimmed);
            i++;
        }
        Flush();
        return html.ToString();
    }

    private static bool ListItem(string line, out bool ordered, out string body)
    {
        ordered = false;
        body = "";
        if (line.Length > 2 && (line[0] is '-' or '*' or '•') && line[1] == ' ')
        {
            body = line[2..];
            return true;
        }
        var digits = 0;
        while (digits < line.Length && char.IsDigit(line[digits])) digits++;
        if (digits > 0 && digits < 4 && line.Length > digits + 2 && (line[digits] is '.' or ')') && line[digits + 1] == ' ')
        {
            ordered = true;
            body = line[(digits + 2)..];
            return true;
        }
        return false;
    }

    private static bool IsTableRow(string line) => line.StartsWith('|') && line.EndsWith('|') && line.Length > 2;

    private static bool IsDivider(string line) =>
        IsTableRow(line) && line.Trim('|').Split('|').All(c => c.Trim().Length > 0 && c.Trim().All(ch => ch is '-' or ':'));

    private static List<string> Cells(string line) => line.Trim('|').Split('|').Select(c => c.Trim()).ToList();

    /// <summary>Inline markup over escaped text: `code`, **bold**, *italic*, [text](url), [n].</summary>
    public static string Inline(string raw, int sources = 0, int message = -1)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == '`')
            {
                var end = raw.IndexOf('`', i + 1);
                if (end > i)
                {
                    sb.Append("<code>").Append(Escape(raw[(i + 1)..end])).Append("</code>");
                    i = end + 1;
                    continue;
                }
            }
            if (c == '*' && i + 1 < raw.Length && raw[i + 1] == '*')
            {
                var end = raw.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    sb.Append("<strong>").Append(Inline(raw[(i + 2)..end], sources, message)).Append("</strong>");
                    i = end + 2;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < raw.Length && raw[i + 1] != ' ' && (i == 0 || !char.IsLetterOrDigit(raw[i - 1])))
            {
                var end = raw.IndexOf(c, i + 1);
                if (end > i + 1 && raw[end - 1] != ' ' && (end + 1 >= raw.Length || !char.IsLetterOrDigit(raw[end + 1])))
                {
                    sb.Append("<em>").Append(Inline(raw[(i + 1)..end], sources, message)).Append("</em>");
                    i = end + 1;
                    continue;
                }
            }
            if (c == '[')
            {
                var close = raw.IndexOf(']', i + 1);
                if (close > i)
                {
                    var label = raw[(i + 1)..close];
                    // [3] or [1, 2] after a sentence: citations.
                    if (label.Length > 0 && label.All(ch => char.IsDigit(ch) || ch == ',' || ch == ' ') && (close + 1 >= raw.Length || raw[close + 1] != '('))
                    {
                        var parts = label.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                        if (parts.Count > 0 && parts.All(p => int.TryParse(p, out var n) && n >= 1 && n <= sources))
                        {
                            foreach (var p in parts)
                                sb.Append("<button type=\"button\" class=\"lk-cite\" data-msg=\"").Append(message).Append("\" data-n=\"").Append(p).Append("\" title=\"Sumber ").Append(p).Append("\">").Append(p).Append("</button>");
                            i = close + 1;
                            continue;
                        }
                    }
                    if (close + 1 < raw.Length && raw[close + 1] == '(')
                    {
                        var end = raw.IndexOf(')', close + 2);
                        if (end > close)
                        {
                            var url = raw[(close + 2)..end].Trim();
                            if (url.StartsWith("https://") || url.StartsWith("http://") || url.StartsWith("mailto:"))
                            {
                                sb.Append("<a href=\"").Append(Escape(url)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
                                    .Append(Inline(label, sources, message)).Append("</a>");
                                i = end + 1;
                                continue;
                            }
                        }
                    }
                }
            }
            sb.Append(Escape(c.ToString()));
            i++;
        }
        return sb.ToString();
    }

    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '<' => "&lt;",
                '>' => "&gt;",
                '&' => "&amp;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>The conversation as Markdown, for export.</summary>
    public static string Export(Conv conv)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(conv.Ticket).Append(" · ").Append(conv.Title.Length > 0 ? conv.Title : "Percakapan").Append("\n\n");
        sb.Append("_").Append(Seed.Office).Append(", ").Append(Seed.City).Append(" · ").Append(conv.Updated.AddHours(7).ToString("yyyy-MM-dd HH:mm")).Append(" WIB_\n\n");
        foreach (var item in conv.Items)
        {
            sb.Append(item.Role == "user" ? "**Warga:** " : "**Loket:** ").Append(item.Text.Trim()).Append("\n\n");
            foreach (var t in item.Tools) sb.Append("> Tool `").Append(t.Name).Append("` ").Append(t.Args).Append(" → ").Append(t.Result.Replace("\n", " ")).Append("\n\n");
            foreach (var s in item.Sources) sb.Append("[").Append(s.N).Append("]: ").Append(s.DocTitle).Append(s.Section.Length > 0 ? " — " + s.Section : "").Append("\n");
            if (item.Sources.Count > 0) sb.Append('\n');
        }
        sb.Append("---\nBuilt by Gravicode Studios, led by Kang Fadhil.\n");
        return sb.ToString();
    }
}
