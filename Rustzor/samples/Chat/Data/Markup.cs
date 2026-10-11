using System.Text;
using System.Text.RegularExpressions;

namespace Chat.Data;

/// <summary>
/// The little formatting a chat needs: **bold**, _italic_, `code`, links,
/// @mentions and line breaks. The text is HTML-escaped first, so nothing a
/// person types can inject markup.
/// </summary>
public static class Markup
{
    private static readonly Regex Code = new("`([^`\n]+)`");
    private static readonly Regex Bold = new(@"\*\*([^*\n]+)\*\*");
    private static readonly Regex Italic = new(@"(^|\s)_([^_\n]+)_(?=\s|$|[.,!?])");
    private static readonly Regex Link = new(@"(https?://[^\s<]+[^\s<.,!?)])");

    public static string ToHtml(string text)
    {
        var html = Escape(text);
        html = Code.Replace(html, "<code>$1</code>");
        html = Bold.Replace(html, "<strong>$1</strong>");
        html = Italic.Replace(html, "$1<em>$2</em>");
        html = Link.Replace(html, "<a href=\"$1\" target=\"_blank\" rel=\"noopener\">$1</a>");
        foreach (var p in ChatStore.People)
        {
            var me = ChatStore.Me?.Id == p.Id ? " self" : "";
            html = Regex.Replace(html, "@" + p.First + @"\b", $"<span class=\"mention{me}\" style=\"--who:{p.Color}\">@{p.First}</span>", RegexOptions.IgnoreCase);
        }
        foreach (var c in ChatStore.Channels)
            html = Regex.Replace(html, "(^|\\s)#" + c.Name + @"\b", $"$1<span class=\"chan\">#{c.Name}</span>");
        return html.Replace("\n", "<br>");
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (var ch in s)
            sb.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;", _ => ch.ToString() });
        return sb.ToString();
    }
}
