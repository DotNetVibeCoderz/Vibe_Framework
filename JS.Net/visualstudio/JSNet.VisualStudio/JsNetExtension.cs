using Microsoft.VisualStudio.Extensibility;

namespace JSNet.VisualStudio;

/// <summary>JS.Net for Visual Studio. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.</summary>
[VisualStudioContribution]
internal sealed class JsNetExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "JSNet.VisualStudio.Gravicode",
            version: this.ExtensionAssemblyVersion,
            publisherName: "Gravicode Studios",
            displayName: "JS.Net",
            description: "JS.Net language support: diagnostics, completion, hover, go to definition, references, rename, quick fixes and colouring for .jsn files.")
        {
            Icon = "Images/icon.png",
            Tags = ["JS.Net", "JavaScript", "TypeScript", ".NET", "language"],
            MoreInfo = "https://github.com/DotNetVibeCoderz/Vibe_Framework/tree/main/JS.Net",
        },
    };
}
