using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;
using RustzorAppGen.Services;

namespace RustzorAppGen.Plugins;

/// <summary>Building and running the open project, so the assistant can check its own work.</summary>
public sealed class BuildPlugin(IWorkbench workbench)
{
    [KernelFunction("build_project")]
    [Description("Builds the open project with dotnet build and returns success or the compiler errors with file and line. Always build after changing code, and fix every error before finishing.")]
    public async Task<string> BuildProjectAsync(CancellationToken cancel)
    {
        var result = await workbench.BuildAsync(cancel);
        return result.Summary();
    }

    [KernelFunction("run_project")]
    [Description("Builds the project, serves it locally and opens it in the browser. Returns the URL.")]
    public async Task<string> RunProjectAsync(CancellationToken cancel)
    {
        var url = await workbench.RunAsync(cancel);
        return $"The app is running at {url}.";
    }
}

/// <summary>The web: search with Tavily, and read pages as plain text.</summary>
public sealed partial class WebPlugin(AppSettings settings)
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; RustzorAppGen/0.1)");
        return http;
    }

    [KernelFunction("search_internet")]
    [Description("Searches the internet (Tavily) and returns a short answer plus the top results with title, URL and snippet.")]
    public async Task<string> SearchInternetAsync(
        [Description("What to search for")] string query,
        [Description("How many results, 1-10")] int maxResults = 5,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(settings.TavilyApiKey)) return "Internet search is not set up: add a Tavily API key in Settings → Tools.";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.TavilyApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            query,
            max_results = Math.Clamp(maxResults, 1, 10),
            include_answer = true,
        }), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) return $"Search failed: HTTP {(int)response.StatusCode} {body[..Math.Min(body.Length, 300)]}";
        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.String)
            sb.AppendLine("Answer: " + answer.GetString()).AppendLine();
        if (doc.RootElement.TryGetProperty("results", out var results))
            foreach (var r in results.EnumerateArray())
                sb.AppendLine($"- {r.GetProperty("title").GetString()} ({r.GetProperty("url").GetString()})\n  {r.GetProperty("content").GetString()}");
        return sb.ToString();
    }

    [KernelFunction("scrape_webpage")]
    [Description("Downloads a web page and returns its readable text (scripts, styles and markup removed) and its title.")]
    public async Task<string> ScrapeWebpageAsync(
        [Description("Absolute http(s) URL")] string url,
        [Description("Most characters to return")] int maxChars = 12000,
        CancellationToken cancel = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "Give an absolute http or https URL.";
        using var response = await Http.GetAsync(uri, cancel);
        var html = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) return $"HTTP {(int)response.StatusCode} from {url}.";
        var title = TitleRegex().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : "";
        var text = StripRegex().Replace(html, " ");
        text = BlockRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = SpaceRegex().Replace(text, " ");
        text = LinesRegex().Replace(text, "\n\n").Trim();
        if (text.Length > maxChars) text = text[..maxChars] + "\n… (truncated)";
        return $"Title: {title}\nURL: {url}\n\n{text}";
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();
    [GeneratedRegex(@"<(script|style|noscript|svg|head)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StripRegex();
    [GeneratedRegex(@"</?(p|div|br|li|h[1-6]|tr|section|article|header|footer)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockRegex();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex SpaceRegex();
    [GeneratedRegex(@"\s*\n\s*(\n\s*)+")]
    private static partial Regex LinesRegex();
}

/// <summary>Exact arithmetic so the model does not have to guess.</summary>
public sealed class MathPlugin
{
    [KernelFunction("calculate")]
    [Description("Evaluates a math expression: + - * / % ^, parentheses, and functions sqrt, abs, round, floor, ceil, sin, cos, tan, asin, acos, atan, log (natural), log10, exp, min, max, pow; constants pi and e.")]
    public string Calculate([Description("The expression, e.g. (2^10 + sqrt(81)) / 3")] string expression)
    {
        try
        {
            var value = new ExpressionParser(expression).Parse();
            return value.ToString("G15", CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            return "Could not evaluate: " + e.Message;
        }
    }

    [KernelFunction("convert_units")]
    [Description("Converts a value between units of length (mm cm m km in ft yd mi), mass (g kg lb oz), temperature (c f k), or data (b kb mb gb).")]
    public string ConvertUnits(double value, [Description("Unit to convert from")] string from, [Description("Unit to convert to")] string to)
    {
        from = from.Trim().ToLowerInvariant();
        to = to.Trim().ToLowerInvariant();
        if (from is "c" or "f" or "k" && to is "c" or "f" or "k")
        {
            var celsius = from switch { "f" => (value - 32) * 5 / 9, "k" => value - 273.15, _ => value };
            var result = to switch { "f" => celsius * 9 / 5 + 32, "k" => celsius + 273.15, _ => celsius };
            return $"{value} {from} = {result:0.####} {to}";
        }
        var factors = new Dictionary<string, (string Kind, double ToBase)>
        {
            ["mm"] = ("len", 0.001), ["cm"] = ("len", 0.01), ["m"] = ("len", 1), ["km"] = ("len", 1000),
            ["in"] = ("len", 0.0254), ["ft"] = ("len", 0.3048), ["yd"] = ("len", 0.9144), ["mi"] = ("len", 1609.344),
            ["g"] = ("mass", 0.001), ["kg"] = ("mass", 1), ["lb"] = ("mass", 0.45359237), ["oz"] = ("mass", 0.028349523125),
            ["b"] = ("data", 1), ["kb"] = ("data", 1024), ["mb"] = ("data", 1024 * 1024), ["gb"] = ("data", 1024d * 1024 * 1024),
        };
        if (!factors.TryGetValue(from, out var f) || !factors.TryGetValue(to, out var t)) return $"Unknown unit '{from}' or '{to}'.";
        if (f.Kind != t.Kind) return $"Cannot convert {f.Kind} to {t.Kind}.";
        return $"{value} {from} = {(value * f.ToBase / t.ToBase).ToString("0.######", CultureInfo.InvariantCulture)} {to}";
    }

    /// <summary>Recursive-descent parser: expr := term (('+'|'-') term)*, term := power (('*'|'/'|'%') power)*, power := unary ('^' power)?.</summary>
    private sealed class ExpressionParser(string text)
    {
        private int _pos;

        public double Parse()
        {
            var v = Expr();
            Skip();
            if (_pos < text.Length) throw new FormatException($"unexpected '{text[_pos]}' at {_pos + 1}");
            return v;
        }

        private void Skip()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private bool Eat(char c)
        {
            Skip();
            if (_pos < text.Length && text[_pos] == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private double Expr()
        {
            var v = Term();
            while (true)
            {
                if (Eat('+')) v += Term();
                else if (Eat('-')) v -= Term();
                else return v;
            }
        }

        private double Term()
        {
            var v = Power();
            while (true)
            {
                if (Eat('*')) v *= Power();
                else if (Eat('/')) v /= Power();
                else if (Eat('%')) v %= Power();
                else return v;
            }
        }

        private double Power()
        {
            var b = Unary();
            return Eat('^') ? Math.Pow(b, Power()) : b;
        }

        private double Unary()
        {
            if (Eat('-')) return -Unary();
            if (Eat('+')) return Unary();
            return Primary();
        }

        private double Primary()
        {
            Skip();
            if (Eat('('))
            {
                var v = Expr();
                if (!Eat(')')) throw new FormatException("missing ')'");
                return v;
            }
            var start = _pos;
            if (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.'))
            {
                while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.')) _pos++;
                if (_pos < text.Length && (text[_pos] == 'e' || text[_pos] == 'E') && _pos + 1 < text.Length && (char.IsDigit(text[_pos + 1]) || text[_pos + 1] is '-' or '+'))
                {
                    _pos += 2;
                    while (_pos < text.Length && char.IsDigit(text[_pos])) _pos++;
                }
                return double.Parse(text[start.._pos], CultureInfo.InvariantCulture);
            }
            while (_pos < text.Length && char.IsLetterOrDigit(text[_pos])) _pos++;
            var name = text[start.._pos].ToLowerInvariant();
            if (name.Length == 0) throw new FormatException($"expected a number at {_pos + 1}");
            if (name == "pi") return Math.PI;
            if (name == "e") return Math.E;
            if (!Eat('(')) throw new FormatException($"unknown name '{name}'");
            var args = new List<double> { Expr() };
            while (Eat(',')) args.Add(Expr());
            if (!Eat(')')) throw new FormatException("missing ')'");
            double A(int i) => i < args.Count ? args[i] : throw new FormatException($"{name} needs {i + 1} argument(s)");
            return name switch
            {
                "sqrt" => Math.Sqrt(A(0)), "abs" => Math.Abs(A(0)), "floor" => Math.Floor(A(0)), "ceil" => Math.Ceiling(A(0)),
                "round" => args.Count > 1 ? Math.Round(A(0), (int)A(1)) : Math.Round(A(0)),
                "sin" => Math.Sin(A(0)), "cos" => Math.Cos(A(0)), "tan" => Math.Tan(A(0)),
                "asin" => Math.Asin(A(0)), "acos" => Math.Acos(A(0)), "atan" => Math.Atan(A(0)),
                "log" => args.Count > 1 ? Math.Log(A(0), A(1)) : Math.Log(A(0)), "log10" => Math.Log10(A(0)), "exp" => Math.Exp(A(0)),
                "min" => args.Min(), "max" => args.Max(), "pow" => Math.Pow(A(0), A(1)),
                _ => throw new FormatException($"unknown function '{name}'"),
            };
        }
    }
}

/// <summary>The current date and time, time zones and date arithmetic.</summary>
public sealed class TimePlugin
{
    [KernelFunction("get_current_date_time")]
    [Description("Returns the current date and time, in the user's time zone or in a named IANA/Windows time zone, with the day of the week.")]
    public string GetCurrentDateTime([Description("Time zone id like Asia/Jakarta or UTC; empty for the user's own")] string timeZone = "")
    {
        var zone = string.IsNullOrWhiteSpace(timeZone) ? TimeZoneInfo.Local : Find(timeZone);
        if (zone is null) return $"Unknown time zone '{timeZone}'.";
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        return $"{now:dddd, dd MMMM yyyy HH:mm:ss} (UTC{now:zzz}, {zone.Id})";
    }

    [KernelFunction("convert_time_zone")]
    [Description("Converts a date and time from one time zone to another.")]
    public string ConvertTimeZone(
        [Description("Date and time, e.g. 2026-03-01 14:30")] string dateTime,
        [Description("Source time zone id")] string fromZone,
        [Description("Target time zone id")] string toZone)
    {
        if (!DateTime.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return $"Could not read '{dateTime}' as a date.";
        var from = Find(fromZone);
        var to = Find(toZone);
        if (from is null || to is null) return "Unknown time zone.";
        var converted = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), from, to);
        return $"{dt:yyyy-MM-dd HH:mm} {from.Id} = {converted:yyyy-MM-dd HH:mm} {to.Id}";
    }

    [KernelFunction("date_difference")]
    [Description("Days, weeks and hours between two dates, and the weekday of each.")]
    public string DateDifference(string start, string end)
    {
        if (!DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) ||
            !DateTime.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
            return "Give two dates like 2026-01-31.";
        var span = b - a;
        return $"{a:yyyy-MM-dd} ({a:dddd}) to {b:yyyy-MM-dd} ({b:dddd}): {span.TotalDays:0.##} days, {span.TotalDays / 7:0.##} weeks, {span.TotalHours:0.#} hours.";
    }

    [KernelFunction("add_to_date")]
    [Description("Adds days, months or years (negative to subtract) to a date.")]
    public string AddToDate(string date, int days = 0, int months = 0, int years = 0)
    {
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return "Give a date like 2026-01-31.";
        var r = d.AddYears(years).AddMonths(months).AddDays(days);
        return $"{r:yyyy-MM-dd} ({r:dddd})";
    }

    private static TimeZoneInfo? Find(string id)
    {
        if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Small helpers that are easy to get subtly wrong by hand.</summary>
public sealed class UtilityPlugin
{
    [KernelFunction("new_guid")]
    [Description("Generates new GUIDs.")]
    public string NewGuid(int count = 1) => string.Join("\n", Enumerable.Range(0, Math.Clamp(count, 1, 50)).Select(_ => Guid.NewGuid().ToString()));

    [KernelFunction("encode_base64")]
    [Description("Encodes UTF-8 text as Base64.")]
    public string EncodeBase64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [KernelFunction("decode_base64")]
    [Description("Decodes Base64 to UTF-8 text.")]
    public string DecodeBase64(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
        }
        catch (FormatException)
        {
            return "That is not valid Base64.";
        }
    }

    [KernelFunction("sha256")]
    [Description("SHA-256 hash of UTF-8 text, as hex.")]
    public string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [KernelFunction("format_json")]
    [Description("Validates JSON and returns it pretty-printed, or the position of the first error.")]
    public string FormatJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException e)
        {
            return $"Invalid JSON: {e.Message}";
        }
    }

    [KernelFunction("color_palette")]
    [Description("Builds a harmonious colour palette (hex) from a base colour: tints, shades and the complementary hue.")]
    public string ColorPalette([Description("Base colour like #c2410c")] string baseColor)
    {
        var hex = baseColor.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return "Give a colour like #c2410c.";
        double r = (rgb >> 16 & 255) / 255.0, g = (rgb >> 8 & 255) / 255.0, b = (rgb & 255) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        double h = 0, s = 0;
        if (max != min)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        string Hsl(double hh, double ss, double ll)
        {
            hh = (hh % 360 + 360) % 360 / 360;
            double Hue(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
            }
            var qq = ll < 0.5 ? ll * (1 + ss) : ll + ss - ll * ss;
            var pp = 2 * ll - qq;
            int C(double v) => (int)Math.Round(v * 255);
            return $"#{C(Hue(pp, qq, hh + 1.0 / 3)):x2}{C(Hue(pp, qq, hh)):x2}{C(Hue(pp, qq, hh - 1.0 / 3)):x2}";
        }
        var steps = new[] { 0.95, 0.85, 0.7, 0.55, l, 0.35, 0.25, 0.15 };
        var scale = string.Join(" ", steps.Select(x => Hsl(h, s, x)));
        return $"Scale (light→dark): {scale}\nComplement: {Hsl(h + 180, s, l)}\nAnalogous: {Hsl(h - 30, s, l)} {Hsl(h + 30, s, l)}";
    }
}
