using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace RustzorAppGen.Views;

/// <summary>Small modal dialogs built in code: prompt, confirm, three-way choice, message.</summary>
public static class Dialogs
{
    private static Window Shell(string title, double width = 440)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        return w;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Classes = { "display" },
        FontSize = 18,
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Classes = { "muted" },
        LineHeight = 20,
    };

    private static Button Primary(string text) => new() { Content = text, Classes = { "primary" }, IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
    private static Button Secondary(string text) => new() { Content = text, Classes = { "secondary" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    public static async Task<string?> PromptAsync(Window owner, string title, string message, string initial = "", string action = "OK")
    {
        var w = Shell(title);
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 0) };
        var ok = Primary(action);
        var cancel = Secondary("Cancel");
        cancel.IsCancel = true;
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = box.Text;
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                result = box.Text;
                w.Close();
            }
        };
        w.Content = new StackPanel { Margin = new Thickness(22), Spacing = 10, Children = { Heading(title), Body(message), box, Buttons(cancel, ok) } };
        w.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        await w.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string action = "OK", bool danger = false)
    {
        var w = Shell(title);
        var ok = Primary(action);
        if (danger) ok.Background = new SolidColorBrush(Color.Parse("#C62828"));
        var cancel = Secondary("Cancel");
        cancel.IsCancel = true;
        var result = false;
        ok.Click += (_, _) =>
        {
            result = true;
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new Thickness(22), Spacing = 10, Children = { Heading(title), Body(message), Buttons(cancel, ok) } };
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>Yes / No / Cancel: returns true, false, or null.</summary>
    public static async Task<bool?> ChooseAsync(Window owner, string title, string message, string yes, string no)
    {
        var w = Shell(title, 480);
        var y = Primary(yes);
        var n = Secondary(no);
        var c = Secondary("Cancel");
        c.IsCancel = true;
        bool? result = null;
        y.Click += (_, _) => { result = true; w.Close(); };
        n.Click += (_, _) => { result = false; w.Close(); };
        c.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new Thickness(22), Spacing = 10, Children = { Heading(title), Body(message), Buttons(c, n, y) } };
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task MessageAsync(Window owner, string title, string message)
    {
        var w = Shell(title);
        var ok = Primary("OK");
        ok.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new Thickness(22), Spacing = 10, Children = { Heading(title), new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20 }, Buttons(ok) } };
        await w.ShowDialog(owner);
    }
}
