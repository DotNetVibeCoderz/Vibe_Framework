namespace Siaga.Model;

/// <summary>The built-in layers, bottom to top, with the style each starts with.</summary>
public static class Catalog
{
    public const string Water = "'Source Serif 4', Georgia, serif";

    public static List<MapLayer> Create() => new()
    {
        new()
        {
            Id = "kontur", Name = "Garis kontur", Group = "Peta dasar", Kind = "line", Url = "data/contours.json", Background = true,
            TitleField = "elev", Columns = new() { "elev" },
            Note = "Every 25 m, thicker every 250 m. Generated from a model of the basin, not surveyed.",
            Style = new() { Stroke = "var(--map-contour)", Width = 0.8, EmphasisField = "index", EmphasisValue = "1", EmphasisWidth = 1.3, EmphasisStroke = "var(--map-contour-index)", MinorZoom = 11.2, NoHit = true, Label = "elev", LabelZoom = 14.5, LabelColor = "var(--map-contour-ink)", LabelFont = "500 10px 'IBM Plex Mono', monospace", LabelPrio = 0 },
        },
        new()
        {
            Id = "kecamatan", Name = "Batas kecamatan", Group = "Wilayah", Kind = "polygon", Url = "data/kecamatan.json",
            Columns = new() { "name", "kabupaten", "population", "households", "affected_hh", "low_ground_pct", "risk" },
            Note = "Kecamatan near each cell of a generated mesh; boundaries are not official.",
            Style = new() { Fill = "var(--map-admin-fill)", Stroke = "var(--map-admin)", Width = 1.1, Dash = new[] { 5.0, 3.0 }, Label = "name", LabelZoom = 11.2, LabelColor = "var(--map-admin-ink)", LabelFont = "700 10.5px 'Archivo', sans-serif", LabelPrio = 2 },
        },
        new()
        {
            Id = "air", Name = "Waduk dan situ", Group = "Hidrologi", Kind = "polygon", Url = "data/water.json",
            Columns = new() { "name", "kind" },
            Style = new() { Fill = "var(--map-water-fill)", Stroke = "var(--map-water)", Width = 1, Label = "name", LabelZoom = 10.5, LabelColor = "var(--map-water-ink)", LabelFont = "italic 600 12px " + Water, LabelPrio = 3 },
        },
        new()
        {
            Id = "genangan", Name = "Genangan banjir", Group = "Situasi", Kind = "polygon", Url = "data/flood.json",
            Columns = new() { "name", "max_depth_cm", "since" },
            Note = "Flooded areas mapped on 21 October 2026 at 18:00 (simulated).",
            Style = new() { Mode = "graduated", Field = "max_depth_cm", Palette = "air", Fill = "var(--map-flood)", Stroke = "var(--map-flood-edge)", Width = 1.4, Label = "name", LabelZoom = 12.5, LabelColor = "var(--map-water-ink)", LabelFont = "italic 600 11.5px " + Water, LabelPrio = 2 },
        },
        new()
        {
            Id = "sungai", Name = "Sungai", Group = "Hidrologi", Kind = "line", Url = "data/rivers.json",
            Columns = new() { "name", "order", "length_km" },
            Style = new() { Stroke = "var(--map-water)", Width = 1.7, EmphasisField = "order", EmphasisValue = "1", EmphasisWidth = 3.4, Label = "name", LabelZoom = 10.8, LabelColor = "var(--map-water-ink)", LabelFont = "italic 600 12.5px " + Water, LabelPrio = 3 },
        },
        new()
        {
            Id = "jalan", Name = "Jalan utama", Group = "Peta dasar", Kind = "line", Url = "data/roads.json",
            Columns = new() { "name", "class" },
            Style = new()
            {
                Mode = "categorized", Field = "class", Width = 2, Casing = "var(--map-halo)",
                Cats = new() { ["toll"] = "var(--map-road-toll)", ["arterial"] = "var(--map-road)", ["local"] = "var(--map-road-local)" },
                EmphasisField = "class", EmphasisValue = "toll", EmphasisWidth = 3.2, Label = "name", LabelZoom = 13, LabelPrio = 1,
            },
        },
        new()
        {
            Id = "laporan", Name = "Laporan warga", Group = "Situasi", Kind = "point", Url = "data/reports.json", Opacity = 0.7,
            TitleField = "time", Columns = new() { "time", "depth_cm", "source" },
            Note = "Reports of water depth from residents, volunteers and Linmas overnight (simulated).",
            Style = new() { Mode = "heat", Field = "depth_cm", Max = 120, Radius = 380, Fill = "var(--map-report)", Stroke = "var(--map-halo)", Size = 6 },
        },
        new()
        {
            Id = "infrastruktur", Name = "Pompa dan kolam retensi", Group = "Penanganan", Kind = "point", Url = "data/facilities.json",
            Columns = new() { "name", "kind", "state" },
            Style = new()
            {
                Mode = "categorized", Field = "kind", Shape = "square", Size = 9, Stroke = "var(--map-halo)", Width = 1.5,
                Cats = new() { ["pump"] = "#1f6f9f", ["retention"] = "#2a9db5", ["tunnel"] = "#3d4f8a", ["floodway"] = "#4c8bc4" },
                Label = "name", LabelZoom = 13.5,
            },
        },
        new()
        {
            Id = "kesehatan", Name = "Fasilitas kesehatan", Group = "Penanganan", Kind = "point", Url = "data/health.json",
            Columns = new() { "name", "kind", "beds" },
            Style = new() { Fill = "var(--map-health)", Stroke = "var(--map-halo)", Shape = "cross", Size = 12, Width = 1.2, Label = "name", LabelZoom = 13.5 },
        },
        new()
        {
            Id = "posko", Name = "Posko pengungsian", Group = "Penanganan", Kind = "point", Url = "data/shelters.json",
            Columns = new() { "name", "capacity", "occupied", "status" },
            Style = new()
            {
                Mode = "categorized", Field = "status", Shape = "triangle", Size = 11, Stroke = "var(--map-halo)", Width = 1.5,
                Cats = new() { ["Tersedia"] = "var(--lvl-normal)", ["Hampir penuh"] = "var(--lvl-waspada)", ["Penuh"] = "var(--lvl-awas)" },
                Label = "name", LabelZoom = 13, LabelPrio = 2,
            },
        },
        new()
        {
            Id = "pda", Name = "Pos duga air", Group = "Situasi", Kind = "point", Url = "data/gauges.json",
            Columns = new() { "name", "river", "level_cm", "status", "updated" },
            Note = "Water level against the Waspada, Siaga and Awas marks (simulated readings).",
            Style = new()
            {
                Mode = "categorized", Field = "status", Shape = "diamond", Size = 13, Stroke = "var(--map-ink)", Width = 1.6,
                Cats = new(Palettes.Alert), Label = "name", LabelZoom = 11.5, LabelPrio = 4,
                LabelFont = "700 11px 'Public Sans', sans-serif",
            },
        },
    };

    /// <summary>The colour of an alert level, for the parts of the page that are not the map.</summary>
    public static string AlertClass(string status) => status switch
    {
        "Awas" => "awas",
        "Siaga" => "siaga",
        "Waspada" => "waspada",
        _ => "normal",
    };

    /// <summary>Friendlier column titles.</summary>
    public static string Column(string field) => field switch
    {
        "name" => "Name",
        "kabupaten" => "Kabupaten/kota",
        "population" => "Population",
        "households" => "Households",
        "affected_hh" => "Affected households",
        "low_ground_pct" => "Low ground %",
        "flooded_pct" => "Flooded %",
        "mean_elev_m" => "Mean elevation (m)",
        "risk" => "Flood risk",
        "max_depth_cm" => "Max depth (cm)",
        "depth_cm" => "Depth (cm)",
        "since" => "Since",
        "time" => "Reported",
        "source" => "Source",
        "level_cm" => "Level (cm)",
        "waspada_cm" => "Waspada at (cm)",
        "siaga_cm" => "Siaga at (cm)",
        "awas_cm" => "Awas at (cm)",
        "status" => "Status",
        "updated" => "Updated",
        "river" => "River",
        "capacity" => "Capacity",
        "occupied" => "Occupied",
        "contact" => "Contact",
        "kind" => "Kind",
        "state" => "State",
        "beds" => "Beds",
        "order" => "Order",
        "length_km" => "Length (km)",
        "class" => "Class",
        "elev" => "Elevation (m)",
        "note" => "Note",
        _ => field,
    };
}
