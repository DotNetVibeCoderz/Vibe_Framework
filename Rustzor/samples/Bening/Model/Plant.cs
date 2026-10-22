namespace Bening.Model;

/// <summary>A measured value: what it is called, its unit and how many decimals it deserves.</summary>
public sealed record TagInfo(string Id, string NameId, string NameEn, string Unit, int Decimals)
{
    public string Name => L.T(NameId, NameEn);
}

/// <summary>One row of the inspector: a tag, optionally scaled (a stream carries half the flow), with a limit to draw.</summary>
public sealed record Reading(string Tag, double Factor = 1, double? Limit = null, string? LabelId = null, string? LabelEn = null);

public enum Kind { Pump, Distribution, Basin, Filter, Dosing, Tank, River, Intake }

/// <summary>A piece of equipment the 3D model names with the same id.</summary>
public sealed record Asset(string Id, Kind Kind, string NameId, string NameEn, string RoleId, string RoleEn, Reading[] Readings)
{
    public string Name => L.T(NameId, NameEn);
    public string Role => L.T(RoleId, RoleEn);
    public bool HasState => Kind is Kind.Pump or Kind.Distribution or Kind.Filter;
}

/// <summary>A rule: when the tag crosses the limit for the delay, the alarm stands until it is back past the deadband.</summary>
public sealed record AlarmDef(string Id, string Tag, string Op, double Limit, double Deadband, int Delay, int Priority, string Asset, string TextId, string TextEn, string ShortId, string ShortEn)
{
    public string Text => L.T(TextId, TextEn);
    public string Short => L.T(ShortId, ShortEn);
}

/// <summary>A treatment stage in the jar row: the water as it leaves that stage.</summary>
public sealed record Stage(string Id, string NameId, string NameEn, string Tag, double Factor, string Asset)
{
    public string Name => L.T(NameId, NameEn);
}

/// <summary>IPA Leuwi Bening: what it is made of, how it is measured and when it should complain.</summary>
public static class Plant
{
    public const string Name = "IPA Leuwi Bening";
    public const string Owner = "Perumda Air Minum Tirta Sangga";
    public const int Seed = 2610;
    public const int Start = 6 * 3600;
    public const string Date = "2026-10-22";
    public const double Design = 400; // L/s

    public static readonly TagInfo[] Tags = new TagInfo[]
    {
        new("river.level", "Muka air sungai", "River level", "m", 2),
        new("raw.turb", "Kekeruhan air baku", "Raw water turbidity", "NTU", 0),
        new("raw.flow", "Debit air baku", "Raw water flow", "L/s", 0),
        new("flow.sp", "Setpoin debit", "Flow setpoint", "L/s", 0),
        new("pac.dose", "Dosis PAC", "PAC dose", "mg/L", 1),
        new("pac.demand", "Kebutuhan PAC (uji jar)", "PAC demand (jar test)", "mg/L", 1),
        new("pac.tank", "Isi tangki PAC", "PAC tank", "%", 0),
        new("settled.turb", "Kekeruhan air endapan", "Settled water turbidity", "NTU", 1),
        new("treated.turb", "Kekeruhan air olahan", "Treated water turbidity", "NTU", 2),
        new("prod.flow", "Debit produksi", "Production flow", "L/s", 0),
        new("cl.dose", "Dosis klor", "Chlorine dose", "mg/L", 2),
        new("cl.res", "Sisa klor", "Chlorine residual", "mg/L", 2),
        new("res.level", "Level reservoir", "Reservoir level", "m", 2),
        new("demand", "Pemakaian kota", "City demand", "L/s", 0),
        new("dist.press", "Tekanan distribusi", "Distribution pressure", "bar", 2),
        new("plant.kw", "Daya listrik IPA", "Plant power", "kW", 0),
    }.Concat(Enumerable.Range(1, 3).SelectMany(n => new TagInfo[]
    {
        new($"RW-{n}.state", "Status", "State", "", 0),
        new($"RW-{n}.flow", "Debit", "Flow", "L/s", 0),
        new($"RW-{n}.kw", "Daya", "Power", "kW", 1),
        new($"RW-{n}.amp", "Arus motor", "Motor current", "A", 0),
        new($"RW-{n}.vib", "Getaran", "Vibration", "mm/s", 2),
        new($"RW-{n}.temp", "Suhu bantalan", "Bearing temperature", "°C", 1),
        new($"DS-{n}.state", "Status", "State", "", 0),
        new($"DS-{n}.flow", "Debit", "Flow", "L/s", 0),
        new($"DS-{n}.kw", "Daya", "Power", "kW", 1),
    })).Concat(Enumerable.Range(1, 4).SelectMany(n => new TagInfo[]
    {
        new($"F-{n}.hl", "Kehilangan tekanan", "Head loss", "m", 2),
        new($"F-{n}.state", "Status", "State", "", 0),
        new($"F-{n}.turb", "Kekeruhan keluaran", "Outlet turbidity", "NTU", 2),
    })).ToArray();

    public static readonly Dictionary<string, TagInfo> TagById = Tags.ToDictionary(t => t.Id);

    private static Asset RawPump(int n) => new($"RW-{n}", Kind.Pump, $"Pompa air baku {n}", $"Raw water pump {n}",
        n == 3 ? "Pompa turbin vertikal 225 L/s · cadangan" : "Pompa turbin vertikal 225 L/s · tugas",
        n == 3 ? "Vertical turbine pump, 225 L/s · standby" : "Vertical turbine pump, 225 L/s · duty",
        new Reading[] { new($"RW-{n}.flow"), new($"RW-{n}.amp"), new($"RW-{n}.temp", Limit: 80), new($"RW-{n}.vib", Limit: 4.5), new($"RW-{n}.kw") });

    private static Asset DistPump(int n) => new($"DS-{n}", Kind.Distribution, $"Pompa distribusi {n}", $"Distribution pump {n}",
        n == 3 ? "Pompa sentrifugal 260 L/s · cadangan" : "Pompa sentrifugal 260 L/s · tugas",
        n == 3 ? "End-suction pump, 260 L/s · standby" : "End-suction pump, 260 L/s · duty",
        new Reading[] { new($"DS-{n}.flow"), new($"DS-{n}.kw"), new("dist.press", Limit: 2.5), new("demand") });

    private static Asset Filter(int n) => new($"F-{n}", Kind.Filter, $"Saringan pasir cepat {n}", $"Rapid sand filter {n}",
        "Pasir silika 0,8 m · 66 m²", "Silica sand bed 0.8 m · 66 m²",
        new Reading[] { new($"F-{n}.hl", Limit: 2.1), new($"F-{n}.turb", Limit: 1), new("settled.turb") });

    public static readonly Asset[] Assets =
    {
        new("RIVER", Kind.River, "Sungai Ci Sangga", "Ci Sangga river", "Sumber air baku", "The raw water source",
            new Reading[] { new("raw.turb", Limit: 150), new("river.level") }),
        new("INTAKE", Kind.Intake, "Intake & rumah pompa", "Intake and pump deck", "Saringan kasar dan tiga pompa air baku", "Bar screen and three raw water pumps",
            new Reading[] { new("raw.flow"), new("flow.sp"), new("raw.turb", Limit: 150) }),
        RawPump(1), RawPump(2), RawPump(3),
        new("FM-1", Kind.Basin, "Pengaduk cepat", "Flash mixer", "Koagulasi · PAC diaduk 60 detik", "Coagulation · PAC mixed in 60 seconds",
            new Reading[] { new("raw.flow"), new("pac.dose"), new("pac.demand"), new("raw.turb") }),
        new("PAC", Kind.Dosing, "Tangki & pompa dosis PAC", "PAC tanks and dosing pump", "Poly aluminium chloride 10 %, 2 × 20 m³", "Poly aluminium chloride 10 %, 2 × 20 m³",
            new Reading[] { new("pac.dose"), new("pac.demand"), new("pac.tank", Limit: 15) }),
        new("FL-1", Kind.Basin, "Flokulator A", "Flocculator A", "Tiga roda pengaduk · 25 menit", "Three paddle wheels · 25 minutes",
            new Reading[] { new("raw.flow", 0.5, LabelId: "Debit lajur A", LabelEn: "Stream A flow"), new("pac.dose"), new("raw.turb") }),
        new("FL-2", Kind.Basin, "Flokulator B", "Flocculator B", "Tiga roda pengaduk · 25 menit", "Three paddle wheels · 25 minutes",
            new Reading[] { new("raw.flow", 0.5, LabelId: "Debit lajur B", LabelEn: "Stream B flow"), new("pac.dose"), new("raw.turb") }),
        new("SD-1", Kind.Basin, "Bak pengendap A", "Settling basin A", "Pengendap tube settler · 2 jam", "Tube settler basin · 2 hours",
            new Reading[] { new("settled.turb", Limit: 5), new("raw.flow", 0.5, LabelId: "Debit lajur A", LabelEn: "Stream A flow") }),
        new("SD-2", Kind.Basin, "Bak pengendap B", "Settling basin B", "Pengendap tube settler · 2 jam", "Tube settler basin · 2 hours",
            new Reading[] { new("settled.turb", Limit: 5), new("raw.flow", 0.5, LabelId: "Debit lajur B", LabelEn: "Stream B flow") }),
        Filter(1), Filter(2), Filter(3), Filter(4),
        new("CL-1", Kind.Dosing, "Klorinator", "Chlorinator", "Gas klor, 3 drum 1 ton", "Chlorine gas, three 1-tonne drums",
            new Reading[] { new("cl.dose"), new("cl.res", Limit: 0.3), new("prod.flow") }),
        new("RES-1", Kind.Tank, "Reservoir air bersih", "Clear water reservoir", "4.000 m³ · tinggi muka air maks. 4,8 m", "4,000 m³ · top water level 4.8 m",
            new Reading[] { new("res.level", Limit: 1.5), new("treated.turb", Limit: 1), new("cl.res", Limit: 0.3), new("prod.flow"), new("demand") }),
        DistPump(1), DistPump(2), DistPump(3),
    };

    public static readonly Dictionary<string, Asset> AssetById = Assets.ToDictionary(a => a.Id);

    /// <summary>The jar row, left to right: the water as each stage leaves it.</summary>
    public static readonly Stage[] Stages =
    {
        new("raw", "Air baku", "Raw", "raw.turb", 1, "RIVER"),
        new("mix", "Koagulasi", "Coagulation", "raw.turb", 1, "FM-1"),
        new("floc", "Flokulasi", "Flocculation", "raw.turb", 0.75, "FL-1"),
        new("settle", "Sedimentasi", "Settling", "settled.turb", 1, "SD-1"),
        new("filter", "Filtrasi", "Filtration", "treated.turb", 1, "F-1"),
        new("res", "Reservoir", "Reservoir", "treated.turb", 1, "RES-1"),
    };

    private static IEnumerable<AlarmDef> PumpAlarms(string p, int n, string kind)
    {
        if (kind == "RW")
        {
            yield return new($"{p}.temp", $"{p}.temp", ">", 80, 2, 60, 2, p, $"Suhu bantalan {p} di atas 80 °C", $"{p} bearing temperature above 80 °C", "suhu bantalan", "bearing temp");
            yield return new($"{p}.vib", $"{p}.vib", ">", 4.5, 0.3, 60, 3, p, $"Getaran {p} di atas 4,5 mm/s (ISO 10816 zona C)", $"{p} vibration above 4.5 mm/s (ISO 10816 zone C)", "getaran", "vibration");
        }
        yield return new($"{p}.trip", $"{p}.state", "=", 2, 0, 0, 1, p, $"{p} trip", $"{p} tripped", "trip", "tripped");
    }

    public static readonly AlarmDef[] Alarms = new AlarmDef[]
    {
        new("raw.hi", "raw.turb", ">", 150, 10, 120, 3, "RIVER", "Air baku sangat keruh (> 150 NTU)", "Raw water very turbid (> 150 NTU)", "air baku keruh", "turbid raw water"),
        new("settled.hi", "settled.turb", ">", 5, 0.5, 120, 3, "SD-1", "Kekeruhan air endapan di atas 5 NTU", "Settled water above 5 NTU", "endapan keruh", "settled turbid"),
        new("treated.hi", "treated.turb", ">", 1, 0.1, 120, 2, "RES-1", "Kekeruhan air olahan di atas 1 NTU (target IPA)", "Treated water above 1 NTU (plant target)", "air olahan > 1 NTU", "treated > 1 NTU"),
        new("treated.hihi", "treated.turb", ">", 3, 0.2, 60, 1, "RES-1", "Kekeruhan air olahan melewati baku mutu 3 NTU (Permenkes 2/2023)", "Treated water over the 3 NTU limit (Permenkes 2/2023)", "lewat baku mutu", "over the limit"),
        new("cl.lo", "cl.res", "<", 0.3, 0.05, 120, 2, "CL-1", "Sisa klor di bawah 0,3 mg/L", "Chlorine residual below 0.3 mg/L", "sisa klor rendah", "chlorine low"),
        new("cl.hi", "cl.res", ">", 1.5, 0.1, 120, 3, "CL-1", "Sisa klor di atas 1,5 mg/L", "Chlorine residual above 1.5 mg/L", "sisa klor tinggi", "chlorine high"),
        new("res.lo", "res.level", "<", 1.5, 0.1, 60, 2, "RES-1", "Level reservoir di bawah 1,5 m", "Reservoir level below 1.5 m", "level rendah", "level low"),
        new("res.lolo", "res.level", "<", 0.8, 0.1, 30, 1, "RES-1", "Level reservoir di bawah 0,8 m: pompa distribusi akan berhenti", "Reservoir below 0.8 m: the distribution pumps will stop", "level sangat rendah", "level very low"),
        new("res.hi", "res.level", ">", 4.6, 0.1, 60, 3, "RES-1", "Reservoir hampir meluap (> 4,6 m)", "Reservoir close to overflowing (> 4.6 m)", "hampir meluap", "near overflow"),
        new("press.lo", "dist.press", "<", 2.5, 0.1, 60, 2, "DS-2", "Tekanan distribusi di bawah 2,5 bar", "Distribution pressure below 2.5 bar", "tekanan rendah", "pressure low"),
        new("pac.lo", "pac.tank", "<", 15, 2, 60, 3, "PAC", "Stok PAC di bawah 15 %", "PAC stock below 15 %", "stok PAC", "PAC stock"),
    }
    .Concat(Enumerable.Range(1, 4).Select(n => new AlarmDef($"F-{n}.hl", $"F-{n}.hl", ">", 2.1, 0.1, 60, 3, $"F-{n}",
        $"Kehilangan tekanan F-{n} di atas 2,1 m: saatnya backwash", $"F-{n} head loss above 2.1 m: backwash due", "perlu backwash", "backwash due")))
    .Concat(Enumerable.Range(1, 3).SelectMany(n => PumpAlarms($"RW-{n}", n, "RW")))
    .Concat(Enumerable.Range(1, 3).SelectMany(n => PumpAlarms($"DS-{n}", n, "DS")))
    .ToArray();

    public static readonly Dictionary<string, AlarmDef> AlarmById = Alarms.ToDictionary(a => a.Id);

    public static readonly string[] Technicians = { "Dadang Suryana", "Yusuf Hidayat", "Euis Rahmawati", "Ujang Kurniawan" };
    public const string Operator = "Rina Kartika";
    public const string NightOperator = "Asep Saepudin";
}
