using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RustNet.Cli;

/// <summary>
/// NuGet-like package manager for RustNet driver/sensor libraries.
/// A package is a zip (.rnpkg) with an rnpkg.json manifest; the registry
/// is a directory (local folder or network share) — see docs/packages.md.
/// </summary>
internal static class PkgCommands
{
    public sealed class Manifest
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "0.1.0";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("authors")] public string[] Authors { get; set; } = [];
        [JsonPropertyName("files")] public string[] Files { get; set; } = [];
        [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string RegistryDir =>
        Environment.GetEnvironmentVariable("RUSTNET_REGISTRY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rustnet", "registry");

    public static int Dispatch(string[] args) => args.ElementAtOrDefault(0) switch
    {
        "init" => Init(args.Skip(1).ToArray()),
        "pack" => Pack(),
        "publish" => Publish(),
        "list" => List(),
        "search" => Search(args.ElementAtOrDefault(1) ?? ""),
        "install" => Install(args.Skip(1).ToArray()),
        _ => Usage(),
    };

    private static int Usage()
    {
        Console.Error.WriteLine("usage: rustnet pkg init <name> | pack | publish | list | search <term> | install <name> [--version v]");
        return 2;
    }

    private static int Init(string[] args)
    {
        string name = args.FirstOrDefault() ?? Path.GetFileName(Directory.GetCurrentDirectory());
        var manifest = new Manifest
        {
            Name = name,
            Description = $"RustNet driver package {name}",
            Files = ["*.cs"],
        };
        File.WriteAllText("rnpkg.json", JsonSerializer.Serialize(manifest, JsonOpts));
        Console.WriteLine($"created rnpkg.json for '{name}'");
        return 0;
    }

    private static Manifest LoadManifest()
    {
        if (!File.Exists("rnpkg.json"))
        {
            throw new InvalidOperationException("no rnpkg.json here — run 'rustnet pkg init' first");
        }
        return JsonSerializer.Deserialize<Manifest>(File.ReadAllText("rnpkg.json"))
            ?? throw new InvalidOperationException("invalid rnpkg.json");
    }

    private static string PackageFileName(Manifest m) => $"{m.Name}-{m.Version}.rnpkg";

    private static int Pack()
    {
        var manifest = LoadManifest();
        string output = PackageFileName(manifest);
        File.Delete(output);
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        zip.CreateEntryFromFile("rnpkg.json", "rnpkg.json");
        int count = 0;
        foreach (string pattern in manifest.Files)
        {
            foreach (string file in Directory.GetFiles(".", pattern, SearchOption.TopDirectoryOnly))
            {
                string entryName = Path.GetFileName(file);
                if (entryName != "rnpkg.json")
                {
                    zip.CreateEntryFromFile(file, entryName);
                    count++;
                }
            }
        }
        Console.WriteLine($"packed {count} file(s) -> {output}");
        return 0;
    }

    private static int Publish()
    {
        var manifest = LoadManifest();
        string package = PackageFileName(manifest);
        if (!File.Exists(package))
        {
            Pack();
        }
        Directory.CreateDirectory(RegistryDir);
        string dest = Path.Combine(RegistryDir, package);
        File.Copy(package, dest, overwrite: true);
        Console.WriteLine($"published {manifest.Name} {manifest.Version} -> {RegistryDir}");
        return 0;
    }

    private static IEnumerable<(Manifest Manifest, string Path)> RegistryPackages()
    {
        if (!Directory.Exists(RegistryDir))
        {
            yield break;
        }
        foreach (string file in Directory.GetFiles(RegistryDir, "*.rnpkg"))
        {
            Manifest? m = null;
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var entry = zip.GetEntry("rnpkg.json");
                if (entry is not null)
                {
                    using var stream = entry.Open();
                    m = JsonSerializer.Deserialize<Manifest>(stream);
                }
            }
            catch (InvalidDataException)
            {
            }
            if (m is not null)
            {
                yield return (m, file);
            }
        }
    }

    private static int List()
    {
        var packages = RegistryPackages().ToList();
        if (packages.Count == 0)
        {
            Console.WriteLine($"registry {RegistryDir} is empty");
            return 0;
        }
        foreach (var (m, _) in packages.OrderBy(p => p.Manifest.Name))
        {
            Console.WriteLine($"{m.Name,-30} {m.Version,-10} {m.Description}");
        }
        return 0;
    }

    private static int Search(string term)
    {
        foreach (var (m, _) in RegistryPackages())
        {
            if (m.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || m.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{m.Name,-30} {m.Version,-10} {m.Description}");
            }
        }
        return 0;
    }

    private static int Install(string[] args)
    {
        string name = args.FirstOrDefault(a => !a.StartsWith('-'))
            ?? throw new ArgumentException("usage: rustnet pkg install <name> [--version v]");
        string? version = Cli.Opt(args, "--version");

        // Build the resolver input from the registry and resolve the transitive
        // dependency closure (highest version satisfying each minimum).
        var available = new List<PackageId>();
        var pathByKey = new Dictionary<string, string>();
        foreach (var (m, path) in RegistryPackages())
        {
            if (!SemVer.TryParse(m.Version, out var v))
            {
                continue;
            }
            available.Add(new PackageId(m.Name, v, m.Dependencies));
            pathByKey[$"{m.Name}@{v}"] = path;
        }

        List<PackageId> plan;
        try
        {
            plan = new PackageResolver(available).Resolve(name, version);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        // Dependencies first, so each package's requirements are already present.
        foreach (var pkg in plan)
        {
            string path = pathByKey[$"{pkg.Name}@{pkg.Version}"];
            string target = Path.Combine("packages", pkg.Name);
            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(path, target, overwriteFiles: true);
            Console.WriteLine($"installed {pkg.Name} {pkg.Version} -> {target}/");
        }
        Console.WriteLine($"resolved {plan.Count} package(s)");
        return 0;
    }
}
