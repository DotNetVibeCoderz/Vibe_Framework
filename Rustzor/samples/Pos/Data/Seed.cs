using Rustzor.Data;

namespace Pos.Data;

/// <summary>
/// A believable morning-to-now at Kedai Senja: the menu, a shift opened at
/// 07:00, and the orders rung since. Seeded by date, so a reset on the same
/// day gives the same history.
/// </summary>
public static class Seed
{
    private static readonly (string Name, string Category, long Price)[] Items =
    {
        ("Kopi Susu Gula Aren", "Kopi", 28000), ("Es Kopi Pandan", "Kopi", 30000), ("Americano", "Kopi", 24000),
        ("Caffè Latte", "Kopi", 32000), ("Cappuccino", "Kopi", 32000), ("Kopi Tubruk", "Kopi", 18000),
        ("V60 Gayo", "Kopi", 35000), ("Affogato", "Kopi", 36000),
        ("Teh Tarik", "Non-kopi", 22000), ("Matcha Latte", "Non-kopi", 34000), ("Coklat Senja", "Non-kopi", 30000),
        ("Es Jeruk Kalamansi", "Non-kopi", 20000), ("Teh Melati", "Non-kopi", 15000), ("Wedang Jahe", "Non-kopi", 18000),
        ("Nasi Goreng Kampung", "Makanan", 38000), ("Mie Goreng Jawa", "Makanan", 35000), ("Ayam Sambal Matah", "Makanan", 42000),
        ("Roti Bakar Srikaya", "Makanan", 24000), ("Pisang Goreng Keju", "Makanan", 22000), ("Kentang Goreng", "Makanan", 25000),
        ("Croissant Mentega", "Kue", 26000), ("Klepon Cake", "Kue", 32000), ("Lapis Legit", "Kue", 28000),
        ("Banana Bread", "Kue", 24000), ("Bolu Pandan", "Kue", 22000),
    };

    public static async Task RunAsync(IndexedDb db)
    {
        var products = Items.Select((x, i) => new Product
        {
            Id = $"p{i + 1:00}",
            Name = x.Name,
            Category = x.Category,
            Price = x.Price,
            Rank = Items.Take(i).Count(y => y.Category == x.Category) + 1,
            SoldOut = x.Name == "Bolu Pandan",
        }).ToList();
        await db.Store<Product>("products").PutManyAsync(products);

        // Yesterday: a full day, counted and closed Rp 2.000 short.
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        var before = new Shift { Id = "shift-" + yesterday.ToString("yyyyMMdd") + "0700", Cashier = "Bagas", Opened = yesterday.AddHours(7).ToString("yyyy-MM-ddTHH:mm:ss"), Closed = yesterday.AddHours(21).ToString("yyyy-MM-ddTHH:mm:ss"), OpeningCash = 500_000 };
        var past = Day(Selling(products), before, yesterday, yesterday.AddHours(21));
        before.CountedCash = before.OpeningCash + past.Where(o => o.Status == "paid" && o.Method == "cash").Sum(o => o.Total) - 2_000;

        // Today: open since 07:00, with the orders rung until a few minutes ago.
        var shift = new Shift { Id = "shift-" + today.ToString("yyyyMMdd") + "0700", Cashier = "Ayu", Opened = today.AddHours(7).ToString("yyyy-MM-ddTHH:mm:ss"), OpeningCash = 500_000 };
        var orders = Day(Selling(products), shift, today, DateTime.Now.AddMinutes(-3));

        await db.Store<Shift>("shifts").PutManyAsync(new[] { before, shift });
        await db.Store<Order>("orders").PutManyAsync(past.Concat(orders));
    }

    private static List<Product> Selling(List<Product> products) => products.Where(p => !p.SoldOut).ToList();

    private static List<Order> Day(List<Product> selling, Shift shift, DateTime day, DateTime end)
    {
        var rng = new Random(day.Year * 1000 + day.DayOfYear);
        var orders = new List<Order>();
        var at = day.AddHours(7).AddMinutes(4);
        while (at < end && orders.Count < 120)
        {
            // Busier at breakfast and after lunch; quiet mid-morning.
            var busy = at.Hour is 7 or 8 or 12 or 13 or 16 ? 6 : 13;
            var lines = new List<CartLine>();
            var count = rng.Next(1, 5);
            for (var i = 0; i < count; i++)
            {
                var cat = Menu.Categories[rng.Next(10) switch { < 5 => 0, < 7 => 1, < 9 => 2, _ => 3 }];
                var pool = selling.Where(x => x.Category == cat).ToList();
                // Best sellers sell more: squaring the draw leans it towards the top of the list.
                var p = pool[(int)(Math.Pow(rng.NextDouble(), 2) * pool.Count)];
                var groups = Menu.OptionsFor(cat);
                var picks = groups.Select(g => g.Choices[rng.Next(10) < 7 ? 0 : rng.Next(g.Choices.Length)]).ToList();
                var options = Menu.Describe(groups, picks);
                var line = lines.FirstOrDefault(l => l.ProductId == p.Id && l.Options == options);
                if (line is not null) line.Qty++;
                else lines.Add(new CartLine { ProductId = p.Id, Name = p.Name, Category = p.Category, Options = options, Unit = p.Price + picks.Sum(c => c.Extra) });
            }
            Cart.Lines.Clear();
            Cart.Lines.AddRange(lines);
            Cart.Service = rng.Next(3) == 0 ? "takeaway" : "dine-in";
            Cart.Table = Cart.Service == "dine-in" ? rng.Next(1, 15).ToString() : "";
            Cart.ApplyPromo(at.Hour < 10 && rng.Next(4) == 0 ? "PAGI20" : rng.Next(12) == 0 ? "SENJA10" : "");
            var method = rng.Next(10) switch { < 5 => "qris", < 8 => "cash", _ => "card" };
            var total = Cart.Total;
            var tendered = method == "cash" ? new[] { 50_000L, 100_000L, 200_000L }.First(n => n >= total || n == 200_000) : total;
            if (tendered < total) tendered = (total / 50_000 + 1) * 50_000;
            var order = Cart.ToOrder(method, tendered, method == "card" ? rng.Next(100000, 999999).ToString() : "", shift);
            order.Time = at.ToString("yyyy-MM-ddTHH:mm:ss");
            order.Number = orders.Count + 1;
            order.Id = $"KS-{day:yyyyMMdd}-{order.Number:000}";
            orders.Add(order);
            at = at.AddMinutes(rng.Next(2, busy * 2));
        }
        if (orders.Count > 6)
        {
            orders[orders.Count / 3].Status = "void";
            orders[orders.Count / 3].VoidReason = "Customer left before the order was made";
        }
        Cart.Clear();
        return orders;
    }
}
