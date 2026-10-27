using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JSNet.Sdk.Tasks;

/// <summary>One line the compiler printed; <see cref="IsError"/> lines went to standard error.</summary>
public readonly struct OutputLine(bool isError, string text)
{
    public bool IsError { get; } = isError;
    public string Text { get; } = text;
}

/// <summary>
/// Wire format between the MSBuild task (client) and <c>jsnet server</c>. Request: version, working directory and the
/// <c>jsnet</c> arguments. Response: exit code and the output lines. Strings are length-prefixed UTF-8
/// (<see cref="BinaryWriter"/>), so both .NET Framework MSBuild and .NET can speak it without extra dependencies.
/// </summary>
public static class BuildServerProtocol
{
    public const string Version = "jsnet-server-1";

    /// <summary>The argument that asks a server to shut down.</summary>
    public const string StopCommand = "--stop";

    /// <summary>
    /// A pipe name unique to one compiler build and one user, so a rebuilt or updated compiler never talks to a server
    /// running an older one.
    /// </summary>
    public static string PipeName(string compilerPath)
    {
        string full = Path.GetFullPath(compilerPath);
        long stamp = File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks : 0;
        string key = full.ToLowerInvariant() + "|" + stamp + "|" + Environment.UserName;
        ulong hash = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(key))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return "jsnet-" + hash.ToString("x16");
    }

    public static void WriteRequest(Stream stream, string workingDirectory, IList<string> args)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Version);
        writer.Write(workingDirectory);
        writer.Write(args.Count);
        foreach (var a in args) writer.Write(a);
        writer.Flush();
    }

    public static (string Version, string WorkingDirectory, List<string> Args) ReadRequest(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        string version = reader.ReadString();
        if (version != Version) return (version, string.Empty, []);
        string cwd = reader.ReadString();
        int count = reader.ReadInt32();
        var args = new List<string>(count);
        for (int i = 0; i < count; i++) args.Add(reader.ReadString());
        return (version, cwd, args);
    }

    public static void WriteResponse(Stream stream, int exitCode, IList<OutputLine> lines)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(exitCode);
        writer.Write(lines.Count);
        foreach (var line in lines)
        {
            writer.Write(line.IsError);
            writer.Write(line.Text);
        }
        writer.Flush();
    }

    public static (int ExitCode, List<OutputLine> Lines) ReadResponse(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        int exitCode = reader.ReadInt32();
        int count = reader.ReadInt32();
        var lines = new List<OutputLine>(count);
        for (int i = 0; i < count; i++)
        {
            bool isError = reader.ReadBoolean();
            lines.Add(new OutputLine(isError, reader.ReadString()));
        }
        return (exitCode, lines);
    }
}
