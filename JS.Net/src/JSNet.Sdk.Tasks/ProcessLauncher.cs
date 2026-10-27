using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JSNet.Sdk.Tasks;

/// <summary>
/// Starts a background process that outlives the build. <see cref="Process.Start()"/> on Windows always lets the child
/// inherit every inheritable handle of MSBuild (console and node pipes); a long-lived child holding those makes
/// <c>dotnet build | tee</c> and CI steps wait for it. Windows therefore uses CreateProcess without inheritance.
/// </summary>
internal static class ProcessLauncher
{
    public static void StartDetached(string fileName, string arguments, string workingDirectory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            StartWindows(fileName, arguments, workingDirectory);
            return;
        }
        // .NET marks its own descriptors close-on-exec; redirecting the standard streams keeps the console's out of the child.
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi);
    }

    private static void StartWindows(string fileName, string arguments, string workingDirectory)
    {
        string exe = ResolveOnPath(fileName);
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        string commandLine = JsNetCompile.Quote(exe) + " " + arguments;
        const uint CreateNoWindow = 0x08000000, DetachedProcess = 0x00000008, CreateUnicodeEnvironment = 0x00000400;
        if (!CreateProcess(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CreateNoWindow | DetachedProcess | CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory, ref startup, out var info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(info.hProcess);
        CloseHandle(info.hThread);
    }

    private static string ResolveOnPath(string fileName)
    {
        if (System.IO.Path.IsPathRooted(fileName)) return fileName;
        string name = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".exe";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            string candidate = System.IO.Path.Combine(dir.Trim(), name);
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        return fileName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
