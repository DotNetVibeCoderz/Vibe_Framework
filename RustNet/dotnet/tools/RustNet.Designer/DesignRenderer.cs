using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RustNet.UI;

namespace RustNet.Designer;

/// <summary>
/// Renders a <see cref="UiElement"/> tree onto a WPF <see cref="Canvas"/>,
/// mirroring how RustNet.UI paints the device display, so the designer is
/// WYSIWYG. Uses the model's own two-pass layout (Measure/Arrange), then
/// draws each element's laid-out bounds. Records the element behind each
/// visual so clicks select the right node.
/// </summary>
public static class DesignRenderer
{
    /// <summary>Convert an RGB565 int to a WPF brush.</summary>
    public static Color FromRgb565(int v)
    {
        int r = ((v >> 11) & 0x1F) << 3;
        int g = ((v >> 5) & 0x3F) << 2;
        int b = (v & 0x1F) << 3;
        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }

    private static Brush Brush(int rgb565) => new SolidColorBrush(FromRgb565(rgb565));

    /// <summary>
    /// Lay out and draw <paramref name="root"/> onto <paramref name="canvas"/>.
    /// Returns a shape→element map for hit-selection. The canvas is sized to
    /// the root's bounds.
    /// </summary>
    public static Dictionary<FrameworkElement, UiElement> Render(Canvas canvas, UiElement root)
    {
        canvas.Children.Clear();
        var map = new Dictionary<FrameworkElement, UiElement>();

        int w = root.Width > 0 ? root.Width : 160;
        int h = root.Height > 0 ? root.Height : 128;
        canvas.Width = w;
        canvas.Height = h;
        canvas.Background = Brush(root.Background);

        root.Arrange(0, 0, w, h);
        Draw(canvas, root, map);
        return map;
    }

    private static void Draw(Canvas canvas, UiElement e, Dictionary<FrameworkElement, UiElement> map)
    {
        int x = e.LayoutX, y = e.LayoutY, w = e.LayoutW, h = e.LayoutH;

        switch (e.Kind)
        {
            case "label":
            case "textblock":
                AddText(canvas, map, e, x, y, e.Text, e.Foreground, e.Scale);
                break;

            case "button":
                AddBox(canvas, map, e, x, y, w, h, e.Background, e.Border);
                AddText(canvas, map, e, x + 4, y + 4, e.Text, e.Foreground, e.Scale);
                break;

            case "textbox":
                AddBox(canvas, map, e, x, y, w, h, UiColors.Black, e.Border);
                AddText(canvas, map, e, x + 3, y + 4, e.Text, e.Foreground, e.Scale);
                break;

            case "checkbox":
                AddBox(canvas, map, e, x, y + 1, 10, 10, UiColors.Black, e.Border);
                if (e.Checked)
                {
                    AddText(canvas, map, e, x + 1, y, "x", e.Foreground, 1);
                }
                AddText(canvas, map, e, x + 14, y + 2, e.Text, e.Foreground, e.Scale);
                break;

            case "radio":
                AddEllipse(canvas, map, e, x, y + 1, 10, 10, e.Border, e.Checked ? e.Foreground : -1);
                AddText(canvas, map, e, x + 14, y + 2, e.Text, e.Foreground, e.Scale);
                break;

            case "slider":
            {
                int midY = y + h / 2;
                AddLine(canvas, e, x, midY, x + w, midY, e.Border);
                int span = e.Max > e.Min ? e.Max - e.Min : 1;
                int knob = x + (w - 6) * (e.Value - e.Min) / span;
                AddBox(canvas, map, e, knob, y + 2, 6, h - 4, e.Foreground, e.Foreground);
                break;
            }

            case "progress":
                AddBox(canvas, map, e, x, y, w, h, UiColors.DarkGray, UiColors.DarkGray);
                int fillSpan = e.Max > e.Min ? e.Max - e.Min : 1;
                AddBox(canvas, map, e, x, y, w * (e.Value - e.Min) / fillSpan, h, e.Foreground, e.Foreground);
                break;

            case "listbox":
            {
                AddBox(canvas, map, e, x, y, w, h, UiColors.Black, e.Border);
                int rowH = 8 * e.Scale + 4;
                for (int i = 0; i < e.Items.Count; i++)
                {
                    int ry = y + 2 + i * rowH;
                    if (i == e.Selected)
                    {
                        AddFill(canvas, e, x + 1, ry, w - 2, rowH, UiColors.Accent);
                    }
                    AddText(canvas, map, e, x + 3, ry + 2, e.Items[i], e.Foreground, e.Scale);
                }
                break;
            }

            case "image":
            case "rect":
                AddBox(canvas, map, e, x, y, w, h, e.Background, e.Border);
                break;

            default: // containers
                if (e.Kind == "border" || e.Kind == "panel" || e.Background != UiColors.Black)
                {
                    AddBox(canvas, map, e, x, y, w, h, e.Background,
                        e.Kind == "border" ? e.Border : e.Background);
                }
                else
                {
                    // Invisible container: a transparent hit rect for selection.
                    AddHitRect(canvas, map, e, x, y, w, h);
                }
                for (int i = 0; i < e.Children.Count; i++)
                {
                    Draw(canvas, e.Children[i], map);
                }
                break;
        }
    }

    private static void AddBox(Canvas c, Dictionary<FrameworkElement, UiElement> map, UiElement e,
        int x, int y, int w, int h, int fill, int stroke)
    {
        var r = new Rectangle
        {
            Width = System.Math.Max(1, w),
            Height = System.Math.Max(1, h),
            Fill = Brush(fill),
            Stroke = Brush(stroke),
            StrokeThickness = 1,
        };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        c.Children.Add(r);
        map[r] = e;
    }

    private static void AddFill(Canvas c, UiElement e, int x, int y, int w, int h, int fill)
    {
        var r = new Rectangle { Width = System.Math.Max(1, w), Height = System.Math.Max(1, h), Fill = Brush(fill) };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        c.Children.Add(r);
    }

    private static void AddHitRect(Canvas c, Dictionary<FrameworkElement, UiElement> map, UiElement e,
        int x, int y, int w, int h)
    {
        var r = new Rectangle
        {
            Width = System.Math.Max(1, w),
            Height = System.Math.Max(1, h),
            Fill = Brushes.Transparent,
        };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        c.Children.Add(r);
        map[r] = e;
    }

    private static void AddEllipse(Canvas c, Dictionary<FrameworkElement, UiElement> map, UiElement e,
        int x, int y, int w, int h, int stroke, int fill)
    {
        var el = new Ellipse
        {
            Width = w,
            Height = h,
            Stroke = Brush(stroke),
            StrokeThickness = 1,
            Fill = fill >= 0 ? Brush(fill) : Brushes.Transparent,
        };
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        c.Children.Add(el);
        map[el] = e;
    }

    private static void AddLine(Canvas c, UiElement e, int x0, int y0, int x1, int y1, int stroke)
    {
        var l = new Line { X1 = x0, Y1 = y0, X2 = x1, Y2 = y1, Stroke = Brush(stroke), StrokeThickness = 1 };
        c.Children.Add(l);
    }

    private static void AddText(Canvas c, Dictionary<FrameworkElement, UiElement> map, UiElement e,
        int x, int y, string text, int color, int scale)
    {
        var t = new TextBlock
        {
            Text = text,
            Foreground = Brush(color),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 7 * (scale < 1 ? 1 : scale),
        };
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        c.Children.Add(t);
        map[t] = e;
    }
}
