using System.Diagnostics;
using System.Text;

namespace JSNet.Runtime;

/// <summary>
/// The JavaScript <c>console</c> object. Output goes to <see cref="Console.Out"/> unless redirected with
/// <see cref="capture"/>, which uses an <see cref="AsyncLocal{T}"/> so concurrent captures (e.g. the
/// JS.Net Gallery running samples in parallel) never interleave.
/// </summary>
public static class console
{
    private static readonly AsyncLocal<TextWriter?> Override = new();
    private static readonly AsyncLocal<int> GroupDepth = new();
    private static readonly Dictionary<string, Stopwatch> Timers = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Counters = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    private static TextWriter Out => Override.Value ?? Console.Out;

    public static void log(params object?[] args) => Write(Out, args, null);
    public static void info(params object?[] args) => Write(Out, args, ConsoleColor.Cyan);
    public static void debug(params object?[] args) => Write(Out, args, ConsoleColor.DarkGray);
    public static void warn(params object?[] args) => Write(Override.Value ?? Console.Error, args, ConsoleColor.Yellow);
    public static void error(params object?[] args) => Write(Override.Value ?? Console.Error, args, ConsoleColor.Red);

    public static void table(object? data) => Out.WriteLine(TableFormatter.Format(data));

    public static void group(params object?[] label)
    {
        if (label.Length > 0) log(label);
        GroupDepth.Value++;
    }

    public static void groupEnd() => GroupDepth.Value = Math.Max(0, GroupDepth.Value - 1);

    public static void assert(bool condition, params object?[] args)
    {
        if (!condition) error(["Assertion failed:", .. args]);
    }

    public static void time(string label = "default")
    {
        lock (Gate) Timers[label] = Stopwatch.StartNew();
    }

    public static void timeEnd(string label = "default")
    {
        Stopwatch? sw;
        lock (Gate)
        {
            if (!Timers.Remove(label, out sw)) return;
        }
        log($"{label}: {sw.Elapsed.TotalMilliseconds:0.###}ms");
    }

    public static void count(string label = "default")
    {
        int n;
        lock (Gate) n = Counters[label] = Counters.GetValueOrDefault(label) + 1;
        log($"{label}: {n}");
    }

    public static void clear()
    {
        if (Override.Value is null && !Console.IsOutputRedirected) Console.Clear();
    }

    /// <summary>Runs <paramref name="action"/> and returns everything it wrote through <c>console</c>.</summary>
    public static async Task<string> capture(Func<Task> action)
    {
        var writer = new StringWriter();
        var previous = Override.Value;
        Override.Value = writer;
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            Override.Value = previous;
        }
        return writer.ToString();
    }

    public static string capture(Action action)
    {
        var writer = new StringWriter();
        var previous = Override.Value;
        Override.Value = writer;
        try { action(); }
        finally { Override.Value = previous; }
        return writer.ToString();
    }

    /// <summary>Permanently redirects console output for the current async flow (null restores the default).</summary>
    public static void setOut(TextWriter? writer) => Override.Value = writer;

    private static void Write(TextWriter writer, object?[] args, ConsoleColor? color)
    {
        var sb = new StringBuilder();
        sb.Append(' ', GroupDepth.Value * 2);
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(args[i] is string s ? s : JS.inspect(args[i]));
        }
        bool colorize = color is not null && Override.Value is null && !Console.IsOutputRedirected;
        lock (Gate)
        {
            if (colorize) Console.ForegroundColor = color!.Value;
            writer.WriteLine(sb.ToString());
            if (colorize) Console.ResetColor();
        }
    }
}

internal static class TableFormatter
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Best-effort display/introspection of user objects; trimmed members are simply omitted.")]
    public static string Format(object? data)
    {
        if (data is not System.Collections.IEnumerable rows || data is string) return JS.inspect(data);
        var list = rows.Cast<object?>().ToList();
        var columns = new List<string>();
        var cells = new List<Dictionary<string, string>>();
        foreach (var row in list)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (row is null || row is string || row.GetType().IsPrimitive)
            {
                dict["Values"] = JS.str(row);
            }
            else
            {
                foreach (var p in row.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0))
                    dict[p.Name] = JS.str(p.GetValue(row));
            }
            foreach (var k in dict.Keys) if (!columns.Contains(k)) columns.Add(k);
            cells.Add(dict);
        }
        columns.Insert(0, "(index)");
        for (int i = 0; i < cells.Count; i++) cells[i]["(index)"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var widths = columns.Select(c => Math.Max(c.Length, cells.Select(r => r.GetValueOrDefault(c, "").Length).DefaultIfEmpty(0).Max())).ToArray();
        var sb = new StringBuilder();
        string Line(char l, char m, char r) => l + string.Join(m, widths.Select(w => new string('─', w + 2))) + r;
        sb.AppendLine(Line('┌', '┬', '┐'));
        sb.AppendLine("│" + string.Join("│", columns.Select((c, i) => " " + c.PadRight(widths[i]) + " ")) + "│");
        sb.AppendLine(Line('├', '┼', '┤'));
        foreach (var r in cells)
            sb.AppendLine("│" + string.Join("│", columns.Select((c, i) => " " + r.GetValueOrDefault(c, "").PadRight(widths[i]) + " ")) + "│");
        sb.Append(Line('└', '┴', '┘'));
        return sb.ToString();
    }
}
