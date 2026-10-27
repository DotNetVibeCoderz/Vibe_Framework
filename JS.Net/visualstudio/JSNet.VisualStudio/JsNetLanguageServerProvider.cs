using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.LanguageServer;
using Microsoft.VisualStudio.RpcContracts.LanguageServerProvider;
using Nerdbank.Streams;

namespace JSNet.VisualStudio;

/// <summary>
/// Starts <c>jsnet lsp</c> (from the JSNet.Cli .NET tool, or the path in <c>JSNET_PATH</c>) for .jsn documents.
/// </summary>
[VisualStudioContribution]
internal sealed class JsNetLanguageServerProvider : LanguageServerProvider
{
    [VisualStudioContribution]
    public static DocumentTypeConfiguration JsNetDocumentType => new("jsnet")
    {
        FileExtensions = [".jsn"],
        BaseDocumentType = LanguageServerBaseDocumentType,
    };

    public override LanguageServerProviderConfiguration LanguageServerProviderConfiguration =>
        new("JS.Net Language Server", [DocumentFilter.FromDocumentType(JsNetDocumentType)]);

    public override Task<IDuplexPipe?> CreateServerConnectionAsync(CancellationToken cancellationToken)
    {
        string command = Environment.GetEnvironmentVariable("JSNET_PATH") is { Length: > 0 } custom ? custom : "jsnet";
        var info = new ProcessStartInfo
        {
            FileName = command,
            Arguments = "lsp",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            var process = Process.Start(info);
            if (process is null) return Task.FromResult<IDuplexPipe?>(null);
            return Task.FromResult<IDuplexPipe?>(new DuplexPipe(
                PipeReader.Create(process.StandardOutput.BaseStream),
                PipeWriter.Create(process.StandardInput.BaseStream)));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The jsnet CLI is not installed: `dotnet tool install -g JSNet.Cli`.
            return Task.FromResult<IDuplexPipe?>(null);
        }
    }
}
