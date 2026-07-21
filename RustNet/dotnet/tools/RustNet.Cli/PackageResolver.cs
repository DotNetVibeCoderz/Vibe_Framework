namespace RustNet.Cli;

/// <summary>A `major.minor.patch` semantic version (pre-release/build ignored).</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public static bool TryParse(string? s, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }
        // Drop any pre-release/build suffix (`-rc1`, `+meta`).
        int cut = s.IndexOfAny(['-', '+']);
        string core = cut >= 0 ? s[..cut] : s;
        string[] parts = core.Split('.');
        int Part(int i) => i < parts.Length && int.TryParse(parts[i], out int n) ? n : 0;
        if (!int.TryParse(parts[0], out _))
        {
            return false;
        }
        version = new SemVer(Part(0), Part(1), Part(2));
        return true;
    }

    public static SemVer Parse(string s) =>
        TryParse(s, out var v) ? v : throw new FormatException($"invalid version '{s}'");

    public int CompareTo(SemVer other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        return Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>One package version available in a registry.</summary>
public sealed record PackageId(
    string Name,
    SemVer Version,
    IReadOnlyDictionary<string, string> Dependencies);

/// <summary>
/// Resolves a package plus its transitive dependencies against the set of
/// versions available in a registry. Dependency version strings are treated as
/// <em>minimums</em> (the highest available version that is at least the
/// requested one is chosen); the root may be pinned to an exact version. The
/// result is ordered dependencies-first, so installing in order satisfies each
/// package's requirements.
/// </summary>
public sealed class PackageResolver
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private readonly List<PackageId> _available;

    public PackageResolver(IEnumerable<PackageId> available) => _available = available.ToList();

    public List<PackageId> Resolve(string root, string? exactVersion = null)
    {
        var required = new Dictionary<string, SemVer>(Ci);          // name -> accumulated minimum
        var chosen = new Dictionary<string, PackageId>(Ci);
        SemVer? pinned = exactVersion is not null && SemVer.TryParse(exactVersion, out var pv) ? pv : null;

        var queue = new Queue<string>();
        required[root] = pinned ?? default;
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            string name = queue.Dequeue();
            bool isRootPinned = pinned is { } && Ci.Equals(name, root);
            PackageId pkg = isRootPinned
                ? SelectExact(name, pinned!.Value)
                : SelectAtLeast(name, required[name]);

            if (chosen.TryGetValue(name, out var already)
                && already.Version.CompareTo(pkg.Version) == 0)
            {
                continue; // stable; no change to propagate
            }
            chosen[name] = pkg;

            foreach (var (dep, verSpec) in pkg.Dependencies)
            {
                SemVer.TryParse(verSpec, out var depMin);
                if (!required.TryGetValue(dep, out var cur) || depMin.CompareTo(cur) > 0)
                {
                    required[dep] = depMin;
                    queue.Enqueue(dep);
                }
                else if (!chosen.ContainsKey(dep))
                {
                    queue.Enqueue(dep);
                }
            }
        }

        return TopoSort(chosen);
    }

    private PackageId SelectAtLeast(string name, SemVer min)
    {
        PackageId? best = _available
            .Where(p => Ci.Equals(p.Name, name) && p.Version.CompareTo(min) >= 0)
            .OrderByDescending(p => p.Version)
            .FirstOrDefault();
        return best ?? throw new InvalidOperationException(
            $"no version of '{name}' >= {min} is available in the registry");
    }

    private PackageId SelectExact(string name, SemVer exact)
    {
        PackageId? match = _available.FirstOrDefault(p =>
            Ci.Equals(p.Name, name) && p.Version.CompareTo(exact) == 0);
        return match ?? throw new InvalidOperationException(
            $"version {exact} of '{name}' is not available in the registry");
    }

    /// <summary>Order the chosen packages so every package appears after all of
    /// its (resolved) dependencies. Cycles are broken deterministically.</summary>
    private static List<PackageId> TopoSort(Dictionary<string, PackageId> chosen)
    {
        var order = new List<PackageId>();
        var visited = new HashSet<string>(Ci);
        var onStack = new HashSet<string>(Ci);

        void Visit(string name)
        {
            if (!chosen.TryGetValue(name, out var pkg) || !visited.Add(name))
            {
                return;
            }
            onStack.Add(name);
            foreach (string dep in pkg.Dependencies.Keys)
            {
                if (chosen.ContainsKey(dep) && !onStack.Contains(dep))
                {
                    Visit(dep);
                }
            }
            onStack.Remove(name);
            order.Add(pkg);
        }

        foreach (string name in chosen.Keys.OrderBy(n => n, Ci))
        {
            Visit(name);
        }
        return order;
    }
}
