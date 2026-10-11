namespace Dashboard.Data;

public enum Source { Coal, Gas, Hydro, Geothermal, Solar, Wind }

public sealed class Plant
{
    public required string Name { get; init; }
    public required Source Source { get; init; }
    public required string Region { get; init; }
    public required double Capacity { get; init; }
    /// <summary>"online", "maintenance" or "tripped".</summary>
    public string Status { get; set; } = "online";
    public string Note { get; set; } = "";
    public double Output { get; set; }
    public bool Online => Status == "online";
}

public sealed record Region(string Id, string Name, double Share);

/// <summary>A 500 kV substation on the one-line diagram, placed in diagram units (0–1000 by 0–260).</summary>
public sealed record Node(string Id, string Name, string Region, double X, double Y);

public sealed class Line
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required double Capacity { get; init; }
    public double Flow { get; set; }
    public double Loading => Math.Abs(Flow) / Capacity;
}

public sealed class Alarm
{
    public required DateTime Time { get; init; }
    /// <summary>"info", "warn" or "crit".</summary>
    public required string Severity { get; init; }
    public required string Where { get; init; }
    public required string Text { get; init; }
    public bool Acked { get; set; }
}

/// <summary>One 15-minute settlement interval of the system.</summary>
public sealed record Interval(DateTime Time, double Demand, double[] BySource);

/// <summary>
/// A simulated Java–Bali power system: demand follows the daily curve (trough
/// before dawn, peak after sunset), plants are dispatched in merit order, and
/// frequency answers every imbalance. Plant names are real places; every
/// number is simulated.
/// </summary>
public static class Grid
{
    public static readonly Source[] Sources = { Source.Coal, Source.Gas, Source.Hydro, Source.Geothermal, Source.Solar, Source.Wind };

    public static readonly Region[] Regions =
    {
        new("west", "Banten & Jakarta", 0.36), new("jabar", "West Java", 0.22), new("jateng", "Central Java & DIY", 0.17),
        new("jatim", "East Java", 0.21), new("bali", "Bali", 0.04),
    };

    public static readonly List<Plant> Plants = new()
    {
        new() { Name = "PLTU Suralaya", Source = Source.Coal, Region = "west", Capacity = 3400 },
        new() { Name = "PLTU Jawa 7", Source = Source.Coal, Region = "west", Capacity = 2000 },
        new() { Name = "PLTU Lontar", Source = Source.Coal, Region = "west", Capacity = 945, Status = "maintenance", Note = "Boiler inspection until Wednesday" },
        new() { Name = "PLTGU Muara Karang", Source = Source.Gas, Region = "west", Capacity = 1600 },
        new() { Name = "PLTGU Priok", Source = Source.Gas, Region = "west", Capacity = 1400 },
        new() { Name = "PLTA Cirata", Source = Source.Hydro, Region = "jabar", Capacity = 1008 },
        new() { Name = "PLTA Saguling", Source = Source.Hydro, Region = "jabar", Capacity = 700 },
        new() { Name = "PLTA Jatiluhur", Source = Source.Hydro, Region = "jabar", Capacity = 187 },
        new() { Name = "PLTP Kamojang", Source = Source.Geothermal, Region = "jabar", Capacity = 235 },
        new() { Name = "PLTP Wayang Windu", Source = Source.Geothermal, Region = "jabar", Capacity = 227 },
        new() { Name = "PLTP Darajat", Source = Source.Geothermal, Region = "jabar", Capacity = 271 },
        new() { Name = "PLTS Terapung Cirata", Source = Source.Solar, Region = "jabar", Capacity = 145 },
        new() { Name = "PLTU Cirebon", Source = Source.Coal, Region = "jabar", Capacity = 1660 },
        new() { Name = "PLTGU Muara Tawar", Source = Source.Gas, Region = "jabar", Capacity = 2000 },
        new() { Name = "PLTU Tanjung Jati B", Source = Source.Coal, Region = "jateng", Capacity = 2640 },
        new() { Name = "PLTGU Tambak Lorok", Source = Source.Gas, Region = "jateng", Capacity = 1000 },
        new() { Name = "PLTP Dieng", Source = Source.Geothermal, Region = "jateng", Capacity = 60 },
        new() { Name = "PLTB Samas", Source = Source.Wind, Region = "jateng", Capacity = 50 },
        new() { Name = "PLTS Gunungkidul", Source = Source.Solar, Region = "jateng", Capacity = 100 },
        new() { Name = "PLTU Paiton", Source = Source.Coal, Region = "jatim", Capacity = 4700 },
        new() { Name = "PLTGU Gresik", Source = Source.Gas, Region = "jatim", Capacity = 2200 },
        new() { Name = "PLTGU Grati", Source = Source.Gas, Region = "jatim", Capacity = 830 },
        new() { Name = "PLTA Sutami", Source = Source.Hydro, Region = "jatim", Capacity = 105 },
        new() { Name = "PLTU Celukan Bawang", Source = Source.Coal, Region = "bali", Capacity = 380 },
        new() { Name = "PLTG Pesanggaran", Source = Source.Gas, Region = "bali", Capacity = 300 },
        new() { Name = "PLTS Bali Barat", Source = Source.Solar, Region = "bali", Capacity = 50 },
    };

    public static readonly Node[] Nodes =
    {
        new("SRLYA", "Suralaya", "west", 40, 70), new("BLRJA", "Balaraja", "west", 120, 120), new("GNDUL", "Gandul", "west", 200, 95),
        new("CBATU", "Cibatu", "jabar", 300, 120), new("BDSLN", "Bandung Selatan", "jabar", 330, 190), new("TSMYA", "Tasikmalaya", "jabar", 430, 205),
        new("UNGAR", "Ungaran", "jateng", 540, 100), new("PEDAN", "Pedan", "jateng", 590, 185),
        new("KDIRI", "Kediri", "jatim", 700, 195), new("KRIAN", "Krian", "jatim", 740, 110), new("PITON", "Paiton", "jatim", 860, 150), new("GRATI", "Grati", "jatim", 800, 170),
        new("GLMNK", "Gilimanuk", "bali", 940, 165), new("KAPAL", "Kapal", "bali", 975, 220),
    };

    public static readonly List<Line> Lines = new()
    {
        new() { From = "SRLYA", To = "BLRJA", Capacity = 4000 }, new() { From = "BLRJA", To = "GNDUL", Capacity = 3200 },
        new() { From = "GNDUL", To = "CBATU", Capacity = 3200 }, new() { From = "CBATU", To = "BDSLN", Capacity = 2600 },
        new() { From = "BDSLN", To = "TSMYA", Capacity = 2200 }, new() { From = "CBATU", To = "UNGAR", Capacity = 2600 },
        new() { From = "TSMYA", To = "PEDAN", Capacity = 2200 }, new() { From = "UNGAR", To = "PEDAN", Capacity = 2400 },
        new() { From = "UNGAR", To = "KRIAN", Capacity = 2600 }, new() { From = "PEDAN", To = "KDIRI", Capacity = 2400 },
        new() { From = "KDIRI", To = "GRATI", Capacity = 2400 }, new() { From = "KRIAN", To = "GRATI", Capacity = 2600 },
        new() { From = "GRATI", To = "PITON", Capacity = 4000 }, new() { From = "PITON", To = "GLMNK", Capacity = 600 },
        new() { From = "GLMNK", To = "KAPAL", Capacity = 600 },
    };

    public static List<Interval> History { get; } = new();
    public static List<(DateTime Time, double Hz)> Frequency { get; } = new();
    public static List<Alarm> Alarms { get; } = new();

    public static double Demand { get; private set; }
    public static double Hz { get; private set; } = 50;
    public static Dictionary<Source, double> Mix { get; } = Sources.ToDictionary(s => s, _ => 0d);
    public static Dictionary<string, double> RegionDemand { get; } = new();
    public static Dictionary<string, double> RegionSupply { get; } = new();

    public static event Action? Ticked;

    private static readonly Random Noise = new(2026);
    private static IDisposable? _timer;
    private static DateTime? _tripAt;
    private static double _tripMw;

    public static double Supply => Mix.Values.Sum();
    public static double OnlineCapacity => Plants.Where(p => p.Online).Sum(p => Available(p, DateTime.Now));
    public static double Reserve => OnlineCapacity - Demand;
    public static double RenewableShare => Supply <= 0 ? 0 : (Mix[Source.Hydro] + Mix[Source.Geothermal] + Mix[Source.Solar] + Mix[Source.Wind]) / Supply;

    /// <summary>Grams of CO₂ per kWh across the current mix.</summary>
    public static double Carbon => Supply <= 0 ? 0 : (Mix[Source.Coal] * 1010 + Mix[Source.Gas] * 430 + Mix[Source.Geothermal] * 40) / Supply;

    public static void Start()
    {
        if (_timer is not null) return;
        BuildHistory();
        Step(DateTime.Now);
        for (var i = 90; i > 0; i--) Frequency.Add((DateTime.Now.AddSeconds(-2 * i), 50 + Jitter(0.012)));
        Raise("info", "Control room", "Shift handover complete. Lontar is out for its planned boiler inspection.", DateTime.Now.AddMinutes(-42));
        Raise("warn", "Ungaran–Krian", "Loading above 85% during the afternoon ramp.", DateTime.Now.AddMinutes(-17));
        Raise("info", "Dispatch", "Hydro raised at Cirata and Saguling ahead of the evening peak.", DateTime.Now.AddMinutes(-6));
        _timer = Rustzor.Interop.Scheduler.SetInterval(() => { Step(DateTime.Now); Ticked?.Invoke(); }, 2000);
    }

    // ── the model ─────────────────────────────────────────────────────────

    /// <summary>System demand in MW: a pre-dawn trough, a daytime plateau and a peak after sunset.</summary>
    public static double DemandAt(DateTime t)
    {
        var h = t.Hour + t.Minute / 60d + t.Second / 3600d;
        var day = h is > 6 and < 18 ? Math.Sin(Math.PI * (h - 6) / 12) : 0;
        var evening = Math.Exp(-Math.Pow(h - 19.2, 2) / 3.2);
        var weekday = t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.91 : 1.0;
        return (20_600 + 4_200 * day + 5_600 * evening) * weekday;
    }

    private static double SolarAt(DateTime t)
    {
        var h = t.Hour + t.Minute / 60d;
        if (h is <= 6 or >= 18) return 0;
        var clouds = 0.75 + 0.25 * Math.Sin(t.DayOfYear * 1.7 + h * 0.9);
        return Math.Pow(Math.Sin(Math.PI * (h - 6) / 12), 1.4) * clouds;
    }

    private static double WindAt(DateTime t) => 0.28 + 0.18 * Math.Sin(t.DayOfYear * 0.6 + t.Hour * 0.35);

    /// <summary>What a plant could give now: renewables follow the sky; the rest their rating less auxiliaries.</summary>
    private static double Available(Plant p, DateTime t) => p.Source switch
    {
        Source.Solar => p.Capacity * SolarAt(t),
        Source.Wind => p.Capacity * WindAt(t),
        Source.Geothermal => p.Capacity * 0.92,
        _ => p.Capacity * 0.94,
    };

    /// <summary>Merit-order dispatch: geothermal, solar and wind first, hydro shaped to the peak, then coal, then gas.</summary>
    private static double[] Dispatch(DateTime t, double demand, bool apply)
    {
        var out_ = new double[Sources.Length];
        var remaining = demand;
        double Take(Source s, double fraction, double minFraction = 0)
        {
            var plants = Plants.Where(p => p.Source == s && p.Online).ToList();
            var avail = plants.Sum(p => Available(p, t));
            var floor = avail * minFraction;
            var take = Math.Clamp(remaining, floor, avail * fraction);
            if (apply)
                foreach (var p in plants) p.Output = avail <= 0 ? 0 : Available(p, t) / avail * take;
            remaining -= take;
            out_[(int)s] = take;
            return take;
        }
        Take(Source.Geothermal, 1);
        Take(Source.Solar, 1);
        Take(Source.Wind, 1);
        var peakiness = Math.Clamp((demand - 21_000) / 8_000, 0.25, 0.95);
        Take(Source.Hydro, peakiness);
        Take(Source.Coal, 1, 0.55);
        Take(Source.Gas, 1);
        // Short after gas (a unit has tripped): hydro is the fast reserve and opens up fully.
        if (remaining > 0)
        {
            var shaped = out_[(int)Source.Hydro];
            remaining += shaped;
            Take(Source.Hydro, 1);
        }
        if (apply)
            foreach (var p in Plants.Where(p => !p.Online)) p.Output = 0;
        return out_;
    }

    private static void BuildHistory()
    {
        var rng = new Random(DateTime.Today.DayOfYear);
        var now = DateTime.Now;
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute / 15 * 15, 0).AddDays(-7);
        for (var t = start; t <= now; t = t.AddMinutes(15))
        {
            var d = DemandAt(t) * (1 + (rng.NextDouble() - 0.5) * 0.02);
            History.Add(new Interval(t, d, Dispatch(t, d, apply: false)));
        }
    }

    private static double Jitter(double size) => (Noise.NextDouble() - 0.5) * 2 * size;

    private static void Step(DateTime now)
    {
        Demand = DemandAt(now) * (1 + Jitter(0.004));
        var mix = Dispatch(now, Demand, apply: true);
        foreach (var s in Sources) Mix[s] = mix[(int)s];

        // Frequency: governor noise, an under-frequency pull when supply is short,
        // and the dip-and-recover of a trip.
        var shortfall = Math.Max(0, Demand - Supply);
        var hz = 50 + Jitter(0.015) - shortfall / 40_000;
        if (_tripAt is { } at)
        {
            var dt = (now - at).TotalSeconds;
            // A 1 GW loss on this system dips roughly 0.08 Hz before governors and hydro recover it.
            var depth = _tripMw / 12_000;
            hz -= depth * (1 - Math.Exp(-dt / 1.2)) * Math.Exp(-dt / 9);
            if (dt > 90) _tripAt = null;
        }
        Hz = Math.Round(hz, 3);
        Frequency.Add((now, Hz));
        if (Frequency.Count > 150) Frequency.RemoveAt(0);
        if (Hz < 49.8 && !Alarms.Any(a => a.Severity == "crit" && (now - a.Time).TotalSeconds < 60))
            Raise("crit", "System", $"Frequency down to {Hz:0.000} Hz. Spinning reserve responding.", now);

        // Regions and the 500 kV backbone: each region's surplus flows along the chain.
        // Flows balance on what is generated; any shortfall is the frequency's problem, not a line's.
        foreach (var r in Regions)
        {
            RegionDemand[r.Id] = Demand * r.Share;
            RegionSupply[r.Id] = Plants.Where(p => p.Region == r.Id).Sum(p => p.Output);
        }
        var cumulative = 0d;
        for (var i = 0; i < Regions.Length - 1; i++)
        {
            // Positive flow runs west to east.
            cumulative += RegionSupply[Regions[i].Id] - Supply * Regions[i].Share;
            var westEnd = Regions[i].Id;
            var eastEnd = Regions[i + 1].Id;
            var ties = Lines.Where(l => RegionOf(l.From) == westEnd && RegionOf(l.To) == eastEnd).ToList();
            foreach (var l in ties) l.Flow = cumulative / ties.Count;
        }
        foreach (var l in Lines.Where(l => RegionOf(l.From) == RegionOf(l.To)))
            l.Flow = RegionDemand[RegionOf(l.From)] * (0.34 + 0.1 * Math.Sin(now.Minute / 9d + l.Capacity)) * (Lines.IndexOf(l) % 2 == 0 ? 1 : -1);
        foreach (var l in Lines.Where(l => l.Loading > 1.0))
            if (!Alarms.Any(a => a.Where == $"{Name(l.From)}–{Name(l.To)}" && (now - a.Time).TotalMinutes < 5))
                Raise("warn", $"{Name(l.From)}–{Name(l.To)}", $"Loading {l.Loading:P0} of {l.Capacity:N0} MW rating.", now);

        // The live interval replaces the last point; a new one starts every 15 minutes.
        var slot = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute / 15 * 15, 0);
        var point = new Interval(slot, Demand, mix);
        if (History.Count > 0 && History[^1].Time == slot) History[^1] = point;
        else
        {
            History.Add(point);
            if (History.Count > 7 * 96 + 1) History.RemoveAt(0);
        }
    }

    public static string RegionOf(string node) => Nodes.First(n => n.Id == node).Region;
    public static string Name(string node) => Nodes.First(n => n.Id == node).Name;

    // ── operator actions ─────────────────────────────────────────────────

    /// <summary>Trips a plant offline, as a protection relay would. The frequency dips and the rest pick up.</summary>
    public static void Trip(Plant p)
    {
        if (!p.Online) return;
        _tripMw = p.Output;
        _tripAt = DateTime.Now;
        p.Status = "tripped";
        p.Note = $"Tripped at {DateTime.Now:HH:mm:ss} with {p.Output:N0} MW";
        Raise("crit", p.Name, $"Unit tripped, {_tripMw:N0} MW lost. Reserves picking up the load.", DateTime.Now);
        Step(DateTime.Now);
        Ticked?.Invoke();
    }

    public static void Restore(Plant p)
    {
        if (p.Status != "tripped") return;
        p.Status = "online";
        p.Note = "";
        Raise("info", p.Name, "Unit synchronised and back on the grid.", DateTime.Now);
        Step(DateTime.Now);
        Ticked?.Invoke();
    }

    public static void Raise(string severity, string where, string text, DateTime time) =>
        Alarms.Insert(0, new Alarm { Time = time, Severity = severity, Where = where, Text = text });

    public static void AckAll()
    {
        foreach (var a in Alarms) a.Acked = true;
        Ticked?.Invoke();
    }

    // ── presentation ─────────────────────────────────────────────────────

    public static string Label(Source s) => s switch { Source.Geothermal => "Geothermal", _ => s.ToString() };

    public static string Color(Source s) => s switch
    {
        Source.Coal => "#55524c",
        Source.Gas => "#d9822b",
        Source.Hydro => "#2c7bb6",
        Source.Geothermal => "#b6403a",
        Source.Solar => "#e8b923",
        _ => "#4fa58a",
    };

    public static string Mw(double mw) => mw >= 10_000 ? $"{mw / 1000:0.0} GW" : $"{mw:N0} MW";
}
