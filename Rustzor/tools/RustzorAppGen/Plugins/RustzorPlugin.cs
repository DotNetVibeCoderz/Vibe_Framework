using System.ComponentModel;
using Microsoft.SemanticKernel;
using RustzorAppGen.Services;

namespace RustzorAppGen.Plugins;

/// <summary>Rustzor knowledge and project creation.</summary>
public sealed class RustzorPlugin(IWorkbench workbench, AppSettings settings)
{
    private readonly TemplateService _templates = new(settings);

    [KernelFunction("get_guide")]
    [Description("Returns Rustzor documentation for one topic: project, components, ui, interop, browser, game, graphics, data, ai, charts, bootstrap, tailwind, mui, pitfalls.")]
    public string GetGuide([Description("The topic")] string topic) => RustzorKnowledge.Guide(topic);

    [KernelFunction("get_library_api")]
    [Description("Returns the public classes, methods, properties and component parameters of one Rustzor library, read from its source, e.g. Rustzor.Game, Rustzor.Browser.Fetch, Rustzor.UI, Rustzor.Data.")]
    public string GetLibraryApi([Description("Library name, e.g. Rustzor.Browser.Canvas")] string library) =>
        RustzorKnowledge.LibraryApi(settings.RustzorRoot, library);

    [KernelFunction("list_templates")]
    [Description("Lists the project templates (id, category, name, description, libraries).")]
    public string ListTemplates() =>
        string.Join("\n", _templates.All().Select(t => $"- {t.Id} [{t.Category}] {t.Name}: {t.Description} (uses {string.Join(", ", t.Packages.DefaultIfEmpty("Rustzor.UI"))})"));

    [KernelFunction("add_package")]
    [Description("Adds a Rustzor library to the open project (the .csproj reference and an @using in _Imports.razor), e.g. Rustzor.Browser.DOM, Rustzor.Data, Rustzor.Charts.ECharts. Use this instead of editing the .csproj by hand.")]
    public string AddPackage([Description("Library name, e.g. Rustzor.Browser.Storage")] string package)
    {
        var root = workbench.ProjectRoot;
        if (root is null) return "No project is open.";
        var project = Directory.EnumerateFiles(root, "*.csproj").FirstOrDefault();
        if (project is null) return "The open folder has no .csproj.";
        package = package.Trim();
        if (!package.StartsWith("Rustzor.", StringComparison.Ordinal)) return $"'{package}' is not a Rustzor library. Rustzor libraries are named Rustzor.*.";

        var text = File.ReadAllText(project);
        if (text.Contains($"\\{package}.csproj\"") || text.Contains($"Include=\"{package}\"")) return $"{package} is already referenced.";

        string reference;
        if (text.Contains("<PackageReference Include=\"Rustzor."))
        {
            reference = $"    <PackageReference Include=\"{package}\" Version=\"{settings.RustzorVersion}\" />";
        }
        else
        {
            var source = TemplateService.SourcePath(package);
            if (settings.RustzorRoot.Length > 0 && !File.Exists(Path.Combine(settings.RustzorRoot, source)))
                return $"There is no library called {package}. Check the name with rustzor.get_library_api or the guide.";
            reference = $"    <ProjectReference Include=\"$(RustzorRoot)\\{source}\" />";
        }
        var at = text.IndexOf("</ItemGroup>", StringComparison.Ordinal);
        if (at < 0) return "The .csproj has no <ItemGroup> to add to.";
        File.WriteAllText(project, text.Insert(at, reference.TrimStart() + "\n  "));

        var imports = Path.Combine(root, "_Imports.razor");
        if (File.Exists(imports) && !File.ReadAllText(imports).Contains($"@using {package}\n") && !File.ReadAllText(imports).TrimEnd().EndsWith($"@using {package}"))
            File.AppendAllText(imports, $"@using {package}\n");

        workbench.Log($"Added {package}", LogKind.Ai);
        return $"Added {package} to {Path.GetFileName(project)} and _Imports.razor. Build to restore it.";
    }

    [KernelFunction("create_project")]
    [Description("Creates a new Rustzor project from a template in the user's projects folder and opens it. Use 'blank' for a minimal app. Returns the project folder.")]
    public async Task<string> CreateProjectAsync(
        [Description("Template id from list_templates")] string templateId,
        [Description("Human-readable project name, e.g. 'Coffee Shop POS'")] string projectName)
    {
        var template = _templates.Find(templateId) ?? _templates.Find("blank");
        if (template is null) return "No templates are installed.";
        var folder = _templates.Create(template, projectName, settings.ProjectsFolder);
        settings.Save();
        await workbench.OpenProjectAsync(folder);
        workbench.Log($"Created {projectName} from the {template.Name} template", LogKind.Ai);
        return $"Created and opened {folder} from template '{template.Id}'. Call project.get_project_overview next.";
    }
}
