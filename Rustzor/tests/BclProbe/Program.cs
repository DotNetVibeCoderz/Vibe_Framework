// What a typical business web app asks of the base class library. Each line
// prints `label = value`; run it on dotnet and on RustCLR and diff the output.
// Every difference is a RustBCL gap to fix in Vibe_Runtime.
using System.Globalization;
using System.Text;

static class P
{
    static int fails;
    static int index;
    static int only = -1;
    static bool listing;

    // `BclProbe N` runs check N alone, so a missing native, which ends the
    // process rather than throwing, costs one line and not the whole report.
    static void T(string label, Func<object?> f)
    {
        var mine = index++;
        if (listing) return;
        if (only >= 0 && mine != only) return;
        try { Console.WriteLine(label + " = " + Show(f())); }
        catch (Exception e) { fails++; Console.WriteLine(label + " !! " + e.GetType().Name + ": " + e.Message); }
    }

    static string Show(object? o) => o switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        _ => o.ToString() ?? "",
    };

    enum Status { Draft, Active, Closed }

    record Customer(string Name, string City, double Revenue);

    class Pub
    {
        public event EventHandler<string>? Said;
        public event EventHandler? Plain;

        public void Say(string s)
        {
            Said?.Invoke(this, s);
            Plain?.Invoke(this, EventArgs.Empty);
        }
    }

    class Shape { public virtual string Name => "shape"; public string Hidden() => "base"; }
    class Circle : Shape { public override string Name => "circle"; public new string Hidden() => "circle"; }
    interface IGreeter { string Greet(string n); }
    class Greeter : IGreeter { public string Greet(string n) => $"Hi {n}"; }

    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0] == "count") listing = true;
            else only = int.Parse(args[0]);
        }
        Checks();
        if (listing) Console.WriteLine(index);
        else if (only < 0) Console.WriteLine("fails=" + fails);
    }

    static void Checks()
    {
        double d = 1234567.891;
        int i = 42;
        long l = 9876543210L;
        double neg = -3.5;
        DateTime dt = default;
        T("DateTime ctor", () => { dt = new DateTime(2026, 3, 9, 14, 5, 7); return dt.Ticks; });

        // numbers
        T("int.ToString(D5)", () => i.ToString("D5"));
        T("int.ToString(N0)", () => 1234567.ToString("N0"));
        T("int.ToString(X)", () => 255.ToString("X"));
        T("double.ToString()", () => d.ToString());
        T("double.ToString(N2)", () => d.ToString("N2"));
        T("double.ToString(F1)", () => d.ToString("F1"));
        T("double.ToString(F0)", () => neg.ToString("F0"));
        T("double.ToString(0.00)", () => 3.14159.ToString("0.00"));
        T("double.ToString(#,##0)", () => d.ToString("#,##0"));
        T("double.ToString(P1)", () => 0.256.ToString("P1"));
        T("double.ToString(C)", () => 12.5.ToString("C", CultureInfo.InvariantCulture));
        T("double invariant", () => 2.5.ToString(CultureInfo.InvariantCulture));
        T("interp :N2", () => $"{d:N2}|{i,5}|{i,-5}|{0.5:P0}");
        T("long.ToString(N0)", () => l.ToString("N0"));
        T("int.Parse", () => int.Parse("123") + 1);
        T("int.TryParse ok", () => int.TryParse("77", out var v) ? v : -1);
        T("int.TryParse bad", () => int.TryParse("x7", out var v) ? v : -1);
        T("double.TryParse", () => double.TryParse("2.75", NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1);
        T("double.TryParse plain", () => double.TryParse("3.25", out var v) ? v : -1);
        T("double.Parse invariant", () => double.Parse("1.5", CultureInfo.InvariantCulture));
        T("bool.TryParse", () => bool.TryParse("true", out var b) && b);
        T("Math", () => $"{Math.Max(3, 9)} {Math.Min(2.5, 1.5)} {Math.Floor(2.7)} {Math.Ceiling(2.1)} {Math.Pow(2, 10)} {Math.Sqrt(81)} {Math.Clamp(15, 0, 10)} {Math.Round(2.345, 2)} {Math.Abs(-4)}");
        T("Math.Round away", () => Math.Round(2.5, MidpointRounding.AwayFromZero));
        T("Math trig", () => Math.Round(Math.Sin(Math.PI / 2) + Math.Cos(0) + Math.Atan2(1, 1), 4));
        T("Convert", () => Convert.ToInt32("12") + Convert.ToInt32(3.7) + Convert.ToDouble("0.5"));
        T("Convert hex", () => Convert.ToInt32("ff", 16));
        T("int.MaxValue", () => int.MaxValue - 1);
        T("double.IsNaN", () => double.IsNaN(0.0 / 0.0));

        // dates
        T("DateTime parts", () => $"{dt.Year}-{dt.Month}-{dt.Day} {dt.Hour}:{dt.Minute}:{dt.Second} {dt.DayOfWeek}");
        T("DateTime format", () => dt.ToString("yyyy-MM-dd HH:mm"));
        T("DateTime format2", () => dt.ToString("dd MMM yyyy"));
        T("DateTime format3", () => dt.ToString("dddd, MMMM d"));
        T("DateTime interp", () => $"{dt:yyyy/MM/dd}");
        T("DateTime AddDays", () => dt.AddDays(30).ToString("yyyy-MM-dd"));
        T("DateTime AddMonths", () => dt.AddMonths(11).ToString("yyyy-MM-dd"));
        T("DateTime AddHours", () => dt.AddHours(12).AddMinutes(1).ToString("yyyy-MM-dd HH:mm"));
        T("DateTime Date", () => dt.Date.ToString("yyyy-MM-dd HH:mm"));
        T("DateTime diff", () => (dt.AddDays(3) - dt).TotalDays);
        T("DateTime compare", () => dt < dt.AddSeconds(1));
        T("DateTime.Parse", () => DateTime.Parse("2026-01-15").Day);
        T("DateTime.TryParse", () => DateTime.TryParse("2026-02-03", out var p) ? p.Month : -1);
        T("DateTime DayOfYear", () => dt.DayOfYear);
        T("TimeSpan", () => TimeSpan.FromMinutes(90).TotalHours + TimeSpan.FromSeconds(30).TotalSeconds);
        T("TimeSpan fmt", () => TimeSpan.FromSeconds(3725).ToString(@"hh\:mm\:ss"));
        T("TimeSpan parts", () => $"{TimeSpan.FromSeconds(3725).Hours}h{TimeSpan.FromSeconds(3725).Minutes}m");
        T("DaysInMonth", () => DateTime.DaysInMonth(2024, 2));

        // strings
        string s = "  Hello, Rustzor World  ";
        T("Trim/Upper", () => s.Trim().ToUpperInvariant());
        T("IndexOf(char,int)", () => "a,b,c".IndexOf(',', 2));
        T("IndexOf(string,int)", () => "abcabc".IndexOf("bc", 2));
        T("IndexOfAny", () => "path?q#f".IndexOfAny(new[] { '?', '#' }));
        T("IndexOf cmp", () => "Hello".IndexOf("LL", StringComparison.OrdinalIgnoreCase));
        T("Contains(char)", () => "abc".Contains('b'));
        T("Contains cmp", () => "Hello".Contains("ELL", StringComparison.OrdinalIgnoreCase));
        T("StartsWith cmp", () => "Hello".StartsWith("he", StringComparison.OrdinalIgnoreCase));
        T("StartsWith char", () => "Hello".StartsWith('H'));
        T("EndsWith cmp", () => "Hello".EndsWith("LO", StringComparison.OrdinalIgnoreCase));
        T("Equals cmp", () => string.Equals("ABC", "abc", StringComparison.OrdinalIgnoreCase));
        T("Equals inst cmp", () => "ABC".Equals("abc", StringComparison.OrdinalIgnoreCase));
        T("PadLeft(char)", () => "7".PadLeft(3, '0'));
        T("PadRight(char)", () => "7".PadRight(3, '.'));
        T("Replace", () => "a-b-c".Replace("-", "+"));
        T("Split opts", () => string.Join("|", "a,,b, c".Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        T("Split string", () => string.Join("|", "a::b::c".Split("::")));
        T("Join IEnumerable", () => string.Join(", ", new List<int> { 1, 2, 3 }));
        T("Join char", () => string.Join('-', new[] { "x", "y" }));
        T("Substring", () => "Rustzor".Substring(1, 3));
        T("char ops", () => $"{char.IsLower('a')}{char.IsUpper('A')}{char.IsLetterOrDigit('_')}{char.IsPunctuation('!')}");
        T("string.Format", () => string.Format("{0} has {1:N1} pts", "Ana", 12.345));
        T("ToCharArray reverse", () => new string("abc".ToCharArray().Reverse().ToArray()));
        T("new string(c,n)", () => new string('*', 4));
        T("Concat many", () => string.Concat("a", "b", "c", "d", "e"));
        T("Insert/Remove", () => "abcdef".Insert(2, "XY").Remove(0, 1));
        T("LastIndexOf char", () => "a/b/c".LastIndexOf('/'));
        T("TrimEnd chars", () => "x...".TrimEnd('.'));
        T("Trim chars", () => "--x--".Trim('-'));
        T("compare", () => string.Compare("a", "B", StringComparison.OrdinalIgnoreCase));
        T("string indexer/range", () => "Rustzor"[0] + "Rustzor"[^1].ToString() + "Rustzor"[1..3]);
        T("string.IsNullOrEmpty", () => string.IsNullOrEmpty(""));
        T("ToLowerInvariant", () => "ABC".ToLowerInvariant());
        T("StringBuilder", () => new StringBuilder().Append("ab").Append(3L).Append(1.5).Append('c').Append("hello", 1, 3).AppendFormat("{0}-{1}", 1, 2).Insert(0, ">").ToString());
        T("SB Remove/Replace", () => new StringBuilder("hello world").Replace("world", "there").Remove(0, 1).ToString());
        T("SB Append(bool)", () => new StringBuilder().Append(true).Append((object?)null).Append(' ', 2).Length);

        // enums
        T("Enum ToString", () => Status.Active.ToString());
        T("Enum interp", () => $"{Status.Closed}");
        T("Enum Parse", () => Enum.Parse<Status>("Closed"));
        T("Enum Parse type", () => Enum.Parse(typeof(Status), "Active"));
        T("Enum.GetValues", () => string.Join(",", Enum.GetValues<Status>()));
        T("Enum TryParse", () => Enum.TryParse<Status>("Draft", out var st) ? st.ToString() : "no");
        T("Enum cast", () => (int)Status.Closed);

        // collections
        var nums = new List<int> { 5, 3, 8, 1, 9, 2 };
        T("List Sort cmp", () => { var c = new List<int>(nums); c.Sort((a, b) => b.CompareTo(a)); return string.Join(",", c); });
        T("List Sort", () => { var c = new List<int>(nums); c.Sort(); return string.Join(",", c); });
        T("List Find/Index", () => $"{nums.Find(x => x > 6)} {nums.FindIndex(x => x == 8)} {nums.IndexOf(1)} {nums.Contains(9)} {nums.Exists(x => x < 0)}");
        T("List FindAll", () => nums.FindAll(x => x > 4).Count);
        T("List RemoveAll", () => { var c = new List<int>(nums); c.RemoveAll(x => x % 2 == 0); return string.Join(",", c); });
        T("List Insert/RemoveAt", () => { var c = new List<int>(nums); c.Insert(0, 100); c.RemoveAt(2); return string.Join(",", c); });
        T("List AddRange/GetRange", () => { var c = new List<int> { 1 }; c.AddRange(new[] { 2, 3, 4 }); return string.Join(",", c.GetRange(1, 2)); });
        T("List Remove obj", () => { var c = new List<string> { "a", "b" }; c.Remove("a"); return c[0]; });
        T("List ForEach", () => { var t = 0; nums.ForEach(x => t += x); return t; });
        T("List ToArray/Reverse", () => { var c = new List<int>(nums); c.Reverse(); return c.ToArray()[0]; });
        T("Array.Sort/Reverse", () => { var a = new[] { 3, 1, 2 }; Array.Sort(a); Array.Reverse(a); return string.Join(",", a); });
        T("Array.IndexOf/Empty", () => Array.IndexOf(new[] { "a", "b" }, "b") + Array.Empty<int>().Length);
        T("Array.Copy/Resize", () => { var a = new[] { 1, 2, 3 }; var b = new int[5]; Array.Copy(a, b, 3); Array.Resize(ref a, 5); return b[2] + a.Length; });
        T("Array.Fill", () => { var a = new int[3]; Array.Fill(a, 7); return a.Sum(); });
        T("2D array", () => { var g = new int[3, 4]; g[1, 2] = 5; return g[1, 2] + g.GetLength(1); });
        T("Dictionary", () => { var m = new Dictionary<string, int> { ["a"] = 1 }; m["b"] = 2; m.TryAdd("a", 9); m.Remove("x"); return $"{m.Count} {m.ContainsKey("b")} {m.GetValueOrDefault("z", -1)} {string.Join(",", m.Keys)} {m.Values.Sum()}"; });
        T("Dictionary foreach", () => { var m = new Dictionary<int, string> { [1] = "x", [2] = "y" }; var sb = new StringBuilder(); foreach (var kv in m) sb.Append(kv.Key).Append(kv.Value); return sb.ToString(); });
        T("Dictionary deconstruct", () => { var m = new Dictionary<int, string> { [1] = "x" }; var r = ""; foreach (var (k, v) in m) r += k + v; return r; });
        T("Dictionary comparer", () => { var m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Key"] = 1 }; return m.ContainsKey("KEY"); });
        T("HashSet", () => { var h = new HashSet<string> { "a", "b" }; h.Add("a"); h.UnionWith(new[] { "c" }); return h.Count; });
        T("Queue/Stack", () => { var q = new Queue<int>(); q.Enqueue(1); q.Enqueue(2); var st = new Stack<int>(); st.Push(q.Dequeue()); st.Push(5); return st.Peek() + q.Count; });
        T("SortedDictionary", () => { var m = new SortedDictionary<string, int> { ["b"] = 2, ["a"] = 1 }; return string.Join(",", m.Keys); });
        T("LinkedList", () => { var ll = new LinkedList<int>(); ll.AddLast(1); ll.AddFirst(0); return ll.First!.Value + ll.Count; });

        // linq
        var cs = new List<Customer>
        {
            new("Ana", "Jakarta", 1200),
            new("Budi", "Bandung", 800.5),
            new("Citra", "Jakarta", 2300),
            new("Dedi", "Surabaya", 450),
        };
        T("Linq Where/Sum", () => cs.Where(c => c.City == "Jakarta").Sum(c => c.Revenue));
        T("Linq Avg/Max/Min", () => $"{cs.Average(c => c.Revenue):F2} {cs.Max(c => c.Revenue)} {cs.Min(c => c.Revenue)}");
        T("Linq OrderByDesc/ThenBy", () => string.Join(",", cs.OrderByDescending(c => c.City).ThenBy(c => c.Name).Select(c => c.Name)));
        T("Linq GroupBy", () => string.Join(";", cs.GroupBy(c => c.City).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}:{g.Sum(c => c.Revenue)}")));
        T("Linq Skip/Take", () => string.Join(",", nums.Skip(2).Take(3)));
        T("Linq Select index", () => string.Join(",", nums.Select((n, ix) => n * ix)));
        T("Linq Distinct/Any/All", () => $"{new[] { 1, 1, 2 }.Distinct().Count()} {nums.Any(n => n > 8)} {nums.All(n => n > 0)}");
        T("Linq First/Last/Default", () => $"{nums.First()} {nums.Last()} {nums.FirstOrDefault(n => n > 100)} {cs.FirstOrDefault(c => c.Name == "Zed")?.Name ?? "none"}");
        T("Linq ToDictionary", () => cs.ToDictionary(c => c.Name, c => c.Revenue)["Citra"]);
        T("Linq SelectMany/Zip", () => string.Join(",", new[] { new[] { 1, 2 }, new[] { 3 } }.SelectMany(x => x).Zip(new[] { 10, 20, 30 }, (a, b) => a + b)));
        T("Linq Aggregate", () => nums.Aggregate(0, (acc, n) => acc + n));
        T("Linq Range/Repeat", () => Enumerable.Range(1, 5).Sum() + Enumerable.Repeat(2, 3).Sum());
        T("Linq Count pred", () => cs.Count(c => c.Revenue > 1000));
        T("Linq MaxBy/MinBy", () => cs.MaxBy(c => c.Revenue)!.Name + cs.MinBy(c => c.Revenue)!.Name);
        T("Linq Chunk", () => nums.Chunk(4).Count());
        T("Linq ToHashSet/Contains", () => nums.ToHashSet().Contains(3));
        T("Linq Reverse/Concat", () => string.Join(",", nums.Take(2).Reverse().Concat(new[] { 0 })));
        T("Linq DistinctBy", () => cs.DistinctBy(c => c.City).Count());
        T("Linq OrderBy string cmp", () => string.Join(",", new[] { "pear", "Apple", "fig" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        T("Linq OfType/Cast", () => new object[] { 1, "a", 2 }.OfType<int>().Sum());
        T("Linq SequenceEqual", () => new[] { 1, 2 }.SequenceEqual(new List<int> { 1, 2 }));
        T("Linq ElementAt/Single", () => nums.ElementAt(2) + new[] { 4 }.Single());
        T("Linq TakeWhile/SkipWhile", () => nums.TakeWhile(n => n > 2).Count() + nums.SkipWhile(n => n > 2).Count());
        T("Linq Sum double", () => new[] { 0.1, 0.2 }.Sum());
        T("Linq Average int", () => nums.Average());
        T("Linq Max int", () => nums.Max() + nums.Min());
        T("Linq LastOrDefault", () => nums.LastOrDefault(n => n < 3));
        T("Linq Join", () => cs.Join(new[] { "Jakarta" }, c => c.City, c => c, (c, _) => c.Name).Count());
        T("Linq ToLookup", () => cs.ToLookup(c => c.City)["Jakarta"].Count());
        T("record", () => cs[0] with { City = "Bogor" });
        T("record eq", () => new Customer("A", "B", 1) == new Customer("A", "B", 1));

        // language
        T("events", () => { var p = new Pub(); var got = ""; p.Said += (_, m) => got += m; p.Plain += (_, e) => got += "!"; p.Say("hi"); return got; });
        T("event remove", () => { var p = new Pub(); var n = 0; EventHandler<string> h = (_, _) => n++; p.Said += h; p.Said -= h; p.Say("x"); return n; });
        T("multicast", () => { var n = 0; Action a = () => n++; a += () => n += 10; a(); return n; });
        T("EventArgs.Empty", () => EventArgs.Empty != null);
        T("virtual/new", () => { Shape sh = new Circle(); return sh.Name + sh.Hidden(); });
        T("interface", () => ((IGreeter)new Greeter()).Greet("Ana"));
        T("switch expr", () => 7 switch { < 5 => "low", < 10 => "mid", _ => "high" });
        T("tuple", () => { var (a, b) = (1, "x"); return $"{a}{b}"; });
        T("nullable", () => { int? n = null; return (n ?? 5) + (n.HasValue ? 1 : 0); });
        T("nullable double", () => { double? n = 2.5; return n.Value * 2; });
        T("pattern", () => (object)3.5 is double dd && dd > 3);
        T("exception inner", () => { try { throw new InvalidOperationException("outer", new ArgumentException("inner")); } catch (Exception e) { return e.InnerException!.Message; } });
        T("exception typed catch", () => { try { int z = 0; return 1 / z; } catch (DivideByZeroException) { return -1; } });
        T("custom exception", () => { try { throw new MyError("boom"); } catch (MyError e) { return e.Message + e.Code; } });
        T("Guid", () => Guid.NewGuid().ToString().Length);
        T("Random range", () => { var r = new Random(1); var x = r.Next(5, 10); return x >= 5 && x < 10; });
        T("object.Equals", () => Equals("a", "a") && !Equals(null, "a"));
        T("ReferenceEquals", () => ReferenceEquals(cs, cs));
        T("GetHashCode str", () => "abc".GetHashCode() == "abc".GetHashCode());
        T("HashCode.Combine", () => HashCode.Combine(1, "a") == HashCode.Combine(1, "a"));
        T("lambda closure", () => { var acc = 0; Action<int> add = x => acc += x; add(2); add(3); return acc; });
        T("Func compose", () => { Func<int, int> f = x => x * 2; Func<int, int> g = x => f(x) + 1; return g(5); });
        T("local func", () => { int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2); return Fib(15); });
        T("params", () => Sum(1, 2, 3, 4));
        T("generic method inst", () => new Box().Get<int>() + new Box().Name<string>());
        T("static generic class", () => Cache<string>.Count + Cache<int>.Count);
        T("Lazy", () => new Lazy<int>(() => 41).Value + 1);
        T("Uri unescape", () => Uri.UnescapeDataString("a%20b"));
        T("Uri escape", () => Uri.EscapeDataString("a b&c"));
        T("Type name", () => typeof(Customer).Name);
        T("is type", () => (object)new Circle() is Shape);
        T("as type", () => ((object)"x" as string)?.Length);
        T("default interface", () => ((IDefault)new UsesDefault()).Hello());
        T("struct", () => { var p = new Point2(3, 4); p.X += 1; return p.Length(); });
        T("properties init", () => new WithInit { A = 3 }.A);
        T("indexer", () => new Bag()[2]);
        T("operator overload", () => (new Money(5) + new Money(7)).Amount);
        T("iterator", () => string.Join(",", Count3()));
        T("async", () => RunAsync().Result);
        T("Task.WhenAll", () => Task.WhenAll(Task.FromResult(1), Task.FromResult(2)).Result.Sum());
        T("TaskCompletionSource", () => { var tcs = new TaskCompletionSource<int>(); tcs.SetResult(9); return tcs.Task.Result; });
        T("checked overflow", () => { try { int big = int.MaxValue; return checked(big + 1); } catch (OverflowException) { return -1; } });
        T("nameof", () => nameof(Customer.Revenue));
        T("string interpolation align", () => $"[{"ab",-4}][{3.14159,8:F2}]");
        T("char arithmetic", () => (char)('a' + 2));
        T("bitwise", () => (0b1010 & 0b0110) | (1 << 4) ^ 3);
        T("long math", () => l / 3 % 1000);
        T("double format R", () => (0.1 + 0.2).ToString("R", CultureInfo.InvariantCulture));
    }

    static int Sum(params int[] xs) => xs.Sum();

    static IEnumerable<int> Count3()
    {
        yield return 1;
        yield return 2;
        yield return 3;
    }

    static async Task<int> RunAsync()
    {
        await Task.Yield();
        var a = await Task.FromResult(20);
        return a + 1;
    }

    class Box
    {
        public int Get<TT>() => typeof(TT) == typeof(int) ? 1 : 0;
        public string Name<TT>() => typeof(TT).Name;
    }

    static class Cache<TT> { public static int Count = typeof(TT).Name.Length; }

    class MyError : Exception
    {
        public MyError(string m) : base(m) { }
        public int Code => 7;
    }

    interface IDefault { string Hello() => "default"; }
    class UsesDefault : IDefault { }

    struct Point2
    {
        public double X, Y;
        public Point2(double x, double y) { X = x; Y = y; }
        public double Length() => Math.Sqrt(X * X + Y * Y);
    }

    class WithInit { public int A { get; init; } }

    class Bag { public int this[int i] => i * 10; }

    readonly record struct Money(double Amount)
    {
        public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);
    }
}
