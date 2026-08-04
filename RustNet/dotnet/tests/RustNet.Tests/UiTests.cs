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

    /// <summary>
    /// Docking is order-dependent by design: a top strip declared before a
    /// left rail spans the full width and the rail starts underneath it.
    /// Getting this backwards is the classic dock-panel bug, so it is pinned.
    /// </summary>
    [Fact]
    public void DockPanelTakesEdgesInDeclarationOrder()
    {
        UiElement dock = UiElement.Make("dockpanel");
        dock.Padding = 0;
        dock.Gap = 0;

        UiElement top = UiElement.Make("rect");
        top.Dock = "top";
        top.Height = 20;
        dock.Add(top);

        UiElement rail = UiElement.Make("rect");
        rail.Dock = "left";
        rail.Width = 40;
        dock.Add(rail);

        UiElement body = UiElement.Make("rect");
        dock.Add(body);

        dock.Arrange(0, 0, 200, 100);

        Assert.Equal(200, top.LayoutW);
        Assert.Equal(0, top.LayoutY);
        Assert.Equal(20, rail.LayoutY);
        Assert.Equal(40, rail.LayoutW);
        Assert.Equal(40, body.LayoutX);
        Assert.Equal(160, body.LayoutW);
    }

    /// <summary>A collapsed expander occupies only its header, so whatever is
    /// under it does not move when the body is hidden.</summary>
    [Fact]
    public void ExpanderMeasuresOnlyItsHeaderWhenCollapsed()
    {
        UiElement exp = UiElement.Make("expander");
        exp.Text = "Advanced";
        UiElement child = UiElement.Make("rect");
        child.Height = 60;
        exp.Add(child);

        exp.Checked = false;
        int collapsed = exp.Measure(200);
        exp.Checked = true;
        int expanded = exp.Measure(200);

        Assert.True(expanded > collapsed + 50, $"{collapsed} -> {expanded}");
    }

    /// <summary>A tab control shows one page. Measuring all of them would
    /// size the panel to its largest tab and leave a hole under the rest.</summary>
    [Fact]
    public void TabControlArrangesOnlyTheSelectedPage()
    {
        UiElement tabs = UiElement.Make("tabcontrol");
        tabs.Padding = 0;
        for (int i = 0; i < 3; i++)
        {
            UiElement page = UiElement.Make("tabitem");
            page.Text = "T" + i;
            tabs.Add(page);
        }
        tabs.Selected = 2;
        tabs.Arrange(0, 0, 200, 100);

        Assert.Equal(tabs.Children[2], tabs.SelectedPage());
        Assert.True(tabs.Children[2].LayoutW > 0);
    }

    /// <summary>Tapping a combo box advances it and wraps, because there is
    /// no dropdown to open on a screen this size.</summary>
    [Fact]
    public void ComboBoxCyclesOnTap()
    {
        UiElement combo = UiElement.Make("combobox");
        combo.Items.Add("slow");
        combo.Items.Add("normal");
        combo.Items.Add("fast");
        combo.Selected = 0;
        combo.Arrange(0, 0, 100, 20);

        Ui.Tap(combo, 10, 10);
        Assert.Equal(1, combo.Selected);
        Ui.Tap(combo, 10, 10);
        Ui.Tap(combo, 10, 10);
        Assert.Equal(0, combo.Selected);
    }

    /// <summary>Word wrapping keeps words whole; only a word longer than the
    /// line is broken.</summary>
    [Fact]
    public void TextFlowWrapsOnWords()
    {
        // 8 pixels per character at scale 1, so 80 pixels is ten characters.
        List<string> lines = UiElement.WrapText("alpha beta gamma", 80, 1);
        Assert.Equal(2, lines.Count);
        Assert.Equal("alpha beta", lines[0]);
        Assert.Equal("gamma", lines[1]);

        List<string> broken = UiElement.WrapText("supercalifragilistic", 80, 1);
        Assert.True(broken.Count >= 2);
        Assert.Equal("supercalif", broken[0]);
    }

    /// <summary>The weekday of the first drives the whole month grid, and it
    /// is computed without a real-time clock so a calendar can show a month
    /// the device is not in.</summary>
    [Fact]
    public void CalendarFindsTheFirstWeekday()
    {
        // 1 January 2026 is a Thursday; 1 March 2026 a Sunday.
        Assert.Equal(4, UiElement.FirstWeekday(2026, 1));
        Assert.Equal(0, UiElement.FirstWeekday(2026, 3));
        Assert.Equal(29, UiElement.DaysInMonth(2024, 2));
        Assert.Equal(28, UiElement.DaysInMonth(2100, 2));
    }

    /// <summary>Every new control survives the designer's save/load round
    /// trip, including the attributes only it uses.</summary>
    [Fact]
    public void NewControlsRoundTripThroughXml()
    {
        UiElement root = UiElement.Make("window");

        UiElement chart = UiElement.Make("chart");
        chart.Id = "trace";
        chart.Series.Add(3);
        chart.Series.Add(9);
        chart.Series.Add(4);
        root.Add(chart);

        UiElement poly = UiElement.Make("polygon");
        poly.Points.Add(0);
        poly.Points.Add(0);
        poly.Points.Add(10);
        poly.Points.Add(20);
        root.Add(poly);

        UiElement line = UiElement.Make("line");
        line.X2 = 40;
        line.Y2 = 25;
        root.Add(line);

        UiElement cal = UiElement.Make("calendar");
        cal.Year = 2031;
        cal.Month = 7;
        root.Add(cal);

        UiElement docked = UiElement.Make("rect");
        docked.Dock = "bottom";
        root.Add(docked);

        UiElement again = Ui.LoadXml(Ui.ToXml(root));

        UiElement trace = again.FindById("trace");
        Assert.Equal(new List<int> { 3, 9, 4 }, trace.Series);
        Assert.Equal(new List<int> { 0, 0, 10, 20 }, again.Children[1].Points);
        Assert.Equal(40, again.Children[2].X2);
        Assert.Equal(25, again.Children[2].Y2);
        Assert.Equal(2031, again.Children[3].Year);
        Assert.Equal(7, again.Children[3].Month);
        Assert.Equal("bottom", again.Children[4].Dock);
    }

    /// <summary>A tree hides the subtree of a collapsed node but still counts
    /// the node itself, so the rows below it do not jump.</summary>
    [Fact]
    public void TreeViewMeasuresVisibleRowsOnly()
    {
        UiElement tree = UiElement.Make("treeview");
        tree.Padding = 0;
        UiElement parent = UiElement.Make("label");
        parent.Text = "sensors";
        for (int i = 0; i < 3; i++)
        {
            parent.Add(UiElement.Make("label"));
        }
        tree.Add(parent);

        parent.Checked = false;
        int collapsed = tree.Measure(200);
        parent.Checked = true;
        int expanded = tree.Measure(200);

        Assert.True(expanded > collapsed, $"{collapsed} -> {expanded}");
    }
}
