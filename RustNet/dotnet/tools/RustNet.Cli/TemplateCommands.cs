namespace RustNet.Cli;

/// <summary>Project generator: instantiates app templates with drivers preloaded.</summary>
internal static class TemplateCommands
{
    public static string? FindTemplatesDir()
    {
        string? env = Environment.GetEnvironmentVariable("RUSTNET_TEMPLATES");
        if (env is not null && Directory.Exists(env))
        {
            return env;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "templates");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "TEMPLATES.md")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    public static int List(string[] _)
    {
        string dir = FindTemplatesDir()
            ?? throw new InvalidOperationException("templates directory not found (set RUSTNET_TEMPLATES)");
        Console.WriteLine($"templates in {dir}:");
        foreach (string t in Directory.GetDirectories(dir).OrderBy(x => x))
        {
            string name = Path.GetFileName(t);
            string readme = Path.Combine(t, "README.md");
            string desc = File.Exists(readme)
                ? File.ReadLines(readme).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? ""
                : "";
            Console.WriteLine($"  {name,-20} {desc}");
        }
        return 0;
    }

    public static int New(string[] args)
    {
        var positional = Cli.Positional(args);
        if (positional.Length < 2)
        {
            Console.Error.WriteLine("usage: rustnet new <template> <ProjectName>");
            Console.Error.WriteLine("run 'rustnet templates' to see available templates");
            return 2;
        }
        string templateName = positional[0];
        string projectName = positional[1];
        if (!projectName.All(c => char.IsLetterOrDigit(c) || c is '.' or '_'))
        {
            throw new ArgumentException("project name must be alphanumeric");
        }
        string templatesDir = FindTemplatesDir()
            ?? throw new InvalidOperationException("templates directory not found (set RUSTNET_TEMPLATES)");
        string source = Path.Combine(templatesDir, templateName);
        if (!Directory.Exists(source))
        {
            throw new ArgumentException($"template '{templateName}' not found — run 'rustnet templates'");
        }
        string target = Path.Combine(Directory.GetCurrentDirectory(), projectName);
        if (Directory.Exists(target))
        {
            throw new InvalidOperationException($"directory {projectName} already exists");
        }
        CopyTemplate(source, target, projectName);
        Console.WriteLine($"created {projectName}/ from template '{templateName}'");
        Console.WriteLine("next steps:");
        Console.WriteLine($"  cd {projectName}");
        Console.WriteLine("  dotnet build");
        Console.WriteLine($"  rustnet flash bin/Debug/net10.0/{projectName}.dll --name {projectName.ToLowerInvariant()} --key <priv.der> --start");
        return 0;
    }

    private static void CopyTemplate(string source, string target, string projectName)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file).Replace("__NAME__", projectName);
            string dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            // Binary assets (embedded resources like images/fonts) must be
            // copied byte-for-byte — text processing corrupts them.
            if (IsBinaryAsset(file))
            {
                File.Copy(file, dest, overwrite: true);
            }
            else
            {
                string content = File.ReadAllText(file).Replace("__NAME__", projectName);
                File.WriteAllText(dest, content);
            }
        }
    }

    private static bool IsBinaryAsset(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".gif" or ".bmp" or ".png" or ".jpg" or ".jpeg"
            or ".ico" or ".bin" or ".ttf" or ".otf" or ".wav" or ".mp3"
            or ".dll" or ".rnx" or ".rnsb";
    }
}
