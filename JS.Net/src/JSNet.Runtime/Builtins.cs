using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace JSNet.Runtime;

/// <summary>Global functions, imported into every JS.Net file via <c>global using static</c>.</summary>
public static class Globals
{
    public static double parseFloat(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return double.NaN;
        s = s.Trim();
        int end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] is '.' or '-' or '+' or 'e' or 'E')) end++;
        return double.TryParse(s.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    /// <summary>Parses a leading integer (JavaScript semantics); returns 0 instead of NaN to stay an <c>int</c>.</summary>
    public static int parseInt(string? s, int radix = 10)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        int sign = 1, i = 0;
        if (s[0] is '-' or '+') { sign = s[0] == '-' ? -1 : 1; i = 1; }
        if (radix == 16 && s.Length > i + 1 && s[i] == '0' && s[i + 1] is 'x' or 'X') i += 2;
        long result = 0;
        for (; i < s.Length; i++)
        {
            int d = char.IsDigit(s[i]) ? s[i] - '0' : char.IsLetter(s[i]) ? char.ToLowerInvariant(s[i]) - 'a' + 10 : 99;
            if (d >= radix) break;
            result = result * radix + d;
        }
        return (int)(sign * result);
    }

    public static int parseInt(double value) => (int)Math.Truncate(value);

    public static bool isNaN(double value) => double.IsNaN(value);
    public static bool isNaN(object? value) => double.IsNaN(JS.toNumber(value));
    public static bool isFinite(double value) => double.IsFinite(value);

    // ---------------------------------------------------------------- timers

    public static int setTimeout(Action callback, double ms = 0) => EventLoop.Schedule(callback, ms, repeat: false);
    public static int setTimeout(Func<Task> callback, double ms = 0) => EventLoop.Schedule(() => callback().GetAwaiter().GetResult(), ms, repeat: false);
    public static int setInterval(Action callback, double ms) => EventLoop.Schedule(callback, ms, repeat: true);
    public static void clearTimeout(int id) => EventLoop.Cancel(id);
    public static void clearInterval(int id) => EventLoop.Cancel(id);

    /// <summary><c>await delay(500)</c> — the idiomatic replacement for <c>new Promise(r =&gt; setTimeout(r, 500))</c>.</summary>
    public static Task delay(double ms) => Task.Delay(TimeSpan.FromMilliseconds(ms));

    public static void queueMicrotask(Action callback) => ThreadPool.QueueUserWorkItem(_ => callback());

    // ---------------------------------------------------------------- fetch

    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient { DefaultRequestHeaders = { { "User-Agent", "JS.Net/0.1 (Gravicode Studios)" } } });

    /// <summary>Minimal WHATWG <c>fetch</c> backed by a shared <see cref="HttpClient"/>.</summary>
    public static async Task<FetchResponse> fetch(string url, FetchOptions? options = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(options?.method ?? "GET"), url);
        if (options?.body is not null)
        {
            request.Content = options.body is string s
                ? new StringContent(s, System.Text.Encoding.UTF8, options.headers?.GetValueOrDefault("Content-Type") ?? "text/plain")
                : new StringContent(JSON.stringify(options.body), System.Text.Encoding.UTF8, "application/json");
        }
        if (options?.headers is not null)
            foreach (var (k, v) in options.headers)
                if (!k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation(k, v);
        using var response = await Http.Value.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return new FetchResponse((int)response.StatusCode, response.ReasonPhrase ?? string.Empty, text,
            response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class FetchOptions
{
    public string method { get; set; } = "GET";
    public Dictionary<string, string>? headers { get; set; }
    public object? body { get; set; }
}

public sealed class FetchResponse(int status, string statusText, string body, Dictionary<string, string> headers)
{
    public int status { get; } = status;
    public string statusText { get; } = statusText;
    public bool ok => status is >= 200 and < 300;
    public Dictionary<string, string> headers { get; } = headers;
    public Task<string> text() => Task.FromResult(body);
    public Task<dynamic?> json() => Task.FromResult(JSON.parse(body));
    public Task<T?> json<T>() => Task.FromResult(JSON.parse<T>(body));
}

/// <summary>Tracks pending timers so a JS.Net program, like Node.js, exits only when no timers remain.</summary>
public static class EventLoop
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, CancellationTokenSource> Pending = new();
    private static int _nextId;

    internal static int Schedule(Action callback, double ms, bool repeat)
    {
        int id = Interlocked.Increment(ref _nextId);
        var cts = new CancellationTokenSource();
        Pending[id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, ms)), cts.Token).ConfigureAwait(false);
                    callback();
                } while (repeat && !cts.IsCancellationRequested);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { console.error("Uncaught", ex); }
            finally
            {
                if (Pending.TryRemove(id, out var c)) c.Dispose();
            }
        });
        return id;
    }

    internal static void Cancel(int id)
    {
        if (Pending.TryRemove(id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public static int pending => Pending.Count;

    /// <summary>Awaited at the end of a program's top-level statements.</summary>
    public static async Task drainAsync()
    {
        while (!Pending.IsEmpty) await Task.Delay(10).ConfigureAwait(false);
    }
}

/// <summary>Node.js-flavoured <c>process</c> object.</summary>
public static class process
{
    public static string[] argv => Environment.GetCommandLineArgs();
    public static IDictionary<string, string?> env => Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .ToDictionary(e => (string)e.Key, e => e.Value as string, StringComparer.OrdinalIgnoreCase);
    public static string platform => OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
    public static string cwd() => Environment.CurrentDirectory;
    public static int pid => Environment.ProcessId;
    public static void exit(int code = 0) => Environment.Exit(code);
    public static double uptime() => (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds;
    public static string version => "jsnet-" + typeof(process).Assembly.GetName().Version?.ToString(3) + " / .NET " + Environment.Version;
}

/// <summary>JavaScript <c>Math</c> (lower-case API, double precision).</summary>
public static class JSMath
{
    public const double PI = Math.PI;
    public const double E = Math.E;
    public const double LN2 = 0.6931471805599453;
    public const double LN10 = 2.302585092994046;
    public const double LOG2E = 1.4426950408889634;
    public const double LOG10E = 0.4342944819032518;
    public const double SQRT2 = 1.4142135623730951;
    public const double SQRT1_2 = 0.7071067811865476;

    public static double abs(double x) => Math.Abs(x);
    public static int abs(int x) => Math.Abs(x);
    public static double floor(double x) => Math.Floor(x);
    public static double ceil(double x) => Math.Ceiling(x);
    /// <summary>Rounds half up like JavaScript (not banker's rounding).</summary>
    public static double round(double x) => Math.Floor(x + 0.5);
    public static double trunc(double x) => Math.Truncate(x);
    public static double sign(double x) => double.IsNaN(x) ? double.NaN : Math.Sign(x);
    public static double sqrt(double x) => Math.Sqrt(x);
    public static double cbrt(double x) => Math.Cbrt(x);
    public static double pow(double x, double y) => Math.Pow(x, y);
    public static double exp(double x) => Math.Exp(x);
    public static double log(double x) => Math.Log(x);
    public static double log2(double x) => Math.Log2(x);
    public static double log10(double x) => Math.Log10(x);
    public static double sin(double x) => Math.Sin(x);
    public static double cos(double x) => Math.Cos(x);
    public static double tan(double x) => Math.Tan(x);
    public static double asin(double x) => Math.Asin(x);
    public static double acos(double x) => Math.Acos(x);
    public static double atan(double x) => Math.Atan(x);
    public static double atan2(double y, double x) => Math.Atan2(y, x);
    public static double sinh(double x) => Math.Sinh(x);
    public static double cosh(double x) => Math.Cosh(x);
    public static double tanh(double x) => Math.Tanh(x);
    public static double hypot(params double[] values) => Math.Sqrt(values.Sum(v => v * v));
    public static double random() => Random.Shared.NextDouble();
    public static double max(params double[] values) => values.Length == 0 ? double.NegativeInfinity : values.Max();
    public static double min(params double[] values) => values.Length == 0 ? double.PositiveInfinity : values.Min();
    public static int max(int a, int b) => Math.Max(a, b);
    public static int min(int a, int b) => Math.Min(a, b);
    public static double clamp(double x, double lo, double hi) => Math.Clamp(x, lo, hi);
}

public static class JSNumber
{
    public const double MAX_SAFE_INTEGER = 9007199254740991;
    public const double MIN_SAFE_INTEGER = -9007199254740991;
    public const double EPSILON = double.Epsilon;
    public const double MAX_VALUE = double.MaxValue;
    public const double MIN_VALUE = double.Epsilon;
    public const double POSITIVE_INFINITY = double.PositiveInfinity;
    public const double NEGATIVE_INFINITY = double.NegativeInfinity;
    public const double NaN = double.NaN;
    public static bool isInteger(double x) => double.IsFinite(x) && Math.Floor(x) == x;
    public static bool isSafeInteger(double x) => isInteger(x) && Math.Abs(x) <= MAX_SAFE_INTEGER;
    public static bool isFinite(double x) => double.IsFinite(x);
    public static bool isNaN(double x) => double.IsNaN(x);
    public static double parseFloat(string s) => Globals.parseFloat(s);
    public static int parseInt(string s, int radix = 10) => Globals.parseInt(s, radix);
}

public static class JSString
{
    public static string fromCharCode(params int[] codes) => new(codes.Select(c => (char)c).ToArray());
}

public static class JSArray
{
    public static bool isArray(object? value) => value is IList;
    public static List<T> from<T>(IEnumerable<T> source) => [.. source];
    public static List<TResult> from<T, TResult>(IEnumerable<T> source, Func<T, int, TResult> map) => source.Select(map).ToList();
    public static List<char> from(string source) => [.. source];
    public static List<T> of<T>(params T[] items) => [.. items];
    /// <summary><c>Array.range(5)</c> → [0,1,2,3,4] (JS.Net extension).</summary>
    public static List<int> range(int count) => [.. Enumerable.Range(0, count)];
    public static List<int> range(int start, int count) => [.. Enumerable.Range(start, count)];
}

public static class JSObject
{
    public static List<string> keys(object? obj) => [.. JS.keys(obj)];

    public static List<object?> values(object? obj) => obj switch
    {
        null => [],
        IDictionary d => d.Values.Cast<object?>().ToList(),
        _ => obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Select(p => p.GetValue(obj)).ToList(),
    };

    public static List<(string key, object? value)> entries(object? obj) => obj switch
    {
        null => [],
        IDictionary d => d.Cast<DictionaryEntry>().Select(e => (e.Key.ToString()!, e.Value)).ToList(),
        _ => obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Select(p => (p.Name, p.GetValue(obj))).ToList(),
    };

    public static List<(TKey key, TValue value)> entries<TKey, TValue>(IDictionary<TKey, TValue> dict) where TKey : notnull =>
        dict.Select(kv => (kv.Key, kv.Value)).ToList();

    public static T freeze<T>(T obj) => obj;

    public static JsObject assign(JsObject target, params object?[] sources)
    {
        foreach (var s in sources)
            foreach (var (k, v) in entries(s)) target[k] = v;
        return target;
    }

    public static JsObject fromEntries<TValue>(IEnumerable<(string, TValue)> entries)
    {
        var o = new JsObject();
        foreach (var (k, v) in entries) o[k] = v;
        return o;
    }
}

/// <summary>Arbitrary-precision helpers for <c>bigint</c>.</summary>
public static class JSBigInt
{
    public static BigInteger of(string value) => BigInteger.Parse(value, CultureInfo.InvariantCulture);
}
