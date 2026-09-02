using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
// The formatter is shared with the WPF front-end and lives in the Core.
using RustNet.Designer.Editor;

namespace RustNet.Designer.Avalonia.Editor;

/// <summary>
/// A text editor with the toolbar, find/replace bar and formatter the Designer
/// needs, wrapping AvaloniaEdit.
/// </summary>
/// <remarks>
/// A port of the WPF pane, and the closest thing to a straight translation in
/// this application: AvaloniaEdit is AvalonEdit by the same lineage, so the
/// document model, the caret, the highlighting definitions and the offsets all
/// behave the same. What changed is the property system (StyledProperty rather
/// than DependencyProperty), that clipboard and undo are methods on the editor
/// instead of routed commands, and that a modal prompt has to be awaited.
/// </remarks>
public partial class CodePane : UserControl
{
    public static readonly StyledProperty<object?> FooterContentProperty =
        AvaloniaProperty.Register<CodePane, object?>(nameof(FooterContent));

    public static readonly StyledProperty<string> SyntaxProperty =
        AvaloniaProperty.Register<CodePane, string>(nameof(Syntax), "csharp");

    public CodePane()
    {
        InitializeComponent();
        CodeEditorTheme.ApplyDarkPalette();
        CodeEditorTheme.Style(Editor);

        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretReadout();
        Editor.TextChanged += (_, _) =>
        {
            // Loading a document is not an edit; only what the person types is.
            if (_loading)
            {
                return;
            }
            Dirty = true;
            TextEdited?.Invoke(this, EventArgs.Empty);
        };

        // Gestures land on the editor, so they are registered on the text area
        // rather than on this control.
        void Bind(Action run, Key key, KeyModifiers modifiers = KeyModifiers.None)
            => Editor.TextArea.KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(key, modifiers),
                Command = new Relay(run),
            });

        Bind(() => ShowFind(false), Key.F, KeyModifiers.Control);
        Bind(() => ShowFind(true), Key.H, KeyModifiers.Control);
        Bind(() => _ = GoToLineAsync(), Key.G, KeyModifiers.Control);
        Bind(() => FindNext(forward: true), Key.F3);
        Bind(() => FindNext(forward: false), Key.F3, KeyModifiers.Shift);
        Bind(FormatDocument, Key.F, KeyModifiers.Alt | KeyModifiers.Shift);
        Bind(CloseFind, Key.Escape);
    }

    /// <summary>Buttons for whatever this pane is used for; shown along its bottom edge.</summary>
    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    /// <summary>Language tag driving syntax highlighting and the formatter.
    /// Not called Language: a Control already has one, and it means something
    /// else entirely.</summary>
    public string Syntax
    {
        get => GetValue(SyntaxProperty);
        set => SetValue(SyntaxProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FooterContentProperty)
        {
            FooterHost.Content = change.GetNewValue<object?>();
        }
        else if (change.Property == SyntaxProperty)
        {
            Editor.SyntaxHighlighting = CodeEditorTheme.DefinitionFor(change.GetNewValue<string>());
        }
    }

    private bool _loading;

    public string Text
    {
        get => Editor.Text;
        set
        {
            _loading = true;
            try
            {
                Editor.Text = value;
            }
            finally
            {
                _loading = false;
            }
            Dirty = false;
            TextEdited?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Set on every edit; cleared by <see cref="Text"/> and <see cref="MarkClean"/>.</summary>
    public bool Dirty { get; private set; }

    public void MarkClean() => Dirty = false;

    /// <summary>Raised on every user edit, for the window's dirty indicator.</summary>
    public event EventHandler? TextEdited;

    /// <summary>Raised when something worth reporting happened, e.g. a format error.</summary>
    public event EventHandler<string>? Status;

    public void FocusEditor() => Editor.TextArea.Focus();

    private void UpdateCaretReadout()
    {
        var caret = Editor.TextArea.Caret;
        CaretReadout.Text = $"Ln {caret.Line}, Col {caret.Column}";
    }

    // ---- clipboard and undo --------------------------------------------
    //
    // WPF routed these through ApplicationCommands so the focused control
    // could answer. AvaloniaEdit exposes them as methods, which is more
    // direct and removes the "is the command enabled" dance entirely.

    private void OnCut(object? sender, RoutedEventArgs e) { Editor.Cut(); Editor.TextArea.Focus(); }
    private void OnCopy(object? sender, RoutedEventArgs e) { Editor.Copy(); Editor.TextArea.Focus(); }
    private void OnPaste(object? sender, RoutedEventArgs e) { Editor.Paste(); Editor.TextArea.Focus(); }
    private void OnUndo(object? sender, RoutedEventArgs e) { Editor.Undo(); Editor.TextArea.Focus(); }
    private void OnRedo(object? sender, RoutedEventArgs e) { Editor.Redo(); Editor.TextArea.Focus(); }

    // ---- format ---------------------------------------------------------

    private void OnFormat(object? sender, RoutedEventArgs e) => FormatDocument();

    /// <summary>Reformat in place, keeping the caret line.</summary>
    public void FormatDocument()
    {
        int line = Editor.TextArea.Caret.Line;
        string formatted = CodeFormatter.Format(Editor.Text, Syntax, out string? error);
        if (error != null)
        {
            Status?.Invoke(this, error);
            return;
        }
        if (formatted == Editor.Text)
        {
            Status?.Invoke(this, "Already formatted.");
            return;
        }
        Editor.Document.Replace(0, Editor.Document.TextLength, formatted);
        Editor.ScrollToLine(Math.Min(line, Editor.Document.LineCount));
        Status?.Invoke(this, "Formatted.");
    }

    // ---- go to line ----------------------------------------------------

    private void OnGoToLine(object? sender, RoutedEventArgs e) => _ = GoToLineAsync();

    public async Task GoToLineAsync()
    {
        int count = Editor.Document.LineCount;
        Window? owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            return;
        }
        string? answer = await Dialogs.Prompt(owner, "Go to line", $"Line number (1-{count})",
            Editor.TextArea.Caret.Line.ToString());
        if (answer == null)
        {
            return;
        }
        if (!int.TryParse(answer.Trim(), out int line))
        {
            Status?.Invoke(this, $"\"{answer}\" is not a line number.");
            return;
        }
        line = Math.Clamp(line, 1, count);
        DocumentLine target = Editor.Document.GetLineByNumber(line);
        Editor.CaretOffset = target.Offset;
        Editor.ScrollToLine(line);
        Editor.Select(target.Offset, 0);
        Editor.TextArea.Focus();
    }

    // ---- find and replace ----------------------------------------------

    private void OnFind(object? sender, RoutedEventArgs e) => ShowFind(false);
    private void OnReplace(object? sender, RoutedEventArgs e) => ShowFind(true);

    /// <summary>Open the bar; <paramref name="withReplace"/> shows the replace row.</summary>
    public void ShowFind(bool withReplace)
    {
        FindBar.IsVisible = true;
        ReplaceLabel.IsVisible = withReplace;
        ReplaceBox.IsVisible = withReplace;
        ReplaceActions.IsVisible = withReplace;

        // Seed from the selection: searching for what you just highlighted is
        // almost always the intent.
        if (Editor.SelectionLength is > 0 and < 200)
        {
            FindBox.Text = Editor.SelectedText;
        }
        FindBox.Focus();
        FindBox.SelectAll();
        UpdateMatchCount();
    }

    private void OnCloseFind(object? sender, RoutedEventArgs e) => CloseFind();

    private void CloseFind()
    {
        if (!FindBar.IsVisible)
        {
            return;
        }
        FindBar.IsVisible = false;
        Editor.TextArea.Focus();
    }

    private void OnFindTextChanged(object? sender, TextChangedEventArgs e) => UpdateMatchCount();

    private void OnSearchOptionChanged(object? sender, RoutedEventArgs e) => UpdateMatchCount();

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext(forward: (e.KeyModifiers & KeyModifiers.Shift) == 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    private void OnFindNext(object? sender, RoutedEventArgs e) => FindNext(forward: true);
    private void OnFindPrevious(object? sender, RoutedEventArgs e) => FindNext(forward: false);

    /// <summary>
    /// Move to the next or previous match, wrapping. Reports and returns false
    /// when there is nothing to find.
    /// </summary>
    private bool FindNext(bool forward)
    {
        Regex? pattern = BuildPattern(out string? error);
        if (pattern == null)
        {
            if (error != null)
            {
                Status?.Invoke(this, error);
            }
            return false;
        }

        string text = Editor.Text;
        MatchCollection matches = pattern.Matches(text);
        if (matches.Count == 0)
        {
            Status?.Invoke(this, "No matches.");
            return false;
        }

        // Search from just past the caret forward, or from just before the
        // selection backward, then wrap.
        if (forward)
        {
            int from = Editor.SelectionLength > 0 ? Editor.SelectionStart + 1 : Editor.CaretOffset;
            foreach (Match m in matches)
            {
                if (m.Index >= from)
                {
                    Reveal(m);
                    return true;
                }
            }
            Reveal(matches[0]);
        }
        else
        {
            int before = Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset;
            Match? best = null;
            foreach (Match m in matches)
            {
                if (m.Index < before)
                {
                    best = m;
                }
            }
            Reveal(best ?? matches[^1]);
        }
        return true;
    }

    private void Reveal(Match match)
    {
        Editor.Select(match.Index, match.Length);
        DocumentLine line = Editor.Document.GetLineByOffset(match.Index);
        Editor.ScrollToLine(line.LineNumber);
        UpdateMatchCount();
    }

    private void OnReplaceOne(object? sender, RoutedEventArgs e)
    {
        Regex? pattern = BuildPattern(out _);
        if (pattern == null)
        {
            return;
        }
        // Replace only if the current selection is itself a match; otherwise
        // just move to the next one, so a stray click cannot overwrite text.
        if (Editor.SelectionLength > 0)
        {
            Match m = pattern.Match(Editor.SelectedText);
            if (m.Success && m.Index == 0 && m.Length == Editor.SelectedText.Length)
            {
                int at = Editor.SelectionStart;
                string replacement = UseRegex.IsChecked == true
                    ? m.Result(ReplaceBox.Text ?? "")
                    : ReplaceBox.Text ?? "";
                Editor.Document.Replace(at, Editor.SelectionLength, replacement);
                Editor.CaretOffset = at + replacement.Length;
            }
        }
        FindNext(forward: true);
    }

    private void OnReplaceAll(object? sender, RoutedEventArgs e)
    {
        Regex? pattern = BuildPattern(out string? error);
        if (pattern == null)
        {
            if (error != null)
            {
                Status?.Invoke(this, error);
            }
            return;
        }
        string original = Editor.Text;
        int count = pattern.Matches(original).Count;
        if (count == 0)
        {
            Status?.Invoke(this, "No matches.");
            return;
        }
        string with = ReplaceBox.Text ?? "";
        string replaced = UseRegex.IsChecked == true
            ? pattern.Replace(original, with)
            : pattern.Replace(original, with.Replace("$", "$$"));
        Editor.Document.Replace(0, original.Length, replaced);
        Status?.Invoke(this, $"Replaced {count} occurrence(s).");
        UpdateMatchCount();
    }

    private Regex? BuildPattern(out string? error)
    {
        error = null;
        string needle = FindBox.Text ?? "";
        if (needle.Length == 0)
        {
            return null;
        }
        RegexOptions options = MatchCase.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
        try
        {
            return new Regex(UseRegex.IsChecked == true ? needle : Regex.Escape(needle), options);
        }
        catch (ArgumentException ex)
        {
            error = "Bad pattern: " + ex.Message;
            return null;
        }
    }

    private void UpdateMatchCount()
    {
        Regex? pattern = BuildPattern(out _);
        if (pattern == null)
        {
            MatchCount.Text = "";
            return;
        }
        int count = pattern.Matches(Editor.Text).Count;
        MatchCount.Text = count switch
        {
            0 => "none",
            1 => "1 match",
            _ => $"{count} matches",
        };
    }

    /// <summary>Minimal ICommand so a gesture can call a method.</summary>
    private sealed class Relay : System.Windows.Input.ICommand
    {
        private readonly Action _run;

        public Relay(Action run) => _run = run;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _run();
    }
}
