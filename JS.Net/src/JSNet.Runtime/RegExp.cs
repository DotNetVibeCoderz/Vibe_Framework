using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace JSNet.Runtime;

/// <summary>
/// JavaScript <c>RegExp</c> (from <c>/pattern/flags</c> literals). Instances are cached per pattern and flags and
/// compiled, so literals inside loops cost nothing extra. Converts implicitly to <see cref="Regex"/>.
/// </summary>
public sealed class RegExp
{
    private static readonly ConcurrentDictionary<(string, string), RegExp> Cache = new();

    public RegExp(string pattern, string flags = "")
    {
        source = pattern;
        this.flags = flags;
        var options = RegexOptions.CultureInvariant;
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        if (flags.Contains('m')) options |= RegexOptions.Multiline;
        if (flags.Contains('s')) options |= RegexOptions.Singleline;
        regex = new Regex(pattern, options | RegexOptions.Compiled);
    }

    /// <summary>Used by compiled regex literals; returns a cached instance.</summary>
    public static RegExp of(string pattern, string flags) => Cache.GetOrAdd((pattern, flags), static k => new RegExp(k.Item1, k.Item2));

    public Regex regex { get; }
    public string source { get; }
    public string flags { get; }
    public bool global => flags.Contains('g');
    public bool ignoreCase => flags.Contains('i');
    public bool multiline => flags.Contains('m');

    public bool test(string input) => regex.IsMatch(input);

    /// <summary>First match as <c>[ whole, group1, ... ]</c>, or null.</summary>
    public List<string>? exec(string input)
    {
        var m = regex.Match(input);
        return m.Success ? m.Groups.Cast<Group>().Select(g => g.Value).ToList() : null;
    }

    public static implicit operator Regex(RegExp r) => r.regex;
    public override string ToString() => $"/{source}/{flags}";
}

/// <summary>String methods that accept regular expressions.</summary>
public static class JsRegExpExtensions
{
    extension(string s)
    {
        /// <summary>Global regex → all matches; otherwise the first match with its groups.</summary>
        public List<string>? match(RegExp re)
        {
            if (re.global)
            {
                var all = re.regex.Matches(s).Select(m => m.Value).ToList();
                return all.Count == 0 ? null : all;
            }
            return re.exec(s);
        }

        public IEnumerable<List<string>> matchAll(RegExp re) =>
            re.regex.Matches(s).Select(m => m.Groups.Cast<Group>().Select(g => g.Value).ToList());

        public string replace(RegExp re, string replacement) =>
            re.global ? re.regex.Replace(s, replacement) : re.regex.Replace(s, replacement, 1);

        public string replace(RegExp re, Func<string, string> replacer) =>
            re.global ? re.regex.Replace(s, m => replacer(m.Value)) : re.regex.Replace(s, m => replacer(m.Value), 1);

        public string replaceAll(RegExp re, string replacement) => re.regex.Replace(s, replacement);

        public List<string> split(RegExp re) => [.. re.regex.Split(s)];

        public int search(RegExp re)
        {
            var m = re.regex.Match(s);
            return m.Success ? m.Index : -1;
        }
    }
}
