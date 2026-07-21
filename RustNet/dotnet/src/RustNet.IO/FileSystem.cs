using RustNet.Core;

namespace RustNet.IO;

// (byte-level APIs live alongside the text ones; streams build on them)

/// <summary>
/// Device filesystem (FAT on SD/flash, or RAM on the virtual device).
/// Paths are rooted at the device data area, e.g. "/data/log.txt".
/// </summary>
public static class FileSystem
{
    [InternalCall]
    public static void WriteAllText(string path, string contents) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void AppendText(string path, string contents) => throw new RuntimeOnlyException();

    [InternalCall]
    public static string ReadAllText(string path) => throw new RuntimeOnlyException();

    [InternalCall]
    public static bool Exists(string path) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Delete(string path) => throw new RuntimeOnlyException();

    /// <summary>Newline-separated entries of a directory.</summary>
    [InternalCall]
    public static string List(string path) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void CreateDirectory(string path) => throw new RuntimeOnlyException();

    [InternalCall]
    public static byte[] ReadAllBytes(string path) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void WriteAllBytes(string path, byte[] data) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void AppendBytes(string path, byte[] data) => throw new RuntimeOnlyException();
}
