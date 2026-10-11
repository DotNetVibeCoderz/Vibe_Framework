using System.Text.Json;
using System.Xml.Linq;

namespace Rustzor.Cli;

/// <summary>rustzor info [project]: versions, packages and the built site.</summary>
public static class InfoCommand
{
    public static async Task<int> RunAsync(Options o)
    {
        Console.WriteLine($"{Out.Bold("rustzor")}       {Cli.Version}");
        var (_, sdk) = await Proc.CaptureAsync("dotnet", "--version");
        Console.WriteLine($"{Out.Bold(".NET SDK")}      {sdk.Trim()}");

        string project;
        try
        {
            project = Project.Find(o.Positional.FirstOrDefault());
        }
        catch (CliException)
        {
            Console.WriteLine(Out.Dim("No project in this folder."));
            return 0;
        }

        var xml = XDocument.Load(project);
        var sdkAttr = xml.Root?.Attribute("Sdk")?.Value ?? "";
        Console.WriteLine($"{Out.Bold("project")}       {Path.GetFileName(project)}  {Out.Dim(sdkAttr)}");
        var packages = xml.Descendants("PackageReference")
            .Select(p => (Id: p.Attribute("Include")?.Value ?? "", Version: p.Attribute("Version")?.Value ?? ""))
            .Where(p => p.Id.Length > 0).ToList();
        foreach (var (id, version) in packages)
        {
            var note = id.StartsWith("Rustzor.") && version != Cli.Version ? Out.Dim($"  (this tool is {Cli.Version})") : "";
            Console.WriteLine($"  {id} {version}{note}");
        }

        var site = await Project.SiteAsync(project);
        var boot = Path.Combine(site, "_rustzor", "boot.json");
        if (!File.Exists(boot))
        {
            Console.WriteLine(Out.Dim("Not built yet: rustzor serve or dotnet build."));
            return 0;
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(boot));
        var assemblies = doc.RootElement.TryGetProperty("assemblies", out var a) ? a.GetArrayLength() : 0;
        var modules = doc.RootElement.TryGetProperty("modules", out var m) ? m.GetArrayLength() : 0;
        var aot = doc.RootElement.TryGetProperty("aot", out _);
        var files = Directory.GetFiles(site, "*", SearchOption.AllDirectories);
        var total = files.Sum(f => new FileInfo(f).Length);
        var wasm = files.Where(f => f.EndsWith(".wasm")).Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"{Out.Bold("site")}          {site}");
        Console.WriteLine($"  {files.Length} files, {Out.Size(total)} ({Out.Size(wasm)} WebAssembly), {assemblies} assemblies, {modules} browser modules{(aot ? ", AOT" : "")}");
        return 0;
    }
}
