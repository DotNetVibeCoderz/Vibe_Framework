using JSNet.Gallery;

namespace JSNet.Tests;

/// <summary>Every JS.Net Gallery sample is itself a compiler regression test: it must build and run cleanly.</summary>
public class GalleryTests
{
    public static TheoryData<string> SampleIds() => new(Catalog.samples.Select(s => s.id));

    [Theory]
    [MemberData(nameof(SampleIds))]
    public async Task SampleRunsWithoutErrors(string id)
    {
        var result = await Catalog.runSample(id);
        Assert.True(result.ok, result.error);
        Assert.False(string.IsNullOrWhiteSpace(result.output));
    }

    [Fact]
    public void LoweredCSharpIsAvailableForEverySample()
    {
        foreach (var s in Catalog.samples)
            Assert.Contains("namespace JSNet.Gallery", Catalog.lowered(s.file));
    }

    [Fact]
    public void HighlighterRoundTripsSource()
    {
        string source = Catalog.source("Classes.jsn");
        var spans = Highlighter.highlight(source);
        Assert.Equal(source, string.Concat(spans.Select(s => s.text)));
        Assert.Contains(spans, s => s.kind == TokenClass.Keyword && s.text == "class");
        Assert.Contains(spans, s => s.kind == TokenClass.Comment);
    }
}
