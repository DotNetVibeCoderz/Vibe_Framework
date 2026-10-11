using System.Reflection;

namespace Rustzor.Cli;

/// <summary>Parses the command line and runs a command.</summary>
public static class Cli
{
    /// <summary>This tool's version, which is also the Rustzor version it creates and adds.</summary>
    public static string Version { get; } =
        (typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static async Task<int> RunAsync(string[] args)
    {
        // The arrows and ticks in the output are UTF-8; Windows consoles default to a code page.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Help();
            return 0;
        }
        if (args[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine(Version);
            return 0;
        }

        var options = Options.Parse(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "new" => await NewCommand.RunAsync(options),
                "serve" or "run" => await ServeCommand.RunAsync(options),
                "publish" => await PublishCommand.RunAsync(options),
                "add" => await AddCommand.RunAsync(options),
                "info" => await InfoCommand.RunAsync(options),
                _ => Unknown(args[0]),
            };
        }
        catch (CliException e)
        {
            Out.Error(e.Message);
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Out.Error($"Unknown command '{command}'.");
        Help();
        return 1;
    }

    private static void Help()
    {
        Console.WriteLine($"""
            rustzor {Version}: C# Razor apps in the browser on RustNetRuntime

            Usage: rustzor <command> [options]

              new [template] [name]   Create a project. Templates: app (default), empty, lib.
                  --title "My App"    The app's display name (app template).
                  -o, --output <dir>  Where to create it (default: ./<name>).
              serve [project]         Build, serve and rebuild on every save, reloading the browser.
                  -p, --port <port>   Port (default 5080).
                  --no-watch          Build and serve once.
                  --open              Open the browser.
              publish [project]       Publish the static site (Release).
                  --aot               Also compile hot code ahead of time to WebAssembly.
                  -o, --output <dir>  Output folder (default: bin/Release/net10.0/publish).
              add <package...>        Add Rustzor packages: ui, fetch, storage, data, signalr, grpc, ...
                                      (or full ids like Rustzor.Browser.Media), at version {Version}.
              info [project]          Versions, packages and the size of the built site.

            [project] is a .csproj or a folder containing one (default: the current folder).
            Docs: https://github.com/DotNetVibeCoderz/Vibe_Framework/tree/main/Rustzor/docs/en
            Built by Gravicode Studios, led by Kang Fadhil.
            """);
    }
}

/// <summary>An error to report to the user without a stack trace.</summary>
public sealed class CliException(string message) : Exception(message);

/// <summary>Positional arguments and --flags.</summary>
public sealed class Options
{
    public List<string> Positional { get; } = new();
    public Dictionary<string, string?> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith('-'))
            {
                var name = a.TrimStart('-');
                var eq = name.IndexOf('=');
                if (eq > 0)
                {
                    o.Flags[name[..eq]] = name[(eq + 1)..];
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && TakesValue(name))
                {
                    o.Flags[name] = args[++i];
                }
                else
                {
                    o.Flags[name] = null;
                }
            }
            else
            {
                o.Positional.Add(a);
            }
        }
        return o;
    }

    private static bool TakesValue(string flag) => flag is "o" or "output" or "p" or "port" or "title" or "c" or "configuration" or "project";

    public bool Has(params string[] names) => names.Any(Flags.ContainsKey);

    public string? Get(params string[] names) => names.Select(n => Flags.TryGetValue(n, out var v) ? v : null).FirstOrDefault(v => v is not null);
}
