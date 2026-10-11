namespace Rustzor.Cli;

/// <summary>
/// rustzor new [app|empty|lib] [name]: dotnet new with the Rustzor templates,
/// installing the templates of this tool's version first when they are missing.
/// </summary>
public static class NewCommand
{
    private static readonly Dictionary<string, string> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app"] = "rustzor",
        ["rustzor"] = "rustzor",
        ["empty"] = "rustzor-empty",
        ["rustzor-empty"] = "rustzor-empty",
        ["lib"] = "rustzorlib",
        ["library"] = "rustzorlib",
        ["rustzorlib"] = "rustzorlib",
    };

    public static async Task<int> RunAsync(Options o)
    {
        var args = o.Positional;
        string template = "rustzor";
        string? name = null;
        if (args.Count >= 1 && Templates.TryGetValue(args[0], out var t))
        {
            template = t;
            name = args.Count >= 2 ? args[1] : null;
        }
        else if (args.Count >= 1)
        {
            name = args[0];
        }
        name ??= template == "rustzorlib" ? "MyRustzorLibrary" : "MyRustzorApp";
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.]*$"))
            throw new CliException($"'{name}' is not a valid project name: use letters, digits, '_' and '.', starting with a letter.");

        await EnsureTemplatesAsync(o.Has("update"));

        var output = o.Get("o", "output") ?? name;
        var command = $"new {template} -n {name} -o \"{output}\"";
        if (o.Get("title") is { } title && template == "rustzor") command += $" --Title \"{title.Replace("\"", "'")}\"";
        if (o.Has("force")) command += " --force";

        Out.Step($"Creating {Out.Bold(name)} from the {template} template");
        var code = await Proc.RunAsync("dotnet", command);
        if (code != 0) return code;

        Out.Ok($"Created {Path.GetFullPath(output)}");
        Console.WriteLine();
        if (template == "rustzorlib")
        {
            Console.WriteLine($"  Reference it from an app: dotnet add <app.csproj> reference {output}/{name}.csproj");
        }
        else
        {
            Console.WriteLine($"  cd {output}");
            Console.WriteLine("  rustzor serve        " + Out.Dim("# build, serve on http://localhost:5080, reload on save"));
        }
        return 0;
    }

    /// <summary>Installs Rustzor.Templates at this tool's version unless the templates are already there.</summary>
    private static async Task EnsureTemplatesAsync(bool force)
    {
        if (!force)
        {
            var (_, list) = await Proc.CaptureAsync("dotnet", "new list rustzor");
            if (list.Contains("rustzor-empty") && list.Contains("rustzorlib")) return;
        }
        Out.Step($"Installing Rustzor.Templates {Cli.Version}");
        // RUSTZOR_FEED: a folder or feed URL to install from as well, for builds not yet on nuget.org.
        var feed = Environment.GetEnvironmentVariable("RUSTZOR_FEED");
        var args = $"new install Rustzor.Templates::{Cli.Version} --force";
        if (!string.IsNullOrWhiteSpace(feed)) args += $" --add-source \"{feed}\"";
        var (code, output) = await Proc.CaptureAsync("dotnet", args);
        if (code != 0) throw new CliException($"Could not install the Rustzor templates:\n{output.Trim()}");
    }
}
