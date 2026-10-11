using System.Text;
using Rustzor.Interop;

namespace ApiPlayground.Data;

/// <summary>Pretty-prints JSON as HTML with a class per token kind, for the response viewer.</summary>
public static class JsonView
{
    public static string ToHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "<span class=\"j-none\">(empty body)</span>";
        var t = text.TrimStart();
        if (t[0] != '{' && t[0] != '[') return Escape(text);
        try
        {
            var sb = new StringBuilder();
            Write(sb, JsonValue.Parse(text), 0);
            return sb.ToString();
        }
        catch
        {
            return Escape(text);
        }
    }

    /// <summary>Indented JSON text, for editors; the input unchanged when it is not JSON.</summary>
    public static string Indent(string text)
    {
        try { return Strip(ToHtml(text)); } catch { return text; }
    }

    private static string Strip(string html)
    {
        var sb = new StringBuilder();
        var inTag = false;
        foreach (var c in html)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString().Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&amp;", "&");
    }

    private static void Write(StringBuilder sb, JsonValue v, int depth)
    {
        var pad = new string(' ', (depth + 1) * 2);
        var close = new string(' ', depth * 2);
        if (v.IsObject)
        {
            if (v.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{\n");
            var first = true;
            foreach (var key in v.Keys)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append(pad).Append("<span class=\"j-key\">\"").Append(Escape(key)).Append("\"</span>: ");
                Write(sb, v[key], depth + 1);
            }
            sb.Append('\n').Append(close).Append('}');
        }
        else if (v.IsArray)
        {
            if (v.Count == 0) { sb.Append("[]"); return; }
            sb.Append("[\n");
            var i = 0;
            foreach (var item in v.Items)
            {
                if (i++ > 0) sb.Append(",\n");
                sb.Append(pad);
                Write(sb, item, depth + 1);
            }
            sb.Append('\n').Append(close).Append(']');
        }
        else if (v.IsString) sb.Append("<span class=\"j-str\">").Append(Escape(v.ToJson())).Append("</span>");
        else if (v.IsNumber) sb.Append("<span class=\"j-num\">").Append(v.ToJson()).Append("</span>");
        else if (v.IsBool) sb.Append("<span class=\"j-bool\">").Append(v.ToJson()).Append("</span>");
        else sb.Append("<span class=\"j-null\">null</span>");
    }

    public static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
