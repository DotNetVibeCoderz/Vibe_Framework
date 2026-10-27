using System.Collections;
using System.Dynamic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JSNet.Runtime;

/// <summary>
/// A mutable, dynamic JavaScript object (<c>let o: dynamic = { a: 1 }; o.b = 2;</c>). Plain object literals
/// compile to faster immutable anonymous types; use this when properties are added at runtime.
/// </summary>
public sealed class JsObject : DynamicObject, IDictionary<string, object?>
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public object? this[string key]
    {
        get => _values.GetValueOrDefault(key);
        set => _values[key] = value;
    }

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        result = _values.GetValueOrDefault(binder.Name);
        return true;
    }

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        _values[binder.Name] = value;
        return true;
    }

    public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
    {
        if (_values.GetValueOrDefault(binder.Name) is Delegate d)
        {
            result = d.DynamicInvoke(args);
            return true;
        }
        return base.TryInvokeMember(binder, args, out result);
    }

    public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
    {
        result = _values.GetValueOrDefault(Convert.ToString(indexes[0], CultureInfo.InvariantCulture) ?? string.Empty);
        return true;
    }

    public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object? value)
    {
        _values[Convert.ToString(indexes[0], CultureInfo.InvariantCulture) ?? string.Empty] = value;
        return true;
    }

    public override IEnumerable<string> GetDynamicMemberNames() => _values.Keys;
    public override string ToString() => JS.inspect(this);

    public ICollection<string> Keys => _values.Keys;
    public ICollection<object?> Values => _values.Values;
    public int Count => _values.Count;
    public bool IsReadOnly => false;
    public void Add(string key, object? value) => _values.Add(key, value);
    public void Add(KeyValuePair<string, object?> item) => _values.Add(item.Key, item.Value);
    public void Clear() => _values.Clear();
    public bool Contains(KeyValuePair<string, object?> item) => _values.Contains(item);
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex) => ((ICollection<KeyValuePair<string, object?>>)_values).CopyTo(array, arrayIndex);
    public bool Remove(string key) => _values.Remove(key);
    public bool Remove(KeyValuePair<string, object?> item) => _values.Remove(item.Key);
    public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>The JavaScript <c>JSON</c> object backed by System.Text.Json.</summary>
public static class JSON
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };

    public static string stringify(object? value, object? replacer = null, int space = 0) =>
        JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), space > 0 ? Indented : Compact);

    /// <summary>Parses into dynamic JS values: <see cref="JsObject"/>, <c>List&lt;dynamic&gt;</c>, double, string, bool or null.</summary>
    public static dynamic? parse(string text) => Convert(JsonNode.Parse(text));

    public static T? parse<T>(string text) => JsonSerializer.Deserialize<T>(text, Compact);

    private static object? Convert(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => o.Aggregate(new JsObject(), (acc, kv) => { acc[kv.Key] = Convert(kv.Value); return acc; }),
        JsonArray a => a.Select(Convert).ToList<dynamic?>(),
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => v.GetValue<double>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        },
        _ => null,
    };
}

/// <summary>JavaScript <c>Error</c>. Throwing a string or any value wraps it in an <see cref="Error"/>.</summary>
public class Error(string? message = null, Exception? inner = null) : Exception(message, inner)
{
    public virtual string name => GetType().Name;
    public string message => Message;
    public string? stack => StackTrace;
    /// <summary>The original thrown value when a non-Error was thrown.</summary>
    public object? value { get; init; }
    public override string ToString() => $"{name}: {Message}";
}

public class TypeError(string? message = null) : Error(message);
public class RangeError(string? message = null) : Error(message);
public class SyntaxError(string? message = null) : Error(message);

/// <summary>
/// JavaScript-style <c>Date</c> (months are 0-based like JS). Converts implicitly to and from
/// <see cref="DateTime"/> for seamless .NET interop.
/// </summary>
public sealed class Date : IComparable<Date>
{
    private readonly DateTime _value;

    public Date() => _value = DateTime.Now;
    public Date(double milliseconds) => _value = DateTime.UnixEpoch.AddMilliseconds(milliseconds).ToLocalTime();
    public Date(string text) => _value = DateTime.Parse(text, CultureInfo.InvariantCulture);
    public Date(int year, int month, int day = 1, int hours = 0, int minutes = 0, int seconds = 0, int ms = 0) =>
        _value = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Local).AddMonths(month).AddDays(day - 1).AddHours(hours).AddMinutes(minutes).AddSeconds(seconds).AddMilliseconds(ms);
    public Date(DateTime value) => _value = value;

    public static double now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static double parse(string text) => new Date(text).getTime();

    public double getTime() => new DateTimeOffset(_value).ToUnixTimeMilliseconds();
    public int getFullYear() => _value.Year;
    public int getMonth() => _value.Month - 1;
    public int getDate() => _value.Day;
    public int getDay() => (int)_value.DayOfWeek;
    public int getHours() => _value.Hour;
    public int getMinutes() => _value.Minute;
    public int getSeconds() => _value.Second;
    public int getMilliseconds() => _value.Millisecond;
    public string toISOString() => _value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    public string toJSON() => toISOString();
    public string toLocaleDateString(string? locale = null) => _value.ToString("d", locale is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(locale));
    public string toLocaleTimeString(string? locale = null) => _value.ToString("T", locale is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(locale));
    public string toLocaleString(string? locale = null) => _value.ToString("G", locale is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(locale));
    public string toDateString() => _value.ToString("ddd MMM dd yyyy", CultureInfo.InvariantCulture);
    public Date addDays(double days) => new(_value.AddDays(days));
    public override string ToString() => _value.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT'zzz", CultureInfo.InvariantCulture);
    public int CompareTo(Date? other) => other is null ? 1 : _value.CompareTo(other._value);

    public static implicit operator DateTime(Date d) => d._value;
    public static implicit operator Date(DateTime d) => new(d);
    public static double operator -(Date a, Date b) => (a._value - b._value).TotalMilliseconds;
    public static bool operator <(Date a, Date b) => a._value < b._value;
    public static bool operator >(Date a, Date b) => a._value > b._value;
}

/// <summary>
/// <c>new Promise((resolve, reject) =&gt; ...)</c> plus the static combinators. Promises are awaitable and
/// convert implicitly to <see cref="Task"/>, so they interoperate with every async .NET API.
/// </summary>
public sealed class Promise
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Promise(Action<Action, Action<object?>> executor)
    {
        try { executor(() => _tcs.TrySetResult(), e => _tcs.TrySetException(JS.toException(e))); }
        catch (Exception ex) { _tcs.TrySetException(ex); }
    }

    public Promise(Action<Action> executor) : this((resolve, _) => executor(resolve)) { }

    public Task task => _tcs.Task;
    public TaskAwaiter GetAwaiter() => _tcs.Task.GetAwaiter();
    public static implicit operator Task(Promise p) => p._tcs.Task;

    public static Task<T[]> all<T>(IEnumerable<Task<T>> tasks) => Task.WhenAll(tasks);
    public static Task all(IEnumerable<Task> tasks) => Task.WhenAll(tasks);
    public static async Task<T> race<T>(IEnumerable<Task<T>> tasks) => await await Task.WhenAny(tasks).ConfigureAwait(false);
    public static async Task<T> any<T>(IEnumerable<Task<T>> tasks)
    {
        var list = tasks.ToList();
        var errors = new List<Exception>();
        while (list.Count > 0)
        {
            var done = await Task.WhenAny(list).ConfigureAwait(false);
            list.Remove(done);
            if (done.IsCompletedSuccessfully) return done.Result;
            errors.Add(done.Exception?.InnerException ?? new Error("Promise rejected"));
        }
        throw new AggregateException("All promises were rejected", errors);
    }

    public static async Task<List<SettledResult<T>>> allSettled<T>(IEnumerable<Task<T>> tasks)
    {
        var list = tasks.ToList();
        try { await Task.WhenAll(list).ConfigureAwait(false); } catch { /* inspected below */ }
        return list.Select(t => t.IsCompletedSuccessfully
            ? new SettledResult<T>("fulfilled", t.Result, null)
            : new SettledResult<T>("rejected", default, t.Exception?.InnerException)).ToList();
    }

    public static Task<T> resolve<T>(T value) => Task.FromResult(value);
    public static Task resolve() => Task.CompletedTask;
    public static Task reject(object? reason) => Task.FromException(JS.toException(reason));
    public static Task delay(double ms) => Task.Delay(TimeSpan.FromMilliseconds(ms));
}

public sealed record SettledResult<T>(string status, T? value, Exception? reason);

public sealed class Promise<T>
{
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Promise(Action<Action<T>, Action<object?>> executor)
    {
        try { executor(v => _tcs.TrySetResult(v), e => _tcs.TrySetException(JS.toException(e))); }
        catch (Exception ex) { _tcs.TrySetException(ex); }
    }

    public Promise(Action<Action<T>> executor) : this((resolve, _) => executor(resolve)) { }

    public Task<T> task => _tcs.Task;
    public TaskAwaiter<T> GetAwaiter() => _tcs.Task.GetAwaiter();
    public static implicit operator Task<T>(Promise<T> p) => p._tcs.Task;
}
