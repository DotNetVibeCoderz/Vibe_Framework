using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace JSNet.Runtime;

/// <summary>
/// JavaScript string API on <see cref="string"/> via C# 14 extension members (zero-cost static calls).
/// </summary>
public static class JsStringExtensions
{
    extension(string s)
    {
        public int length => s.Length;
        public string toUpperCase() => s.ToUpperInvariant();
        public string toLowerCase() => s.ToLowerInvariant();
        public bool includes(string value) => s.Contains(value, StringComparison.Ordinal);
        public bool startsWith(string value) => s.StartsWith(value, StringComparison.Ordinal);
        public bool endsWith(string value) => s.EndsWith(value, StringComparison.Ordinal);
        public int indexOf(string value) => s.IndexOf(value, StringComparison.Ordinal);
        public int lastIndexOf(string value) => s.LastIndexOf(value, StringComparison.Ordinal);
        public string trim() => s.Trim();
        public string trimStart() => s.TrimStart();
        public string trimEnd() => s.TrimEnd();
        public string padStart(int length, string fill = " ") => Pad(s, length, fill, start: true);
        public string padEnd(int length, string fill = " ") => Pad(s, length, fill, start: false);
        public string repeat(int count) => string.Concat(Enumerable.Repeat(s, Math.Max(0, count)));
        public string charAt(int index) => index >= 0 && index < s.Length ? s[index].ToString() : string.Empty;
        public int charCodeAt(int index) => index >= 0 && index < s.Length ? s[index] : -1;
        public string at(int index) => s.charAt(index < 0 ? s.Length + index : index);
        public string concat(params string[] parts) => s + string.Concat(parts);
        public int localeCompare(string other) => string.Compare(s, other, StringComparison.CurrentCulture);
        public string replace(string search, string replacement) => ReplaceFirst(s, search, replacement);
        public string replace(Regex pattern, string replacement) => pattern.Replace(s, replacement);
        public string replaceAll(string search, string replacement) => s.Replace(search, replacement, StringComparison.Ordinal);
        public List<string> split(string separator = "", int limit = int.MaxValue)
        {
            var parts = separator.Length == 0 ? s.Select(c => c.ToString()).ToList() : [.. s.Split(separator)];
            return limit < parts.Count ? parts.GetRange(0, limit) : parts;
        }
        public string slice(int start, int? end = null)
        {
            int len = s.Length;
            int from = start < 0 ? Math.Max(0, len + start) : Math.Min(start, len);
            int to = end is null ? len : end < 0 ? Math.Max(0, len + end.Value) : Math.Min(end.Value, len);
            return to > from ? s[from..to] : string.Empty;
        }
        public string substring(int start, int? end = null)
        {
            int a = Math.Clamp(start, 0, s.Length), b = Math.Clamp(end ?? s.Length, 0, s.Length);
            return a <= b ? s[a..b] : s[b..a];
        }
        public List<string>? match(Regex pattern)
        {
            var m = pattern.Matches(s);
            return m.Count == 0 ? null : m.Select(x => x.Value).ToList();
        }
        public string toString() => s;
    }

    private static string Pad(string s, int length, string fill, bool start)
    {
        if (s.Length >= length || fill.Length == 0) return s;
        var pad = new StringBuilder();
        while (pad.Length < length - s.Length) pad.Append(fill);
        string p = pad.ToString(0, length - s.Length);
        return start ? p + s : s + p;
    }

    private static string ReplaceFirst(string s, string search, string replacement)
    {
        int i = s.IndexOf(search, StringComparison.Ordinal);
        return i < 0 ? s : string.Concat(s.AsSpan(0, i), replacement, s.AsSpan(i + search.Length));
    }
}

/// <summary>
/// JavaScript array methods and lower-case LINQ on every sequence. <c>map</c>/<c>filter</c> are eager
/// (JavaScript semantics, return a new list); <c>where</c>/<c>select</c> are lazy (LINQ semantics).
/// </summary>
public static class JsEnumerableExtensions
{
    extension<T>(IEnumerable<T> source)
    {
        // JavaScript array API (eager)
        public List<TResult> map<TResult>(Func<T, TResult> selector) => source.Select(selector).ToList();
        public List<TResult> map<TResult>(Func<T, int, TResult> selector) => source.Select(selector).ToList();
        public List<T> filter(Func<T, bool> predicate) => source.Where(predicate).ToList();
        public List<T> filter(Func<T, int, bool> predicate) => source.Where(predicate).ToList();
        public TAcc reduce<TAcc>(Func<TAcc, T, TAcc> reducer, TAcc initial) => source.Aggregate(initial, reducer);
        public T reduce(Func<T, T, T> reducer) => source.Aggregate(reducer);
        /// <summary>Chosen when an integer seed meets fractional accumulation (<c>reduce((s, x) =&gt; s + x.price, 0)</c>).</summary>
        public double reduce(Func<double, T, double> reducer, double initial) => source.Aggregate(initial, reducer);
        public void forEach(Action<T> action) { foreach (var x in source) action(x); }
        public void forEach(Action<T, int> action) { int i = 0; foreach (var x in source) action(x, i++); }
        public T? find(Func<T, bool> predicate) => source.FirstOrDefault(predicate);
        public int findIndex(Func<T, bool> predicate)
        {
            int i = 0;
            foreach (var x in source) { if (predicate(x)) return i; i++; }
            return -1;
        }
        public bool some(Func<T, bool> predicate) => source.Any(predicate);
        public bool every(Func<T, bool> predicate) => source.All(predicate);
        public bool includes(T value) => source.Contains(value);
        public int indexOf(T value)
        {
            int i = 0;
            var cmp = EqualityComparer<T>.Default;
            foreach (var x in source) { if (cmp.Equals(x, value)) return i; i++; }
            return -1;
        }
        public string join(string separator = ",") => string.Join(separator, source.Select(x => JS.str(x)));
        public List<TResult> flatMap<TResult>(Func<T, IEnumerable<TResult>> selector) => source.SelectMany(selector).ToList();
        public List<T> toSorted(Comparison<T>? compare = null)
        {
            var list = source.ToList();
            if (compare is null) list.Sort(); else list.Sort(compare);
            return list;
        }
        public List<T> toReversed() => source.Reverse().ToList();
        public List<T> concat(IEnumerable<T> other) => source.Concat(other).ToList();

        // Lower-case LINQ (lazy)
        public IEnumerable<T> where(Func<T, bool> predicate) => source.Where(predicate);
        public IEnumerable<TResult> select<TResult>(Func<T, TResult> selector) => source.Select(selector);
        public IEnumerable<TResult> selectMany<TResult>(Func<T, IEnumerable<TResult>> selector) => source.SelectMany(selector);
        public IOrderedEnumerable<T> orderBy<TKey>(Func<T, TKey> key) => source.OrderBy(key);
        public IOrderedEnumerable<T> orderByDescending<TKey>(Func<T, TKey> key) => source.OrderByDescending(key);
        public IEnumerable<IGrouping<TKey, T>> groupBy<TKey>(Func<T, TKey> key) => source.GroupBy(key);
        public IEnumerable<T> distinct() => source.Distinct();
        public IEnumerable<T> take(int count) => source.Take(count);
        public IEnumerable<T> skip(int count) => source.Skip(count);
        public IEnumerable<T[]> chunk(int size) => source.Chunk(size);
        public IEnumerable<(T, TOther)> zip<TOther>(IEnumerable<TOther> other) => source.Zip(other);
        public T first() => source.First();
        public T first(Func<T, bool> predicate) => source.First(predicate);
        public T? firstOrDefault() => source.FirstOrDefault();
        public T? firstOrDefault(Func<T, bool> predicate) => source.FirstOrDefault(predicate);
        public T last() => source.Last();
        public int count() => source.Count();
        public int count(Func<T, bool> predicate) => source.Count(predicate);
        public bool any() => source.Any();
        public bool any(Func<T, bool> predicate) => source.Any(predicate);
        public bool all(Func<T, bool> predicate) => source.All(predicate);
        public double sum(Func<T, double> selector) => source.Sum(selector);
        public double average(Func<T, double> selector) => source.Average(selector);
        public TKey? max<TKey>(Func<T, TKey> selector) => source.Max(selector);
        public TKey? min<TKey>(Func<T, TKey> selector) => source.Min(selector);
        public List<T> toList() => source.ToList();
        public T[] toArray() => source.ToArray();
        public HashSet<T> toSet() => source.ToHashSet();
        public Dictionary<TKey, T> toDictionary<TKey>(Func<T, TKey> key) where TKey : notnull => source.ToDictionary(key);
    }

    extension<T>(IEnumerable<T> source) where T : INumber<T>
    {
        public T sum() => source.Aggregate(T.Zero, (a, b) => a + b);
        public double average() => source.Select(x => double.CreateChecked(x)).Average();
        public T max() => source.Max()!;
        public T min() => source.Min()!;
    }

    extension<T>(T[] array)
    {
        public int length => array.Length;
        public T at(int index) => array[index < 0 ? array.Length + index : index];
        public List<T> slice(int start = 0, int? end = null) => Slice(array, start, end);
        public T[] sort(Comparison<T>? compare = null)
        {
            if (compare is null) Array.Sort(array); else Array.Sort(array, compare);
            return array;
        }
        public T[] reverse()
        {
            Array.Reverse(array);
            return array;
        }
        public T[] fill(T value)
        {
            Array.Fill(array, value);
            return array;
        }
    }

    extension<T>(List<T> list)
    {
        public int length => list.Count;
        public int push(params T[] items)
        {
            list.AddRange(items);
            return list.Count;
        }
        public T? pop()
        {
            if (list.Count == 0) return default;
            var last = list[^1];
            list.RemoveAt(list.Count - 1);
            return last;
        }
        public T? shift()
        {
            if (list.Count == 0) return default;
            var first = list[0];
            list.RemoveAt(0);
            return first;
        }
        public int unshift(params T[] items)
        {
            list.InsertRange(0, items);
            return list.Count;
        }
        public List<T> splice(int start, int deleteCount = int.MaxValue, params T[] items)
        {
            if (start < 0) start = Math.Max(0, list.Count + start);
            start = Math.Min(start, list.Count);
            deleteCount = Math.Clamp(deleteCount, 0, list.Count - start);
            var removed = list.GetRange(start, deleteCount);
            list.RemoveRange(start, deleteCount);
            list.InsertRange(start, items);
            return removed;
        }
        public T at(int index) => list[index < 0 ? list.Count + index : index];
        public List<T> slice(int start = 0, int? end = null) => Slice(list, start, end);
        /// <summary>Sorts in place (JavaScript semantics) and returns the same list.</summary>
        public List<T> sort(Comparison<T>? compare = null)
        {
            if (compare is null) list.Sort(); else list.Sort(compare);
            return list;
        }
        public List<T> sort(Func<T, T, double> compare)
        {
            list.Sort((a, b) => Math.Sign(compare(a, b)));
            return list;
        }
        public List<T> reverse()
        {
            list.Reverse();
            return list;
        }
        public void clear() => list.Clear();
    }

    private static List<T> Slice<T>(IReadOnlyList<T> source, int start, int? end)
    {
        int len = source.Count;
        int from = start < 0 ? Math.Max(0, len + start) : Math.Min(start, len);
        int to = end is null ? len : end < 0 ? Math.Max(0, len + end.Value) : Math.Min(end.Value, len);
        var result = new List<T>(Math.Max(0, to - from));
        for (int i = from; i < to; i++) result.Add(source[i]);
        return result;
    }

    /// <summary>Enables <c>const [a, b] = list</c> and <c>for (const [k, v] of pairs)</c>.</summary>
    public static void Deconstruct<T>(this IList<T> list, out T first, out T second)
    {
        first = list.Count > 0 ? list[0] : default!;
        second = list.Count > 1 ? list[1] : default!;
    }

    public static void Deconstruct<T>(this IList<T> list, out T first, out T second, out T third)
    {
        first = list.Count > 0 ? list[0] : default!;
        second = list.Count > 1 ? list[1] : default!;
        third = list.Count > 2 ? list[2] : default!;
    }
}

/// <summary>JavaScript <c>Map</c>/<c>Set</c> API on <see cref="Dictionary{TKey,TValue}"/> and <see cref="HashSet{T}"/>.</summary>
public static class JsCollectionExtensions
{
    extension<TKey, TValue>(Dictionary<TKey, TValue> map) where TKey : notnull
    {
        public int size => map.Count;
        public TValue? get(TKey key) => map.GetValueOrDefault(key);
        public Dictionary<TKey, TValue> set(TKey key, TValue value)
        {
            map[key] = value;
            return map;
        }
        public bool has(TKey key) => map.ContainsKey(key);
        public bool delete(TKey key) => map.Remove(key);
        public void clear() => map.Clear();
        public List<TKey> keys() => [.. map.Keys];
        public List<TValue> values() => [.. map.Values];
        public List<(TKey, TValue)> entries() => map.Select(kv => (kv.Key, kv.Value)).ToList();
        public void forEach(Action<TValue, TKey> action)
        {
            foreach (var (k, v) in map) action(v, k);
        }
    }

    extension<T>(HashSet<T> set)
    {
        public int size => set.Count;
        public HashSet<T> add(T value)
        {
            set.Add(value);
            return set;
        }
        public bool has(T value) => set.Contains(value);
        public bool delete(T value) => set.Remove(value);
        public void clear() => set.Clear();
    }
}

/// <summary>Number formatting (<c>toFixed</c>, radix <c>toString</c>) and error properties.</summary>
public static class JsValueExtensions
{
    extension(double value)
    {
        public string toFixed(int digits = 0) => value.ToString("F" + digits, CultureInfo.InvariantCulture);
        public string toPrecision(int digits) => value.ToString("G" + digits, CultureInfo.InvariantCulture);
        public string toString() => JS.FormatNumber(value);
        public string toLocaleString(string? locale = null) => value.ToString("N", locale is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(locale));
    }

    extension(int value)
    {
        public string toFixed(int digits = 0) => value.ToString("F" + digits, CultureInfo.InvariantCulture);
        public string toString(int radix = 10) => radix == 10 ? value.ToString(CultureInfo.InvariantCulture) : Convert.ToString(value, radix);
        public string toLocaleString(string? locale = null) => value.ToString("N0", locale is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(locale));
    }

    extension(bool value)
    {
        public string toString() => value ? "true" : "false";
    }

    extension(Exception error)
    {
        public string message => error.Message;
        public string? stack => error.StackTrace;
        public string name => error.GetType().Name;
    }
}
