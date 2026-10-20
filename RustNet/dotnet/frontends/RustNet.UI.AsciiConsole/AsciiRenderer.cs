using System.Text;

namespace RustNet.UI.AsciiConsole;

/// <summary>
/// Renders a <see cref="UiElement"/> tree as text. The tree is arranged with
/// the toolkit's own layout code — exactly as the device arranges it — and
/// each pixel rectangle is mapped onto character cells, one cell per glyph of
/// the device's 8×8 font. A 160×128 window is therefore 20×16 characters and
/// text lands on the same cells it occupies on the panel.
/// </summary>
public sealed class AsciiRenderer
{
    /// <summary>Pixels per character cell horizontally (the device font is 8 wide).</summary>
    public int CellWidth { get; init; } = 8;

    /// <summary>Pixels per character cell vertically (the device font is 8 high).</summary>
    public int CellHeight { get; init; } = 8;

    /// <summary>Use box-drawing and block characters; false restricts output to 7-bit ASCII.</summary>
    public bool Unicode { get; init; } = true;

    /// <summary>The element that keyboard focus is on; drawn with a marker.</summary>
    public UiElement? Focused { get; set; }

    private char[,] _grid = new char[0, 0];
    private int _cols;
    private int _rows;
    private (int X0, int Y0, int X1, int Y1) _clip;

    /// <summary>Arrange <paramref name="root"/> at its declared size (or the given one) and render it.</summary>
    public string Render(UiElement root, int widthPx = 0, int heightPx = 0)
    {
        int w = widthPx > 0 ? widthPx : root.Width > 0 ? root.Width : 160;
        int h = heightPx > 0 ? heightPx : root.Height > 0 ? root.Height : 128;
        root.Arrange(0, 0, w, h);
        _cols = Math.Max(1, (w + CellWidth - 1) / CellWidth);
        _rows = Math.Max(1, (h + CellHeight - 1) / CellHeight);
        _grid = new char[_rows, _cols];
        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < _cols; c++)
            {
                _grid[r, c] = ' ';
            }
        }
        _clip = (0, 0, _cols, _rows);
        Draw(root);
        var sb = new StringBuilder(_rows * (_cols + 1));
        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < _cols; c++)
            {
                sb.Append(_grid[r, c]);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Character cell of a pixel coordinate.</summary>
    public (int Col, int Row) CellOf(int px, int py) => (px / CellWidth, py / CellHeight);

    /// <summary>Pixel centre of a character cell (for routing a keypress as a tap).</summary>
    public (int X, int Y) PixelOf(int col, int row) => (col * CellWidth + CellWidth / 2, row * CellHeight + CellHeight / 2);

    // ---- glyph set -------------------------------------------------------

    private char H => Unicode ? '─' : '-';
    private char V => Unicode ? '│' : '|';
    private char TL => Unicode ? '┌' : '+';
    private char TR => Unicode ? '┐' : '+';
    private char BL => Unicode ? '└' : '+';
    private char BR => Unicode ? '┘' : '+';
    private char Full => Unicode ? '█' : '#';
    private char Light => Unicode ? '░' : '.';
    private char Knob => Unicode ? '●' : 'O';

    // ---- primitives (cell coordinates, clipped) --------------------------

    private void Put(int col, int row, char ch)
    {
        if (col >= _clip.X0 && col < _clip.X1 && row >= _clip.Y0 && row < _clip.Y1
            && col >= 0 && col < _cols && row >= 0 && row < _rows)
        {
            _grid[row, col] = ch;
        }
    }

    private void Text(int col, int row, string text, int maxCols)
    {
        for (int i = 0; i < text.Length && i < maxCols; i++)
        {
            Put(col + i, row, text[i] < ' ' ? ' ' : text[i]);
        }
    }

    private void Fill(int c0, int r0, int cols, int rows, char ch)
    {
        for (int r = r0; r < r0 + rows; r++)
        {
            for (int c = c0; c < c0 + cols; c++)
            {
                Put(c, r, ch);
            }
        }
    }

    private void Box(int c0, int r0, int cols, int rows)
    {
        if (cols < 2 || rows < 2)
        {
            Fill(c0, r0, Math.Max(cols, 1), Math.Max(rows, 1), H);
            return;
        }
        int c1 = c0 + cols - 1;
        int r1 = r0 + rows - 1;
        for (int c = c0 + 1; c < c1; c++)
        {
            Put(c, r0, H);
            Put(c, r1, H);
        }
        for (int r = r0 + 1; r < r1; r++)
        {
            Put(c0, r, V);
            Put(c1, r, V);
        }
        Put(c0, r0, TL);
        Put(c1, r0, TR);
        Put(c0, r1, BL);
        Put(c1, r1, BR);
    }

    private void Line(int c0, int r0, int c1, int r1, char ch)
    {
        int dc = Math.Abs(c1 - c0), sc = c0 < c1 ? 1 : -1;
        int dr = -Math.Abs(r1 - r0), sr = r0 < r1 ? 1 : -1;
        int err = dc + dr;
        while (true)
        {
            Put(c0, r0, ch);
            if (c0 == c1 && r0 == r1)
            {
                return;
            }
            int e2 = 2 * err;
            if (e2 >= dr)
            {
                err += dr;
                c0 += sc;
            }
            if (e2 <= dc)
            {
                err += dc;
                r0 += sr;
            }
        }
    }

    // ---- the tree ---------------------------------------------------------

    private record struct Cells(int C, int R, int W, int H);

    private Cells CellsOf(UiElement e)
    {
        int c = e.LayoutX / CellWidth;
        int r = e.LayoutY / CellHeight;
        int c1 = Math.Max(c + 1, (e.LayoutX + e.LayoutW + CellWidth / 2) / CellWidth);
        int r1 = Math.Max(r + 1, (e.LayoutY + e.LayoutH + CellHeight / 2) / CellHeight);
        return new Cells(c, r, c1 - c, r1 - r);
    }

    private void Draw(UiElement e)
    {
        var b = CellsOf(e);
        string focus = ReferenceEquals(e, Focused) ? ">" : "";
        switch (e.Kind)
        {
            case "label":
            case "textblock":
                Text(b.C, b.R, e.Text, Math.Max(b.W, e.Text.Length));
                break;
            case "textflow":
            {
                var lines = UiElement.WrapText(e.Text, e.LayoutW, e.Scale);
                for (int i = 0; i < lines.Count && i < b.H; i++)
                {
                    Text(b.C, b.R + i, lines[i], b.W);
                }
                break;
            }
            case "button":
            {
                string label = $"[{focus}{e.Text}]";
                if (b.H >= 3 && b.W >= label.Length)
                {
                    Box(b.C, b.R, b.W, b.H);
                    Text(b.C + Math.Max(1, (b.W - e.Text.Length) / 2), b.R + b.H / 2, focus + e.Text, b.W - 2);
                }
                else
                {
                    Text(b.C, b.R, label, b.W + 2);
                }
                break;
            }
            case "textbox":
            {
                int inner = Math.Max(1, b.W - 2);
                string shown = e.Text.Length > inner ? e.Text[^inner..] : e.Text.PadRight(inner, '_');
                Text(b.C, b.R, "[" + shown + "]", b.W);
                break;
            }
            case "checkbox":
                Text(b.C, b.R, (e.Checked ? "[x] " : "[ ] ") + focus + e.Text, Math.Max(b.W, e.Text.Length + 5));
                break;
            case "radio":
                Text(b.C, b.R, (e.Checked ? "(*) " : "( ) ") + focus + e.Text, Math.Max(b.W, e.Text.Length + 5));
                break;
            case "slider":
            {
                int track = Math.Max(3, b.W);
                int span = e.Max > e.Min ? e.Max - e.Min : 1;
                int pos = (e.Value - e.Min) * (track - 1) / span;
                Fill(b.C, b.R, track, 1, H);
                Put(b.C + Math.Clamp(pos, 0, track - 1), b.R, Knob);
                break;
            }
            case "progress":
            {
                int inner = Math.Max(1, b.W - 2);
                int span = e.Max > e.Min ? e.Max - e.Min : 1;
                int filled = Math.Clamp((e.Value - e.Min) * inner / span, 0, inner);
                Put(b.C, b.R, '[');
                Fill(b.C + 1, b.R, filled, 1, Full);
                Fill(b.C + 1 + filled, b.R, inner - filled, 1, Light);
                Put(b.C + 1 + inner, b.R, ']');
                break;
            }
            case "listbox":
                for (int i = 0; i < e.Items.Count && i < b.H; i++)
                {
                    Text(b.C, b.R + i, (i == e.Selected ? ">" : " ") + e.Items[i], b.W);
                }
                break;
            case "combobox":
            {
                string item = e.Selected >= 0 && e.Selected < e.Items.Count ? e.Items[e.Selected] : e.Text;
                Text(b.C, b.R, "[" + focus + item + (Unicode ? " ▾]" : " v]"), b.W + 3);
                break;
            }
            case "gauge":
            {
                string label = $"{e.Text} {e.Value}/{e.Max}".Trim();
                Text(b.C, b.R, label, b.W);
                if (b.H > 1)
                {
                    int span = e.Max > e.Min ? e.Max - e.Min : 1;
                    int filled = Math.Clamp((e.Value - e.Min) * b.W / span, 0, b.W);
                    Fill(b.C, b.R + 1, filled, 1, Full);
                    Fill(b.C + filled, b.R + 1, b.W - filled, 1, Light);
                }
                break;
            }
            case "chart":
                DrawChart(e, b);
                break;
            case "datagrid":
            {
                int cols = Math.Max(1, e.Columns);
                int colW = Math.Max(1, b.W / cols);
                for (int r = 0; r < e.Items.Count && r < b.H; r++)
                {
                    string[] cells = e.Items[r].Split('|');
                    for (int c = 0; c < cols && c < cells.Length; c++)
                    {
                        string prefix = c == 0 && r == e.Selected && r > 0 ? ">" : "";
                        Text(b.C + c * colW, b.R + r, prefix + cells[c], colW - 1);
                    }
                    if (r == 0 && b.H > 1 && e.Items.Count > 1)
                    {
                        // Header underline would steal a data row; mark the header instead.
                        for (int c = 0; c < b.W; c++)
                        {
                            if (_grid[Math.Clamp(b.R, 0, _rows - 1), Math.Clamp(b.C + c, 0, _cols - 1)] == ' ')
                            {
                                Put(b.C + c, b.R, Unicode ? '·' : '.');
                            }
                        }
                    }
                }
                break;
            }
            case "calendar":
                DrawCalendar(e, b);
                break;
            case "expander":
                Text(b.C, b.R, (e.Checked ? "- " : "+ ") + focus + e.Text, b.W);
                if (e.Checked)
                {
                    DrawChildren(e);
                }
                break;
            case "tabcontrol":
            {
                var sb = new StringBuilder();
                int sel = e.Selected < 0 ? 0 : e.Selected;
                for (int i = 0; i < e.Children.Count; i++)
                {
                    sb.Append(i == sel ? $"[{e.Children[i].Text}]" : $" {e.Children[i].Text} ");
                }
                Text(b.C, b.R, sb.ToString(), b.W);
                if (sel < e.Children.Count)
                {
                    Draw(e.Children[sel]);
                }
                break;
            }
            case "treeview":
                DrawTree(e, 0);
                break;
            case "messagebox":
            {
                int bw = Math.Max(4, b.W * 3 / 4);
                int bh = Math.Max(3, b.H / 3);
                int bc = b.C + (b.W - bw) / 2;
                int br = b.R + (b.H - bh) / 2;
                Fill(bc, br, bw, bh, ' ');
                Box(bc, br, bw, bh);
                var lines = UiElement.WrapText(e.Text, (bw - 2) * CellWidth, 1);
                for (int i = 0; i < lines.Count && i < bh - 2; i++)
                {
                    Text(bc + 1, br + 1 + i, lines[i], bw - 2);
                }
                DrawChildren(e);
                break;
            }
            case "groupbox":
                Box(b.C, b.R, b.W, b.H);
                Text(b.C + 2, b.R, $" {e.Text} ", b.W - 3);
                DrawChildren(e);
                break;
            case "border":
            case "window":
                Box(b.C, b.R, b.W, b.H);
                if (e.Kind == "window" && e.Text.Length > 0)
                {
                    Text(b.C + 2, b.R, $" {e.Text} ", b.W - 3);
                }
                DrawChildren(e);
                break;
            case "scrollviewer":
            {
                var saved = _clip;
                _clip = (Math.Max(saved.X0, b.C), Math.Max(saved.Y0, b.R),
                    Math.Min(saved.X1, b.C + b.W), Math.Min(saved.Y1, b.R + b.H));
                DrawChildren(e);
                _clip = saved;
                if (e.ContentH > e.LayoutH && b.H > 0)
                {
                    int thumb = Math.Clamp(e.ScrollOffset * b.H / Math.Max(1, e.ContentH), 0, b.H - 1);
                    Put(b.C + b.W - 1, b.R + thumb, Full);
                }
                break;
            }
            case "image":
                Fill(b.C, b.R, b.W, b.H, Light);
                Text(b.C, b.R, e.Text.Length > 0 ? e.Text : "[img]", b.W);
                break;
            case "rect":
                Box(b.C, b.R, b.W, b.H);
                break;
            case "ellipse":
                DrawEllipse(b);
                break;
            case "line":
                Line(b.C, b.R, (e.LayoutX + e.X2 - e.X) / CellWidth, (e.LayoutY + e.Y2 - e.Y) / CellHeight, Unicode ? '•' : '*');
                break;
            case "polygon":
                for (int i = 0; i + 3 < e.Points.Count; i += 2)
                {
                    Line((e.LayoutX + e.Points[i]) / CellWidth, (e.LayoutY + e.Points[i + 1]) / CellHeight,
                        (e.LayoutX + e.Points[i + 2]) / CellWidth, (e.LayoutY + e.Points[i + 3]) / CellHeight,
                        Unicode ? '•' : '*');
                }
                break;
            default:
                // stack, panel, grid, canvas, dockpanel, tabitem: layout only.
                DrawChildren(e);
                break;
        }
    }

    private void DrawChildren(UiElement e)
    {
        foreach (var child in e.Children)
        {
            Draw(child);
        }
    }

    private void DrawChart(UiElement e, Cells b)
    {
        if (e.Series.Count == 0 || b.W < 1)
        {
            return;
        }
        int lo = e.Series.Min();
        int hi = e.Series.Max();
        int range = Math.Max(1, hi - lo);
        string ramp = Unicode ? "▁▂▃▄▅▆▇█" : "_.-=+*#@";
        int n = Math.Min(e.Series.Count, b.W);
        int start = e.Series.Count - n;
        if (b.H <= 1)
        {
            for (int i = 0; i < n; i++)
            {
                int level = (e.Series[start + i] - lo) * (ramp.Length - 1) / range;
                Put(b.C + i, b.R, ramp[level]);
            }
            return;
        }
        // Taller charts: columns of full blocks topped with a partial one.
        int levels = b.H * 8;
        for (int i = 0; i < n; i++)
        {
            int height = (e.Series[start + i] - lo) * (levels - 1) / range + 1;
            for (int row = 0; row < b.H; row++)
            {
                int fromBottom = height - row * 8;
                char ch = fromBottom >= 8 ? ramp[^1] : fromBottom > 0 ? ramp[fromBottom - 1] : ' ';
                Put(b.C + i, b.R + b.H - 1 - row, ch);
            }
        }
    }

    private void DrawCalendar(UiElement e, Cells b)
    {
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
        Text(b.C, b.R, $"{months[Math.Clamp(e.Month, 1, 12) - 1]} {e.Year}", b.W);
        Text(b.C, b.R + 1, "Su Mo Tu We Th Fr Sa", b.W);
        int first = UiElement.FirstWeekday(e.Year, e.Month);
        int days = UiElement.DaysInMonth(e.Year, e.Month);
        for (int d = 1; d <= days; d++)
        {
            int index = first + d - 1;
            int row = b.R + 2 + index / 7;
            string label = d == e.Value ? $"{d,2}*" : $"{d,2}";
            Text(b.C + (index % 7) * 3, row, label, 3);
        }
    }

    private void DrawTree(UiElement node, int depth)
    {
        foreach (var child in node.Children)
        {
            var b = CellsOf(child);
            string marker = child.Children.Count > 0 ? (child.Checked ? "- " : "+ ") : "  ";
            Text(b.C, b.R, marker + (ReferenceEquals(child, Focused) ? ">" : "") + child.Text, Math.Max(b.W, child.Text.Length + 3));
            if (child.Checked)
            {
                DrawTree(child, depth + 1);
            }
        }
    }

    private void DrawEllipse(Cells b)
    {
        double rx = (b.W - 1) / 2.0, ry = (b.H - 1) / 2.0;
        double cx = b.C + rx, cy = b.R + ry;
        int steps = Math.Max(16, (b.W + b.H) * 4);
        for (int i = 0; i < steps; i++)
        {
            double a = 2 * Math.PI * i / steps;
            Put((int)Math.Round(cx + rx * Math.Cos(a)), (int)Math.Round(cy + ry * Math.Sin(a)), Unicode ? '•' : 'o');
        }
    }
}
