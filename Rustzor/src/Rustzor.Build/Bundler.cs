using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Rustzor.Build;

/// <summary>
/// Assembles the static site: runtime, loader, the app's assemblies in load
/// order, the browser modules libraries embed, and a boot manifest naming
/// every file by content hash so it can be cached forever.
/// </summary>
public static class Bundler
{
    public static int Run(string assemblyDir, string entry, string runtimeDir, string webRoot, string? aotDir)
    {
        var outDir = Path.Combine(webRoot, "_rustzor");
        var modulesDir = Path.Combine(outDir, "modules");
        Directory.CreateDirectory(outDir);
        if (Directory.Exists(modulesDir)) Directory.Delete(modulesDir, true);
        Directory.CreateDirectory(modulesDir);
        foreach (var stale in Directory.GetFiles(outDir, "*.dll")) File.Delete(stale);

        var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var references = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var moduleFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var styleFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.GetFiles(assemblyDir, "*.dll"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var md = pe.GetMetadataReader();
                if (!md.IsAssembly) continue;
                var name = md.GetString(md.GetAssemblyDefinition().Name);
                assemblies[name] = path;
                references[name] = md.AssemblyReferences.Select(r => md.GetString(md.GetAssemblyReference(r).Name)).ToList();
                var module = ReadResource(pe, md, "rustzor.module.js");
                if (module is not null)
                {
                    var target = Path.Combine(modulesDir, name + ".js");
                    File.WriteAllBytes(target, module);
                    moduleFiles[name] = target;
                }
                var style = ReadResource(pe, md, "rustzor.module.css");
                if (style is not null)
                {
                    var target = Path.Combine(modulesDir, name + ".css");
                    File.WriteAllBytes(target, style);
                    styleFiles[name] = target;
                }
            }
            catch (BadImageFormatException)
            {
            }
        }

        if (!assemblies.ContainsKey(entry))
        {
            Console.Error.WriteLine($"error RZ0001: Rustzor: the entry assembly {entry} is not in {assemblyDir}");
            return 1;
        }

        // Libraries first: a TypeRef binds only to a type already registered.
        // Only what the entry assembly reaches is shipped.
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string name)
        {
            if (!seen.Add(name)) return;
            foreach (var r in references[name])
                if (assemblies.ContainsKey(r)) Visit(r);
            ordered.Add(name);
        }
        Visit(entry);

        var files = new List<string>();
        foreach (var name in ordered)
        {
            var target = Path.Combine(outDir, name + ".dll");
            File.Copy(assemblies[name], target, true);
            files.Add($"{name}.dll?v={Hash(target)}");
        }
        var modules = ordered.Where(moduleFiles.ContainsKey).Select(n => $"modules/{n}.js?v={Hash(moduleFiles[n])}").ToList();
        var styles = ordered.Where(styleFiles.ContainsKey).Select(n => $"modules/{n}.css?v={Hash(styleFiles[n])}").ToList();
        foreach (var unused in moduleFiles.Keys.Except(ordered, StringComparer.OrdinalIgnoreCase).ToList())
            File.Delete(moduleFiles[unused]);
        foreach (var unused in styleFiles.Keys.Except(ordered, StringComparer.OrdinalIgnoreCase).ToList())
            File.Delete(styleFiles[unused]);

        foreach (var file in new[] { "rustzor.wasm", "rustzor.js" })
        {
            var source = Path.Combine(runtimeDir, file);
            if (!File.Exists(source))
            {
                Console.Error.WriteLine($"error RZ0002: Rustzor: runtime file missing: {source}. Run scripts/build-runtime.sh.");
                return 1;
            }
            File.Copy(source, Path.Combine(outDir, file), true);
        }

        var json = new StringBuilder();
        json.Append("{\n");
        json.Append($"  \"runtime\": \"rustzor.wasm?v={Hash(Path.Combine(outDir, "rustzor.wasm"))}\",\n");
        json.Append($"  \"entry\": {Quote(entry)},\n");
        json.Append($"  \"assemblies\": [{string.Join(", ", files.Select(Quote))}],\n");
        json.Append($"  \"modules\": [{string.Join(", ", modules.Select(Quote))}],\n");
        json.Append($"  \"styles\": [{string.Join(", ", styles.Select(Quote))}]");
        var aotModule = aotDir is null ? null : Path.Combine(aotDir, "app.aot.wasm");
        var aotManifest = aotDir is null ? null : Path.Combine(aotDir, "app.aot.txt");
        if (aotModule is not null && File.Exists(aotModule) && File.Exists(aotManifest))
        {
            File.Copy(aotModule, Path.Combine(outDir, "app.aot.wasm"), true);
            File.Copy(aotManifest!, Path.Combine(outDir, "app.aot.txt"), true);
            json.Append($",\n  \"aot\": {{ \"module\": \"app.aot.wasm?v={Hash(aotModule)}\", \"manifest\": \"app.aot.txt?v={Hash(aotManifest!)}\" }}");
        }
        else
        {
            File.Delete(Path.Combine(outDir, "app.aot.wasm"));
            File.Delete(Path.Combine(outDir, "app.aot.txt"));
        }
        json.Append(",\n  \"builtBy\": \"Rustzor - Gravicode Studios, led by Kang Fadhil\"\n}\n");
        File.WriteAllText(Path.Combine(outDir, "boot.json"), json.ToString());

        long bytes = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"Rustzor: {ordered.Count} assemblies, {modules.Count} browser modules, {bytes / 1024} KB -> {webRoot}");
        return 0;
    }

    /// <summary>An embedded resource by name (<c>rustzor.module.js</c>, <c>rustzor.module.css</c>).</summary>
    private static byte[]? ReadResource(PEReader pe, MetadataReader md, string wanted)
    {
        foreach (var handle in md.ManifestResources)
        {
            var resource = md.GetManifestResource(handle);
            if (md.GetString(resource.Name) != wanted || !resource.Implementation.IsNil) continue;
            var directory = pe.PEHeaders.CorHeader!.ResourcesDirectory;
            var block = pe.GetSectionData(directory.RelativeVirtualAddress);
            var reader = block.GetReader((int)resource.Offset, block.Length - (int)resource.Offset);
            var length = reader.ReadInt32();
            return reader.ReadBytes(length);
        }
        return null;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..12].ToLowerInvariant();
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
