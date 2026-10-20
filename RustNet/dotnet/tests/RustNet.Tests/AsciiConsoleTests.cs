using RustNet.UI;
using RustNet.UI.AsciiConsole;
using Xunit;

namespace RustNet.Tests;

/// <summary>The AsciiConsole frontend: the same layout the device draws, as text.</summary>
public class AsciiConsoleTests
{
    private const string Markup =
        "<window width=\"160\" height=\"128\" pad=\"8\" gap=\"0\">" +
        "<label id=\"title\" text=\"Boiler\"/>" +
        "<checkbox id=\"eco\" text=\"Eco\" checked=\"true\"/>" +
        "<radio id=\"r1\" text=\"Auto\" group=\"mode\" checked=\"true\"/>" +
        "<radio id=\"r2\" text=\"Manual\" group=\"mode\"/>" +
        "<progress id=\"p\" value=\"50\" max=\"100\"/>" +
        "<listbox id=\"zones\" items=\"Kitchen;Garage\" selected=\"1\"/>" +
        "<button id=\"apply\" text=\"Apply\"/>" +
        "</window>";

    private static string[] Lines(string frame) => frame.TrimEnd('\n').Split('\n');

    [Fact]
    public void WindowMapsPixelsToCharacterCells()
    {
        UiElement root = Ui.LoadXml(Markup);
        string frame = new AsciiRenderer().Render(root);
        string[] lines = Lines(frame);
        Assert.Equal(16, lines.Length);            // 128 px / 8
        Assert.All(lines, l => Assert.Equal(20, l.Length)); // 160 px / 8
        Assert.Equal('┌', lines[0][0]);
        Assert.Equal('┘', lines[15][19]);
        // The label sits on the cell its pixel position maps to.
        var title = root.FindById("title");
        Assert.Equal("Boiler", lines[title.LayoutY / 8].Substring(title.LayoutX / 8, 6));
    }

    [Fact]
    public void ControlsShowTheirState()
    {
        UiElement root = Ui.LoadXml(Markup);
        string frame = new AsciiRenderer().Render(root);
        Assert.True(frame.Contains("[x] Eco"), frame);
        Assert.Contains("(*) Auto", frame);
        Assert.Contains("( ) Manual", frame);
        Assert.Contains(">Garage", frame);
        Assert.Contains(" Kitchen", frame);
        Assert.Contains("Apply", frame);
        // Half-full progress bar.
        string bar = Lines(frame).First(l => l.Contains("░"));
        Assert.Contains("█", bar);
        Assert.Contains("░", bar);
    }

    [Fact]
    public void AsciiModeUsesSevenBitCharactersOnly()
    {
        string frame = new AsciiRenderer { Unicode = false }.Render(Ui.LoadXml(Markup));
        Assert.All(frame, ch => Assert.True(ch < 128, $"non-ASCII '{ch}'"));
        Assert.Equal('+', frame[0]);
    }

    [Fact]
    public void KeyboardDrivesTheSameInputRoutingAsTaps()
    {
        UiElement root = Ui.LoadXml(Markup);
        var host = new AsciiConsoleHost(root);
        Assert.Equal("eco", host.Focused!.Id);
        UiElement? changed = null;
        host.Changed = e => changed = e;

        host.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false));
        Assert.False(root.FindById("eco").Checked);
        Assert.Equal("eco", changed!.Id);

        // Tab to the second radio and select it: the group's other member clears.
        host.HandleKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
        host.HandleKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
        host.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        Assert.True(root.FindById("r2").Checked);
        Assert.False(root.FindById("r1").Checked);

        // Listbox: arrows move the selection.
        host.HandleKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
        Assert.Equal("zones", host.Focused!.Id);
        host.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false));
        Assert.Equal(0, root.FindById("zones").Selected);
        Assert.Contains(">Kitchen", host.Frame());

        Assert.False(host.HandleKey(new ConsoleKeyInfo((char)27, ConsoleKey.Escape, false, false, false)));
    }

    [Fact]
    public void ChartAndCalendarRender()
    {
        UiElement root = Ui.LoadXml(
            "<window width=\"240\" height=\"120\" pad=\"8\">" +
            "<chart id=\"c\" height=\"8\" series=\"1,5,9,5,1\"/>" +
            "<calendar id=\"cal\" year=\"2026\" month=\"10\" value=\"20\" height=\"72\"/>" +
            "</window>");
        string frame = new AsciiRenderer().Render(root);
        Assert.True(frame.Contains("▁▄█▄▁"), frame);
        Assert.Contains("Oct 2026", frame);
        Assert.Contains("20*", frame); // the selected day
    }
}
