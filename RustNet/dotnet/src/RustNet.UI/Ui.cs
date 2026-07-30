using System.Collections.Generic;
using RustNet.Graphics;
using RustNet.Serialization;

namespace RustNet.UI;

/// <summary>RGB565 colors for UI markup.</summary>
public static class UiColors
{
    public const int Black = 0x0000;
    public const int White = 0xFFFF;
    public const int Red = 0xF800;
    public const int Green = 0x07E0;
    public const int Blue = 0x001F;
    public const int Yellow = 0xFFE0;
    public const int Cyan = 0x07FF;
    public const int Gray = 0x8410;
    public const int DarkGray = 0x4208;
    public const int LightGray = 0xC618;
    public const int Accent = 0x05BF; // blue-cyan highlight
}

/// <summary>
/// One node of the UI tree. A single concrete class carries every control
/// kind (uniform tree — convenient for the XML format and the designer).
///
/// Containers: window, stack, panel, border, canvas, grid.
/// Controls: label, button, textblock, textbox, checkbox, radio, slider,
/// progress, listbox, image, rect.
///
/// Layout is two-pass: <see cref="Measure"/> gives a desired height at a
/// width, then <see cref="Arrange"/> assigns absolute bounds (LayoutX/Y/W/H)
/// used by both <see cref="Render"/> and hit-testing.
/// </summary>
public class UiElement
{
    public string Kind = "label";
    public string Id = "";
    public string Text = "";

    /// <summary>Explicit size; 0 = auto.</summary>
    public int Width;
    public int Height;

    /// <summary>Absolute position inside a canvas parent.</summary>
    public int X;
    public int Y;

    public int Foreground = UiColors.White;
    public int Background = UiColors.Black;
    public int Border = UiColors.Gray;
    public int Scale = 1;

    /// <summary>Slider/progress value in [Min, Max].</summary>
    public int Value;
    public int Min;
    public int Max = 100;

    /// <summary>Checkbox/radio state; radios share a <see cref="Group"/>.</summary>
    public bool Checked;
    public string Group = "";

    /// <summary>Listbox rows + selected index (-1 = none).</summary>
    public List<string> Items = new List<string>();
    public int Selected = -1;

    /// <summary>Stack/grid layout knobs.</summary>
    public bool Horizontal;
    public int Columns = 1;
    public int Padding = 2;
    public int Gap = 2;

    /// <summary>ScrollViewer vertical scroll position in pixels (clamped to
    /// content on layout).</summary>
    public int ScrollOffset;

    public List<UiElement> Children = new List<UiElement>();

    // Assigned by Arrange; read by Render and HitTest.
    public int LayoutX;
    public int LayoutY;
    public int LayoutW;
    public int LayoutH;

    /// <summary>ScrollViewer content height, computed during Arrange.</summary>
    public int ContentH;

    public static UiElement Make(string kind)
    {
        UiElement e = new UiElement();
        e.Kind = kind;
        return e;
    }

    public static UiElement Label(string text)
    {
        UiElement e = Make("label");
        e.Text = text;
        return e;
    }

    public UiElement Add(UiElement child)
    {
        Children.Add(child);
        return this;
    }

    /// <summary>Depth-first search by id ("" never matches).</summary>
    public UiElement FindById(string id)
    {
        if (Id == id && id != "")
        {
            return this;
        }
        for (int i = 0; i < Children.Count; i++)
        {
            UiElement hit = Children[i].FindById(id);
            if (hit != null)
            {
                return hit;
            }
        }
        return null;
    }

    private bool IsContainer()
    {
        return Kind == "window" || Kind == "stack" || Kind == "panel"
            || Kind == "border" || Kind == "canvas" || Kind == "grid"
            || Kind == "scrollviewer";
    }

    /// <summary>Desired content height at the given width.</summary>
    public int Measure(int width)
    {
        if (Height > 0)
        {
            return Height;
        }
        if (Kind == "label" || Kind == "textblock")
        {
            return 8 * Scale + 2;
        }
        if (Kind == "button" || Kind == "textbox")
        {
            return 8 * Scale + 8;
        }
        if (Kind == "checkbox" || Kind == "radio")
        {
            return 12;
        }
        if (Kind == "slider")
        {
            return 12;
        }
        if (Kind == "progress")
        {
            return 10;
        }
        if (Kind == "scrollviewer")
        {
            // A viewport needs an explicit height (Height>0 returned above);
            // otherwise fall back to a sensible default.
            return 40;
        }
        if (Kind == "rect" || Kind == "image")
        {
            return 12;
        }
        if (Kind == "listbox")
        {
            int rows = Items.Count > 0 ? Items.Count : 1;
            return rows * (8 * Scale + 4) + 4;
        }
        if (Kind == "canvas")
        {
            // Canvas height = the farthest child bottom.
            int bottom = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                UiElement c = Children[i];
                int cb = c.Y + c.Measure(width);
                if (cb > bottom)
                {
                    bottom = cb;
                }
            }
            return bottom + Padding * 2;
        }
        if (Kind == "grid")
        {
            int cols = Columns > 0 ? Columns : 1;
            int cellW = (width - Padding * 2 - Gap * (cols - 1)) / cols;
            int rowH = 0;
            int total = Padding * 2;
            for (int i = 0; i < Children.Count; i++)
            {
                int h = Children[i].Measure(cellW);
                if (h > rowH)
                {
                    rowH = h;
                }
                if ((i + 1) % cols == 0)
                {
                    total = total + rowH + Gap;
                    rowH = 0;
                }
            }
            if (rowH > 0)
            {
                total = total + rowH;
            }
            return total;
        }
        // window / stack / panel / border
        int inner = width - Padding * 2;
        if (Horizontal)
        {
            int maxH = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                int h = Children[i].Measure(inner);
                if (h > maxH)
                {
                    maxH = h;
                }
            }
            return maxH + Padding * 2;
        }
        int sum = Padding * 2;
        for (int i = 0; i < Children.Count; i++)
        {
            sum = sum + Children[i].Measure(inner);
            if (i > 0)
            {
                sum = sum + Gap;
            }
        }
        return sum;
    }

    /// <summary>Assign absolute bounds to this element and its subtree.</summary>
    public void Arrange(int x, int y, int width, int height)
    {
        LayoutX = x;
        LayoutY = y;
        LayoutW = Width > 0 ? Width : width;
        LayoutH = Height > 0 ? Height : height;

        if (!IsContainer())
        {
            return;
        }

        int innerX = x + Padding;
        int innerY = y + Padding;
        int innerW = LayoutW - Padding * 2;

        if (Kind == "canvas")
        {
            for (int i = 0; i < Children.Count; i++)
            {
                UiElement c = Children[i];
                int cw = c.Width > 0 ? c.Width : innerW;
                c.Arrange(innerX + c.X, innerY + c.Y, cw, c.Measure(cw));
            }
            return;
        }

        if (Kind == "scrollviewer")
        {
            // Stack children at full content height, offset by the (clamped)
            // scroll position. Rendering clips to the viewport.
            int contentH = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                if (i > 0)
                {
                    contentH = contentH + Gap;
                }
                contentH = contentH + Children[i].Measure(innerW);
            }
            ContentH = contentH;
            int viewInner = LayoutH - Padding * 2;
            int maxOff = contentH - viewInner;
            if (maxOff < 0)
            {
                maxOff = 0;
            }
            if (ScrollOffset < 0)
            {
                ScrollOffset = 0;
            }
            if (ScrollOffset > maxOff)
            {
                ScrollOffset = maxOff;
            }
            int sy = innerY - ScrollOffset;
            for (int i = 0; i < Children.Count; i++)
            {
                UiElement c = Children[i];
                int ch = c.Measure(innerW);
                c.Arrange(innerX, sy, innerW, ch);
                sy = sy + ch + Gap;
            }
            return;
        }

        if (Kind == "grid")
        {
            int cols = Columns > 0 ? Columns : 1;
            int cellW = (innerW - Gap * (cols - 1)) / cols;
            int cx = innerX;
            int cy = innerY;
            int rowH = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                UiElement c = Children[i];
                int ch = c.Measure(cellW);
                c.Arrange(cx, cy, cellW, ch);
                if (ch > rowH)
                {
                    rowH = ch;
                }
                if ((i + 1) % cols == 0)
                {
                    cx = innerX;
                    cy = cy + rowH + Gap;
                    rowH = 0;
                }
                else
                {
                    cx = cx + cellW + Gap;
                }
            }
            return;
        }

        // stack / window / panel / border
        int px = innerX;
        int py = innerY;
        for (int i = 0; i < Children.Count; i++)
        {
            UiElement c = Children[i];
            if (Horizontal)
            {
                int cw = c.Width > 0 ? c.Width : 40;
                c.Arrange(px, py, cw, c.Measure(cw));
                px = px + cw + Gap;
            }
            else
            {
                int ch = c.Measure(innerW);
                c.Arrange(px, py, innerW, ch);
                py = py + ch + Gap;
            }
        }
    }

    /// <summary>Draw using the bounds assigned by <see cref="Arrange"/>.</summary>
    public void Render()
    {
        int x = LayoutX, y = LayoutY, w = LayoutW, h = LayoutH;

        if (Kind == "label" || Kind == "textblock")
        {
            Display.DrawText(x, y, Text, Foreground, Scale);
        }
        else if (Kind == "button")
        {
            Display.FillRect(x, y, w, h, Background);
            Display.DrawRect(x, y, w, h, Border);
            Display.DrawText(x + 4, y + 4, Text, Foreground, Scale);
        }
        else if (Kind == "textbox")
        {
            Display.FillRect(x, y, w, h, UiColors.Black);
            Display.DrawRect(x, y, w, h, Border);
            Display.DrawText(x + 3, y + 4, Text, Foreground, Scale);
        }
        else if (Kind == "checkbox")
        {
            Display.DrawRect(x, y + 1, 10, 10, Border);
            if (Checked)
            {
                Display.DrawLine(x + 2, y + 6, x + 4, y + 8, Foreground);
                Display.DrawLine(x + 4, y + 8, x + 8, y + 2, Foreground);
            }
            Display.DrawText(x + 14, y + 2, Text, Foreground, Scale);
        }
        else if (Kind == "radio")
        {
            Display.DrawRect(x, y + 1, 10, 10, Border);
            if (Checked)
            {
                Display.FillRect(x + 3, y + 4, 4, 4, Foreground);
            }
            Display.DrawText(x + 14, y + 2, Text, Foreground, Scale);
        }
        else if (Kind == "slider")
        {
            int midY = y + h / 2;
            Display.DrawLine(x, midY, x + w - 1, midY, Border);
            int span = Max > Min ? Max - Min : 1;
            int knob = x + (w - 6) * (Value - Min) / span;
            Display.FillRect(knob, y + 2, 6, h - 4, Foreground);
        }
        else if (Kind == "progress")
        {
            Display.FillRect(x, y, w, h, UiColors.DarkGray);
            int span = Max > Min ? Max - Min : 1;
            int fill = w * (Value - Min) / span;
            Display.FillRect(x, y, fill, h, Foreground);
        }
        else if (Kind == "listbox")
        {
            Display.FillRect(x, y, w, h, UiColors.Black);
            Display.DrawRect(x, y, w, h, Border);
            int rowH = 8 * Scale + 4;
            for (int i = 0; i < Items.Count; i++)
            {
                int ry = y + 2 + i * rowH;
                if (i == Selected)
                {
                    Display.FillRect(x + 1, ry, w - 2, rowH, UiColors.Accent);
                }
                Display.DrawText(x + 3, ry + 2, Items[i], Foreground, Scale);
            }
        }
        else if (Kind == "image")
        {
            Display.FillRect(x, y, w, h, Background);
            Display.DrawRect(x, y, w, h, Border);
        }
        else if (Kind == "rect")
        {
            Display.FillRect(x, y, w, h, Background);
        }
        else if (Kind == "scrollviewer")
        {
            Display.FillRect(x, y, w, h, Background);
            // Clip children to the viewport so scrolled content can't overdraw.
            Display.SetClip(x, y, w, h);
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Render();
            }
            Display.ClearClip();
            // Scrollbar thumb on the right edge when content overflows.
            int viewInner = h - Padding * 2;
            if (ContentH > viewInner && ContentH > 0)
            {
                int thumbH = viewInner * viewInner / ContentH;
                if (thumbH < 4)
                {
                    thumbH = 4;
                }
                int range = ContentH - viewInner;
                int thumbY = y + Padding;
                if (range > 0)
                {
                    thumbY = thumbY + ScrollOffset * (viewInner - thumbH) / range;
                }
                Display.FillRect(x + w - 3, y + Padding, 2, viewInner, UiColors.DarkGray);
                Display.FillRect(x + w - 3, thumbY, 2, thumbH, UiColors.Accent);
            }
        }
        else
        {
            // Containers: border/panel paint a frame; then children.
            if (Background != UiColors.Black || Kind == "border" || Kind == "panel")
            {
                Display.FillRect(x, y, w, h, Background);
            }
            if (Kind == "border")
            {
                Display.DrawRect(x, y, w, h, Border);
            }
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Render();
            }
        }
    }

    /// <summary>Topmost element whose laid-out bounds contain (px, py).</summary>
    public UiElement HitTest(int px, int py)
    {
        // A scrollviewer clips its content: points outside the viewport can't
        // hit scrolled-away children.
        if (Kind == "scrollviewer"
            && (px < LayoutX || px >= LayoutX + LayoutW
                || py < LayoutY || py >= LayoutY + LayoutH))
        {
            return null;
        }
        // Deepest child first (last drawn wins).
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            UiElement hit = Children[i].HitTest(px, py);
            if (hit != null)
            {
                return hit;
            }
        }
        if (px >= LayoutX && px < LayoutX + LayoutW
            && py >= LayoutY && py < LayoutY + LayoutH)
        {
            return this;
        }
        return null;
    }
}

/// <summary>
/// UI entry points: load a tree from XML, render to the display, and route
/// touch/pointer input.
///
/// Markup example:
/// <code>
/// &lt;window width="160" height="128" bg="0000" pad="4" gap="4"&gt;
///   &lt;label id="title" text="Thermostat" scale="2" fg="FFFF"/&gt;
///   &lt;slider id="setpoint" min="10" max="30" value="21" fg="F800"/&gt;
///   &lt;checkbox id="eco" text="Eco mode" checked="true"/&gt;
///   &lt;listbox id="zones" items="Kitchen;Garage;Attic" selected="0"/&gt;
///   &lt;button id="apply" text="Apply" bg="4208"/&gt;
/// &lt;/window&gt;
/// </code>
/// </summary>
public static class Ui
{
    private static bool _inited;
    private static int _width = 160;
    private static int _height = 128;

    /// <summary>Build a UI tree from XML markup (colors are RGB565 hex).</summary>
    public static UiElement LoadXml(string xml)
    {
        XmlNode root = Xml.Parse(xml);
        return FromNode(root);
    }

    private static UiElement FromNode(XmlNode node)
    {
        UiElement e = UiElement.Make(node.Name);
        if (node.HasAttr("id"))
        {
            e.Id = node.GetAttr("id");
        }
        e.Text = node.HasAttr("text") ? node.GetAttr("text") : node.Text;
        SetInt(node, "width", ref e.Width);
        SetInt(node, "height", ref e.Height);
        SetInt(node, "x", ref e.X);
        SetInt(node, "y", ref e.Y);
        SetInt(node, "scale", ref e.Scale);
        SetInt(node, "value", ref e.Value);
        SetInt(node, "min", ref e.Min);
        SetInt(node, "max", ref e.Max);
        SetInt(node, "pad", ref e.Padding);
        SetInt(node, "gap", ref e.Gap);
        SetInt(node, "columns", ref e.Columns);
        SetInt(node, "selected", ref e.Selected);
        SetInt(node, "scroll", ref e.ScrollOffset);
        if (node.HasAttr("fg"))
        {
            e.Foreground = ParseHex(node.GetAttr("fg"));
        }
        if (node.HasAttr("bg"))
        {
            e.Background = ParseHex(node.GetAttr("bg"));
        }
        if (node.HasAttr("border"))
        {
            e.Border = ParseHex(node.GetAttr("border"));
        }
        if (node.HasAttr("checked"))
        {
            e.Checked = node.GetAttr("checked") == "true";
        }
        if (node.HasAttr("group"))
        {
            e.Group = node.GetAttr("group");
        }
        if (node.HasAttr("orient"))
        {
            e.Horizontal = node.GetAttr("orient") == "horizontal";
        }
        if (node.HasAttr("items"))
        {
            string[] parts = node.GetAttr("items").Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                e.Items.Add(parts[i]);
            }
        }
        for (int i = 0; i < node.Children.Count; i++)
        {
            e.Add(FromNode(node.Children[i]));
        }
        return e;
    }

    private static void SetInt(XmlNode node, string attr, ref int target)
    {
        if (node.HasAttr(attr))
        {
            target = int.Parse(node.GetAttr(attr));
        }
    }

    /// <summary>Serialize a tree back to the RustNet.UI XML format
    /// (the designer's save path; round-trips with LoadXml).</summary>
    public static string ToXml(UiElement root)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        WriteNode(root, sb, 0);
        return sb.ToString();
    }

    private static void WriteNode(UiElement e, System.Text.StringBuilder sb, int depth)
    {
        for (int i = 0; i < depth; i++)
        {
            sb.Append("  ");
        }
        sb.Append('<');
        sb.Append(e.Kind);
        WriteAttr(sb, "id", e.Id, "");
        WriteAttrRaw(sb, "text", e.Text, "");
        WriteAttrInt(sb, "x", e.X, 0);
        WriteAttrInt(sb, "y", e.Y, 0);
        WriteAttrInt(sb, "width", e.Width, 0);
        WriteAttrInt(sb, "height", e.Height, 0);
        WriteAttrInt(sb, "scale", e.Scale, 1);
        WriteAttrInt(sb, "value", e.Value, 0);
        WriteAttrInt(sb, "min", e.Min, 0);
        WriteAttrInt(sb, "max", e.Max, 100);
        WriteAttrInt(sb, "columns", e.Columns, 1);
        WriteAttrInt(sb, "selected", e.Selected, -1);
        WriteAttrInt(sb, "scroll", e.ScrollOffset, 0);
        WriteAttrInt(sb, "pad", e.Padding, 2);
        WriteAttrInt(sb, "gap", e.Gap, 2);
        WriteAttr(sb, "fg", Hex(e.Foreground), Hex(UiColors.White));
        WriteAttr(sb, "bg", Hex(e.Background), Hex(UiColors.Black));
        WriteAttr(sb, "border", Hex(e.Border), Hex(UiColors.Gray));
        WriteAttr(sb, "group", e.Group, "");
        if (e.Horizontal)
        {
            sb.Append(" orient=\"horizontal\"");
        }
        if (e.Checked)
        {
            sb.Append(" checked=\"true\"");
        }
        if (e.Items.Count > 0)
        {
            sb.Append(" items=\"");
            sb.Append(string.Join(";", e.Items));
            sb.Append('"');
        }
        if (e.Children.Count == 0)
        {
            sb.Append("/>\n");
            return;
        }
        sb.Append(">\n");
        for (int i = 0; i < e.Children.Count; i++)
        {
            WriteNode(e.Children[i], sb, depth + 1);
        }
        for (int i = 0; i < depth; i++)
        {
            sb.Append("  ");
        }
        sb.Append("</");
        sb.Append(e.Kind);
        sb.Append(">\n");
    }

    private static void WriteAttr(System.Text.StringBuilder sb, string name, string val, string def)
    {
        if (val != def && val.Length > 0)
        {
            sb.Append(' ');
            sb.Append(name);
            sb.Append("=\"");
            sb.Append(val);
            sb.Append('"');
        }
    }

    private static void WriteAttrRaw(System.Text.StringBuilder sb, string name, string val, string def)
    {
        WriteAttr(sb, name, val, def);
    }

    private static void WriteAttrInt(System.Text.StringBuilder sb, string name, int val, int def)
    {
        if (val != def)
        {
            sb.Append(' ');
            sb.Append(name);
            sb.Append("=\"");
            sb.Append(val.ToString());
            sb.Append('"');
        }
    }

    private static string Hex(int v)
    {
        string s = "";
        for (int i = 3; i >= 0; i--)
        {
            int nib = (v >> (i * 4)) & 0xF;
            s = string.Concat(s, HexDigit(nib));
        }
        return s;
    }

    private static string HexDigit(int n)
    {
        if (n < 10)
        {
            return ((char)('0' + n)).ToString();
        }
        return ((char)('A' + (n - 10))).ToString();
    }

    private static int ParseHex(string s)
    {
        int v = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            int d;
            if (c >= '0' && c <= '9')
            {
                d = c - '0';
            }
            else if (c >= 'a' && c <= 'f')
            {
                d = c - 'a' + 10;
            }
            else
            {
                d = c - 'A' + 10;
            }
            v = v * 16 + d;
        }
        return v;
    }

    /// <summary>Lay out and draw the whole tree.</summary>
    public static void Render(UiElement root)
    {
        int w = root.Width > 0 ? root.Width : _width;
        int h = root.Height > 0 ? root.Height : _height;
        if (!_inited || w != _width || h != _height)
        {
            Display.Init(w, h);
            _width = w;
            _height = h;
            _inited = true;
        }
        root.Arrange(0, 0, w, h);
        Display.Clear(root.Background);
        root.Render();
        Display.Present();
    }

    /// <summary>
    /// Route a tap/click at (px, py): updates the hit control's state
    /// (toggle checkbox, select radio, move slider, choose listbox row) and
    /// returns it, or null. Call <see cref="Render"/> afterwards to redraw.
    /// </summary>
    public static UiElement Tap(UiElement root, int px, int py)
    {
        UiElement hit = root.HitTest(px, py);
        if (hit == null)
        {
            return null;
        }
        if (hit.Kind == "checkbox")
        {
            hit.Checked = !hit.Checked;
        }
        else if (hit.Kind == "radio")
        {
            ClearGroup(root, hit.Group);
            hit.Checked = true;
        }
        else if (hit.Kind == "slider")
        {
            // Track spans (LayoutW - knobWidth), matching Render's knob math.
            int track = hit.LayoutW - 6;
            if (track < 1)
            {
                track = 1;
            }
            int rel = px - hit.LayoutX;
            if (rel < 0)
            {
                rel = 0;
            }
            if (rel > track)
            {
                rel = track;
            }
            int span = hit.Max > hit.Min ? hit.Max - hit.Min : 1;
            hit.Value = hit.Min + rel * span / track;
        }
        else if (hit.Kind == "listbox")
        {
            int rowH = 8 * hit.Scale + 4;
            int idx = (py - hit.LayoutY - 2) / rowH;
            if (idx >= 0 && idx < hit.Items.Count)
            {
                hit.Selected = idx;
            }
        }
        return hit;
    }

    /// <summary>Scroll a ScrollViewer (by id) by <paramref name="delta"/>
    /// pixels (positive = down). The offset is clamped to the content on the
    /// next <see cref="Render"/>. Returns the new (pre-clamp) offset.</summary>
    public static int Scroll(UiElement root, string id, int delta)
    {
        UiElement sv = root.FindById(id);
        if (sv == null || sv.Kind != "scrollviewer")
        {
            return 0;
        }
        sv.ScrollOffset = sv.ScrollOffset + delta;
        if (sv.ScrollOffset < 0)
        {
            sv.ScrollOffset = 0;
        }
        return sv.ScrollOffset;
    }

    private static void ClearGroup(UiElement node, string group)
    {
        if (node.Kind == "radio" && node.Group == group)
        {
            node.Checked = false;
        }
        for (int i = 0; i < node.Children.Count; i++)
        {
            ClearGroup(node.Children[i], group);
        }
    }
}
