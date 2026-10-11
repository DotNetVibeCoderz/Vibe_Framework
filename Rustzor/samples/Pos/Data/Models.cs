using System.Globalization;

namespace Pos.Data;

/// <summary>An item on the menu. Prices are whole rupiah.</summary>
public sealed class Product
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public long Price { get; set; }
    public bool SoldOut { get; set; }
    /// <summary>Order on the register grid; best sellers first.</summary>
    public int Rank { get; set; }
}

/// <summary>A choice the barista needs, such as size or ice; each option may cost extra.</summary>
public sealed record OptionGroup(string Name, (string Label, long Extra)[] Choices);

/// <summary>One line on the open ticket.</summary>
public sealed class CartLine
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Options { get; set; } = "";
    public long Unit { get; set; }
    public int Qty { get; set; } = 1;
    public long Total => Unit * Qty;
}

/// <summary>A sold line, as stored with its order.</summary>
public sealed class OrderLine
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Options { get; set; } = "";
    public long Unit { get; set; }
    public int Qty { get; set; }
}

public sealed class Order
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    /// <summary>Local time, "yyyy-MM-ddTHH:mm:ss".</summary>
    public string Time { get; set; } = "";
    public string ShiftId { get; set; } = "";
    public string Cashier { get; set; } = "";
    /// <summary>"dine-in" or "takeaway".</summary>
    public string Service { get; set; } = "dine-in";
    public string Table { get; set; } = "";
    public List<OrderLine> Lines { get; set; } = new();
    public long Subtotal { get; set; }
    public string Promo { get; set; } = "";
    public long Discount { get; set; }
    public long Tax { get; set; }
    public long Rounding { get; set; }
    public long Total { get; set; }
    /// <summary>"cash", "qris" or "card".</summary>
    public string Method { get; set; } = "cash";
    public long Tendered { get; set; }
    public long Change { get; set; }
    public string Reference { get; set; } = "";
    /// <summary>"paid" or "void".</summary>
    public string Status { get; set; } = "paid";
    public string VoidReason { get; set; } = "";

    public int Items => Lines.Sum(l => l.Qty);
    public string Clock => Time.Length >= 16 ? Time.Substring(11, 5) : Time;
    public string Day => Time.Length >= 10 ? Time.Substring(0, 10) : Time;
}

/// <summary>A cashier's session at the till, from opening float to the counted drawer.</summary>
public sealed class Shift
{
    public string Id { get; set; } = "";
    public string Cashier { get; set; } = "";
    public string Opened { get; set; } = "";
    public string Closed { get; set; } = "";
    public long OpeningCash { get; set; }
    public long CountedCash { get; set; }
    public bool IsOpen => Closed.Length == 0;
}

public static class Menu
{
    public static readonly string[] Categories = { "Kopi", "Non-kopi", "Makanan", "Kue" };
    public static readonly string[] Cashiers = { "Ayu", "Bagas", "Sekar" };

    /// <summary>Pajak restoran (PB1): 10% on food and drink served.</summary>
    public const decimal TaxRate = 0.10m;

    public static readonly Dictionary<string, (string Label, decimal Rate)> Promos = new()
    {
        ["SENJA10"] = ("Senja 10%", 0.10m),
        ["PAGI20"] = ("Morning 20%", 0.20m),
        ["STAFF"] = ("Staff 30%", 0.30m),
    };

    public static OptionGroup[] OptionsFor(string category) => category switch
    {
        "Kopi" => new[]
        {
            new OptionGroup("Size", new[] { ("Regular", 0L), ("Large", 6000L) }),
            new OptionGroup("Serve", new[] { ("Iced", 0L), ("Hot", 0L) }),
            new OptionGroup("Sugar", new[] { ("Normal sugar", 0L), ("Less sugar", 0L), ("No sugar", 0L) }),
            new OptionGroup("Extra", new[] { ("No extra", 0L), ("Extra shot", 5000L), ("Oat milk", 7000L) }),
        },
        "Non-kopi" => new[]
        {
            new OptionGroup("Size", new[] { ("Regular", 0L), ("Large", 6000L) }),
            new OptionGroup("Serve", new[] { ("Iced", 0L), ("Hot", 0L) }),
            new OptionGroup("Sugar", new[] { ("Normal sugar", 0L), ("Less sugar", 0L), ("No sugar", 0L) }),
        },
        "Makanan" => new[]
        {
            new OptionGroup("Spice", new[] { ("Medium", 0L), ("Mild", 0L), ("Extra pedas", 0L) }),
            new OptionGroup("Extra", new[] { ("No extra", 0L), ("Fried egg", 5000L), ("Extra rice", 6000L) }),
        },
        _ => new[] { new OptionGroup("Warm", new[] { ("Room temperature", 0L), ("Warmed", 0L) }) },
    };

    /// <summary>
    /// The options as the barista reads them: every non-default choice, plus
    /// hot or iced, which is always written down for a drink.
    /// </summary>
    public static string Describe(OptionGroup[] groups, IReadOnlyList<(string Label, long Extra)> picks) =>
        string.Join(", ", groups.Select((g, i) => (g, pick: picks[i]))
            .Where(x => x.g.Name == "Serve" || x.pick.Label != x.g.Choices[0].Label)
            .Select(x => x.pick.Label));

    public static string Tone(string category) => category switch
    {
        "Kopi" => "roast",
        "Non-kopi" => "pandan",
        "Makanan" => "sambal",
        _ => "aren",
    };

    public static string MethodName(string method) => method switch { "qris" => "QRIS", "card" => "Card", _ => "Cash" };

    /// <summary>Rupiah the way a till prints it: "Rp 28.000".</summary>
    public static string Rp(long amount) =>
        (amount < 0 ? "−Rp " : "Rp ") + Math.Abs(amount).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');

    /// <summary>The figure alone, for receipt columns: "28.000".</summary>
    public static string Num(long amount) => amount.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');

    /// <summary>Thousands for tight spaces: "28k", "1,2 jt".</summary>
    public static string Short(long amount) => amount >= 1_000_000
        ? (amount / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + " jt"
        : (amount / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length == 1 ? name.Substring(0, Math.Min(2, name.Length)) : $"{parts[0][0]}{parts[1][0]}").ToUpperInvariant();
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
    public static string Today() => DateTime.Today.ToString("yyyy-MM-dd");
}
