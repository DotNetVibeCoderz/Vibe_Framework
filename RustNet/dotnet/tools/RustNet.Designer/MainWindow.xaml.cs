using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using RustNet.UI;

namespace RustNet.Designer;

public partial class MainWindow : Window
{
    private UiElement _root = Ui.LoadXml(App.SampleXml);
    private UiElement? _selected;
    private string? _currentFile;
    private Dictionary<FrameworkElement, UiElement> _map = new();

    // The palette shown in the toolbox.
    private static readonly string[] ControlKinds =
    {
        "stack", "panel", "border", "canvas", "grid", "scrollviewer",
        "label", "button", "textbox", "checkbox", "radio",
        "slider", "progress", "listbox", "image", "rect",
    };

    public MainWindow()
    {
        InitializeComponent();
        BuildToolbox();
        _selected = _root;
        RenderAll();

        CommandBindings.Add(new CommandBinding(ApplicationCommands.New, (_, _) => OnNew(this, null!)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => OnOpen(this, null!)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (_, _) => OnSave(this, null!)));
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete)
            {
                OnDelete(this, null!);
            }
        };
    }

    // ---- toolbox -----------------------------------------------------

    private void BuildToolbox()
    {
        foreach (string kind in ControlKinds)
        {
            var btn = new Button
            {
                Content = "+ " + kind,
                Style = (Style)Resources["ToolButton"],
                Tag = kind,
            };
            btn.Click += OnAddControl;
            Toolbox.Children.Add(btn);
        }
    }

    private void OnAddControl(object sender, RoutedEventArgs e)
    {
        string kind = (string)((Button)sender).Tag;
        UiElement child = MakeDefault(kind);

        // Add into the selected container, else its parent, else the root.
        UiElement parent = _selected ?? _root;
        if (!IsContainer(parent))
        {
            parent = FindParent(_root, parent) ?? _root;
        }
        parent.Children.Add(child);
        _selected = child;
        RenderAll();
        Status($"Added {kind}");
    }

    private static UiElement MakeDefault(string kind)
    {
        UiElement e = UiElement.Make(kind);
        e.Id = kind + Environment.TickCount % 1000;
        switch (kind)
        {
            case "label":
            case "textblock":
                e.Text = "Label";
                break;
            case "button":
                e.Text = "Button";
                e.Background = UiColors.DarkGray;
                e.Width = 60;
                break;
            case "textbox":
                e.Text = "text";
                e.Width = 80;
                break;
            case "checkbox":
                e.Text = "Check";
                break;
            case "radio":
                e.Text = "Option";
                e.Group = "group1";
                break;
            case "slider":
                e.Width = 100;
                e.Max = 100;
                e.Value = 50;
                break;
            case "progress":
                e.Width = 100;
                e.Value = 60;
                e.Foreground = UiColors.Green;
                break;
            case "listbox":
                e.Items.Add("Item 1");
                e.Items.Add("Item 2");
                e.Width = 100;
                break;
            case "rect":
            case "image":
                e.Width = 40;
                e.Height = 24;
                e.Background = UiColors.Blue;
                break;
            case "grid":
                e.Columns = 2;
                break;
            case "scrollviewer":
                e.Height = 60;
                break;
        }
        return e;
    }

    private static bool IsContainer(UiElement e)
    {
        return e.Kind == "window" || e.Kind == "stack" || e.Kind == "panel"
            || e.Kind == "border" || e.Kind == "canvas" || e.Kind == "grid"
            || e.Kind == "scrollviewer";
    }

    private static UiElement? FindParent(UiElement node, UiElement target)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            if (node.Children[i] == target)
            {
                return node;
            }
            UiElement? deep = FindParent(node.Children[i], target);
            if (deep != null)
            {
                return deep;
            }
        }
        return null;
    }

    // ---- rendering ---------------------------------------------------

    private void RenderAll()
    {
        _map = DesignRenderer.Render(DesignCanvas, _root);
        DrawSelectionAdorner();
        BuildTree();
        BuildProperties();
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
            Stroke = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(box, _selected.LayoutX);
        Canvas.SetTop(box, _selected.LayoutY);
        DesignCanvas.Children.Add(box);
    }

    // ---- selection + drag-to-move -----------------------------------

    private bool _dragging;
    private Point _dragLast;
    private bool _dragMoved;

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
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
            DesignCanvas.CaptureMouse();
        }
        RenderAll();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
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
            RenderAll();
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            DesignCanvas.ReleaseMouseCapture();
            if (_dragMoved && _selected != null)
            {
                Status($"Moved {_selected.Kind} to ({_selected.X}, {_selected.Y})");
            }
        }
    }

    // ---- tree --------------------------------------------------------

    private void BuildTree()
    {
        Tree.Items.Clear();
        Tree.Items.Add(BuildTreeNode(_root));
    }

    private TreeViewItem BuildTreeNode(UiElement e)
    {
        var item = new TreeViewItem
        {
            Header = e.Id.Length > 0 ? $"{e.Kind}  #{e.Id}" : e.Kind,
            Tag = e,
            IsExpanded = true,
            Foreground = System.Windows.Media.Brushes.Gainsboro,
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

    private void OnTreeSelect(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTree)
        {
            return;
        }
        if (e.NewValue is TreeViewItem item && item.Tag is UiElement el)
        {
            _selected = el;
            _syncingTree = true;
            _map = DesignRenderer.Render(DesignCanvas, _root);
            DrawSelectionAdorner();
            BuildProperties();
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

        AddPropRow("Kind", s.Kind, v => { }, readOnly: true);
        AddPropRow("Id", s.Id, v => s.Id = v);
        AddPropRow("Text", s.Text, v => s.Text = v);
        AddIntRow("X", s.X, v => s.X = v);
        AddIntRow("Y", s.Y, v => s.Y = v);
        AddIntRow("Width", s.Width, v => s.Width = v);
        AddIntRow("Height", s.Height, v => s.Height = v);
        AddIntRow("Scale", s.Scale, v => s.Scale = v);
        AddColorRow("Foreground", s.Foreground, v => s.Foreground = v);
        AddColorRow("Background", s.Background, v => s.Background = v);
        AddColorRow("Border", s.Border, v => s.Border = v);

        if (s.Kind == "slider" || s.Kind == "progress")
        {
            AddIntRow("Min", s.Min, v => s.Min = v);
            AddIntRow("Max", s.Max, v => s.Max = v);
            AddIntRow("Value", s.Value, v => s.Value = v);
        }
        if (s.Kind == "checkbox" || s.Kind == "radio")
        {
            AddBoolRow("Checked", s.Checked, v => s.Checked = v);
        }
        if (s.Kind == "radio")
        {
            AddPropRow("Group", s.Group, v => s.Group = v);
        }
        if (s.Kind == "grid")
        {
            AddIntRow("Columns", s.Columns, v => s.Columns = v);
        }
        if (IsContainer(s))
        {
            AddIntRow("Padding", s.Padding, v => s.Padding = v);
            AddIntRow("Gap", s.Gap, v => s.Gap = v);
            AddBoolRow("Horizontal", s.Horizontal, v => s.Horizontal = v);
        }
        if (s.Kind == "listbox")
        {
            AddPropRow("Items (; sep)", string.Join(";", s.Items), v =>
            {
                s.Items.Clear();
                foreach (string part in v.Split(';'))
                {
                    s.Items.Add(part);
                }
            });
            AddIntRow("Selected", s.Selected, v => s.Selected = v);
        }
    }

    private void AddPropRow(string label, string value, Action<string> set, bool readOnly = false)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox
        {
            Text = value,
            IsReadOnly = readOnly,
            Background = readOnly ? System.Windows.Media.Brushes.DimGray : System.Windows.Media.Brushes.White,
        };
        if (!readOnly)
        {
            box.LostFocus += (_, _) => { set(box.Text); RenderAll(); };
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { set(box.Text); RenderAll(); } };
        }
        Properties.Children.Add(panel);
        panel.Children.Add(box);
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
        AddPropRow(label, Hex(value), v =>
        {
            set(ParseHex(v));
        });
    }

    private void AddBoolRow(string label, bool value, Action<bool> set)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center });
        var check = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        check.Checked += (_, _) => { set(true); RenderAll(); };
        check.Unchecked += (_, _) => { set(false); RenderAll(); };
        panel.Children.Add(check);
        Properties.Children.Add(panel);
    }

    // ---- file ops ----------------------------------------------------

    private void OnNew(object sender, RoutedEventArgs e)
    {
        _root = Ui.LoadXml("<window width=\"160\" height=\"128\" bg=\"0000\" pad=\"4\" gap=\"4\"/>");
        _selected = _root;
        _currentFile = null;
        RenderAll();
        Status("New layout");
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "RustNet UI (*.xml)|*.xml|All files|*.*" };
        if (dlg.ShowDialog() == true)
        {
            OpenFile(dlg.FileName);
        }
    }

    public void OpenFile(string path)
    {
        try
        {
            _root = Ui.LoadXml(System.IO.File.ReadAllText(path));
            _selected = _root;
            _currentFile = path;
            RenderAll();
            Status("Opened " + path);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open: " + ex.Message, "RustNet UI Designer");
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null)
        {
            OnSaveAs(sender, e);
            return;
        }
        System.IO.File.WriteAllText(_currentFile, Ui.ToXml(_root));
        Status("Saved " + _currentFile);
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "RustNet UI (*.xml)|*.xml", FileName = "ui.xml" };
        if (dlg.ShowDialog() == true)
        {
            _currentFile = dlg.FileName;
            System.IO.File.WriteAllText(_currentFile, Ui.ToXml(_root));
            Status("Saved " + _currentFile);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    // ---- edit --------------------------------------------------------

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _selected == _root)
        {
            return;
        }
        UiElement? parent = FindParent(_root, _selected);
        if (parent != null)
        {
            parent.Children.Remove(_selected);
            _selected = parent;
            RenderAll();
            Status("Deleted");
        }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        if (_selected == null)
        {
            return;
        }
        UiElement? parent = FindParent(_root, _selected);
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
            RenderAll();
        }
    }

    // ---- helpers -----------------------------------------------------

    private void Status(string msg) => StatusText.Text = msg;

    private static string Hex(int v)
    {
        return v.ToString("X4");
    }

    private static int ParseHex(string s)
    {
        try
        {
            return Convert.ToInt32(s, 16);
        }
        catch
        {
            return 0;
        }
    }
}
