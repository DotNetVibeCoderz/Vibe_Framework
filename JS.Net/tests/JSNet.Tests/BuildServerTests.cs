using JSNet.Sdk.Tasks;

namespace JSNet.Tests;

/// <summary>
/// The MSBuild task's path through a real <c>jsnet server</c>: start on demand, compile, report errors, stop. Uses a
/// private copy of the compiler so it gets its own server and never touches one serving the developer's builds.
/// </summary>
public sealed class BuildServerTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "jsnet-server-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _compiler;

    public BuildServerTests()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;   // bin/<Configuration>/net10.0
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "JSNet.slnx"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!;
        string cli = Path.Combine(root, "src", "JSNet.Cli", "bin", configuration, "net10.0");
        _compiler = Path.Combine(_work, "compiler", "jsnet.dll");
        Copy(cli, Path.GetDirectoryName(_compiler)!);
    }

    public void Dispose()
    {
        string shadow = BuildServerClient.ShadowDirectory(_compiler);
        SpinWait.SpinUntil(() => !BuildServerClient.Stop(_compiler), TimeSpan.FromSeconds(10));
        // The server's private copy can be removed once the process has let go of it.
        SpinWait.SpinUntil(() => { try { if (Directory.Exists(shadow)) Directory.Delete(shadow, recursive: true); return true; } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } },
            TimeSpan.FromSeconds(10));
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private (int ExitCode, List<OutputLine> Lines) Compile(string source)
    {
        string project = Path.Combine(_work, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Program.jsn"), source);
        File.WriteAllText(Path.Combine(project, "jsnet.rsp"), Path.Combine(project, "Program.jsn"));
        var result = BuildServerClient.TryCompile(_compiler, "dotnet",
            ["compile", "--out", Path.Combine(project, "gen"), "--root-namespace", "App", "--project-dir", project, "@" + Path.Combine(project, "jsnet.rsp")], log: null);
        Assert.True(result is not null, "the build server did not answer");
        return result!.Value;
    }

    [Fact]
    public void CompilesThroughTheServerAndReportsErrors()
    {
        var ok = Compile("const xs = [1, 2, 3];\nconsole.log(xs.map(x => x * 2));\n");
        Assert.Equal(0, ok.ExitCode);
        Assert.Contains(ok.Lines, l => l.Text.StartsWith("jsnet: compiled 1 file(s)", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_work, "project", "gen", "Program.jsn.g.cs")));

        var bad = Compile("const k = 1;\nk = 2;\n");
        Assert.Equal(1, bad.ExitCode);
        var error = Assert.Single(bad.Lines, l => l.IsError);
        Assert.Contains("Program.jsn(2,1): error JSN1001", error.Text);

        Assert.True(BuildServerClient.Stop(_compiler));
        Assert.True(SpinWait.SpinUntil(() => !BuildServerClient.Stop(_compiler), TimeSpan.FromSeconds(10)), "the server did not exit");
        Assert.True(Directory.Exists(BuildServerClient.ShadowDirectory(_compiler)), "the server should run from a private copy");
    }

    [Fact]
    public void CleanupSkipsCopiesThatAreInUse()
    {
        string root = Path.Combine(_work, "shadows");
        string idle = Path.Combine(root, "idle"), busy = Path.Combine(root, "busy");
        Directory.CreateDirectory(idle);
        Directory.CreateDirectory(busy);
        using (File.Open(Path.Combine(busy, "jsnet.dll"), FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            BuildServerClient.CleanOldCopies(root, TimeSpan.Zero);
            Assert.False(Directory.Exists(idle));
            if (OperatingSystem.IsWindows()) Assert.True(File.Exists(Path.Combine(busy, "jsnet.dll")));
        }
    }

    [Fact]
    public void QuotesArgumentsLikeTheDotnetCommandLine()
    {
        Assert.Equal("plain", JsNetCompile.Quote("plain"));
        Assert.Equal("\"a b\"", JsNetCompile.Quote("a b"));
        Assert.Equal(@"""C:\dir with space\\""", JsNetCompile.Quote(@"C:\dir with space\"));
        Assert.Equal("\"say \\\"hi\\\"\"", JsNetCompile.Quote("say \"hi\""));
    }
}
