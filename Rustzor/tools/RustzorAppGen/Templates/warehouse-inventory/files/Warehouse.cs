using Rustzor.Data;

namespace {{Namespace}};

public sealed class Item
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Bin { get; set; } = "";
    public int OnHand { get; set; }
    public int ReorderPoint { get; set; }
    public double UnitCost { get; set; }
    public bool Low => OnHand <= ReorderPoint;
}

public sealed class Movement
{
    public long Id { get; set; }
    public string At { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Quantity { get; set; }
    public string Reference { get; set; } = "";
}

/// <summary>
/// The warehouse database: SQLite (sql.js) saved to the browser under one key.
/// Stock levels change only through <see cref="MoveAsync"/>, so every unit is
/// accounted for in <c>movements</c>.
/// </summary>
public static class Warehouse
{
    private static Task<SqliteDb>? _db;

    public static Task<SqliteDb> Db => _db ??= OpenAsync();

    public static readonly string[] Kinds = { "receive", "pick", "adjust" };

    private static async Task<SqliteDb> OpenAsync()
    {
        var db = await SqliteDb.OpenAsync(persistKey: "warehouse");
        await db.RunAsync("""
            create table if not exists items (
              sku text primary key, name text not null, category text not null, bin text not null,
              onHand integer not null default 0, reorderPoint integer not null default 0, unitCost real not null default 0)
            """);
        await db.RunAsync("""
            create table if not exists movements (
              id integer primary key autoincrement, at text not null, sku text not null references items(sku),
              kind text not null, quantity integer not null, reference text not null default '')
            """);
        if ((await db.QueryAsync("select count(*) from items")).Scalar.AsInt() == 0) await SeedAsync(db);
        return db;
    }

    public static async Task<List<Item>> ItemsAsync(string search = "", bool lowOnly = false)
    {
        var db = await Db;
        var like = "%" + search.Trim() + "%";
        var result = await db.QueryAsync(
            "select * from items where (sku like ? or name like ? or bin like ?) " + (lowOnly ? "and onHand <= reorderPoint " : "") + "order by bin, sku",
            like, like, like);
        return result.As<Item>();
    }

    public static async Task<List<Movement>> MovementsAsync(string? sku = null, int limit = 200)
    {
        var db = await Db;
        var result = sku is null
            ? await db.QueryAsync("select * from movements order by id desc limit ?", limit)
            : await db.QueryAsync("select * from movements where sku = ? order by id desc limit ?", sku, limit);
        return result.As<Movement>();
    }

    /// <summary>Records a movement and updates stock. A pick cannot take more than is on hand.</summary>
    public static async Task MoveAsync(string sku, string kind, int quantity, string reference)
    {
        var db = await Db;
        var signed = kind == "pick" ? -Math.Abs(quantity) : kind == "receive" ? Math.Abs(quantity) : quantity;
        var onHand = (await db.QueryAsync("select onHand from items where sku = ?", sku)).Scalar.AsInt();
        if (onHand + signed < 0) throw new InvalidOperationException($"Only {onHand} of {sku} on hand.");
        await db.TransactionAsync(async tx =>
        {
            await tx.RunAsync("update items set onHand = onHand + ? where sku = ?", signed, sku);
            await tx.RunAsync("insert into movements (at, sku, kind, quantity, reference) values (?, ?, ?, ?, ?)",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm"), sku, kind, signed, reference);
        });
        await db.SaveAsync();
    }

    public static async Task AddItemAsync(Item item)
    {
        var db = await Db;
        await db.RunAsync("insert into items (sku, name, category, bin, onHand, reorderPoint, unitCost) values (?, ?, ?, ?, 0, ?, ?)",
            item.Sku, item.Name, item.Category, item.Bin, item.ReorderPoint, item.UnitCost);
        if (item.OnHand > 0) await MoveAsync(item.Sku, "receive", item.OnHand, "Opening stock");
        else await db.SaveAsync();
    }

    public static async Task ResetAsync()
    {
        var db = await Db;
        await db.RunAsync("delete from movements");
        await db.RunAsync("delete from items");
        await SeedAsync(db);
    }

    private static async Task SeedAsync(SqliteDb db)
    {
        (string Sku, string Name, string Category, string Bin, int Qty, int Reorder, double Cost)[] items =
        {
            ("BRG-6204", "Ball bearing 6204-2RS", "Bearings", "A-01-1", 340, 120, 2.15),
            ("BRG-6305", "Ball bearing 6305-ZZ", "Bearings", "A-01-2", 64, 80, 3.40),
            ("BLT-M8-30", "Hex bolt M8×30, zinc", "Fasteners", "A-02-1", 4200, 1000, 0.06),
            ("NUT-M8", "Hex nut M8, zinc", "Fasteners", "A-02-2", 3900, 1000, 0.03),
            ("WSH-M8", "Flat washer M8", "Fasteners", "A-02-3", 650, 1000, 0.02),
            ("VBL-A42", "V-belt A42", "Drive", "B-01-1", 28, 10, 7.90),
            ("CPL-L075", "Jaw coupling L075", "Drive", "B-01-2", 9, 6, 18.50),
            ("MTR-075", "AC motor 0.75 kW", "Motors", "C-01-1", 4, 2, 142.00),
            ("SNS-PNP12", "Inductive sensor PNP M12", "Sensors", "C-02-1", 37, 15, 21.30),
            ("CBL-M12-5", "M12 cable 5 m, 4-pin", "Sensors", "C-02-2", 12, 20, 9.75),
            ("FLT-HYD10", "Hydraulic filter 10 µm", "Hydraulics", "D-01-1", 18, 8, 34.00),
            ("OIL-ISO46", "Hydraulic oil ISO 46, 20 L", "Hydraulics", "D-01-2", 6, 4, 88.00),
        };
        var day = DateTime.Today.AddDays(-14);
        foreach (var i in items)
        {
            await db.RunAsync("insert into items (sku, name, category, bin, onHand, reorderPoint, unitCost) values (?, ?, ?, ?, ?, ?, ?)",
                i.Sku, i.Name, i.Category, i.Bin, i.Qty, i.Reorder, i.Cost);
            await db.RunAsync("insert into movements (at, sku, kind, quantity, reference) values (?, ?, 'receive', ?, 'Opening stock')",
                day.ToString("yyyy-MM-dd 08:00"), i.Sku, i.Qty);
        }
        await db.SaveAsync();
    }
}
