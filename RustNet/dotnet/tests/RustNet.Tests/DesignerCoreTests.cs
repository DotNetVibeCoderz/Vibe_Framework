using RustNet.Designer;
using RustNet.Designer.Assistant;
using RustNet.UI;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// The Designer's non-UI half, exercised where CI can see it.
/// </summary>
/// <remarks>
/// All of this used to live inside RustNet.Designer, a WPF project on
/// net10.0-windows — so none of it was reachable from a Linux runner, and the
/// only way to run these checks was to launch a GUI executable by hand with
/// <c>--selftest</c>. Nothing here draws anything; it was locked behind that
/// target framework by where the files sat, not by what they did.
/// </remarks>
public class DesignerCoreTests
{
    /// <summary>
    /// The assistant's own self-test: options, sessions, uploads, markdown and
    /// code rendering, the expression evaluator, the design functions, and —
    /// the part most likely to rot — that a Semantic Kernel actually builds
    /// for every provider with the plugins attached.
    /// </summary>
    /// <remarks>
    /// No model is called. This is the wiring, which is what breaks when a
    /// connector package moves; the model path itself is exercised by
    /// <c>rustnet-designer --ask</c> against a real endpoint.
    /// </remarks>
    [Fact]
    public void AssistantSelfTestPasses()
    {
        var log = new StringWriter();
        bool ok = AssistantSelfTest.Run(log);
        Assert.True(ok, log.ToString());
    }

    /// <summary>
    /// The sample layout is the fixture every headless path falls back to, so
    /// it has to stay loadable by the UI parser it is written for.
    /// </summary>
    [Fact]
    public void SampleLayoutParses()
    {
        UiElement root = Ui.LoadXml(SampleLayout.Xml);
        Assert.Equal("window", root.Kind);
        Assert.NotEmpty(root.Children);
        // Round-trip, because the Designer saves what it loaded.
        Assert.Equal(root.Kind, Ui.LoadXml(Ui.ToXml(root)).Kind);
    }

    /// <summary>
    /// Only children of a <c>canvas</c> carry absolute coordinates, so only
    /// they can be dragged; everything else is placed by its container and
    /// moving it would be a lie the next layout pass undoes.
    /// </summary>
    [Fact]
    public void OnlyCanvasChildrenCanBeDragged()
    {
        UiElement canvasRoot = Ui.LoadXml(
            "<window width=\"160\" height=\"128\">" +
            "  <canvas><label id=\"free\" x=\"10\" y=\"10\" text=\"a\"/></canvas>" +
            "</window>");
        UiElement free = FindById(canvasRoot, "free")!;
        Assert.True(DragTool.CanMove(canvasRoot, free));

        UiElement stackRoot = Ui.LoadXml(
            "<window width=\"160\" height=\"128\">" +
            "  <stack><label id=\"managed\" text=\"a\"/></stack>" +
            "</window>");
        UiElement managed = FindById(stackRoot, "managed")!;
        Assert.False(DragTool.CanMove(stackRoot, managed));

        // The root is never draggable, whatever it contains.
        Assert.False(DragTool.CanMove(canvasRoot, canvasRoot));
    }

    private static UiElement? FindById(UiElement el, string id)
    {
        if (el.Id == id)
        {
            return el;
        }
        foreach (UiElement child in el.Children)
        {
            if (FindById(child, id) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }
}
