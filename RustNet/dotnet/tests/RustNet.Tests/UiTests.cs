using RustNet.UI;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// The UI toolkit's layout, hit-testing, input routing and XML round-trip
/// are pure logic (no Display calls), so they're validated on the host.
/// Rendering is exercised on the virtual device (see the E2E suite).
/// </summary>
public class UiTests
{
    private const string Markup =
        "<window width=\"160\" height=\"128\" pad=\"4\" gap=\"4\">" +
        "<label id=\"title\" text=\"Panel\" scale=\"2\"/>" +
        "<checkbox id=\"eco\" text=\"Eco\" checked=\"true\"/>" +
        "<radio id=\"r1\" text=\"A\" group=\"mode\" checked=\"true\"/>" +
        "<radio id=\"r2\" text=\"B\" group=\"mode\"/>" +
        "<slider id=\"sp\" min=\"10\" max=\"30\" value=\"20\"/>" +
        "<listbox id=\"zones\" items=\"Kitchen;Garage;Attic\" selected=\"0\"/>" +
        "<button id=\"apply\" text=\"Apply\"/>" +
        "</window>";

    [Fact]
    public void LoadXmlParsesControls()
    {
        UiElement root = Ui.LoadXml(Markup);
        Assert.Equal("window", root.Kind);
        Assert.True(root.FindById("eco").Checked);
        Assert.Equal(3, root.FindById("zones").Items.Count);
        Assert.Equal("Garage", root.FindById("zones").Items[1]);
        Assert.Equal(30, root.FindById("sp").Max);
    }

    [Fact]
    public void ArrangeStacksChildrenVertically()
    {
        UiElement root = Ui.LoadXml(Markup);
        root.Arrange(0, 0, 160, 128);
        UiElement title = root.FindById("title");
        UiElement eco = root.FindById("eco");
        // Padding 4 → first child at y=4; second below it after gap.
        Assert.Equal(4, title.LayoutX);
        Assert.Equal(4, title.LayoutY);
        Assert.True(eco.LayoutY > title.LayoutY);
    }

    [Fact]
    public void HitTestFindsControlAtPoint()
    {
        UiElement root = Ui.LoadXml(Markup);
        root.Arrange(0, 0, 160, 128);
        UiElement apply = root.FindById("apply");
        int cx = apply.LayoutX + apply.LayoutW / 2;
        int cy = apply.LayoutY + apply.LayoutH / 2;
        Assert.Equal("apply", root.HitTest(cx, cy).Id);
    }

    [Fact]
    public void TapTogglesCheckboxAndSelectsRadioGroup()
    {
        UiElement root = Ui.LoadXml(Markup);
        root.Arrange(0, 0, 160, 128);

        UiElement eco = root.FindById("eco");
        Ui.Tap(root, eco.LayoutX + 3, eco.LayoutY + 3);
        Assert.False(eco.Checked); // toggled off

        UiElement r2 = root.FindById("r2");
        Ui.Tap(root, r2.LayoutX + 3, r2.LayoutY + 3);
        Assert.True(r2.Checked);
        Assert.False(root.FindById("r1").Checked); // group cleared
    }

    [Fact]
    public void TapMovesSliderAndSelectsListboxRow()
    {
        UiElement root = Ui.LoadXml(Markup);
        root.Arrange(0, 0, 160, 128);

        UiElement sp = root.FindById("sp");
        Ui.Tap(root, sp.LayoutX + sp.LayoutW - 1, sp.LayoutY + 5); // far right
        Assert.Equal(30, sp.Value);
        Ui.Tap(root, sp.LayoutX, sp.LayoutY + 5); // far left
        Assert.Equal(10, sp.Value);

        UiElement zones = root.FindById("zones");
        int rowH = 8 * zones.Scale + 4;
        Ui.Tap(root, zones.LayoutX + 5, zones.LayoutY + 2 + rowH * 2 + 1); // 3rd row
        Assert.Equal(2, zones.Selected);
    }

    [Fact]
    public void XmlRoundTripsThroughToXml()
    {
        UiElement root = Ui.LoadXml(Markup);
        string xml = Ui.ToXml(root);
        UiElement again = Ui.LoadXml(xml);

        Assert.Equal("window", again.Kind);
        Assert.True(again.FindById("eco").Checked);
        Assert.Equal(3, again.FindById("zones").Items.Count);
        Assert.Equal(30, again.FindById("sp").Max);
        Assert.Equal("Apply", again.FindById("apply").Text);
    }

    [Fact]
    public void ScrollViewerClampsOffsetAndShiftsContent()
    {
        UiElement sv = UiElement.Make("scrollviewer");
        sv.Id = "sv";
        sv.Width = 60;
        sv.Height = 40; // viewport
        sv.Padding = 0;
        sv.Gap = 0;
        for (int i = 0; i < 10; i++)
        {
            UiElement row = UiElement.Make("button");
            row.Height = 20;
            sv.Add(row);
        }
        // Content = 10 * 20 = 200; viewport inner = 40.
        sv.Arrange(0, 0, 60, 40);
        Assert.Equal(200, sv.ContentH);
        int firstTop = sv.Children[0].LayoutY;
        Assert.Equal(0, firstTop); // no scroll: first row at the top

        // Scroll down 30px: content shifts up by 30.
        Ui.Scroll(sv, "sv", 30);
        sv.Arrange(0, 0, 60, 40);
        Assert.Equal(30, sv.ScrollOffset);
        Assert.Equal(firstTop - 30, sv.Children[0].LayoutY);

        // Over-scroll is clamped to content - viewport = 160.
        Ui.Scroll(sv, "sv", 1000);
        sv.Arrange(0, 0, 60, 40);
        Assert.Equal(160, sv.ScrollOffset);

        // A point below the viewport hit-tests to nothing (clipped).
        Assert.Null(sv.HitTest(10, 100));

        // Scroll survives an XML round-trip.
        string xml = Ui.ToXml(sv);
        UiElement again = Ui.LoadXml(xml);
        Assert.Equal("scrollviewer", again.Kind);
        Assert.Equal(160, again.ScrollOffset);
    }

    [Fact]
    public void GridLaysOutInColumns()
    {
        UiElement grid = UiElement.Make("grid");
        grid.Columns = 2;
        grid.Width = 100;
        grid.Padding = 0;
        grid.Gap = 0;
        for (int i = 0; i < 4; i++)
        {
            UiElement cell = UiElement.Make("button");
            cell.Height = 10;
            grid.Add(cell);
        }
        grid.Arrange(0, 0, 100, 100);
        // Two columns of width 50: cells 0,1 on row 0; 2,3 on row 1.
        Assert.Equal(0, grid.Children[0].LayoutX);
        Assert.Equal(50, grid.Children[1].LayoutX);
        Assert.Equal(0, grid.Children[2].LayoutX);
        Assert.True(grid.Children[2].LayoutY > grid.Children[0].LayoutY);
    }
}
