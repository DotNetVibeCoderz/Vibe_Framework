using System;
using RustNet.UI;

namespace RustNet.Designer;

/// <summary>
/// What a designer knows about a layout, with no window attached: which kinds
/// exist, what a freshly dropped one looks like, which of them can hold
/// children, and how to find a node's parent.
/// </summary>
/// <remarks>
/// This was private to the WPF window. A second front-end would have had to
/// copy it — two hundred lines of defaults that decide what every control
/// looks like the moment it appears on the canvas — and the two copies would
/// have drifted the first time anyone added a control to one of them.
/// </remarks>
public static class DesignModel
{
    public static readonly string[] ControlKinds =
    {
        "border", "button", "calendar", "canvas", "chart", "checkbox",
        "combobox", "datagrid", "dockpanel", "ellipse", "expander", "gauge",
        "grid", "groupbox", "image", "label", "line", "listbox",
        "messagebox", "panel", "polygon", "progress", "radio", "rect",
        "scrollviewer", "slider", "stack", "tabcontrol", "tabitem", "textbox",
        "textflow", "treeview",
    };


    public static UiElement MakeDefault(string kind)
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

            // Everything below arrives on the canvas already showing
            // something. A control dropped from a toolbox that renders as an
            // empty rectangle tells you nothing about whether you wanted it.
            case "combobox":
                e.Items.Add("slow");
                e.Items.Add("normal");
                e.Items.Add("fast");
                e.Selected = 0;
                e.Width = 110;
                break;
            case "textflow":
                e.Text = "Wrapped text flows to the width it is given.";
                e.Width = 160;
                break;
            case "gauge":
                e.Width = 90;
                e.Height = 64;
                e.Max = 100;
                e.Value = 72;
                e.Foreground = UiColors.Green;
                break;
            case "chart":
                e.Width = 140;
                e.Height = 56;
                e.Foreground = UiColors.Cyan;
                foreach (int sample in new[] { 12, 18, 14, 26, 22, 31, 27, 38 })
                {
                    e.Series.Add(sample);
                }
                break;
            case "datagrid":
                e.Columns = 3;
                e.Width = 200;
                e.Items.Add("Sensor|Value|Unit");
                e.Items.Add("temp|21.4|C");
                e.Items.Add("rh|48|%");
                break;
            case "treeview":
                e.Width = 140;
                UiElement branch = UiElement.Make("label");
                branch.Text = "sensors";
                branch.Checked = true;
                UiElement leafA = UiElement.Make("label");
                leafA.Text = "temp";
                UiElement leafB = UiElement.Make("label");
                leafB.Text = "humidity";
                branch.Add(leafA);
                branch.Add(leafB);
                e.Add(branch);
                break;
            case "calendar":
                e.Width = 180;
                e.Year = DateTime.Now.Year;
                e.Month = DateTime.Now.Month;
                e.Value = DateTime.Now.Day;
                break;
            case "messagebox":
                e.Text = "Saved to the device.";
                e.Width = 200;
                e.Height = 100;
                e.Background = UiColors.DarkGray;
                break;
            case "groupbox":
                e.Text = "Group";
                e.Width = 160;
                break;
            case "expander":
                e.Text = "Advanced";
                e.Checked = true;
                e.Width = 160;
                break;
            case "tabcontrol":
                e.Width = 200;
                e.Height = 100;
                e.Selected = 0;
                UiElement first = UiElement.Make("tabitem");
                first.Text = "One";
                UiElement second = UiElement.Make("tabitem");
                second.Text = "Two";
                e.Add(first);
                e.Add(second);
                break;
            case "tabitem":
                e.Text = "Tab";
                break;
            case "dockpanel":
                e.Width = 180;
                e.Height = 100;
                break;
            case "ellipse":
                e.Width = 40;
                e.Height = 40;
                e.Background = UiColors.Blue;
                break;
            case "line":
                e.X2 = 60;
                e.Y2 = 20;
                e.Width = 60;
                e.Height = 20;
                break;
            case "polygon":
                e.Width = 48;
                e.Height = 40;
                foreach (int coord in new[] { 24, 0, 48, 40, 0, 40 })
                {
                    e.Points.Add(coord);
                }
                break;
        }
        return e;
    }


    public static bool IsContainer(UiElement e)
    {
        return e.Kind == "window" || e.Kind == "stack" || e.Kind == "panel"
            || e.Kind == "border" || e.Kind == "canvas" || e.Kind == "grid"
            || e.Kind == "scrollviewer" || e.Kind == "dockpanel"
            || e.Kind == "groupbox" || e.Kind == "expander"
            || e.Kind == "tabcontrol" || e.Kind == "tabitem"
            || e.Kind == "treeview" || e.Kind == "messagebox";
    }


    public static UiElement? FindParent(UiElement node, UiElement target)
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

    /// <summary>
    /// The toolbox palette, grouped by what each control is *for*.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thirty-two kinds in one alphabetical column is a list you read rather
    /// than a palette you reach into. The groups are the fix, and the first one
    /// is load-bearing rather than tidy: the toolbox adds a control *into the
    /// selected container*, so which kinds can hold children decides where a
    /// click will actually put things. "Containers" is exactly the set
    /// <see cref="IsContainer"/> accepts, minus <c>window</c>, which is the
    /// root and cannot be added.
    /// </para>
    /// <para>
    /// The rest are grouped by what the person is looking for when they scan:
    /// something to show words, something to take input, something to display a
    /// measurement, something to draw.
    /// </para>
    /// </remarks>
    public static readonly (string Name, string[] Kinds)[] Palette =
    {
        ("containers", new[]
        {
            "stack", "panel", "border", "canvas", "grid", "dockpanel",
            "scrollviewer", "groupbox", "expander", "tabcontrol", "tabitem",
            "treeview", "messagebox",
        }),
        ("text", new[] { "label", "textflow" }),
        ("input", new[] { "button", "textbox", "checkbox", "radio", "combobox", "listbox", "slider" }),
        ("readouts", new[] { "gauge", "progress", "chart", "datagrid", "calendar" }),
        ("shapes", new[] { "rect", "ellipse", "line", "polygon", "image" }),
    };

    /// <summary>RGB565 as the four hex digits the layout format stores.</summary>
    public static string Hex(int v) => v.ToString("X4");

    /// <summary>Read those digits back, tolerating a leading # and stray space.</summary>
    public static int ParseHex(string s)
    {
        try
        {
            return Convert.ToInt32(s.Trim().TrimStart('#'), 16);
        }
        catch
        {
            return 0;
        }
    }
}
