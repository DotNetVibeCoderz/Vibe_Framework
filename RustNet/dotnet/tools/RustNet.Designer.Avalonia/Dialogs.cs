using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RustNet.Designer.Avalonia;

/// <summary>
/// The two dialogs this application needs: say something, and ask yes or no.
/// </summary>
/// <remarks>
/// Avalonia has no <c>MessageBox</c>. That is not an omission to route around
/// with a package — a message box is a window with a label and two buttons,
/// and writing it here keeps the look consistent with the rest of the tool
/// instead of borrowing whatever a third-party library decided.
/// </remarks>
internal static class Dialogs
{
    public static Task Message(Window owner, string title, string text)
        => Show(owner, title, text, confirm: false);

    public static Task<bool> Confirm(Window owner, string title, string text)
        => Show(owner, title, text, confirm: true);

    /// <summary>Ask for one line of text. Null when the person backed out.</summary>
    public static async Task<string?> Prompt(Window owner, string title, string label, string initial)
    {
        var result = new TaskCompletionSource<string?>();
        var box = new TextBox { Text = initial, Width = 240 };
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var cancel = new Button { Content = "Cancel" };
        cancel.Classes.Add("flat");
        cancel.Click += (_, _) => { result.TrySetResult(null); window.Close(); };

        var ok = new Button { Content = "Go", IsDefault = true };
        ok.Classes.Add("flat");
        ok.Classes.Add("primary");
        ok.Click += (_, _) => { result.TrySetResult(box.Text ?? ""); window.Close(); };

        window.Content = new StackPanel
        {
            Margin = new global::Avalonia.Thickness(18),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = label },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 6,
                    Children = { cancel, ok },
                },
            },
        };
        window.Closed += (_, _) => result.TrySetResult(null);
        window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };

        await window.ShowDialog(owner);
        return await result.Task;
    }

    private static async Task<bool> Show(Window owner, string title, string text, bool confirm)
    {
        var result = new TaskCompletionSource<bool>();

        var body = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new global::Avalonia.Thickness(0, 0, 0, 16),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
        };

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        if (confirm)
        {
            var cancel = new Button { Content = "Cancel" };
            cancel.Classes.Add("flat");
            cancel.Click += (_, _) => { result.TrySetResult(false); window.Close(); };
            buttons.Children.Add(cancel);
        }

        var ok = new Button { Content = confirm ? "Discard" : "OK", IsDefault = true };
        ok.Classes.Add("flat");
        if (confirm)
        {
            ok.Classes.Add("primary");
        }
        ok.Click += (_, _) => { result.TrySetResult(true); window.Close(); };
        buttons.Children.Add(ok);

        window.Content = new StackPanel
        {
            Margin = new global::Avalonia.Thickness(18),
            Children = { body, buttons },
        };

        // Closing by the title bar is a "no" — otherwise a dismissed confirm
        // would hang the caller waiting for an answer that never comes.
        window.Closed += (_, _) => result.TrySetResult(false);

        await window.ShowDialog(owner);
        return await result.Task;
    }
}
