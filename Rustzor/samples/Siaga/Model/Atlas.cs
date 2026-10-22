using Rustzor.Browser.FileSystem;
using Rustzor.Browser.Location;
using Rustzor.Data;
using Rustzor.Interop;
using Rustzor.UI;

namespace Siaga.Model;

/// <summary>A length or area measured on the map.</summary>
public sealed record Measurement(string Kind, List<LonLat> Points, double Length, double Area, bool Done);

/// <summary>Features a query found, with how far each is from what was asked about.</summary>
public sealed record Hit(MapLayer Layer, Feat Feature, double Meters);

public sealed record QueryResult(string Title, string Detail, List<Hit> Hits);

/// <summary>
/// Everything the map shows and does: the layers in draw order, the selection,
/// the tool in hand, measurements and queries, and the user's own layers kept
/// in IndexedDB. Pages and panels read it and subscribe to <see cref="Changed"/>.
/// </summary>
public static class Atlas
{
    public const string NotesId = "u-catatan";
    private const string Db = "siaga-citarum";

    private static Task? _loading;
    private static IndexedDb? _db;
    private static long _seq;

    public static List<MapLayer> Layers { get; private set; } = Catalog.Create();
    public static event Action? Changed;

    /// <summary>Selected feature ids by layer.</summary>
    public static Dictionary<string, HashSet<string>> Selection { get; } = new();
    public static (MapLayer Layer, Feat Feature)? Inspected { get; private set; }
    public static string Tool { get; private set; } = "identify";
    public static Measurement? Measure { get; private set; }
    public static QueryResult? Query { get; private set; }
    public static double BufferMeters { get; set; } = 1500;
    public static bool Tiles { get; private set; }
    public static bool Attached { get; set; }
    public static string? TableLayer { get; set; } = "kecamatan";
    public static string Status { get; private set; } = "Loading the map sheet…";
    public static LonLat? LastView { get; private set; }
    public static double LastZoom { get; private set; } = 11.3;

    public static MapLayer? Layer(string id) => Layers.FirstOrDefault(l => l.Id == id);
    public static IEnumerable<MapLayer> UserLayers => Layers.Where(l => l.User);
    public static bool Ready => _loading is { IsCompleted: true };

    public static void Notify() => Changed?.Invoke();

    public static Task ReadyAsync() => _loading ??= LoadAsync();

    private static async Task LoadAsync()
    {
        _db = await IndexedDb.OpenAsync(Db, 1,
            new StoreSchema("features", "id", Indexes: new IndexSchema("byLayer", "layer")),
            new StoreSchema("layers"),
            new StoreSchema("prefs"));
        foreach (var l in Layers) l.Default = l.Style.Clone();
        var prefs = await _db.Store<PrefRecord>("prefs").AllAsync();
        foreach (var p in prefs) ApplyPref(p);

        var started = Js.Now();
        var vertices = 0;
        foreach (var l in Layers.ToList())
        {
            var res = await Js.CallAsync("sg.load", Js.Args("id", l.Id, "kind", l.Kind, "url", l.Url, "attrs", !l.Background));
            l.Vertices = res["vertices"].AsInt();
            vertices += l.Vertices;
            if (!l.Background) l.Features = res["features"].Items.Select(ReadFeat).ToList();
            l.Loaded = true;
            if (l.Style.Mode is "graduated" or "categorized") Restyle(l, keepCats: true);
        }

        var layerRecs = (await _db.Store<LayerRecord>("layers").AllAsync()).OrderBy(r => r.Seq).ToList();
        if (layerRecs.All(r => r.Id != NotesId))
        {
            var notes = new LayerRecord { Id = NotesId, Name = "Catatan lapangan", Color = "#e2621b", Source = "Drawn on the map", Seq = 0 };
            await _db.Store<LayerRecord>("layers").PutAsync(notes);
            layerRecs.Insert(0, notes);
        }
        var records = await _db.Store<UserRecord>("features").AllAsync();
        _seq = records.Count == 0 ? 0 : records.Max(r => r.Seq);
        foreach (var lr in layerRecs)
        {
            var layer = NewUserLayer(lr);
            layer.Features = records.Where(r => r.Layer == lr.Id).OrderBy(r => r.Seq).Select(ToFeat).ToList();
            Layers.Add(layer);
            SendUserData(layer);
        }
        Status = $"{Layers.Count} layers, {vertices:N0} vertices in {Js.Now() - started:0} ms";
        Push();
        Notify();
    }

    private static Feat ReadFeat(JsonValue f)
    {
        var c = f["c"];
        var b = f["b"];
        return new Feat
        {
            Id = f["id"].AsString(),
            Props = f["p"].IsObject ? f["p"] : JsonValue.NewObject(),
            Center = new LonLat(c[0].AsDouble(), c[1].AsDouble()),
            Box = (b[0].AsDouble(), b[1].AsDouble(), b[2].AsDouble(), b[3].AsDouble()),
        };
    }

    private static Feat ToFeat(UserRecord r)
    {
        var shape = GeoText.Decode(r.Kind, r.Coords);
        return new Feat { Id = r.Id, Props = SafeParse(r.Props), Shape = shape, Center = shape.Center, Box = shape.Bounds };
    }

    private static JsonValue SafeParse(string json)
    {
        try
        {
            var v = JsonValue.Parse(json);
            return v.IsObject ? v : JsonValue.NewObject();
        }
        catch (FormatException)
        {
            return JsonValue.NewObject();
        }
    }

    private static MapLayer NewUserLayer(LayerRecord r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Group = "Lapisan saya",
        Kind = "mixed",
        User = true,
        Note = r.Source,
        Columns = new() { "name", "note" },
        Style = new() { Fill = r.Color + "55", Stroke = r.Color, Width = 2.2, Size = 10, Shape = "circle", Label = "name", LabelZoom = 12, LabelPrio = 3 },
    };

    // ── telling the map ─────────────────────────────────────────────────

    /// <summary>Sends the layer list (order, visibility, opacity, styles) to the map.</summary>
    public static void Push()
    {
        if (!Js.IsBrowser) return;
        Js.Send("sg.layers", w =>
        {
            w.BeginObject().Name("layers").BeginArray();
            foreach (var l in Layers)
            {
                w.BeginObject().Prop("id", l.Id).Prop("kind", l.Kind).Prop("visible", l.Visible).Prop("opacity", l.Opacity).Name("style");
                l.Style.Write(w);
                w.EndObject();
            }
            w.EndArray().EndObject();
        });
    }

    private static void PushSelection()
    {
        if (!Js.IsBrowser) return;
        Js.Send("sg.select", w =>
        {
            w.BeginObject().Name("sel").BeginArray();
            foreach (var kv in Selection)
            {
                w.BeginObject().Prop("layer", kv.Key).Name("ids").BeginArray();
                foreach (var id in kv.Value) w.String(id);
                w.EndArray().EndObject();
            }
            w.EndArray().EndObject();
        });
    }

    private static void SendUserData(MapLayer l) =>
        Js.Send("sg.data", w => w.BeginObject().Prop("id", l.Id).Prop("kind", "mixed").Name("geojson").Raw(GeoText.Collection(l.Features)).EndObject());

    // ── layers ──────────────────────────────────────────────────────────

    public static void SetVisible(MapLayer l, bool on)
    {
        l.Visible = on;
        Push();
        _ = SavePrefAsync(l);
        Notify();
    }

    public static void SetOpacity(MapLayer l, double v)
    {
        l.Opacity = Math.Clamp(v, 0, 1);
        Push();
        _ = SavePrefAsync(l);
        Notify();
    }

    /// <summary>Moves a layer up (drawn later, on top) or down.</summary>
    public static void Move(MapLayer l, int delta)
    {
        var i = Layers.IndexOf(l);
        var j = Math.Clamp(i + delta, 0, Layers.Count - 1);
        if (i == j) return;
        Layers.RemoveAt(i);
        Layers.Insert(j, l);
        Push();
        _ = SaveOrderAsync();
        Notify();
    }

    public static void SetStyle(MapLayer l, Action<LayerStyle> change)
    {
        change(l.Style);
        if (l.Style.Mode is "graduated" or "categorized") Restyle(l, keepCats: false);
        Push();
        _ = SavePrefAsync(l);
        Notify();
    }

    public static void ResetStyle(MapLayer l)
    {
        l.Style = l.Default.Clone();
        if (l.Style.Mode is "graduated" or "categorized") Restyle(l, keepCats: true);
        Push();
        _ = SavePrefAsync(l);
        Notify();
    }

    /// <summary>Works out a graduated layer's class breaks, or a categorized layer's colours.</summary>
    public static void Restyle(MapLayer l, bool keepCats)
    {
        var s = l.Style;
        if (s.Field is null) return;
        if (s.Mode == "graduated")
        {
            var values = l.Features.Select(f => f.Number(s.Field)).Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
            var ramp = Palettes.Ramps.TryGetValue(s.Palette, out var r) ? r : Palettes.Ramps["bahaya"];
            s.Colors = ramp.ToList();
            s.Breaks = Breaks(values, ramp.Length, s.Classify);
        }
        else if (s.Mode == "categorized")
        {
            if (keepCats && s.Cats.Count > 0) return;
            if (s.Field == "status" && l.Id == "pda")
            {
                s.Cats = new Dictionary<string, string>(Palettes.Alert);
                return;
            }
            var distinct = l.Features.Select(f => f.Text(s.Field)).Distinct().OrderBy(v => v).Take(Palettes.Qualitative.Length).ToList();
            s.Cats = new Dictionary<string, string>();
            for (var i = 0; i < distinct.Count; i++) s.Cats[distinct[i]] = Palettes.Qualitative[i];
        }
    }

    /// <summary>Class breaks for n classes: quantiles (equal counts) or equal intervals.</summary>
    public static List<double> Breaks(List<double> sorted, int classes, string method)
    {
        var breaks = new List<double>();
        if (sorted.Count == 0) return breaks;
        for (var k = 1; k < classes; k++)
        {
            double b = method == "equal"
                ? sorted[0] + (sorted[^1] - sorted[0]) * k / classes
                : sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(sorted.Count * (double)k / classes))];
            breaks.Add(Math.Round(b, 2));
        }
        return breaks;
    }

    /// <summary>The legend entries of a layer: swatch colour and label.</summary>
    public static List<(string Color, string Label)> Legend(MapLayer l)
    {
        var s = l.Style;
        var outp = new List<(string, string)>();
        switch (s.Mode)
        {
            case "categorized":
                foreach (var kv in s.Cats) outp.Add((kv.Value, kv.Key));
                break;
            case "graduated":
                for (var i = 0; i < s.Colors.Count; i++)
                {
                    var lo = i == 0 ? (double?)null : s.Breaks.ElementAtOrDefault(i - 1);
                    var hi = i < s.Breaks.Count ? s.Breaks[i] : (double?)null;
                    var label = lo is null ? "≤ " + Fmt.N(hi ?? 0) : hi is null ? "> " + Fmt.N(lo.Value) : Fmt.N(lo.Value) + " – " + Fmt.N(hi.Value);
                    outp.Add((s.Colors[i], label));
                }
                break;
            case "heat":
                outp.Add(("heat", "Fewer → more, weighted by " + Catalog.Column(s.Field ?? "").ToLowerInvariant()));
                break;
            default:
                outp.Add((l.Kind == "line" ? s.Stroke ?? "" : s.Fill ?? s.Stroke ?? "", l.Name));
                break;
        }
        return outp;
    }

    // ── the map's messages ───────────────────────────────────────────────

    public static void OnMessage(JsonValue m)
    {
        switch (m["type"].AsString())
        {
            case "click":
                OnClick(m);
                break;
            case "sketchStep":
                if (Tool.StartsWith("measure"))
                {
                    var pts = Coords(m["coords"]);
                    Measure = new Measurement(Tool, pts, MeasureLength(pts), Tool == "measure-area" ? Geo.Area(pts) : 0, false);
                    Notify();
                }
                break;
            case "sketch":
                _ = OnSketchAsync(Coords(m["coords"]));
                break;
            case "sketchCancel":
                if (Measure is { Done: false }) Measure = null;
                Notify();
                break;
            case "edited":
                _ = OnEditedAsync(m["layer"].AsString(), m["id"].AsString(), Coords(m["coords"]));
                break;
            case "editEnd":
                SetTool("identify");
                break;
            case "view":
                LastView = new LonLat(m["lon"].AsDouble(), m["lat"].AsDouble());
                LastZoom = m["zoom"].AsDouble();
                _ = SaveViewAsync();
                break;
            case "tiles":
                if (Tiles)
                {
                    Tiles = false;
                    Js.Send("sg.tiles", Js.Args("on", false));
                    Toasts.Warning("The OpenStreetMap background could not load: this browser looks offline. The map's own layers still work.");
                    Notify();
                }
                break;
        }
    }

    private static double MeasureLength(List<LonLat> pts) => Tool == "measure-area" ? Geo.Perimeter(pts) : Geo.Length(pts);

    private static List<LonLat> Coords(JsonValue arr) => arr.Items.Select(p => new LonLat(p[0].AsDouble(), p[1].AsDouble())).ToList();

    private static void OnClick(JsonValue m)
    {
        var hit = m["hit"];
        if (Tool == "buffer")
        {
            if (!hit.IsNull) Inspect(hit["layer"].AsString(), hit["id"].AsString(), select: true);
            return;
        }
        if (hit.IsNull)
        {
            if (!m["shift"].AsBool())
            {
                Selection.Clear();
                Inspected = null;
                PushSelection();
                Notify();
            }
            return;
        }
        var layer = hit["layer"].AsString();
        var id = hit["id"].AsString();
        if (m["shift"].AsBool())
        {
            Toggle(layer, id);
            Notify();
            return;
        }
        Inspect(layer, id, select: true);
    }

    /// <summary>Shows a feature in the inspector and, when asked, selects only it.</summary>
    public static void Inspect(string layerId, string id, bool select)
    {
        var l = Layer(layerId);
        var f = l?.Features.FirstOrDefault(x => x.Id == id);
        if (l is null || f is null) return;
        Inspected = (l, f);
        if (select)
        {
            Selection.Clear();
            Selection[layerId] = new HashSet<string> { id };
            PushSelection();
        }
        Query = Tool == "buffer" ? Query : null;
        Notify();
    }

    public static void Toggle(string layerId, string id)
    {
        if (!Selection.TryGetValue(layerId, out var set)) Selection[layerId] = set = new HashSet<string>();
        if (!set.Remove(id)) set.Add(id);
        if (set.Count == 0) Selection.Remove(layerId);
        PushSelection();
    }

    public static void ClearSelection()
    {
        Selection.Clear();
        Inspected = null;
        Query = null;
        Js.Send("sg.buffer", Js.Args());
        PushSelection();
        Notify();
    }

    public static bool IsSelected(string layerId, string id) => Selection.TryGetValue(layerId, out var s) && s.Contains(id);

    public static int SelectedCount => Selection.Values.Sum(s => s.Count);

    // ── tools ──────────────────────────────────────────────────────────

    public static void SetTool(string tool)
    {
        if (tool == "edit" && Inspected is not { Layer.User: true }) return;
        Tool = tool;
        Js.Send("sg.tool", Js.Args("mode", tool));
        if (tool == "edit" && Inspected is { } ins && ins.Feature.Shape is { } shape)
            Js.Send("sg.edit", w =>
            {
                w.BeginObject().Prop("layer", ins.Layer.Id).Prop("id", ins.Feature.Id).Prop("kind", shape.KindName).Name("coords").BeginArray();
                foreach (var p in shape.Outline) w.BeginArray().Number(p.Lon).Number(p.Lat).EndArray();
                w.EndArray().EndObject();
            });
        if (!tool.StartsWith("measure")) Measure = null;
        if (tool != "buffer") Js.Send("sg.buffer", Js.Args());
        if (tool == "buffer" && Inspected is not null) _ = BufferAsync();
        Js.Send("sg.focus", Js.Args());
        Notify();
    }

    private static async Task OnSketchAsync(List<LonLat> pts)
    {
        switch (Tool)
        {
            case "measure-line":
                Measure = new Measurement(Tool, pts, Geo.Length(pts), 0, true);
                break;
            case "measure-area":
                Measure = new Measurement(Tool, pts, Geo.Perimeter(pts), Geo.Area(pts), true);
                break;
            case "select-area":
                SelectWithin(pts);
                break;
            case "point":
            case "line":
            case "polygon":
                var shape = Tool == "point" ? Shape.Point(pts[0]) : Tool == "line" ? Shape.Line(pts) : Shape.Polygon(pts);
                var notes = Layer(NotesId)!;
                var props = JsonValue.NewObject();
                props["name"] = JsonValue.From(Tool switch { "point" => "Titik", "line" => "Jalur", _ => "Area" } + " " + (notes.Features.Count + 1));
                props["note"] = JsonValue.From("");
                props["created"] = JsonValue.From(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                var f = await AddFeatureAsync(notes, shape, props);
                notes.Visible = true;
                SetToolQuiet("identify");
                Inspect(notes.Id, f.Id, select: true);
                Toasts.Success($"Added {props["name"].AsString()} to {notes.Name}");
                break;
        }
        Notify();
    }

    private static void SetToolQuiet(string tool)
    {
        Tool = tool;
        Js.Send("sg.tool", Js.Args("mode", tool));
    }

    /// <summary>The layers a query looks in: shown, with features of their own, and not a heat map (a density, not things to pick).</summary>
    private static bool Queryable(MapLayer l) => l.Visible && !l.Background && l.Style.Mode != "heat";

    /// <summary>Selects every visible feature inside a drawn area: points by position, areas by their centre.</summary>
    public static void SelectWithin(List<LonLat> ring)
    {
        Selection.Clear();
        var hits = new List<Hit>();
        foreach (var l in Layers.Where(Queryable))
            foreach (var f in l.Features)
                if (Geo.Contains(ring, f.Center))
                {
                    hits.Add(new Hit(l, f, 0));
                    if (!Selection.TryGetValue(l.Id, out var set)) Selection[l.Id] = set = new HashSet<string>();
                    set.Add(f.Id);
                }
        Query = new QueryResult("Inside the drawn area", Geo.FormatArea(Geo.Area(ring)) + " · " + hits.Count + " features", hits);
        Inspected = null;
        PushSelection();
        SetToolQuiet("identify");
    }

    /// <summary>The geometry of a feature: kept for the user's own, fetched from the map for the rest.</summary>
    public static Shape? ShapeOf(MapLayer l, Feat f)
    {
        if (f.Shape is not null) return f.Shape;
        var g = Js.Call("sg.geometry", Js.Args("layer", l.Id, "id", f.Id));
        if (g.IsNull) return null;
        var list = GeoText.ParseGeoJson(g.ToJson());
        if (list.Count == 0) return null;
        // A multi-part line or area keeps its parts together for distance. Kept,
        // so the next question about this feature does not ask the map again.
        f.Shape = list.Count == 1 ? list[0].Shape : new Shape { Kind = list[0].Shape.Kind, Parts = list.SelectMany(x => x.Shape.Parts).ToList() };
        return f.Shape;
    }

    /// <summary>
    /// Everything within <see cref="BufferMeters"/> of the inspected feature, in
    /// the visible point layers and by centre in the area layers, nearest first.
    /// </summary>
    public static Task BufferAsync()
    {
        if (Inspected is not { } ins) return Task.CompletedTask;
        var shape = ShapeOf(ins.Layer, ins.Feature);
        if (shape is null) return Task.CompletedTask;
        var d = BufferMeters;
        var hits = new List<Hit>();
        Selection.Clear();
        foreach (var l in Layers.Where(l => Queryable(l) && l != ins.Layer))
            foreach (var f in l.Features)
            {
                var dist = Geo.DistanceTo(f.Center, shape);
                if (dist > d) continue;
                hits.Add(new Hit(l, f, dist));
                if (!Selection.TryGetValue(l.Id, out var set)) Selection[l.Id] = set = new HashSet<string>();
                set.Add(f.Id);
            }
        hits = hits.OrderBy(h => h.Meters).ToList();
        Query = new QueryResult("Within " + Geo.FormatLength(d) + " of " + ins.Layer.Title(ins.Feature),
            hits.Count + " features · " + hits.Count(h => h.Layer.Id == "posko") + " shelters", hits);
        Js.Send("sg.buffer", w =>
        {
            w.BeginObject().Prop("kind", shape.KindName).Prop("meters", d).Name("coords").BeginArray();
            foreach (var p in shape.Outline) w.BeginArray().Number(p.Lon).Number(p.Lat).EndArray();
            w.EndArray().EndObject();
        });
        PushSelection();
        Notify();
        return Task.CompletedTask;
    }

    // ── the user's features ──────────────────────────────────────────────

    public static async Task<Feat> AddFeatureAsync(MapLayer layer, Shape shape, JsonValue props)
    {
        var f = new Feat { Id = "u" + (++_seq) + "-" + Random.Shared.Next(1000, 9999), Props = props, Shape = shape, Center = shape.Center, Box = shape.Bounds };
        layer.Features.Add(f);
        await SaveFeatureAsync(layer, f);
        SendUserData(layer);
        return f;
    }

    private static Task SaveFeatureAsync(MapLayer layer, Feat f) =>
        _db!.Store<UserRecord>("features").PutAsync(new UserRecord
        {
            Id = f.Id, Layer = layer.Id, Kind = f.Shape!.KindName, Coords = GeoText.Encode(f.Shape), Props = f.Props.ToJson(), Seq = _seq,
        });

    private static async Task OnEditedAsync(string layerId, string id, List<LonLat> pts)
    {
        var l = Layer(layerId);
        var f = l?.Features.FirstOrDefault(x => x.Id == id);
        if (l is null || f?.Shape is null) return;
        f.Shape = f.Shape.Kind == ShapeKind.Point ? Shape.Point(pts[0]) : new Shape { Kind = f.Shape.Kind, Parts = new() { pts } };
        f.Center = f.Shape.Center;
        f.Box = f.Shape.Bounds;
        await SaveFeatureAsync(l, f);
        SendUserData(l);
        Notify();
    }

    public static async Task UpdatePropsAsync(MapLayer l, Feat f, string key, string value)
    {
        f.Props[key] = JsonValue.From(value);
        await SaveFeatureAsync(l, f);
        SendUserData(l);
        Notify();
    }

    public static async Task DeleteFeatureAsync(MapLayer l, Feat f)
    {
        l.Features.Remove(f);
        await _db!.Store<UserRecord>("features").DeleteAsync(f.Id);
        SendUserData(l);
        if (Inspected?.Feature == f) Inspected = null;
        if (Selection.TryGetValue(l.Id, out var s)) s.Remove(f.Id);
        if (Tool == "edit") SetToolQuiet("identify");
        Js.Send("sg.edit", Js.Args());
        PushSelection();
        Notify();
    }

    /// <summary>Imports a GeoJSON or CSV text as a new layer of the user's own.</summary>
    public static async Task<MapLayer> ImportAsync(string fileName, string text)
    {
        var lower = fileName.ToLowerInvariant();
        var items = lower.EndsWith(".csv") || lower.EndsWith(".txt") ? GeoText.ParseCsv(text) : GeoText.ParseGeoJson(text);
        if (items.Count == 0) throw new FormatException("The file has no features this map can draw.");
        var color = Palettes.Qualitative[(UserLayers.Count() + 3) % Palettes.Qualitative.Length];
        var rec = new LayerRecord
        {
            Id = "u-" + DateTime.Now.Ticks.ToString("x"),
            Name = System.IO.Path.GetFileNameWithoutExtension(fileName),
            Color = color,
            Source = "Imported from " + fileName,
            Seq = ++_seq,
        };
        await _db!.Store<LayerRecord>("layers").PutAsync(rec);
        var layer = NewUserLayer(rec);
        var title = items[0].Props.Keys.FirstOrDefault(k => k.ToLowerInvariant() is "name" or "nama" or "title") ?? items[0].Props.Keys.FirstOrDefault() ?? "name";
        layer.TitleField = title;
        layer.Style.Label = title;
        layer.Columns = items[0].Props.Keys.Take(6).ToList();
        Layers.Add(layer);
        var records = new List<UserRecord>();
        foreach (var (shape, props) in items)
        {
            var f = new Feat { Id = "u" + (++_seq) + "-" + records.Count, Props = props, Shape = shape, Center = shape.Center, Box = shape.Bounds };
            layer.Features.Add(f);
            records.Add(new UserRecord { Id = f.Id, Layer = layer.Id, Kind = shape.KindName, Coords = GeoText.Encode(shape), Props = props.ToJson(), Seq = _seq });
        }
        await _db.Store<UserRecord>("features").PutManyAsync(records);
        SendUserData(layer);
        Push();
        _ = SaveOrderAsync();
        TableLayer = layer.Id;
        ZoomTo(layer);
        Notify();
        return layer;
    }

    public static string Export(MapLayer l) => GeoText.Collection(l.Features);

    public static async Task SaveExportAsync(MapLayer l) =>
        await Files.SaveTextAsync(Slug(l.Name) + ".geojson", Export(l), "application/geo+json");

    private static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    public static async Task DeleteLayerAsync(MapLayer l)
    {
        if (!l.User || l.Id == NotesId) return;
        Layers.Remove(l);
        foreach (var f in l.Features) await _db!.Store<UserRecord>("features").DeleteAsync(f.Id);
        await _db!.Store<LayerRecord>("layers").DeleteAsync(l.Id);
        Selection.Remove(l.Id);
        if (Inspected?.Layer == l) Inspected = null;
        if (TableLayer == l.Id) TableLayer = "kecamatan";
        Js.Send("sg.drop", Js.Args("id", l.Id));
        Push();
        Notify();
    }

    // ── view ────────────────────────────────────────────────────────────

    public static void ZoomTo(MapLayer l, Feat f)
    {
        var (w, s, e, n) = f.Box;
        if (e - w < 0.002 && n - s < 0.002) Js.Send("sg.view", Js.Args("lon", f.Center.Lon, "lat", f.Center.Lat, "zoom", Math.Max(LastZoom, 14.5)));
        else Js.Send("sg.fit", Js.Args("bbox", new[] { w, s, e, n }, "pad", 70));
    }

    public static void ZoomTo(MapLayer l)
    {
        if (l.Features.Count == 0) return;
        var w = l.Features.Min(f => f.Box.W);
        var s = l.Features.Min(f => f.Box.S);
        var e = l.Features.Max(f => f.Box.E);
        var n = l.Features.Max(f => f.Box.N);
        Js.Send("sg.fit", Js.Args("bbox", new[] { w, s, e, n }, "pad", 60));
    }

    public static void Home() => Js.Send("sg.fit", Js.Args("bbox", new[] { 107.43, -7.22, 107.9, -6.8 }, "pad", 24));

    public static void SetTiles(bool on)
    {
        Tiles = on;
        Js.Send("sg.tiles", Js.Args("on", on));
        Notify();
    }

    public static async Task LocateAsync()
    {
        if (!Geolocation.IsSupported)
        {
            Toasts.Warning("This browser cannot report a location.");
            return;
        }
        try
        {
            var p = await Geolocation.GetCurrentPositionAsync(highAccuracy: true, timeoutMs: 10000);
            Js.Send("sg.locate", Js.Args("lon", p.Longitude, "lat", p.Latitude, "acc", p.Accuracy));
            Js.Send("sg.view", Js.Args("lon", p.Longitude, "lat", p.Latitude, "zoom", 15));
            var inside = p.Longitude is > 107.3 and < 108 && p.Latitude is > -7.3 and < -6.7;
            Toasts.Info(inside ? "You are here, within " + Math.Round(p.Accuracy) + " m." : "You are outside the Bandung basin; the map shows where you are anyway.");
        }
        catch (Exception e)
        {
            Toasts.Warning("Location not available: " + (e.Message.Contains("denied", StringComparison.OrdinalIgnoreCase) ? "permission was denied. Allow it in the browser's site settings to use this." : e.Message));
        }
    }

    // ── search ──────────────────────────────────────────────────────────

    public static List<Hit> Search(string q)
    {
        q = q.Trim();
        if (q.Length < 2) return new();
        var hits = new List<Hit>();
        foreach (var l in Layers.Where(l => !l.Background))
            foreach (var f in l.Features)
            {
                var t = l.Title(f);
                var at = t.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                hits.Add(new Hit(l, f, at == 0 ? 0 : 1));
                if (hits.Count > 60) break;
            }
        return hits.OrderBy(h => h.Meters).ThenBy(h => h.Layer.Title(h.Feature).Length).Take(12).ToList();
    }

    /// <summary>"-6.99, 107.62" or "107.62 -6.99": the coordinates, if the text is a pair of numbers that fits Indonesia.</summary>
    public static LonLat? ParseCoordinate(string q)
    {
        var parts = q.Replace(";", " ").Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ||
            !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b)) return null;
        if (Math.Abs(a) <= 15 && b is > 90 and < 145) return new LonLat(b, a);
        if (Math.Abs(b) <= 15 && a is > 90 and < 145) return new LonLat(a, b);
        return null;
    }

    // ── persistence ─────────────────────────────────────────────────────

    private static async Task SavePrefAsync(MapLayer l)
    {
        if (_db is null) return;
        var w = new JsonWriter();
        var s = l.Style;
        w.BeginObject().Prop("visible", l.Visible).Prop("opacity", l.Opacity).Prop("mode", s.Mode).Prop("field", s.Field)
            .Prop("palette", s.Palette).Prop("classify", s.Classify).Prop("fill", s.Fill).Prop("stroke", s.Stroke)
            .Prop("width", s.Width).Prop("size", s.Size).EndObject();
        await _db.Store<PrefRecord>("prefs").PutAsync(new PrefRecord { Id = "layer:" + l.Id, Json = w.ToString() });
    }

    private static void ApplyPref(PrefRecord p)
    {
        if (p.Id == "view")
        {
            var v = JsonValue.Parse(p.Json);
            LastView = new LonLat(v["lon"].AsDouble(), v["lat"].AsDouble());
            LastZoom = v["zoom"].AsDouble();
            return;
        }
        if (p.Id == "order")
        {
            var ids = JsonValue.Parse(p.Json).Items.Select(x => x.AsString()).ToList();
            Layers = Layers.OrderBy(l => ids.IndexOf(l.Id) is var i && i >= 0 ? i : 999).ToList();
            return;
        }
        if (!p.Id.StartsWith("layer:")) return;
        var l = Layer(p.Id.Substring(6));
        if (l is null) return;
        var j = JsonValue.Parse(p.Json);
        l.Visible = j["visible"].AsBool();
        l.Opacity = j["opacity"].AsDouble();
        var s = l.Style;
        var mode = j["mode"].AsString();
        if (mode != s.Mode) s.Cats = new();
        s.Mode = mode;
        s.Field = j["field"].AsStringOrNull();
        s.Palette = j["palette"].AsString();
        s.Classify = j["classify"].AsString();
        s.Fill = j["fill"].AsStringOrNull();
        s.Stroke = j["stroke"].AsStringOrNull();
        s.Width = j["width"].AsDouble();
        s.Size = j["size"].AsDouble();
    }

    private static Task SaveViewAsync()
    {
        if (_db is null || LastView is not { } v) return Task.CompletedTask;
        var json = new JsonWriter().BeginObject().Prop("lon", v.Lon).Prop("lat", v.Lat).Prop("zoom", LastZoom).EndObject().ToString();
        return _db.Store<PrefRecord>("prefs").PutAsync(new PrefRecord { Id = "view", Json = json });
    }

    private static Task SaveOrderAsync()
    {
        if (_db is null) return Task.CompletedTask;
        var w = new JsonWriter().BeginArray();
        foreach (var l in Layers.Where(l => !l.User)) w.String(l.Id);
        w.EndArray();
        return _db.Store<PrefRecord>("prefs").PutAsync(new PrefRecord { Id = "order", Json = w.ToString() });
    }

    /// <summary>Removes everything this browser kept: drawn and imported layers, styles, the view.</summary>
    public static async Task ResetAsync()
    {
        _db?.Dispose();
        await IndexedDb.DeleteAsync(Db);
        JsObject.Eval("location.reload()");
    }
}
