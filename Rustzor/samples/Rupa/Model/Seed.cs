namespace Rupa.Model;

/// <summary>
/// The sample file: three screens of "Lintas", a commuter-rail app for Jabodetabek
/// (home, timetable, ticket), its components and colour styles. The app is invented;
/// stations and lines are real.
/// </summary>
public static class Seed
{
    private sealed class B
    {
        public readonly Doc Doc = new();

        public Node Add(Node parent, Node n)
        {
            n.Id = Doc.NewId();
            n.Parent = parent;
            parent.Children.Add(n);
            return n;
        }

        public Node Frame(Node parent, string name, double x, double y, double w, double h, string fill, double radius = 0, bool clip = true) =>
            Add(parent, new Node { Kind = NodeKind.Frame, Name = name, X = x, Y = y, W = w, H = h, Fill = fill, Radius = radius, Clip = clip });

        public Node Rect(Node parent, string name, double x, double y, double w, double h, string fill, double radius = 0) =>
            Add(parent, new Node { Kind = NodeKind.Rect, Name = name, X = x, Y = y, W = w, H = h, Fill = fill, Radius = radius });

        public Node Ellipse(Node parent, string name, double x, double y, double w, double h, string fill) =>
            Add(parent, new Node { Kind = NodeKind.Ellipse, Name = name, X = x, Y = y, W = w, H = h, Fill = fill });

        /// <summary>A text layer; centred text is placed by its centre, the rest by the left edge.</summary>
        public Node Text(Node parent, string name, double x, double y, string text, double size, int weight, string fill, Align align = Align.Start) =>
            Add(parent, new Node
            {
                Kind = NodeKind.Text, Name = name, X = align == Align.Center ? x - text.Length * size * 0.275 : x, Y = y, Text = text,
                FontSize = size, Weight = weight, Fill = fill, TextAlign = align, W = text.Length * size * 0.55, H = size * 1.3,
                LineHeight = text.Contains('\n') ? 1.45 : 1.3,
            });

        public Node Line(Node parent, string name, double x, double y, double length, string stroke, double width) =>
            Add(parent, new Node { Kind = NodeKind.Line, Name = name, X = x, Y = y, W = length, H = 0, Stroke = stroke, StrokeWidth = width });

        public Node Instance(Node parent, Node main, double x, double y, params (Node Text, string Value)[] overrides)
        {
            var n = Add(parent, new Node { Kind = NodeKind.Instance, Name = main.Name, MainId = main.Id, X = x, Y = y, W = main.W, H = main.H });
            foreach (var (t, v) in overrides) n.Overrides[t.Id] = v;
            return n;
        }
    }

    // Colour styles.
    private const string Night = "s:night", Signal = "s:signal", Ink = "s:ink", Grey = "s:grey", Mist = "s:mist", Paper = "s:paper", White = "s:white";
    private const string Bogor = "s:bogor", Cikarang = "s:cikarang", Rangkas = "s:rangkas", Tangerang = "s:tangerang", Priok = "s:priok";

    public static Doc Create()
    {
        var b = new B();
        var d = b.Doc;
        d.Name = "Lintas — commuter app";
        d.PageColor = "#e4e9e6";
        d.Styles.AddRange(new[]
        {
            new ColorStyle { Id = "night", Name = "Lintas / Night blue", Hex = "#14234b" },
            new ColorStyle { Id = "signal", Name = "Lintas / Signal red", Hex = "#e5322d" },
            new ColorStyle { Id = "ink", Name = "Neutral / 900", Hex = "#1b1f2a" },
            new ColorStyle { Id = "grey", Name = "Neutral / 500", Hex = "#69728a" },
            new ColorStyle { Id = "mist", Name = "Neutral / 100", Hex = "#e9edf4" },
            new ColorStyle { Id = "paper", Name = "Neutral / Background", Hex = "#f5f6fa" },
            new ColorStyle { Id = "white", Name = "Neutral / White", Hex = "#ffffff" },
            new ColorStyle { Id = "bogor", Name = "Line / Bogor", Hex = "#d7262f" },
            new ColorStyle { Id = "cikarang", Name = "Line / Cikarang", Hex = "#1e7fcb" },
            new ColorStyle { Id = "rangkas", Name = "Line / Rangkasbitung", Hex = "#1e9e5a" },
            new ColorStyle { Id = "tangerang", Name = "Line / Tangerang", Hex = "#8b5a2b" },
            new ColorStyle { Id = "priok", Name = "Line / Tanjung Priok", Hex = "#d9609c" },
        });
        var root = d.Root;

        b.Text(root, "Title", 0, -150, "Lintas · iteration 3", 40, 800, Ink);
        b.Text(root, "Subtitle", 2, -92, "Commuter-rail app for Jabodetabek. Home, timetable and ticket, ready for the usability round on Thursday.", 16, 500, Grey);

        // ── components ────────────────────────────────────────────────────
        var cy = 960.0;
        b.Text(root, "Components heading", 0, cy - 84, "Components", 22, 800, Ink);

        var button = b.Frame(root, "Primary button", 0, cy, 342, 52, Signal, 26);
        button.IsComponent = true;
        button.Layout = LayoutMode.Row;
        button.CrossAlign = Align.Center;
        button.MainAlign = Align.Center;
        button.Padding = 14;
        var buttonLabel = b.Text(button, "Label", 0, 0, "Continue", 16, 700, White);

        var chip = b.Frame(root, "Line chip", 400, cy, 96, 28, Bogor, 14);
        chip.IsComponent = true;
        chip.Layout = LayoutMode.Row;
        chip.CrossAlign = Align.Center;
        chip.Padding = 6;
        chip.Gap = 6;
        chip.Hug = true;
        b.Ellipse(chip, "Dot", 0, 0, 8, 8, White);
        var chipLabel = b.Text(chip, "Line", 0, 0, "Bogor line", 12, 700, White);

        var tile = b.Frame(root, "Shortcut", 580, cy, 76, 88, White, 16);
        tile.IsComponent = true;
        tile.Stroke = Mist;
        tile.StrokeWidth = 1;
        tile.Layout = LayoutMode.Column;
        tile.CrossAlign = Align.Center;
        tile.MainAlign = Align.Center;
        tile.Gap = 10;
        tile.Padding = 8;
        var tileIcon = b.Frame(tile, "Icon", 0, 0, 36, 36, Paper, 12);
        b.Rect(tileIcon, "Glyph", 11, 11, 14, 14, Night, 4);
        var tileLabel = b.Text(tile, "Label", 0, 0, "Timetable", 12, 600, Ink);

        var row = b.Frame(root, "Timetable row", 740, cy, 342, 72, White, 16);
        row.IsComponent = true;
        b.Rect(row, "Line colour", 14, 16, 4, 40, Bogor, 2);
        var rowTime = b.Text(row, "Time", 32, 14, "06.42", 22, 800, Ink);
        var rowDest = b.Text(row, "Destination", 112, 14, "Jakarta Kota", 15, 700, Ink);
        var rowMeta = b.Text(row, "Platform", 112, 38, "Platform 2 · 12 cars", 12, 500, Grey);
        var pill = b.Frame(row, "Status", 236, 22, 92, 28, "#e6f6ee", 14);
        pill.Layout = LayoutMode.Row;
        pill.CrossAlign = Align.Center;
        pill.MainAlign = Align.Center;
        pill.Padding = 6;
        var rowStatus = b.Text(pill, "Status label", 0, 0, "On time", 12, 700, "#137a47");

        // ── home ──────────────────────────────────────────────────────────
        var home = b.Frame(root, "Home", 0, 0, 390, 844, Paper, 0);
        StatusBar(b, home, Ink);
        b.Text(home, "Greeting", 24, 64, "Selamat pagi, Dita", 26, 800, Night);
        b.Text(home, "Date", 24, 100, "Wednesday 22 October · Depok", 14, 500, Grey);

        var card = b.Frame(home, "Card balance", 24, 140, 342, 160, Night, 22);
        b.Ellipse(card, "Arc large", 214, -70, 220, 220, Signal).Opacity = 0.92;
        b.Ellipse(card, "Arc small", 270, 92, 120, 120, "#2b3d72");
        b.Text(card, "Label", 22, 22, "Multi-trip card balance", 13, 600, "#b8c3e3");
        b.Text(card, "Amount", 22, 46, "Rp48.500", 34, 800, White);
        b.Text(card, "Card number", 22, 98, "6032 9841 0077 · tap at the gate", 12, 500, "#b8c3e3");
        var top = b.Frame(card, "Top up", 22, 120, 90, 28, White, 14);
        top.Layout = LayoutMode.Row;
        top.CrossAlign = Align.Center;
        top.MainAlign = Align.Center;
        b.Text(top, "Label", 0, 0, "Top up", 12, 700, Night);

        b.Text(home, "Next heading", 24, 326, "Next train", 18, 800, Ink);
        b.Text(home, "Next link", 296, 329, "Timetable", 14, 700, Signal);
        var next = b.Frame(home, "Next train", 24, 360, 342, 176, White, 18);
        next.Stroke = Mist;
        next.StrokeWidth = 1;
        b.Instance(next, chip, 18, 18);
        b.Text(next, "Route", 18, 58, "Depok Baru → Jakarta Kota", 17, 800, Ink);
        b.Text(next, "Departs", 18, 88, "06.42", 40, 800, Night);
        b.Text(next, "In", 136, 96, "in 3 min", 15, 700, Signal);
        b.Text(next, "Platform", 136, 118, "Platform 2 · 12 cars", 12, 500, Grey);
        var track = b.Line(next, "Track", 18, 154, 306, Mist, 4);
        track.Name = "Track";
        string[] stops = { "Depok Baru", "Manggarai", "Juanda", "Jakarta Kota" };
        for (var i = 0; i < stops.Length; i++)
        {
            var x = 18 + i * 102 - 6;
            var dot = b.Ellipse(next, stops[i], x, 148, 12, 12, i == 0 ? Bogor : White);
            dot.Stroke = Bogor;
            dot.StrokeWidth = 3;
        }

        var shortcuts = b.Frame(home, "Shortcuts", 24, 562, 342, 88, "none", 0, clip: false);
        shortcuts.Layout = LayoutMode.Row;
        shortcuts.Gap = 12;
        b.Instance(shortcuts, tile, 0, 0, (tileLabel, "Timetable"));
        b.Instance(shortcuts, tile, 0, 0, (tileLabel, "Route map"));
        b.Instance(shortcuts, tile, 0, 0, (tileLabel, "Tickets"));
        b.Instance(shortcuts, tile, 0, 0, (tileLabel, "History"));

        var notice = b.Frame(home, "Service notice", 24, 670, 342, 64, "#fff2dc", 16);
        b.Rect(notice, "Mark", 16, 18, 28, 28, "#f2a531", 9);
        b.Text(notice, "Notice", 56, 13, "Manggarai–Jatinegara is running ±10 min late", 13, 700, "#6b4300");
        b.Text(notice, "Updated", 56, 35, "Updated 06.31 · signal maintenance", 12, 500, "#8f6a2c");

        TabBar(b, home, 0);

        // ── timetable ─────────────────────────────────────────────────────
        var sched = b.Frame(root, "Timetable", 470, 0, 390, 844, Paper, 0);
        StatusBar(b, sched, Ink);
        b.Text(sched, "Heading", 24, 64, "Timetable", 28, 800, Night);
        var picker = b.Frame(sched, "Stations", 24, 112, 342, 112, White, 18);
        picker.Stroke = Mist;
        picker.StrokeWidth = 1;
        b.Text(picker, "From label", 20, 16, "From", 12, 600, Grey);
        b.Text(picker, "From", 20, 32, "Depok Baru", 17, 800, Ink);
        b.Line(picker, "Divider", 20, 56, 250, Mist, 1);
        b.Text(picker, "To label", 20, 64, "To", 12, 600, Grey);
        b.Text(picker, "To", 20, 80, "Jakarta Kota", 17, 800, Ink);
        var swap = b.Ellipse(picker, "Swap", 286, 36, 40, 40, Paper);
        swap.Stroke = Mist;
        swap.StrokeWidth = 1;
        var arrows = b.Add(picker, new Node { Kind = NodeKind.Path, Name = "Swap arrows", X = 299, Y = 47, W = 14, H = 18, Stroke = Night, StrokeWidth = 2 });
        arrows.Points.AddRange(new[] { new Pt(4, 18), new Pt(4, 2), new Pt(0, 6), new Pt(4, 2), new Pt(8, 6) });

        var days = b.Frame(sched, "Days", 24, 240, 342, 32, "none", 0, clip: false);
        days.Layout = LayoutMode.Row;
        days.Gap = 8;
        days.Hug = true;
        foreach (var (label, on) in new[] { ("Today", true), ("Tomorrow", false), ("Fri 24", false) })
        {
            var dchip = b.Frame(days, label, 0, 0, 80, 32, on ? Night : White, 16);
            dchip.Layout = LayoutMode.Row;
            dchip.CrossAlign = Align.Center;
            dchip.Padding = 8;
            dchip.Hug = true;
            if (!on)
            {
                dchip.Stroke = Mist;
                dchip.StrokeWidth = 1;
            }
            b.Text(dchip, "Label", 0, 0, "  " + label + "  ", 13, 700, on ? White : Ink);
        }

        var list = b.Frame(sched, "Departures", 24, 286, 342, 470, "none", 0, clip: false);
        list.Layout = LayoutMode.Column;
        list.Gap = 8;
        list.Hug = true;
        (string T, string D, string M, string S)[] trains =
        {
            ("06.42", "Jakarta Kota", "Platform 2 · 12 cars", "On time"),
            ("06.51", "Manggarai", "Platform 2 · 10 cars", "On time"),
            ("07.03", "Jakarta Kota", "Platform 1 · 12 cars", "+4 min"),
            ("07.12", "Angke", "Platform 2 · 12 cars", "On time"),
            ("07.24", "Jakarta Kota", "Platform 2 · 12 cars", "On time"),
            ("07.36", "Manggarai", "Platform 1 · 10 cars", "Full"),
        };
        foreach (var t in trains)
            b.Instance(list, row, 0, 0, (rowTime, t.T), (rowDest, t.D), (rowMeta, t.M), (rowStatus, t.S));

        TabBar(b, sched, 1);

        // ── ticket ────────────────────────────────────────────────────────
        var ticket = b.Frame(root, "Ticket", 940, 0, 390, 844, Night, 0);
        StatusBar(b, ticket, White);
        b.Text(ticket, "Heading", 24, 64, "Your ticket", 28, 800, White);
        b.Text(ticket, "Sub", 24, 102, "Single journey · valid today until 23.59", 14, 500, "#b8c3e3");
        var stub = b.Frame(ticket, "Ticket card", 24, 140, 342, 560, White, 26);
        b.Text(stub, "From label", 24, 24, "From", 12, 600, Grey);
        b.Text(stub, "From", 24, 40, "Depok Baru", 20, 800, Ink);
        b.Text(stub, "To label", 214, 24, "To", 12, 600, Grey);
        b.Text(stub, "To", 214, 40, "Jakarta Kota", 20, 800, Ink);
        b.Line(stub, "Route rule", 24, 86, 294, Mist, 2);
        b.Add(stub, new Node { Kind = NodeKind.Image, Name = "Gate code", X = 61, Y = 110, W = 220, H = 220, Href = QrDataUrl() });
        b.Text(stub, "Hint", 171, 346, "Hold this code to the gate scanner", 13, 600, Grey, Align.Center);
        b.Line(stub, "Tear line", 22, 392, 298, Mist, 2);
        b.Ellipse(stub, "Notch left", -14, 378, 28, 28, Night);
        b.Ellipse(stub, "Notch right", 328, 378, 28, 28, Night);
        b.Text(stub, "Fare label", 24, 420, "Fare", 12, 600, Grey);
        b.Text(stub, "Fare", 24, 436, "Rp5.000", 20, 800, Ink);
        b.Text(stub, "Valid label", 214, 420, "Valid until", 12, 600, Grey);
        b.Text(stub, "Valid", 214, 436, "23.59 today", 20, 800, Ink);
        b.Text(stub, "Ref", 24, 494, "Ref LNT-2210-8841 · Bogor line", 12, 500, Grey);
        b.Instance(ticket, button, 24, 732, (buttonLabel, "Add to wallet"));

        // ── note ──────────────────────────────────────────────────────────
        var note = b.Frame(root, "Note for the team", 1410, 0, 300, 220, "#ffe7a3", 4);
        b.Text(note, "Note", 20, 20, "For Thursday's round:\n· Does the balance card read\n  before the next train?\n· Line colours follow the\n  official route map.\n· Try the ticket in sunlight.", 15, 600, "#5b4500");
        b.Text(note, "Signed", 20, 186, "— Ayu, product design", 12, 700, "#8a6b14");

        d.Reindex();
        return d;
    }

    private static void StatusBar(B b, Node screen, string ink)
    {
        b.Text(screen, "Clock", 28, 16, "06.38", 15, 700, ink);
        var bars = b.Frame(screen, "Status icons", 300, 18, 64, 14, "none", 0, clip: false);
        for (var i = 0; i < 4; i++) b.Rect(bars, "Signal " + (i + 1), i * 5, 10 - i * 3, 3, 4 + i * 3, ink, 1);
        b.Rect(bars, "Battery", 30, 1, 24, 12, "none", 3).Stroke = ink;
        bars.Children[^1].StrokeWidth = 1.5;
        b.Rect(bars, "Charge", 33, 4, 15, 6, ink, 1.5);
    }

    private static void TabBar(B b, Node screen, int active)
    {
        var bar = b.Frame(screen, "Tab bar", 0, 760, 390, 84, White, 0);
        b.Line(bar, "Top rule", 0, 0, 390, Mist, 1);
        string[] tabs = { "Home", "Timetable", "Tickets", "Account" };
        for (var i = 0; i < tabs.Length; i++)
        {
            var x = 24 + i * 90;
            var on = i == active;
            b.Rect(bar, tabs[i] + " icon", x + 24, 16, 22, 22, on ? Signal : Mist, 7);
            b.Text(bar, tabs[i], x + 35, 46, tabs[i], 11, on ? 800 : 600, on ? Signal : Grey, Align.Center);
        }
    }

    /// <summary>A gate code drawn as an SVG image (not a scannable code).</summary>
    private static string QrDataUrl()
    {
        const int size = 25;
        var sb = new System.Text.StringBuilder();
        sb.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 25 25' shape-rendering='crispEdges'><rect width='25' height='25' fill='#fff'/><path fill='#14234b' d='");
        var seed = 2210u;
        bool Finder(int x, int y) => x < 7 && y < 7 || x >= size - 7 && y < 7 || x < 7 && y >= size - 7;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                bool on;
                if (Finder(x, y))
                {
                    var fx = x >= size - 7 ? x - (size - 7) : x;
                    var fy = y >= size - 7 ? y - (size - 7) : y;
                    on = fx == 0 || fx == 6 || fy == 0 || fy == 6 || fx >= 2 && fx <= 4 && fy >= 2 && fy <= 4;
                }
                else if (x == 7 || y == 7 && x < 8 || y == 7 && x >= size - 8 || x == size - 8 && y < 8 || y == size - 8 && x < 8) on = false;
                else on = (seed >> 13 & 3) < 2;
                if (on) sb.Append('M').Append(x).Append(' ').Append(y).Append("h1v1h-1z");
            }
        sb.Append("'/></svg>");
        var svg = sb.ToString().Replace("#", "%23").Replace("<", "%3C").Replace(">", "%3E");
        return "data:image/svg+xml;utf8," + svg;
    }
}
