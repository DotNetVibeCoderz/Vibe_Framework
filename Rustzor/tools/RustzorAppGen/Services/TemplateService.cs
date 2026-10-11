using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RustzorAppGen.Services;

public sealed record NavEntry(string Title, string Href, string Icon);

/// <summary>A project template: a folder under Templates/ with template.json and its own files.</summary>
public sealed class ProjectTemplate
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "sparkles";
    public List<string> Packages { get; init; } = new();
    public List<NavEntry> Nav { get; init; } = new();
    /// <summary>Uses Rustzor.UI's AppShell layout; false for full-screen apps (games, simulators).</summary>
    public bool Shell { get; init; } = true;
    public string Folder { get; set; } = "";

    public string CategoryGlyph => Category switch
    {
        "Game" => "🎮", "Science" => "🔬", "Work" => "💼", "Industry" => "🏭", "Education" => "🎓",
        "Simulator" => "🧪", "Tools" => "🧰", "AI" => "✨", "Data" => "📊", _ => "📄",
    };
}

/// <summary>
/// Creates Rustzor projects: the shared scaffold (.csproj, Program.cs, router,
/// layout, index.html, README) plus the template's own pages.
/// </summary>
public sealed partial class TemplateService(AppSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string TemplatesFolder => Path.Combine(AppContext.BaseDirectory, "Templates");

    public List<ProjectTemplate> All()
    {
        var list = new List<ProjectTemplate>();
        if (!Directory.Exists(TemplatesFolder)) return list;
        foreach (var dir in Directory.EnumerateDirectories(TemplatesFolder))
        {
            var manifest = Path.Combine(dir, "template.json");
            if (!File.Exists(manifest)) continue;
            var t = JsonSerializer.Deserialize<ProjectTemplate>(File.ReadAllText(manifest), JsonOptions);
            if (t is null) continue;
            t.Folder = dir;
            list.Add(t);
        }
        return list.OrderBy(t => t.Id == "blank" ? 0 : 1).ThenBy(t => t.Category).ThenBy(t => t.Name).ToList();
    }

    public ProjectTemplate? Find(string id) => All().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A safe C# identifier from a project name: "My Shop 2" -> "MyShop2".</summary>
    public static string ToIdentifier(string name)
    {
        var id = NonIdentifierRegex().Replace(name, "");
        if (id.Length == 0) id = "RustzorApp";
        if (char.IsDigit(id[0])) id = "App" + id;
        return id;
    }

    /// <summary>
    /// The project's root namespace. It must not equal a type the project
    /// declares at its root (App, MainLayout, a template's Board or Home), or
    /// <c>@using</c> it in _Imports.razor names the type instead of the namespace.
    /// </summary>
    public static string ProjectNamespace(string projectName, ProjectTemplate template)
    {
        var ns = ToIdentifier(projectName);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "App", "MainLayout", "Program", "Pages", "System", "Microsoft", "Rustzor" };
        var files = Path.Combine(template.Folder, "files");
        if (Directory.Exists(files))
            foreach (var f in Directory.EnumerateFiles(files, "*.*", SearchOption.AllDirectories))
                if (f.EndsWith(".cs") || f.EndsWith(".razor")) taken.Add(Path.GetFileNameWithoutExtension(f));
        return taken.Contains(ns) ? ns + "Site" : ns;
    }

    /// <summary>Creates the project folder and returns its path.</summary>
    public string Create(ProjectTemplate template, string projectName, string parentFolder)
    {
        var ns = ProjectNamespace(projectName, template);
        var folder = Path.Combine(parentFolder, ns);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException($"The folder {folder} already exists and is not empty. Choose another name.");
        Directory.CreateDirectory(folder);

        var packages = new List<string> { "Rustzor.UI" };
        foreach (var p in template.Packages)
            if (!packages.Contains(p)) packages.Add(p);

        string Fill(string text) => text
            .Replace("{{ProjectName}}", projectName)
            .Replace("{{Namespace}}", ns)
            .Replace("{{Year}}", DateTime.Now.Year.ToString());

        Write(folder, $"{ns}.csproj", ProjectFile(packages));
        Write(folder, "Program.cs", $$"""
            using Rustzor.Components;

            var builder = RustzorHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<{{ns}}.App>("#app");
            await builder.Build().RunAsync();

            """);
        Write(folder, "_Imports.razor", Imports(ns, packages));
        Write(folder, "App.razor", """
            <Router AppAssembly="typeof(App).Assembly">
                <Found Context="routeData">
                    <RouteView RouteData="routeData" DefaultLayout="typeof(MainLayout)" />
                </Found>
                <NotFound>
                    <EmptyState Icon="map" Title="Nothing here" Text="This page does not exist." />
                </NotFound>
            </Router>

            """);
        Write(folder, "MainLayout.razor", template.Shell ? ShellLayout(projectName, template) : PlainLayout(projectName));
        Write(folder, "wwwroot/index.html", IndexHtml(projectName));
        Write(folder, "wwwroot/app.css", AppCss);
        Write(folder, "wwwroot/template.css", "/* Styles specific to this app. */" + Environment.NewLine);
        Write(folder, "README.md", Readme(projectName, template));

        // The template's own files override the scaffold where they overlap.
        var files = Path.Combine(template.Folder, "files");
        if (Directory.Exists(files))
        {
            foreach (var src in Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(files, src);
                var text = File.ReadAllText(src);
                Write(folder, rel, Fill(text));
            }
        }
        settings.RememberProject(folder);
        return folder;
    }

    private string ProjectFile(List<string> packages)
    {
        var sb = new StringBuilder();
        if (settings.RustzorReference == "NuGet")
        {
            sb.AppendLine($"<Project Sdk=\"Rustzor.Sdk/{settings.RustzorVersion}\">");
            sb.AppendLine("  <ItemGroup>");
            foreach (var p in packages) sb.AppendLine($"    <PackageReference Include=\"{p}\" Version=\"{settings.RustzorVersion}\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("</Project>");
            return sb.ToString();
        }
        // Explicit SDK imports so Rustzor's targets come after the SDK's own,
        // where $(OutDir) and the build targets they hook into exist.
        sb.AppendLine("<Project>");
        sb.AppendLine("  <Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk.Razor\" />");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        sb.AppendLine("    <OutputType>Exe</OutputType>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <!-- A local Rustzor checkout. Switch to NuGet in RustzorAppGen settings once the packages are published. -->");
        sb.AppendLine($"    <RustzorRoot>{settings.RustzorRoot}</RustzorRoot>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");
        foreach (var p in packages) sb.AppendLine($"    <ProjectReference Include=\"$(RustzorRoot)\\{SourcePath(p)}\" />");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("  <Import Project=\"Sdk.targets\" Sdk=\"Microsoft.NET.Sdk.Razor\" />");
        sb.AppendLine("  <Import Project=\"$(RustzorRoot)\\eng\\Rustzor.InRepo.targets\" />");
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    public static string SourcePath(string package) => package switch
    {
        _ when package.StartsWith("Rustzor.Browser.") => $@"src\Browser\{package}\{package}.csproj",
        "Rustzor.Game" or "Rustzor.Graphics" or "Rustzor.Data" or "Rustzor.AI" => $@"src\Frameworks\{package}\{package}.csproj",
        _ when package.StartsWith("Rustzor.Charts.") || package is "Rustzor.UI.Bootstrap" or "Rustzor.UI.Tailwind" or "Rustzor.UI.Mui"
            => $@"src\Integrations\{package}\{package}.csproj",
        _ => $@"src\{package}\{package}.csproj",
    };

    private static string Imports(string ns, List<string> packages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@using Microsoft.AspNetCore.Components.Web");
        sb.AppendLine("@using Microsoft.AspNetCore.Components.Routing");
        sb.AppendLine("@using Microsoft.AspNetCore.Components.Forms");
        sb.AppendLine("@using Rustzor.Interop");
        foreach (var p in packages) sb.AppendLine($"@using {p}");
        sb.AppendLine($"@using {ns}");
        return sb.ToString();
    }

    private static string ShellLayout(string projectName, ProjectTemplate template)
    {
        var nav = template.Nav.Count > 0 ? template.Nav : new List<NavEntry> { new("Home", "", "home") };
        var items = string.Join(",\n        ", nav.Select(n => $"NavItem.Link(\"{n.Title}\", \"{n.Href}\", \"{n.Icon}\")"));
        return $$"""
            @inherits LayoutComponentBase
            @* AppShell's sidebar credits Gravicode Studios. *@
            <AppShell Brand="{{projectName}}" BrandIcon="{{template.Icon}}" Items="nav">
                @Body
            </AppShell>

            @code {
                private readonly List<NavItem> nav = new()
                {
                    {{items}}
                };
            }

            """;
    }

    private static string PlainLayout(string projectName) => $$"""
        @inherits LayoutComponentBase
        <div class="app-full">
            @Body
            <footer class="app-footer rz-muted">{{projectName}} · Built by Gravicode Studios, led by Kang Fadhil</footer>
        </div>

        """;

    private static string IndexHtml(string title) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <base href="/" />
          <title>{{System.Net.WebUtility.HtmlEncode(title)}}</title>
          <meta name="description" content="Built with Rustzor by Gravicode Studios, led by Kang Fadhil." />
          <link rel="preconnect" href="https://fonts.googleapis.com" />
          <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wght@12..96,500;12..96,700&family=IBM+Plex+Sans:wght@400;500;600&family=JetBrains+Mono:wght@400;500&display=swap" />
          <link rel="stylesheet" href="app.css" />
          <link rel="stylesheet" href="template.css" />
        </head>
        <body>
          <div id="app"><div class="app-loading">Loading…</div></div>
          <script type="module">
            import { start } from "./_rustzor/rustzor.js";
            start().catch((e) => {
              document.getElementById("app").innerHTML = "<pre class='app-error'></pre>";
              document.querySelector(".app-error").textContent = e.message;
              console.error(e);
            });
          </script>
        </body>
        </html>

        """;

    private const string AppCss = """
        /* App styles. Rustzor.UI supplies the design tokens (--rz-*) and components. */
        .app-loading { display: grid; place-items: center; height: 100vh; font: 500 14px/1 system-ui, sans-serif; color: #8a8178; }
        .app-error { margin: 2rem; padding: 1rem; border: 1px solid #c2410c; border-radius: 8px; white-space: pre-wrap; }
        .app-full { min-height: 100vh; display: flex; flex-direction: column; background: var(--rz-bg); color: var(--rz-fg); }
        .app-footer { margin-top: auto; padding: 12px 20px; font-size: 12px; text-align: center; }
        .app-credit { font-size: 11px; line-height: 1.4; margin: 0; }

        """;

    private static string Readme(string name, ProjectTemplate t) => $"""
        # {name}

        {t.Description}

        Built with [Rustzor](https://github.com/DotNetVibeCoderz/Vibe_Framework/tree/main/Rustzor): C# Razor components running in the browser on RustNetRuntime (Rust + WebAssembly).

        ## Run

        ```
        dotnet build
        ```

        Then serve `bin/Debug/net10.0/wwwroot` with any static server, or press **Run** in Rustzor App Generator.

        ---
        Built by Gravicode Studios, led by Kang Fadhil.

        """;

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
    }

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NonIdentifierRegex();
}

public static class TemplateConverters
{
    public static readonly Avalonia.Data.Converters.IValueConverter Libraries =
        new Avalonia.Data.Converters.FuncValueConverter<List<string>?, string>(p =>
            p is null || p.Count == 0 ? "Rustzor.UI" : string.Join(" · ", p.Select(x => x.Replace("Rustzor.", ""))));
}
