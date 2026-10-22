using System.Globalization;
using System.Text;
using Rustzor.Browser.Storage;
using Rustzor.Interop;

namespace Tilik.Model;

/// <summary>One node of the assembly: a group, or a part with geometry.</summary>
public sealed class Node
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int Parent { get; init; } = -1;
    /// <summary>The engine's part index, or -1 for a group.</summary>
    public int Part { get; init; } = -1;
    public string PartNo { get; init; } = "";
    public string Description { get; init; } = "";
    public string Material { get; init; } = "";
    public string MakeBuy { get; init; } = "";
    public string Stage { get; init; } = "";
    public double Density { get; init; }
    /// <summary>Cubic metres, from the closed mesh.</summary>
    public double Volume { get; init; }
    public int Triangles { get; init; }
    /// <summary>Bounding box size in metres (x, y, z), or null for a group.</summary>
    public double[]? Box { get; init; }
    public string Color { get; init; } = "";
    public List<Node> Children { get; } = new();

    public bool IsPart => Part >= 0;
    /// <summary>Kilograms: volume × density, when the density is known.</summary>
    public double Mass => Density > 0 ? Volume * Density : 0;
}

public sealed record Measure(int Id, string Kind, double[][] Points, double Value, string Label, string? PartName);

public sealed class Note
{
    public int Id { get; set; }
    public int N { get; set; }
    public double[] Point { get; set; } = new double[3];
    public int Part { get; set; } = -1;
    public string PartName { get; set; } = "";
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public string When { get; set; } = "";
    public bool Resolved { get; set; }
}

public sealed class SavedView
{
    public string Name { get; set; } = "";
    public string Camera { get; set; } = "{}";
    public string Mode { get; set; } = "shaded";
    public bool Section { get; set; }
    public string Axis { get; set; } = "x";
    public double Offset { get; set; } = 0.5;
    public bool Flip { get; set; }
    public double Explode { get; set; }
    public List<int> Hidden { get; set; } = new();
}

/// <summary>Everything the review knows. The engine (rustzor.module.js) draws; this decides.</summary>
public static class Review
{
    public const string ModelUrl = "models/rw63.glb";

    public static event Action? Changed;
    public static void Notify() => Changed?.Invoke();

    // ── the model ───────────────────────────────────────────────────────
    public static bool Ready { get; private set; }
    public static string? Error { get; private set; }
    public static string File { get; private set; } = "";
    public static string Product { get; private set; } = "";
    public static string Revision { get; private set; } = "";
    public static string Maker { get; private set; } = "";
    public static string Ratio { get; private set; } = "";
    public static int LoadMs { get; private set; }
    public static double[] Size { get; private set; } = new double[3];
    public static List<Node> Nodes { get; } = new();
    public static List<Node> Roots { get; } = new();
    public static Dictionary<int, Node> ByPart { get; } = new();

    // ── what is shown ───────────────────────────────────────────────────
    public static HashSet<int> Hidden { get; } = new();
    public static HashSet<int>? Isolated { get; private set; }
    public static HashSet<int> Selected { get; } = new();
    public static Node? Focus { get; private set; }
    public static string Mode { get; private set; } = "edges";
    public static bool Ortho { get; private set; }
    public static double Explode { get; private set; }
    public static bool SectionOn { get; private set; }
    public static string Axis { get; private set; } = "x";
    public static double Offset { get; private set; } = 0.5;
    public static bool Flip { get; private set; }
    public static string Tool { get; private set; } = "select";
    public static string Panel { get; set; } = "part";
    public static int MeasureStep { get; private set; }
    public static int MeasureNeed { get; private set; }
    public static HashSet<int> Expanded { get; } = new();

    // ── what the reviewer adds ──────────────────────────────────────────
    public static List<Measure> Measures { get; } = new();
    public static List<Note> Notes { get; } = new();
    public static List<SavedView> Views { get; } = new();
    public static (double[] Point, int Part)? PendingNote { get; private set; }
    public static int? ActiveNote { get; private set; }
    public static string Reviewer { get; set; } = "Dimas Prasetyo";

    private static int _nextMeasure = 1;
    public static bool Attached { get; set; }

    // ── messages from the engine ────────────────────────────────────────
    public static void OnMessage(JsonValue m)
    {
        switch (m["type"].AsString())
        {
            case "loaded":
                OnLoaded(m);
                break;
            case "error":
                Error = m["message"].AsString();
                Notify();
                break;
            case "pick":
                Pick(m["part"].AsInt(), m["additive"].AsBool());
                break;
            case "measure":
                AddMeasure(m);
                break;
            case "measureStep":
                MeasureStep = m["count"].AsInt();
                MeasureNeed = m["need"].AsInt();
                Notify();
                break;
            case "notePlace":
                var p = m["point"];
                PendingNote = (new[] { p[0].AsDouble(), p[1].AsDouble(), p[2].AsDouble() }, m["part"].AsInt());
                Panel = "notes";
                Notify();
                break;
            case "section":
                Offset = m["offset"].AsDouble();
                Notify();
                break;
            case "sectionEnd":
                Offset = m["offset"].AsDouble();
                Notify();
                break;
        }
    }

    /// <summary>Loads the model's tree without drawing it, for pages without the viewer.</summary>
    public static async Task EnsureLoadedAsync()
    {
        if (Ready) return;
        try
        {
            OnLoaded(await Js.CallAsync("tk.inspect", Js.Args("url", ModelUrl)));
        }
        catch (Exception e)
        {
            Error = e.Message;
            Notify();
        }
    }

    private static void OnLoaded(JsonValue m)
    {
        // the same model again (back from another page): keep what the reviewer had set up
        var same = Ready && File == m["file"].AsString();
        var focusId = Focus?.Id;
        Nodes.Clear();
        Roots.Clear();
        ByPart.Clear();
        if (!same)
        {
            Hidden.Clear();
            Selected.Clear();
            Isolated = null;
            Measures.Clear();
            Explode = 0;
            SectionOn = false;
        }
        Focus = null;
        PendingNote = null;
        Error = null;
        File = m["file"].AsString();
        LoadMs = m["loadMs"].AsInt();
        var info = m["info"];
        Product = info["product"].AsStringOrNull() ?? File;
        Revision = info["revision"].AsStringOrNull() ?? "";
        Maker = info["maker"].AsStringOrNull() ?? "";
        Ratio = info["ratio"].AsStringOrNull() ?? "";
        var size = m["size"];
        Size = new[] { size[0].AsDouble(), size[1].AsDouble(), size[2].AsDouble() };
        foreach (var n in m["nodes"].Items)
        {
            var x = n["extras"];
            var box = n["box"];
            var node = new Node
            {
                Id = n["id"].AsInt(),
                Name = n["name"].AsString(),
                Parent = n["parent"].AsInt(),
                Part = n["part"].AsInt(),
                PartNo = x["part_no"].AsStringOrNull() ?? "",
                Description = x["description"].AsStringOrNull() ?? "",
                Material = x["material"].AsStringOrNull() ?? n["material"].AsString(),
                MakeBuy = x["make_buy"].AsStringOrNull() ?? "",
                Stage = x["stage"].AsStringOrNull() ?? "",
                Density = x["density"].IsNumber ? x["density"].AsDouble() : 0,
                Volume = n["volume"].AsDouble(),
                Triangles = n["tris"].AsInt(),
                Box = box.IsArray ? new[] { box[0].AsDouble(), box[1].AsDouble(), box[2].AsDouble() } : null,
                Color = n["color"].AsString(),
            };
            Nodes.Add(node);
            if (node.IsPart) ByPart[node.Part] = node;
        }
        foreach (var node in Nodes)
        {
            if (node.Parent >= 0 && node.Parent < Nodes.Count) Nodes[node.Parent].Children.Add(node);
            else Roots.Add(node);
        }
        if (same && focusId is { } fid && fid < Nodes.Count) Focus = Nodes[fid];
        if (!same)
        {
            Expanded.Clear();
            foreach (var r in Roots) Expanded.Add(r.Id);
        }
        LoadNotes();
        LoadViews();
        Ready = true;
        Push();
        PushNotes();
        PushMeasures();
        Notify();
    }

    // ── the tree ────────────────────────────────────────────────────────
    public static IEnumerable<int> PartsUnder(Node n)
    {
        if (n.IsPart) yield return n.Part;
        foreach (var c in n.Children)
            foreach (var p in PartsUnder(c))
                yield return p;
    }

    public static bool IsHidden(Node n)
    {
        var any = false;
        foreach (var p in PartsUnder(n))
        {
            any = true;
            if (!Hidden.Contains(p)) return false;
        }
        return any;
    }

    public static bool IsShown(int part) => !Hidden.Contains(part) && (Isolated is null || Isolated.Contains(part));

    public static void ToggleHidden(Node n)
    {
        var hide = !IsHidden(n);
        foreach (var p in PartsUnder(n))
        {
            if (hide) Hidden.Add(p);
            else Hidden.Remove(p);
        }
        Push();
        Notify();
    }

    public static void Isolate(Node? n)
    {
        if (n is null)
        {
            Isolated = null;
        }
        else
        {
            Isolated = new HashSet<int>(PartsUnder(n));
            foreach (var p in Isolated) Hidden.Remove(p);
        }
        Push();
        Fit();
        Notify();
    }

    public static void ShowAll()
    {
        var wasIsolated = Isolated is not null;
        Hidden.Clear();
        Isolated = null;
        Push();
        if (wasIsolated) Fit();
        Notify();
    }

    public static void Pick(int part, bool additive)
    {
        if (!additive) Selected.Clear();
        if (part >= 0 && ByPart.TryGetValue(part, out var node))
        {
            if (additive && Selected.Contains(part)) Selected.Remove(part);
            else Selected.Add(part);
            Focus = node;
            ExpandTo(node);
            Panel = "part";
        }
        else if (!additive)
        {
            Focus = null;
        }
        Push();
        Notify();
    }

    public static void Select(Node n, bool additive = false)
    {
        if (!additive) Selected.Clear();
        foreach (var p in PartsUnder(n)) Selected.Add(p);
        Focus = n;
        ExpandTo(n);
        Push();
        Notify();
    }

    private static void ExpandTo(Node n)
    {
        var p = n.Parent;
        while (p >= 0 && p < Nodes.Count)
        {
            Expanded.Add(p);
            p = Nodes[p].Parent;
        }
    }

    public static void Toggle(Node n)
    {
        if (!Expanded.Remove(n.Id)) Expanded.Add(n.Id);
        Notify();
    }

    public static void FocusOn(Node n)
    {
        if (n.IsPart) Js.Send("tk.focusPart", Js.Args("part", n.Part));
    }

    // ── display ─────────────────────────────────────────────────────────
    public static void SetMode(string mode)
    {
        Mode = mode;
        Push();
        Notify();
    }

    public static void SetOrtho(bool on)
    {
        Ortho = on;
        Push();
        Notify();
    }

    public static void SetExplode(double f)
    {
        Explode = Math.Clamp(f, 0, 1);
        Push();
        Notify();
    }

    public static void SetSection(bool? on = null, string? axis = null, double? offset = null, bool? flip = null)
    {
        if (on is { } o) SectionOn = o;
        if (axis is not null) Axis = axis;
        if (offset is { } f) Offset = Math.Clamp(f, 0, 1);
        if (flip is { } fl) Flip = fl;
        if (SectionOn && Tool != "section" && on == true) SetTool("section");
        Push();
        Notify();
    }

    public static void SetTool(string tool)
    {
        Tool = tool;
        MeasureStep = 0;
        MeasureNeed = tool switch { "measure-distance" => 2, "measure-angle" => 3, "measure-edge" => 1, _ => 0 };
        if (tool.StartsWith("measure")) Panel = "measure";
        if (tool == "note") Panel = "notes";
        if (tool == "section" && !SectionOn)
        {
            SectionOn = true;
            Push();
        }
        Js.Send("tk.tool", Js.Args("tool", tool == "measure-distance" ? "measure-dist" : tool));
        Notify();
    }

    public static void View(string preset) => Js.Send("tk.view", Js.Args("preset", preset));
    public static void Fit() => Js.Send("tk.fit");

    /// <summary>Sends what is shown to the engine in one message.</summary>
    public static void Push()
    {
        if (!Attached) return;
        Js.Send("tk.state", w =>
        {
            w.BeginObject();
            w.Name("hidden").BeginArray();
            foreach (var p in Hidden) w.Number(p);
            w.EndArray();
            w.Name("isolate");
            if (Isolated is null) w.Null();
            else
            {
                w.BeginArray();
                foreach (var p in Isolated) w.Number(p);
                w.EndArray();
            }
            w.Name("selected").BeginArray();
            foreach (var p in Selected) w.Number(p);
            w.EndArray();
            w.Prop("mode", Mode);
            w.Prop("explode", Explode);
            w.Prop("ortho", Ortho);
            w.Name("section").BeginObject().Prop("on", SectionOn).Prop("axis", Axis).Prop("offset", Offset).Prop("flip", Flip).EndObject();
            w.EndObject();
        });
    }

    // ── measurements ────────────────────────────────────────────────────
    private static void AddMeasure(JsonValue m)
    {
        var kind = m["kind"].AsString();
        var pts = new List<double[]>();
        foreach (var p in m["pts"].Items) pts.Add(new[] { p[0].AsDouble(), p[1].AsDouble(), p[2].AsDouble() });
        var value = m["value"].AsDouble();
        var label = kind == "angle" ? value.ToString("0.0", CultureInfo.InvariantCulture) + "°" : value.ToString("0.00", CultureInfo.InvariantCulture) + " mm";
        string? part = m.Has("part") && ByPart.TryGetValue(m["part"].AsInt(), out var n) ? n.Name : null;
        Measures.Add(new Measure(_nextMeasure++, kind, pts.ToArray(), value, label, part));
        MeasureStep = 0;
        PushMeasures();
        Notify();
    }

    public static void RemoveMeasure(int id)
    {
        Measures.RemoveAll(x => x.Id == id);
        PushMeasures();
        Notify();
    }

    public static void ClearMeasures()
    {
        Measures.Clear();
        PushMeasures();
        Notify();
    }

    private static void PushMeasures()
    {
        if (!Attached) return;
        Js.Send("tk.measures", w =>
        {
            w.BeginObject().Name("list").BeginArray();
            foreach (var x in Measures)
            {
                w.BeginObject().Prop("id", x.Id).Prop("kind", x.Kind).Prop("label", x.Label).Name("pts").BeginArray();
                foreach (var p in x.Points) w.BeginArray().Number(p[0]).Number(p[1]).Number(p[2]).EndArray();
                w.EndArray().EndObject();
            }
            w.EndArray().EndObject();
        });
    }

    // ── notes ───────────────────────────────────────────────────────────
    private static string Key(string what) => $"tilik:{what}:{File}";

    public static void AddNote(string text)
    {
        if (PendingNote is not { } pn || string.IsNullOrWhiteSpace(text)) return;
        var id = Notes.Count == 0 ? 1 : Notes.Max(x => x.Id) + 1;
        var n = Notes.Count == 0 ? 1 : Notes.Max(x => x.N) + 1;
        Notes.Add(new Note
        {
            Id = id,
            N = n,
            Point = pn.Point,
            Part = pn.Part,
            PartName = ByPart.TryGetValue(pn.Part, out var node) ? node.Name : "",
            Text = text.Trim(),
            Author = Reviewer,
            When = DateTime.Now.ToString("d MMM yyyy, HH.mm", CultureInfo.InvariantCulture),
        });
        PendingNote = null;
        ActiveNote = id;
        SaveNotes();
        PushNotes();
        Notify();
    }

    public static void CancelNote()
    {
        PendingNote = null;
        Notify();
    }

    public static void ToggleResolved(Note n)
    {
        n.Resolved = !n.Resolved;
        SaveNotes();
        Notify();
    }

    public static void DeleteNote(Note n)
    {
        Notes.Remove(n);
        if (ActiveNote == n.Id) ActiveNote = null;
        SaveNotes();
        PushNotes();
        Notify();
    }

    public static void ShowNote(Note n)
    {
        ActiveNote = n.Id;
        if (ByPart.ContainsKey(n.Part))
        {
            Hidden.Remove(n.Part);
            if (Isolated is not null && !Isolated.Contains(n.Part)) Isolated = null;
            Push();
        }
        PushNotes();
        Notify();
    }

    private static void PushNotes()
    {
        if (!Attached) return;
        Js.Send("tk.notes", w =>
        {
            w.BeginObject().Name("list").BeginArray();
            foreach (var n in Notes)
                w.BeginObject().Prop("id", n.Id).Prop("n", n.N).Prop("part", n.Part).Name("point").BeginArray().Number(n.Point[0]).Number(n.Point[1]).Number(n.Point[2]).EndArray().EndObject();
            w.EndArray();
            w.Name("selected");
            if (ActiveNote is { } a) w.Number(a);
            else w.Null();
            w.EndObject();
        });
    }

    private static void SaveNotes()
    {
        var w = new JsonWriter();
        w.BeginArray();
        foreach (var n in Notes)
            w.BeginObject().Prop("id", n.Id).Prop("n", n.N).Prop("part", n.Part).Prop("partName", n.PartName).Prop("text", n.Text).Prop("author", n.Author).Prop("when", n.When).Prop("resolved", n.Resolved)
                .Name("point").BeginArray().Number(n.Point[0]).Number(n.Point[1]).Number(n.Point[2]).EndArray().EndObject();
        w.EndArray();
        WebStorage.Local.Set(Key("notes"), w.ToString());
    }

    private static void LoadNotes()
    {
        Notes.Clear();
        var text = WebStorage.Local.Get(Key("notes"));
        if (text is null)
        {
            if (File == "rw63.glb") SeedNotes();
            return;
        }
        foreach (var x in JsonValue.Parse(text).Items)
        {
            var p = x["point"];
            Notes.Add(new Note
            {
                Id = x["id"].AsInt(),
                N = x["n"].AsInt(),
                Part = x["part"].AsInt(),
                PartName = x["partName"].AsString(),
                Text = x["text"].AsString(),
                Author = x["author"].AsString(),
                When = x["when"].AsString(),
                Resolved = x["resolved"].AsBool(),
                Point = new[] { p[0].AsDouble(), p[1].AsDouble(), p[2].AsDouble() },
            });
        }
    }

    /// <summary>The review the sample opens on: two notes a colleague left on revision C.</summary>
    private static void SeedNotes()
    {
        Node? Find(string name) => Nodes.FirstOrDefault(n => n.Name == name);
        if (Find("Oil sight glass") is { } glass)
            Notes.Add(new Note { Id = 1, N = 1, Part = glass.Part, PartName = glass.Name, Point = new[] { 0.105, -0.035, 0.0515 }, Text = "Sight glass sits 3 mm below the oil line for the 0.9 L fill in the manual. Move it up to z = −32, or change the fill to 1.0 L.", Author = "Ratna Wulandari", When = "20 Oct 2026, 09.12" });
        if (Find("Output through cap") is { } cap)
            Notes.Add(new Note { Id = 2, N = 2, Part = cap.Part, PartName = cap.Name, Point = new[] { 0.0795 + 0.029, 0.0, -0.061 }, Text = "Rev C: through cap now 8 mm thick so the TC 30×42×6 seal is fully supported. Check the output key still clears the cap face.", Author = "Ratna Wulandari", When = "20 Oct 2026, 09.20", Resolved = true });
        SaveNotes();
    }

    // ── saved views ─────────────────────────────────────────────────────
    public static void SaveView(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var cam = Js.Call("tk.getCamera").ToJson();
        Views.RemoveAll(v => v.Name == name.Trim());
        Views.Add(new SavedView
        {
            Name = name.Trim(), Camera = cam, Mode = Mode, Section = SectionOn, Axis = Axis, Offset = Offset, Flip = Flip, Explode = Explode,
            Hidden = Hidden.ToList(),
        });
        StoreViews();
        Notify();
    }

    public static void RestoreView(SavedView v)
    {
        Mode = v.Mode;
        SectionOn = v.Section;
        Axis = v.Axis;
        Offset = v.Offset;
        Flip = v.Flip;
        Explode = v.Explode;
        Hidden.Clear();
        foreach (var p in v.Hidden) Hidden.Add(p);
        Isolated = null;
        Push();
        Js.Send("tk.setCamera", w => w.BeginObject().Name("cam").Raw(v.Camera).EndObject());
        Notify();
    }

    public static void DeleteView(SavedView v)
    {
        Views.Remove(v);
        StoreViews();
        Notify();
    }

    private static void StoreViews()
    {
        var w = new JsonWriter();
        w.BeginArray();
        foreach (var v in Views)
        {
            w.BeginObject().Prop("name", v.Name).Name("camera").Raw(v.Camera).Prop("mode", v.Mode).Prop("section", v.Section).Prop("axis", v.Axis).Prop("offset", v.Offset).Prop("flip", v.Flip).Prop("explode", v.Explode).Name("hidden").BeginArray();
            foreach (var p in v.Hidden) w.Number(p);
            w.EndArray().EndObject();
        }
        w.EndArray();
        WebStorage.Local.Set(Key("views"), w.ToString());
    }

    private static void LoadViews()
    {
        Views.Clear();
        var text = WebStorage.Local.Get(Key("views"));
        if (text is null) return;
        foreach (var x in JsonValue.Parse(text).Items)
        {
            var v = new SavedView
            {
                Name = x["name"].AsString(), Camera = x["camera"].ToJson(), Mode = x["mode"].AsString(), Section = x["section"].AsBool(),
                Axis = x["axis"].AsString(), Offset = x["offset"].AsDouble(), Flip = x["flip"].AsBool(), Explode = x["explode"].AsDouble(),
            };
            foreach (var p in x["hidden"].Items) v.Hidden.Add(p.AsInt());
            Views.Add(v);
        }
    }

    // ── bill of materials ───────────────────────────────────────────────
    public sealed record BomLine(int Item, string PartNo, string Description, string Material, string MakeBuy, int Qty, double UnitMass, string Group);

    /// <summary>Parts rolled up by part number (or name), in tree order.</summary>
    public static List<BomLine> Bom()
    {
        var lines = new List<BomLine>();
        var index = new Dictionary<string, int>();
        void Walk(Node n, string group)
        {
            if (n.IsPart)
            {
                var key = string.IsNullOrEmpty(n.PartNo) ? n.Name : n.PartNo;
                if (index.TryGetValue(key, out var i))
                {
                    var l = lines[i];
                    lines[i] = l with { Qty = l.Qty + 1 };
                }
                else
                {
                    index[key] = lines.Count;
                    lines.Add(new BomLine(lines.Count + 1, n.PartNo, string.IsNullOrEmpty(n.Description) ? n.Name : n.Description, n.Material, n.MakeBuy, 1, n.Mass, group));
                }
            }
            foreach (var c in n.Children) Walk(c, n.IsPart ? group : (n.Parent < 0 ? group : n.Name));
        }
        foreach (var r in Roots) Walk(r, r.Name);
        return lines;
    }

    public static string BomCsv()
    {
        var sb = new StringBuilder();
        sb.Append("Item,Part number,Description,Material,Make/buy,Qty,Unit mass (kg),Total mass (kg),Group\r\n");
        foreach (var l in Bom())
        {
            sb.Append(l.Item).Append(',').Append(Csv(l.PartNo)).Append(',').Append(Csv(l.Description)).Append(',').Append(Csv(l.Material)).Append(',')
              .Append(Csv(l.MakeBuy)).Append(',').Append(l.Qty).Append(',').Append(l.UnitMass.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
              .Append((l.UnitMass * l.Qty).ToString("0.000", CultureInfo.InvariantCulture)).Append(',').Append(Csv(l.Group)).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static double TotalMass()
    {
        var t = 0.0;
        foreach (var n in Nodes) t += n.Mass;
        return t;
    }

    public static int PartCount => ByPart.Count;

    public static string Mm(double metres) => (metres * 1000).ToString("0.0", CultureInfo.InvariantCulture);

    public static string Kg(double kg) => kg >= 1 ? kg.ToString("0.00", CultureInfo.InvariantCulture) + " kg" : (kg * 1000).ToString("0", CultureInfo.InvariantCulture) + " g";
}
