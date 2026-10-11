using System.Globalization;
using System.Text;
using Rustzor.Data;

namespace {{Namespace}};

public sealed record Order(string Id, DateTime Date, string Region, string Channel, string Product, string Category, int Quantity, double UnitPrice, double UnitCost)
{
    public double Revenue => Quantity * UnitPrice;
    public double Margin => Quantity * (UnitPrice - UnitCost);
}

public sealed class Filter
{
    public string Region { get; set; } = "All";
    public string Channel { get; set; } = "All";
    /// <summary>Months back from the latest order: 3, 6 or 12.</summary>
    public int Months { get; set; } = 12;
}

/// <summary>The order book and the questions the dashboard asks of it.</summary>
public static class Sales
{
    public static readonly string[] Regions = { "Java", "Sumatra", "Kalimantan", "Sulawesi", "Bali & Nusa" };
    public static readonly string[] Channels = { "Online", "Retail", "Wholesale" };

    private static readonly (string Name, string Category, double Price, double Cost, int Weight)[] Products =
    {
        ("Arabica 250 g", "Coffee", 89_000, 41_000, 9), ("Robusta 1 kg", "Coffee", 210_000, 118_000, 5), ("Cold brew pack", "Coffee", 65_000, 22_000, 7),
        ("Pour-over kit", "Equipment", 349_000, 190_000, 3), ("Hand grinder", "Equipment", 525_000, 300_000, 2), ("Gooseneck kettle", "Equipment", 610_000, 355_000, 2),
        ("Tumbler", "Merch", 145_000, 52_000, 6), ("Tote bag", "Merch", 79_000, 24_000, 4), ("Barista class", "Services", 750_000, 260_000, 1),
    };

    // After the tables above: static initializers run in declaration order.
    public static List<Order> Orders { get; private set; } = Generate();
    public static string Source { get; private set; } = "Sample data";

    public static IEnumerable<Order> Apply(Filter f, bool previous = false)
    {
        var end = Orders.Count == 0 ? DateTime.Today : Orders.Max(o => o.Date).Date.AddDays(1);
        var start = end.AddMonths(-f.Months);
        if (previous) (start, end) = (start.AddMonths(-f.Months), start);
        return Orders.Where(o => o.Date >= start && o.Date < end
            && (f.Region == "All" || o.Region == f.Region)
            && (f.Channel == "All" || o.Channel == f.Channel));
    }

    public static string Rupiah(double v) =>
        v >= 1e9 ? $"Rp {v / 1e9:0.0} M" : v >= 1e6 ? $"Rp {v / 1e6:0.0} jt" : $"Rp {v.ToString("#,##0", CultureInfo.InvariantCulture)}";

    public static string ToCsv(IEnumerable<Order> orders)
    {
        var sb = new StringBuilder("id,date,region,channel,product,category,quantity,unit_price,unit_cost\n");
        foreach (var o in orders)
            sb.Append(o.Id).Append(',').Append(o.Date.ToString("yyyy-MM-dd")).Append(',').Append(o.Region).Append(',').Append(o.Channel).Append(',')
              .Append('"').Append(o.Product).Append('"').Append(',').Append(o.Category).Append(',').Append(o.Quantity).Append(',')
              .Append(o.UnitPrice.ToString(CultureInfo.InvariantCulture)).Append(',').Append(o.UnitCost.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Loads orders from CSV with the columns <see cref="ToCsv"/> writes. Returns how many rows were read.</summary>
    public static int Import(string csv, string name)
    {
        var rows = Csv.Parse(csv);
        if (rows.Count < 2) throw new FormatException("The file has no data rows.");
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string n) => header.IndexOf(n) is var i and >= 0 ? i : throw new FormatException($"Missing column '{n}'.");
        var (id, date, region, channel, product, category, qty, price, cost) =
            (Col("id"), Col("date"), Col("region"), Col("channel"), Col("product"), Col("category"), Col("quantity"), Col("unit_price"), Col("unit_cost"));
        var orders = new List<Order>();
        foreach (var r in rows.Skip(1))
        {
            if (r.Length < header.Count || !DateTime.TryParse(r[date], out var d)) continue;
            orders.Add(new Order(r[id], d, r[region], r[channel], r[product], r[category], (int)Csv.Number(r[qty]), Csv.Number(r[price]), Csv.Number(r[cost])));
        }
        if (orders.Count == 0) throw new FormatException("No row had a valid date.");
        Orders = orders;
        Source = name;
        return orders.Count;
    }

    public static void Reset()
    {
        Orders = Generate();
        Source = "Sample data";
    }

    /// <summary>Two years of plausible orders: growth, a December peak, regional mix.</summary>
    private static List<Order> Generate()
    {
        var rng = new Random(7);
        var orders = new List<Order>();
        var end = DateTime.Today;
        var start = end.AddMonths(-24);
        var weights = Products.Select(p => p.Weight).ToArray();
        var totalWeight = weights.Sum();
        var n = 0;
        for (var day = start; day < end; day = day.AddDays(1))
        {
            var growth = 1 + (day - start).TotalDays / 730 * 0.6;
            var season = day.Month == 12 ? 1.8 : day.Month is 6 or 7 ? 1.25 : 1;
            var count = (int)(2 + rng.NextDouble() * 4 * growth * season);
            for (var k = 0; k < count; k++)
            {
                var pick = rng.Next(totalWeight);
                var p = Products[0];
                foreach (var cand in Products) { if (pick < cand.Weight) { p = cand; break; } pick -= cand.Weight; }
                var region = Regions[Math.Min(Regions.Length - 1, (int)Math.Floor(Math.Pow(rng.NextDouble(), 1.6) * Regions.Length))];
                var channel = Channels[rng.NextDouble() < 0.5 ? 0 : rng.NextDouble() < 0.7 ? 1 : 2];
                var qty = channel == "Wholesale" ? 5 + rng.Next(20) : 1 + rng.Next(3);
                var discount = channel == "Wholesale" ? 0.82 : 1;
                orders.Add(new Order($"SO-{++n:00000}", day.AddMinutes(rng.Next(9 * 60, 21 * 60)), region, channel, p.Name, p.Category, qty, Math.Round(p.Price * discount), p.Cost));
            }
        }
        return orders;
    }
}
