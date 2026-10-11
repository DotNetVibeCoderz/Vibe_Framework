namespace Erp.Data;

/// <summary>
/// Jati Lestari, a teak furniture workshop in Tahunan, Jepara: what it buys,
/// what it makes and how, where it keeps stock, and whom it trades with.
/// Names and places are real; the business and its figures are invented.
/// </summary>
public static class Catalog
{
    public const string RawStore = "GBB", GoodsStore = "GBJ", Showroom = "SMG";

    public static readonly Warehouse[] Warehouses =
    {
        new(RawStore, "Gudang Bahan", "Tahunan, Jepara", "Timber, rattan, fabric, oil and fittings for the workshop"),
        new(GoodsStore, "Gudang Barang Jadi", "Tahunan, Jepara", "Finished pieces, wrapped and ready to ship"),
        new(Showroom, "Showroom Semarang", "Jl. Pemuda, Semarang", "Display stock sold to walk-in trade buyers"),
    };

    public static Warehouse WarehouseOf(string id) => Warehouses.First(w => w.Id == id);

    public static Item[] NewItems() => new[]
    {
        Finished("KRS-KTN", "Kartini dining chair, teak", 1_850_000m, 24),
        Finished("MJM-180", "Dining table 180×90, teak", 7_200_000m, 4),
        Finished("LMR-2P", "Two-door wardrobe, mahogany", 5_400_000m, 3),
        Finished("BGK-150", "Garden bench 150 cm, teak", 3_100_000m, 6),
        Finished("KRS-RTN", "Terrace chair, rattan and teak", 2_250_000m, 10),
        Finished("DPN-UKR", "Carved daybed, teak", 9_800_000m, 2),
        Raw("KYU-JTI", "Teak lumber, grade A (Perhutani)", "m³", 2.5m, "S-PHT"),
        Raw("KYU-MHN", "Mahogany lumber", "m³", 1.5m, "S-BLR"),
        Raw("RTN-SGA", "Peeled sega rattan", "kg", 120m, "S-RTN"),
        Raw("PLY-18", "Plywood 18 mm", "sheet", 20m, "S-SNR"),
        Raw("OIL-TEK", "Teak oil finish", "litre", 30m, "S-KIM"),
        Raw("KAN-JOK", "Canvas upholstery fabric", "metre", 50m, "S-TKS"),
        Raw("BSA-D32", "Foam D-32, 5 cm", "sheet", 20m, "S-TKS"),
        Raw("ENG-KNG", "Brass hinge and handle set", "set", 30m, "S-SNR"),
        Raw("SKR-500", "Wood screws, box of 500", "box", 10m, "S-SNR"),
    };

    private static Item Finished(string id, string name, decimal price, decimal reorder) =>
        new() { Id = id, Name = name, Kind = "finished", Unit = "pcs", Price = price, Reorder = reorder };

    private static Item Raw(string id, string name, string unit, decimal reorder, string supplier) =>
        new() { Id = id, Name = name, Kind = "raw", Unit = unit, Reorder = reorder, Supplier = supplier };

    /// <summary>What each supplier charges per unit, before PPN.</summary>
    public static readonly Dictionary<string, decimal> Costs = new()
    {
        ["KYU-JTI"] = 14_500_000m, ["KYU-MHN"] = 6_200_000m, ["RTN-SGA"] = 38_000m, ["PLY-18"] = 285_000m,
        ["OIL-TEK"] = 95_000m, ["KAN-JOK"] = 62_000m, ["BSA-D32"] = 145_000m, ["ENG-KNG"] = 48_000m, ["SKR-500"] = 55_000m,
    };

    public static decimal CostOf(string item) => Costs.TryGetValue(item, out var c) ? c : 0m;

    /// <summary>Bills of materials: per unit, with the wages of the craftsmen and the days a batch takes.</summary>
    public static readonly Bom[] Boms =
    {
        new("KRS-KTN", new[] { ("KYU-JTI", 0.045m), ("KAN-JOK", 0.6m), ("BSA-D32", 0.5m), ("OIL-TEK", 0.15m), ("SKR-500", 0.05m) }, 210_000m, 4),
        new("MJM-180", new[] { ("KYU-JTI", 0.28m), ("OIL-TEK", 0.6m), ("SKR-500", 0.1m) }, 850_000m, 6),
        new("LMR-2P", new[] { ("KYU-MHN", 0.32m), ("PLY-18", 3m), ("ENG-KNG", 2m), ("OIL-TEK", 0.8m), ("SKR-500", 0.15m) }, 720_000m, 6),
        new("BGK-150", new[] { ("KYU-JTI", 0.12m), ("OIL-TEK", 0.4m), ("SKR-500", 0.06m) }, 380_000m, 4),
        new("KRS-RTN", new[] { ("RTN-SGA", 6m), ("KYU-JTI", 0.02m), ("KAN-JOK", 0.8m), ("BSA-D32", 0.5m), ("OIL-TEK", 0.1m) }, 420_000m, 5),
        new("DPN-UKR", new[] { ("KYU-JTI", 0.36m), ("BSA-D32", 2m), ("KAN-JOK", 3m), ("OIL-TEK", 0.9m), ("SKR-500", 0.1m) }, 1_650_000m, 9),
    };

    public static Bom BomOf(string item) => Boms.First(b => b.Item == item);

    /// <summary>Standard cost of one unit at today's supplier prices: what a quote is checked against.</summary>
    public static decimal StandardCost(string item)
    {
        var bom = BomOf(item);
        return Fmt.Whole(bom.Parts.Sum(p => p.Qty * CostOf(p.Item)) + bom.Labour);
    }

    public static List<Partner> NewPartners() => new()
    {
        new() { Id = "S-PHT", Name = "Perum Perhutani KPH Kendal", Kind = "supplier", City = "Kendal", TermsDays = 30 },
        new() { Id = "S-BLR", Name = "CV Mahoni Jaya Blora", Kind = "supplier", City = "Blora", TermsDays = 30 },
        new() { Id = "S-RTN", Name = "UD Rotan Mandiri", Kind = "supplier", City = "Cirebon", TermsDays = 21 },
        new() { Id = "S-SNR", Name = "Toko Bangunan Sinar Jepara", Kind = "supplier", City = "Jepara", TermsDays = 14 },
        new() { Id = "S-KIM", Name = "CV Kimia Kayu Semarang", Kind = "supplier", City = "Semarang", TermsDays = 30 },
        new() { Id = "S-TKS", Name = "PT Tekstil Pekalongan Jaya", Kind = "supplier", City = "Pekalongan", TermsDays = 30 },
        new() { Id = "C-TBN", Name = "Hotel Tanjung Benoa", Kind = "customer", City = "Badung, Bali", TermsDays = 30 },
        new() { Id = "C-RKI", Name = "PT Rumah Kayu Interior", Kind = "customer", City = "Jakarta Selatan", TermsDays = 30 },
        new() { Id = "C-VSU", Name = "Villa Sekar Ubud", Kind = "customer", City = "Gianyar, Bali", TermsDays = 21 },
        new() { Id = "C-KRT", Name = "Kafe Ruang Tengah", Kind = "customer", City = "Yogyakarta", TermsDays = 14 },
        new() { Id = "C-NDH", Name = "Nordhus Living Indonesia", Kind = "customer", City = "Surabaya", TermsDays = 45 },
        new() { Id = "C-GRY", Name = "Griya Mebel Solo", Kind = "customer", City = "Surakarta", TermsDays = 30 },
    };

    /// <summary>Status colours shared by every document list.</summary>
    public static string Tone(string status) => status switch
    {
        "ordered" or "confirmed" or "planned" => "slate",
        "received" or "delivered" or "in progress" => "amber",
        "invoiced" => "teak",
        "paid" or "done" => "leaf",
        "overdue" or "short" => "red",
        _ => "faint",
    };
}
