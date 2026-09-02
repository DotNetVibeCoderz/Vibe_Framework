using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
// Avalonia.Controls.Shapes.Path would shadow System.IO.Path, which this file
// uses far more often; only the one shape is needed.
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RustNet.Designer.Assistant;
using RustNet.Designer.Deployment;
using RustNet.UI;

namespace RustNet.Designer.Avalonia;

public partial class MainWindow : Window, IDesignerBridge
{
    private UiElement _root = Ui.LoadXml(SampleLayout.Xml);
    private UiElement? _selected;
    private Dictionary<Control, UiElement> _map = new();

    // Two documents live in this window: the layout on the canvas, and the code
    // in the code pane. Every file command acts on whichever tab is in front.
    private string? _layoutFile;
    private bool _layoutDirty;
    private string? _codeFile;

    // Footer widgets built in code (see BuildFooters).
    private TextBlock _xmlHint = null!;
    private TextBlock _codeFileName = null!;

    private readonly Dictionary<ColumnDefinition, GridLength> _panelWidths = new();

    // The pane columns, by position in the grid. `x:Name` on a
    // ColumnDefinition compiles to nothing — the generated fields cover
    // controls only — so they are read off the grid rather than named.
    private ColumnDefinition ToolboxColumn => Panes.ColumnDefinitions[0];
    private ColumnDefinition ToolboxSplitterColumn => Panes.ColumnDefinitions[1];
    private ColumnDefinition InspectorSplitterColumn => Panes.ColumnDefinitions[3];
    private ColumnDefinition InspectorColumn => Panes.ColumnDefinitions[4];
    private ColumnDefinition ChatSplitterColumn => Panes.ColumnDefinitions[5];
    private ColumnDefinition ChatColumn => Panes.ColumnDefinitions[6];
    private readonly List<DeviceTarget> _targets = new();
    private DeviceTarget? _target;
    private CancellationTokenSource? _run;
    private string _workspaceRoot = "";
    private string _signingKey = "";

    public MainWindow()
    {
        InitializeComponent();
        BuildToolbox();
        BuildFooters();
        _selected = _root;

        CentreTabs.SelectionChanged += OnCentreTabChanged;
        XmlPane.Status += (_, message) => Status(message);
        CodePane.Status += (_, message) => Status(message);
        XmlPane.TextEdited += (_, _) => UpdateTabHeaders();
        CodePane.TextEdited += (_, _) => UpdateTabHeaders();

        Chat.Status += (_, message) => Status(message);
        Chat.HideRequested += (_, _) => AssistantToggle.IsChecked = false;
        Chat.Initialize(this);

        ResolvePaths();
        SeedTargets();
        RenderAll();
        UpdateTabHeaders();
        UpdateRunButton();
        SetZoom(_zoom);

        BindGestures();
    }

    private void BindGestures()
    {
        void Bind(Action action, Key key, KeyModifiers modifiers = KeyModifiers.None)
            => KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(key, modifiers),
                Command = new RelayCommand(action),
            });

        Bind(() => OnNew(this, null!), Key.N, KeyModifiers.Control);
        Bind(() => OnOpen(this, null!), Key.O, KeyModifiers.Control);
        Bind(() => OnSave(this, null!), Key.S, KeyModifiers.Control);
        Bind(() => OnClose(this, null!), Key.W, KeyModifiers.Control);
        Bind(() => OnRun(this, null!), Key.F5);
        Bind(() => Toggle(ToolboxToggle), Key.D1, KeyModifiers.Control);
        Bind(() => Toggle(InspectorToggle), Key.D2, KeyModifiers.Control);
        Bind(() => Toggle(OutputToggle), Key.D3, KeyModifiers.Control);
        Bind(() => Toggle(AssistantToggle), Key.J, KeyModifiers.Control);
        Bind(() => SetZoom(_zoom + 1), Key.OemPlus, KeyModifiers.Control);
        Bind(() => SetZoom(_zoom - 1), Key.OemMinus, KeyModifiers.Control);

        KeyDown += (_, e) =>
        {
            // Del deletes the selected element — but not while the person is
            // typing in a property box, the composer or an editor. WPF asked
            // Keyboard.FocusedElement; Avalonia keeps focus on the tree, so
            // this asks the focused control what it is.
            if (e.Key == Key.Delete && !FocusIsInTextEntry())
            {
                OnDelete(this, null!);
            }
        };
    }

    /// <summary>Whether the keyboard focus is somewhere that eats a Delete.</summary>
    private bool FocusIsInTextEntry()
    {
        object? focused = FocusManager?.GetFocusedElement();
        return focused is TextBox
            || focused is AvaloniaEdit.Editing.TextArea
            || focused is AvaloniaEdit.TextEditor;
    }

    /// <summary>
    /// Where the checkout and the signing key are. Both come from the
    /// assistant's settings so there is one place to configure them.
    /// </summary>
    private void ResolvePaths()
    {
        AssistantOptions options = AssistantOptions.Load();
        _workspaceRoot = options.WorkspaceRoot;
        _signingKey = Path.Combine(_workspaceRoot, "keys", "rustnet-signing.key");
    }

    // ---- toolbox -----------------------------------------------------

    /// <summary>
    /// The palette, grouped by role, containers first.
    /// </summary>
    /// <remarks>
    /// Thirty-two kinds in one alphabetical column is a list you read rather
    /// than a palette you reach into, and the first group carries information
    /// the others do not: a new control lands *inside the selected container*,
    /// so which kinds are containers decides where the next click puts things.
    /// The grouping lives in <see cref="DesignModel.Palette"/> so both
    /// front-ends offer the same palette in the same order.
    /// </remarks>
    private void BuildToolbox()
    {
        bool first = true;
        foreach ((string name, string[] kinds) in DesignModel.Palette)
        {
            var heading = new TextBlock
            {
                Text = name,
                Margin = new Thickness(12, first ? 2 : 12, 12, 5),
            };
            heading.Classes.Add("heading");
            Toolbox.Children.Add(heading);
            first = false;

            foreach (string kind in kinds)
            {
                var btn = new Button { Content = kind, Tag = kind };
                btn.Classes.Add("tool");
                ToolTip.SetTip(btn, $"Add a <{kind}> to the selected container");
                btn.Click += OnAddControl;
                Toolbox.Children.Add(btn);
            }
        }
    }

    /// <summary>
    /// The tab-specific actions along the bottom of each editor pane. Built here
    /// rather than in markup because a UserControl owns its own name scope.
    /// </summary>
    private void BuildFooters()
    {
        _xmlHint = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center };
        _xmlHint.Classes.Add("readout");
        var xmlFooter = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal };
        xmlFooter.Children.Add(Action("Apply to canvas", "primary", OnApplyXml,
            "Parse this XML and replace the design"));
        xmlFooter.Children.Add(Action("Reload from canvas", null, OnReloadXml,
            "Throw away the edits here and re-read the canvas"));
        xmlFooter.Children.Add(_xmlHint);
        XmlPane.FooterContent = xmlFooter;

        _codeFileName = new TextBlock
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Text = "(nothing generated yet)",
        };
        _codeFileName.Classes.Add("readout");
        var codeFooter = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal };
        codeFooter.Children.Add(Action("Send to assistant", null, OnSendCodeToAssistant,
            "Ask Jack to review or extend what is in this pane"));
        codeFooter.Children.Add(_codeFileName);
        CodePane.FooterContent = codeFooter;

        Button Action(string text, string? extraClass, EventHandler<RoutedEventArgs> click, string tip)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 4, 0) };
            button.Classes.Add("flat");
            if (extraClass is not null)
            {
                button.Classes.Add(extraClass);
            }
            ToolTip.SetTip(button, tip);
            button.Click += click;
            return button;
        }
    }

    private void OnAddControl(object? sender, RoutedEventArgs e)
    {
        string kind = (string)((Button)sender!).Tag!;
        UiElement child = DesignModel.MakeDefault(kind);

        // Add into the selected container, else its parent, else the root.
        UiElement parent = _selected ?? _root;
        if (!DesignModel.IsContainer(parent))
        {
            parent = DesignModel.FindParent(_root, parent) ?? _root;
        }
        parent.Children.Add(child);
        _selected = child;
        TouchLayout();
        RenderAll();
        Status($"Added {kind}");
    }

    // ---- rendering ---------------------------------------------------

    private void RenderAll()
    {
        _map = DesignRenderer.Render(DesignCanvas, _root);
        DrawSelectionAdorner();
        BuildTree();
        BuildProperties();
        UpdateReadout();
        if (ReferenceEquals(CentreTabs.SelectedItem, XmlTab))
        {
            ReloadXmlPane();
        }
    }

    private void DrawSelectionAdorner()
    {
        if (_selected == null)
        {
            return;
        }
        var box = new Rectangle
        {
            Width = Math.Max(2, _selected.LayoutW),
            Height = Math.Max(2, _selected.LayoutH),
            Stroke = Brush("Amber"),
            StrokeThickness = 1,
            StrokeDashArray = new global::Avalonia.Collections.AvaloniaList<double> { 3, 2 },
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(box, _selected.LayoutX);
        Canvas.SetTop(box, _selected.LayoutY);
        DesignCanvas.Children.Add(box);
    }

    /// <summary>
    /// The instrument line: what is selected, where it landed and in which
    /// colours — the numbers you would otherwise hunt for in the property grid.
    /// </summary>
    private void UpdateReadout()
    {
        (int w, int h) = GetPanelSize();
        PanelReadout.Text = $"PANEL {w}x{h}";

        if (_selected == null)
        {
            SelReadout.Text = "-";
            PosReadout.Text = SizeReadout.Text = FgReadout.Text = BgReadout.Text = "-";
            return;
        }
        SelReadout.Text = _selected.Id.Length > 0 ? $"{_selected.Kind} #{_selected.Id}" : _selected.Kind;
        PosReadout.Text = $"{_selected.LayoutX},{_selected.LayoutY}";
        SizeReadout.Text = $"{_selected.LayoutW}x{_selected.LayoutH}";
        FgReadout.Text = DesignModel.Hex(_selected.Foreground);
        BgReadout.Text = DesignModel.Hex(_selected.Background);
    }

    // ---- zoom --------------------------------------------------------

    /// <summary>
    /// How many screen pixels one device pixel occupies.
    /// </summary>
    /// <remarks>
    /// Whole numbers only, and that is a property of the subject rather than a
    /// simplification: this preview claims to show what the panel will show,
    /// and a fractional scale resamples a pixel-exact image into something the
    /// device cannot produce. A 160x128 panel at 1:1 is a postage stamp on a
    /// desktop display, so the default is 2.
    /// </remarks>
    private int _zoom = 2;

    private const int MinZoom = 1;
    private const int MaxZoom = 6;

    private void OnZoomIn(object? sender, RoutedEventArgs e) => SetZoom(_zoom + 1);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => SetZoom(_zoom - 1);

    private void SetZoom(int zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        // Reached through the Bezel rather than by name: x:Name generates a
        // field for controls only, and a transform is not one — the same trap
        // ColumnDefinition sets, and it compiles just as cleanly.
        if (Bezel.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = _zoom;
            scale.ScaleY = _zoom;
        }
        ZoomReadout.Text = _zoom + "x";
    }

    // ---- selection + drag-to-move -----------------------------------

    private bool _dragging;
    private Point _dragLast;
    private bool _dragMoved;

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(DesignCanvas).Properties.IsLeftButtonPressed)
        {
            return;
        }
        // Relative to the canvas, so the bezel's scale is already divided out:
        // this is device pixels, which is what the model's hit test expects.
        Point p = e.GetPosition(DesignCanvas);
        // The model's own hit-test picks the topmost element at the point.
        UiElement? hit = _root.HitTest((int)p.X, (int)p.Y);
        _selected = hit ?? _root;

        // Begin a drag if the picked element lives in a canvas (freely moved).
        if (_selected != null && DragTool.CanMove(_root, _selected))
        {
            _dragging = true;
            _dragMoved = false;
            _dragLast = p;
            e.Pointer.Capture(DesignCanvas);
        }
        RenderAll();
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _selected == null)
        {
            return;
        }
        Point p = e.GetPosition(DesignCanvas);
        int dx = (int)(p.X - _dragLast.X);
        int dy = (int)(p.Y - _dragLast.Y);
        if (DragTool.MoveBy(_root, _selected, dx, dy))
        {
            _dragLast = p;
            _dragMoved = true;
            TouchLayout();
            RenderAll();
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            if (_dragMoved && _selected != null)
            {
                Status($"Moved {_selected.Kind} to ({_selected.X}, {_selected.Y})");
            }
        }
    }

    // ---- tree --------------------------------------------------------

    private void BuildTree()
    {
        _syncingTree = true;
        Tree.Items.Clear();
        Tree.Items.Add(BuildTreeNode(_root));
        _syncingTree = false;
    }

    private TreeViewItem BuildTreeNode(UiElement e)
    {
        var item = new TreeViewItem
        {
            Header = e.Id.Length > 0 ? $"{e.Kind}  #{e.Id}" : e.Kind,
            Tag = e,
            IsExpanded = true,
        };
        if (e == _selected)
        {
            item.IsSelected = true;
        }
        for (int i = 0; i < e.Children.Count; i++)
        {
            item.Items.Add(BuildTreeNode(e.Children[i]));
        }
        return item;
    }

    private bool _syncingTree;

    private void OnTreeSelect(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTree)
        {
            return;
        }
        if (Tree.SelectedItem is TreeViewItem item && item.Tag is UiElement el)
        {
            _selected = el;
            _syncingTree = true;
            _map = DesignRenderer.Render(DesignCanvas, _root);
            DrawSelectionAdorner();
            BuildProperties();
            UpdateReadout();
            _syncingTree = false;
        }
    }

    // ---- property grid ----------------------------------------------

    private void BuildProperties()
    {
        Properties.Children.Clear();
        if (_selected == null)
        {
            return;
        }
        UiElement s = _selected;

        AddPropRow("kind", s.Kind, v => { }, readOnly: true);
        AddPropRow("id", s.Id, v => s.Id = v);
        AddPropRow("text", s.Text, v => s.Text = v);
        AddIntRow("x", s.X, v => s.X = v);
        AddIntRow("y", s.Y, v => s.Y = v);
        AddIntRow("width", s.Width, v => s.Width = v);
        AddIntRow("height", s.Height, v => s.Height = v);
        AddIntRow("scale", s.Scale, v => s.Scale = v);
        AddColorRow("fg", s.Foreground, v => s.Foreground = v);
        AddColorRow("bg", s.Background, v => s.Background = v);
        AddColorRow("border", s.Border, v => s.Border = v);

        if (s.Kind == "slider" || s.Kind == "progress")
        {
            AddIntRow("min", s.Min, v => s.Min = v);
            AddIntRow("max", s.Max, v => s.Max = v);
            AddIntRow("value", s.Value, v => s.Value = v);
        }
        if (s.Kind == "checkbox" || s.Kind == "radio")
        {
            AddBoolRow("checked", s.Checked, v => s.Checked = v);
        }
        if (s.Kind == "radio")
        {
            AddPropRow("group", s.Group, v => s.Group = v);
        }
        if (s.Kind == "grid")
        {
            AddIntRow("columns", s.Columns, v => s.Columns = v);
        }
        if (DesignModel.IsContainer(s))
        {
            AddIntRow("pad", s.Padding, v => s.Padding = v);
            AddIntRow("gap", s.Gap, v => s.Gap = v);
            AddBoolRow("horizontal", s.Horizontal, v => s.Horizontal = v);
        }
        if (s.Kind == "listbox")
        {
            AddPropRow("items (; sep)", string.Join(";", s.Items), v =>
            {
                s.Items.Clear();
                foreach (string part in v.Split(';'))
                {
                    s.Items.Add(part);
                }
            });
            AddIntRow("selected", s.Selected, v => s.Selected = v);
        }
    }

    private TextBlock RowLabel(string label)
    {
        var t = new TextBlock
        {
            Text = label,
            Width = 84,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        t.Classes.Add("tag");
        return t;
    }

    /// <summary>A value box: mono, because every one of these is a number or
    /// an identifier the device will read.</summary>
    private static TextBox ValueBox(string text, bool readOnly = false)
    {
        var box = new TextBox { Text = text, IsReadOnly = readOnly };
        box.Classes.Add("value");
        return box;
    }

    private void AddPropRow(string label, string value, Action<string> set, bool readOnly = false)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        panel.Children.Add(RowLabel(label));
        TextBox box = ValueBox(value, readOnly);
        if (!readOnly)
        {
            box.LostFocus += (_, _) => { set(box.Text ?? ""); TouchLayout(); RenderAll(); };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    set(box.Text ?? "");
                    TouchLayout();
                    RenderAll();
                }
            };
        }
        panel.Children.Add(box);
        Properties.Children.Add(panel);
    }

    private void AddIntRow(string label, int value, Action<int> set)
    {
        AddPropRow(label, value.ToString(), v =>
        {
            if (int.TryParse(v, out int n))
            {
                set(n);
            }
        });
    }

    private void AddColorRow(string label, int value, Action<int> set)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        panel.Children.Add(RowLabel(label));
        // A swatch beside the hex: RGB565 is unreadable as four digits, and the
        // quantised colour is what the panel will actually show.
        var swatch = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            BorderBrush = Brush("Rail"),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(DesignRenderer.FromRgb565(value)),
            Margin = new Thickness(0, 0, 4, 0),
        };
        DockPanel.SetDock(swatch, Dock.Right);
        panel.Children.Add(swatch);

        TextBox box = ValueBox(DesignModel.Hex(value));
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
            }
        };
        panel.Children.Add(box);
        Properties.Children.Add(panel);

        void Commit()
        {
            set(DesignModel.ParseHex(box.Text ?? ""));
            TouchLayout();
            RenderAll();
        }
    }

    private void AddBoolRow(string label, bool value, Action<bool> set)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 1, 0, 3) };
        panel.Children.Add(RowLabel(label));
        var check = new CheckBox
        {
            IsChecked = value,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        check.IsCheckedChanged += (_, _) =>
        {
            set(check.IsChecked == true);
            TouchLayout();
            RenderAll();
        };
        panel.Children.Add(check);
        Properties.Children.Add(panel);
    }

    // ---- documents ---------------------------------------------------

    /// <summary>True when the code tab is in front; otherwise the layout is the active document.</summary>
    private bool CodeIsActive => ReferenceEquals(CentreTabs.SelectedItem, CodeTab);

    private void TouchLayout()
    {
        _layoutDirty = true;
        UpdateTabHeaders();
    }

    private void UpdateTabHeaders()
    {
        string layoutName = _layoutFile == null ? "LAYOUT XML" : Path.GetFileName(_layoutFile).ToUpperInvariant();
        XmlTab.Header = layoutName + (_layoutDirty || XmlPane.Dirty ? " *" : "");
        string codeName = _codeFile == null ? "CODE" : Path.GetFileName(_codeFile).ToUpperInvariant();
        CodeTab.Header = codeName + (CodePane.Dirty ? " *" : "");
    }

    private async void OnNew(object? sender, RoutedEventArgs e)
    {
        if (CodeIsActive)
        {
            if (!await ConfirmDiscard(CodePane.Dirty, "the code"))
            {
                return;
            }
            CodePane.Text = "";
            _codeFile = null;
            _codeFileName.Text = "(new file)";
            Status("New C# file");
        }
        else
        {
            if (!await ConfirmDiscard(_layoutDirty, "the layout"))
            {
                return;
            }
            _root = Ui.LoadXml("<window width=\"320\" height=\"240\" bg=\"0000\" pad=\"8\" gap=\"6\"/>");
            _selected = _root;
            _layoutFile = null;
            _layoutDirty = false;
            RenderAll();
            Status("New layout");
        }
        UpdateTabHeaders();
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a layout or a C# file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Layout or code") { Patterns = ["*.xml", "*.cs"] },
                new FilePickerFileType("RustNet UI layout") { Patterns = ["*.xml"] },
                new FilePickerFileType("C# source") { Patterns = ["*.cs"] },
                FilePickerFileTypes.All,
            ],
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            await OpenFileAsync(path);
        }
    }

    /// <summary>Open a layout or a C# file; the extension decides which pane it lands in.</summary>
    public void OpenFile(string path) => _ = OpenFileAsync(path);

    private async Task OpenFileAsync(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (!await ConfirmDiscard(CodePane.Dirty, "the code"))
                {
                    return;
                }
                CodePane.Text = File.ReadAllText(path);
                CodePane.Syntax = "csharp";
                _codeFile = path;
                _codeFileName.Text = Path.GetFileName(path);
                CentreTabs.SelectedItem = CodeTab;
                Status("Opened " + path);
            }
            else
            {
                if (!await ConfirmDiscard(_layoutDirty, "the layout"))
                {
                    return;
                }
                _root = Ui.LoadXml(File.ReadAllText(path));
                _selected = _root;
                _layoutFile = path;
                _layoutDirty = false;
                CentreTabs.SelectedItem = DesignTab;
                RenderAll();
                Status("Opened " + path);
            }
            UpdateTabHeaders();
        }
        catch (Exception ex)
        {
            await Dialogs.Message(this, "RustNet UI Designer", "Could not open: " + ex.Message);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (CodeIsActive)
        {
            if (_codeFile == null)
            {
                OnSaveAs(sender, e);
                return;
            }
            File.WriteAllText(_codeFile, CodePane.Text);
            CodePane.MarkClean();
            Status("Saved " + _codeFile);
        }
        else
        {
            if (_layoutFile == null)
            {
                OnSaveAs(sender, e);
                return;
            }
            File.WriteAllText(_layoutFile, CurrentLayoutXml());
            _layoutDirty = false;
            XmlPane.MarkClean();
            Status("Saved " + _layoutFile);
        }
        UpdateTabHeaders();
        await Task.CompletedTask;
    }

    private async void OnSaveAs(object? sender, RoutedEventArgs e)
    {
        bool code = CodeIsActive;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = code ? "Save C# source" : "Save RustNet UI layout",
            SuggestedFileName = code
                ? (_codeFile != null ? Path.GetFileName(_codeFile) : "Program.cs")
                : (_layoutFile != null ? Path.GetFileName(_layoutFile) : "ui.xml"),
            DefaultExtension = code ? "cs" : "xml",
            FileTypeChoices = code
                ? [new FilePickerFileType("C# source") { Patterns = ["*.cs"] }, FilePickerFileTypes.All]
                : [new FilePickerFileType("RustNet UI layout") { Patterns = ["*.xml"] }, FilePickerFileTypes.All],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        if (code)
        {
            _codeFile = path;
            File.WriteAllText(path, CodePane.Text);
            CodePane.MarkClean();
            _codeFileName.Text = Path.GetFileName(path);
        }
        else
        {
            _layoutFile = path;
            File.WriteAllText(path, CurrentLayoutXml());
            _layoutDirty = false;
            XmlPane.MarkClean();
        }
        Status("Saved " + path);
        UpdateTabHeaders();
    }

    private async void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CodeIsActive)
        {
            if (!await ConfirmDiscard(CodePane.Dirty, "the code"))
            {
                return;
            }
            CodePane.Text = "";
            _codeFile = null;
            _codeFileName.Text = "(nothing generated yet)";
            Status("Closed the code file");
        }
        else
        {
            if (!await ConfirmDiscard(_layoutDirty, "the layout"))
            {
                return;
            }
            _root = Ui.LoadXml("<window width=\"320\" height=\"240\" bg=\"0000\" pad=\"8\" gap=\"6\"/>");
            _selected = _root;
            _layoutFile = null;
            _layoutDirty = false;
            RenderAll();
            Status("Closed the layout");
        }
        UpdateTabHeaders();
    }

    /// <summary>
    /// The layout as text. When the XML pane has unapplied edits its text is
    /// what the person means by "the layout", so it wins over the tree.
    /// </summary>
    private string CurrentLayoutXml()
        => XmlPane.Dirty && XmlPane.Text.Trim().Length > 0 ? XmlPane.Text : Ui.ToXml(_root);

    private async Task<bool> ConfirmDiscard(bool dirty, string what)
        => !dirty || await Dialogs.Confirm(this, "RustNet UI Designer",
            $"Discard unsaved changes to {what}?");

    // ---- edit --------------------------------------------------------

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_selected == null || _selected == _root)
        {
            return;
        }
        UiElement? parent = DesignModel.FindParent(_root, _selected);
        if (parent != null)
        {
            parent.Children.Remove(_selected);
            _selected = parent;
            TouchLayout();
            RenderAll();
            Status("Deleted");
        }
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object? sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        if (_selected == null)
        {
            return;
        }
        UiElement? parent = DesignModel.FindParent(_root, _selected);
        if (parent == null)
        {
            return;
        }
        int i = parent.Children.IndexOf(_selected);
        int j = i + delta;
        if (j >= 0 && j < parent.Children.Count)
        {
            parent.Children.RemoveAt(i);
            parent.Children.Insert(j, _selected);
            TouchLayout();
            RenderAll();
        }
    }

    // ---- centre pane: XML and code ----------------------------------

    private void OnCentreTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CentreTabs))
        {
            return;
        }
        if (ReferenceEquals(CentreTabs.SelectedItem, XmlTab) && !XmlPane.Dirty)
        {
            ReloadXmlPane();
        }
        UpdateRunButton();
    }

    private void ReloadXmlPane()
    {
        XmlPane.Text = Ui.ToXml(_root);
        _xmlHint.Text = "edit here, then apply";
    }

    private void OnReloadXml(object? sender, RoutedEventArgs e)
    {
        ReloadXmlPane();
        UpdateTabHeaders();
        Status("Layout XML reloaded from the canvas");
    }

    private async void OnApplyXml(object? sender, RoutedEventArgs e)
    {
        try
        {
            ApplyLayoutXml(XmlPane.Text);
            XmlPane.MarkClean();
            _xmlHint.Text = "applied";
            Status("Layout applied from the XML pane");
        }
        catch (Exception ex)
        {
            _xmlHint.Text = "not applied";
            await Dialogs.Message(this, "Apply to canvas", "That XML did not parse:\n\n" + ex.Message);
        }
    }

    private void OnSendCodeToAssistant(object? sender, RoutedEventArgs e)
    {
        if (CodePane.Text.Trim().Length == 0)
        {
            Status("The code pane is empty.");
            return;
        }
        AssistantToggle.IsChecked = true;
        Chat.Compose("Review the code in my code pane (read it with get_generated_code) and tell me what "
            + "would break on the device. Then put a corrected version back with set_generated_code.");
    }

    // ---- panels ------------------------------------------------------

    private static void Toggle(ToggleButton button) => button.IsChecked = button.IsChecked != true;

    private void OnPanelToggled(object? sender, RoutedEventArgs e)
    {
        // The toggles start checked in markup, so this fires while the rest of
        // the tree is still being built. The initial state already matches.
        if (Panes == null || Chat == null || OutputPane == null)
        {
            return;
        }

        SetColumn(ToolboxColumn, ToolboxSplitterColumn, ToolboxPanel, ToolboxSplitter,
            ToolboxToggle.IsChecked == true, 172);
        SetColumn(InspectorColumn, InspectorSplitterColumn, InspectorPanel, InspectorSplitter,
            InspectorToggle.IsChecked == true, 292);
        SetColumn(ChatColumn, ChatSplitterColumn, Chat, ChatSplitter,
            AssistantToggle.IsChecked == true, 420);

        OutputPane.IsVisible = OutputToggle.IsChecked == true;

        if (AssistantToggle.IsChecked == true)
        {
            Chat.FocusComposer();
        }
    }

    /// <summary>
    /// Collapse or restore one panel column. The width is remembered so hiding
    /// and showing does not resize the panel the person set.
    /// </summary>
    private void SetColumn(ColumnDefinition column, ColumnDefinition splitterColumn,
        Control panel, Control splitter, bool show, double fallback)
    {
        if (!show && column.Width.Value > 0)
        {
            _panelWidths[column] = column.Width;
        }
        GridLength width = show
            ? _panelWidths.TryGetValue(column, out GridLength remembered) ? remembered : new GridLength(fallback)
            : new GridLength(0);

        column.Width = width;
        splitterColumn.Width = new GridLength(show ? 1 : 0);
        panel.IsVisible = show;
        splitter.IsVisible = show;
    }

    // ---- output ------------------------------------------------------

    /// <summary>Append a line to the output pane, opening it on first use.</summary>
    private void Output(string line) => Dispatcher.UIThread.Post(() =>
    {
        if (OutputToggle.IsChecked != true)
        {
            OutputToggle.IsChecked = true;
        }
        OutputText.Text += line + Environment.NewLine;
        // Avalonia's TextBox has no ScrollToEnd; putting the caret at the end
        // scrolls it there, which is the same thing the reader wants.
        OutputText.CaretIndex = OutputText.Text?.Length ?? 0;
    });

    private void OnClearOutput(object? sender, RoutedEventArgs e) => OutputText.Text = "";

    private async void OnCopyOutput(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(OutputText.Text) && Clipboard is not null)
        {
            await Clipboard.SetTextAsync(OutputText.Text);
            Status("Output copied");
        }
    }

    private void OnCloseOutput(object? sender, RoutedEventArgs e) => OutputToggle.IsChecked = false;

    // ---- devices and deployment --------------------------------------

    /// <summary>
    /// Offer the candidates before anything has been probed, so Run works
    /// immediately against the virtual device.
    /// </summary>
    private void SeedTargets()
    {
        _targets.Clear();
        foreach (string spec in DeviceDiscovery.Candidates())
        {
            string label = spec == DeviceDiscovery.VirtualDeviceSpec
                ? "virtual device - not probed"
                : spec.Replace("serial:", "") + " - not probed";
            _targets.Add(new DeviceTarget(spec, label));
        }
        RebuildTargetBox(_targets[0]);
    }

    private void RebuildTargetBox(DeviceTarget? select)
    {
        TargetBox.ItemsSource = null;
        TargetBox.ItemsSource = _targets;
        TargetBox.SelectedItem = select ?? (_targets.Count > 0 ? _targets[0] : null);
    }

    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        _target = TargetBox.SelectedItem as DeviceTarget;
        DeployState.Text = _target == null
            ? ""
            : _target.Answered ? $"chip {_target.Chip}" : "not probed";
    }

    private async void OnDetectDevices(object? sender, RoutedEventArgs e)
    {
        Status("Probing devices...");
        Output("--- detecting devices ---");
        try
        {
            List<DeviceTarget> found = await DeviceDiscovery.ScanAsync(Output, CancellationToken.None);
            if (found.Count > 0)
            {
                _targets.Clear();
                _targets.AddRange(found);
                // Keep the unprobed candidates too: a board can be plugged in
                // after a scan, and re-scanning for it should not be the only way.
                foreach (string spec in DeviceDiscovery.Candidates())
                {
                    if (!found.Exists(t => t.Spec == spec))
                    {
                        _targets.Add(new DeviceTarget(spec, spec.Replace("serial:", "") + " - no answer"));
                    }
                }
                RebuildTargetBox(found[0]);
                Status($"{found.Count} device(s) answered.");
            }
            else
            {
                Status("No device answered.");
            }
        }
        catch (Exception ex)
        {
            Output("detect failed: " + ex.Message);
            Status("Detect failed.");
        }
    }

    private void UpdateRunButton()
    {
        bool code = CodeIsActive;
        RunButton.Content = code ? "Run" : "Push layout";
        ToolTip.SetTip(RunButton, code
            ? "Build the code pane, sign it and flash it to the target, then start it (F5)"
            : $"Push the layout to {Deployer.DefaultLayoutPath} on the target (F5)");
    }

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (_run != null)
        {
            return;
        }
        if (_target == null)
        {
            Status("Pick a target first.");
            return;
        }

        _run = new CancellationTokenSource();
        RunButton.IsVisible = false;
        CancelRunButton.IsVisible = true;
        var deployer = new Deployer(Output);
        try
        {
            Deployer.Result result = CodeIsActive
                ? await deployer.DeployCodeAsync(
                    CodePane.Text, AppNameBox.Text ?? "", _target, _signingKey, _workspaceRoot,
                    start: true, _run.Token)
                : await deployer.PushLayoutAsync(
                    CurrentLayoutXml(), Deployer.DefaultLayoutPath, _target, _run.Token);
            Status(result.Summary);
        }
        catch (OperationCanceledException)
        {
            Output("cancelled");
            Status("Cancelled.");
        }
        catch (Exception ex)
        {
            Output("failed: " + ex.Message);
            Status("Run failed: " + ex.Message);
        }
        finally
        {
            _run.Dispose();
            _run = null;
            RunButton.IsVisible = true;
            CancelRunButton.IsVisible = false;
        }
    }

    private void OnCancelRun(object? sender, RoutedEventArgs e) => _run?.Cancel();

    private async void OnStopApp(object? sender, RoutedEventArgs e)
    {
        if (_target == null)
        {
            return;
        }
        Deployer.Result result = await new Deployer(Output).StopAsync(_target, CancellationToken.None);
        Status(result.Summary);
    }

    private async void OnReadLogs(object? sender, RoutedEventArgs e)
    {
        if (_target == null)
        {
            return;
        }
        Output($"--- device log ({_target.Spec}) ---");
        Deployer.Result result = await new Deployer(Output).ReadLogsAsync(_target, 100, CancellationToken.None);
        Status(result.Summary);
    }

    // ---- IDesignerBridge --------------------------------------------

    public string GetLayoutXml() => Dispatcher.UIThread.Invoke(() => Ui.ToXml(_root));

    public void ApplyLayoutXml(string xml)
    {
        // Parse before touching the canvas so a bad document leaves the design
        // alone and the caller gets the parser's message.
        UiElement parsed = Ui.LoadXml(xml);
        Dispatcher.UIThread.Invoke(() =>
        {
            _root = parsed;
            _selected = _root;
            _layoutDirty = true;
            CentreTabs.SelectedItem = DesignTab;
            RenderAll();
            UpdateTabHeaders();
        });
    }

    public (int Width, int Height) GetPanelSize() => Dispatcher.UIThread.Invoke(() =>
        (_root.Width > 0 ? _root.Width : 160, _root.Height > 0 ? _root.Height : 128));

    public string DescribeSelection() => Dispatcher.UIThread.Invoke(() =>
    {
        if (_selected == null)
        {
            return "none";
        }
        string name = _selected.Id.Length > 0 ? $"{_selected.Kind} #{_selected.Id}" : _selected.Kind;
        return $"{name} at {_selected.LayoutX},{_selected.LayoutY} sized {_selected.LayoutW}x{_selected.LayoutH}";
    });

    public void SetGeneratedCode(string fileName, string language, string code) => Dispatcher.UIThread.Invoke(() =>
    {
        CodePane.Syntax = language.Length > 0 ? language : "csharp";
        CodePane.Text = code;
        _codeFile = null;
        _codeFileName.Text = $"{fileName} - {code.Split('\n').Length} lines - unsaved";
        CentreTabs.SelectedItem = CodeTab;
        UpdateTabHeaders();
        Status("Generated " + fileName);
    });

    public string GetGeneratedCode() => Dispatcher.UIThread.Invoke(() => CodePane.Text);

    // ---- helpers -----------------------------------------------------

    private void Status(string msg) => Dispatcher.UIThread.Post(() => StatusText.Text = msg);

    /// <summary>A brush from the application theme, by resource key.</summary>
    private IBrush Brush(string key)
        => this.TryFindResource(key, out object? value) && value is IBrush brush
            ? brush
            : Brushes.Gray;

    /// <summary>Minimal ICommand so a keyboard gesture can call a method.</summary>
    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _run;

        public RelayCommand(Action run) => _run = run;

        // Always executable, so there is nothing to notify about.
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _run();
    }
}
