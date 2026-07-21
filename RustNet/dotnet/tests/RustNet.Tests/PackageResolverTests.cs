using RustNet.Cli;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// Unit tests for `rustnet pkg install` dependency resolution: semver ordering,
/// transitive closure, diamond deduplication, minimum-version selection, and
/// dependencies-first install order.
/// </summary>
public class PackageResolverTests
{
    private static PackageId Pkg(string name, string version, params (string, string)[] deps) =>
        new(name, SemVer.Parse(version),
            deps.ToDictionary(d => d.Item1, d => d.Item2));

    [Fact]
    public void SemVerComparesNumericallyNotLexically()
    {
        Assert.True(SemVer.Parse("0.10.0").CompareTo(SemVer.Parse("0.9.0")) > 0);
        Assert.True(SemVer.Parse("1.0.0").CompareTo(SemVer.Parse("1.0.0")) == 0);
        Assert.True(SemVer.Parse("2.0.0").CompareTo(SemVer.Parse("1.9.9")) > 0);
        Assert.True(SemVer.TryParse("1.2.3-rc1", out var v) && v == new SemVer(1, 2, 3));
        Assert.False(SemVer.TryParse("not-a-version", out _));
    }

    [Fact]
    public void PicksHighestVersionByDefault()
    {
        var r = new PackageResolver(new[]
        {
            Pkg("bme280", "0.9.0"),
            Pkg("bme280", "0.10.0"),
            Pkg("bme280", "0.2.0"),
        });
        var plan = r.Resolve("bme280");
        Assert.Single(plan);
        Assert.Equal(new SemVer(0, 10, 0), plan[0].Version);
    }

    [Fact]
    public void ResolvesTransitiveDependenciesDepsFirst()
    {
        var r = new PackageResolver(new[]
        {
            Pkg("display", "1.0.0", ("i2c-bus", "1.0.0"), ("font", "2.0.0")),
            Pkg("i2c-bus", "1.2.0"),
            Pkg("font", "2.1.0"),
        });
        var plan = r.Resolve("display");
        Assert.Equal(3, plan.Count);
        // Both dependencies come before the package that needs them.
        int display = plan.FindIndex(p => p.Name == "display");
        int i2c = plan.FindIndex(p => p.Name == "i2c-bus");
        int font = plan.FindIndex(p => p.Name == "font");
        Assert.True(i2c < display);
        Assert.True(font < display);
        // Highest satisfying versions were chosen.
        Assert.Equal(new SemVer(1, 2, 0), plan[i2c].Version);
        Assert.Equal(new SemVer(2, 1, 0), plan[font].Version);
    }

    [Fact]
    public void DiamondDependencyIsInstalledOnce()
    {
        var r = new PackageResolver(new[]
        {
            Pkg("app", "1.0.0", ("left", "1.0.0"), ("right", "1.0.0")),
            Pkg("left", "1.0.0", ("core", "1.0.0")),
            Pkg("right", "1.0.0", ("core", "1.1.0")),
            Pkg("core", "1.0.0"),
            Pkg("core", "1.1.0"),
            Pkg("core", "1.2.0"),
        });
        var plan = r.Resolve("app");
        // core appears exactly once, at the highest version satisfying both mins.
        var cores = plan.Where(p => p.Name == "core").ToList();
        Assert.Single(cores);
        Assert.Equal(new SemVer(1, 2, 0), cores[0].Version);
        Assert.Equal(4, plan.Count);
    }

    [Fact]
    public void MissingOrUnsatisfiableDependencyThrows()
    {
        var r = new PackageResolver(new[]
        {
            Pkg("a", "1.0.0", ("b", "2.0.0")),
            Pkg("b", "1.0.0"), // only 1.0.0, but a needs >= 2.0.0
        });
        Assert.Throws<InvalidOperationException>(() => r.Resolve("a"));
        Assert.Throws<InvalidOperationException>(() => r.Resolve("missing"));
    }

    [Fact]
    public void ExactVersionPinRespected()
    {
        var r = new PackageResolver(new[]
        {
            Pkg("sensor", "1.0.0"),
            Pkg("sensor", "1.1.0"),
            Pkg("sensor", "1.2.0"),
        });
        var plan = r.Resolve("sensor", "1.1.0");
        Assert.Single(plan);
        Assert.Equal(new SemVer(1, 1, 0), plan[0].Version);
    }
}
