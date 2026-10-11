using Rustzor.Build;

// rustzor build tool. Run by the Rustzor MSBuild targets:
//   bundle <assemblyDir> <entryAssembly> <runtimeDir> <webRoot> [--aot <dir>]
//   aot    <assemblyDir> <entryAssembly> <outputDir>
//
// Paths may carry a trailing space: MSBuild's Exec quoting turns `dir\"` into an
// escaped quote, so the targets write `"dir\ "` and the space is trimmed here.

static string Arg(string[] args, int i) => args[i].Trim();

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: Rustzor.Build bundle|aot ...");
    return 2;
}

try
{
    switch (args[0])
    {
        case "bundle" when args.Length >= 5:
            string? aot = null;
            for (var i = 5; i < args.Length - 1; i++)
                if (args[i] == "--aot") aot = Arg(args, i + 1);
            return Bundler.Run(Arg(args, 1), Arg(args, 2), Arg(args, 3), Arg(args, 4), aot);
        case "aot" when args.Length >= 4:
            return Rustzor.Build.Aot.AotCompiler.Run(Arg(args, 1), Arg(args, 2), Arg(args, 3));
        default:
            Console.Error.WriteLine("usage: Rustzor.Build bundle|aot ...");
            return 2;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine("error RZ0000: Rustzor build tool failed: " + e);
    return 1;
}
