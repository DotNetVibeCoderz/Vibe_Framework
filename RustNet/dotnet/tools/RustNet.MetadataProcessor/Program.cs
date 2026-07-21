using RustNet.MetadataProcessor;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.WriteLine("RustNet MetadataProcessor — compiles a .NET assembly to RNX");
    Console.WriteLine("usage: rustnet-mdp <app.dll> [-o out.rnx]");
    return args.Length < 1 ? 2 : 0;
}

string input = args[0];
string output = Path.ChangeExtension(input, ".rnx");
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "-o")
    {
        output = args[i + 1];
    }
}

if (!File.Exists(input))
{
    Console.Error.WriteLine($"error: {input} not found");
    return 1;
}

try
{
    byte[] rnx = RnxCompiler.Compile(input, out var warnings);
    foreach (string w in warnings)
    {
        Console.Error.WriteLine($"warning: {w}");
    }
    File.WriteAllBytes(output, rnx);
    Console.WriteLine($"{Path.GetFileName(input)} -> {output} ({rnx.Length} bytes)");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
