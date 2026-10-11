namespace Erp.Data;

/// <summary>
/// Eleven weeks of the workshop, played through the same actions the screens
/// use: timber bought and received, batches made, furniture sold, delivered,
/// invoiced and paid, rent and salaries paid. So every figure in the books
/// comes from a document, exactly as it would after real use. Dates count back
/// from today; the random numbers are seeded, so a reset gives the same story.
/// </summary>
public static class Seed
{
    private static readonly (string Customer, double Weight)[] Buyers =
    {
        ("C-TBN", 2), ("C-RKI", 3), ("C-VSU", 1.5), ("C-KRT", 1.5), ("C-NDH", 2), ("C-GRY", 2),
    };

    private static readonly Dictionary<string, (int Min, int Max)> OrderSizes = new()
    {
        ["KRS-KTN"] = (4, 12), ["MJM-180"] = (1, 2), ["LMR-2P"] = (1, 2), ["BGK-150"] = (1, 4), ["KRS-RTN"] = (2, 8), ["DPN-UKR"] = (1, 1),
    };

    private static readonly Dictionary<string, int> LeadDays = new()
    {
        ["S-PHT"] = 7, ["S-BLR"] = 5, ["S-RTN"] = 4, ["S-SNR"] = 1, ["S-KIM"] = 3, ["S-TKS"] = 4,
    };

    public static async Task RunAsync()
    {
        var rng = new Random(1407);
        var today = DateTime.Today;
        var start = today.AddDays(-77);
        var payDay = new Dictionary<string, string>();

        ErpStore.Open(Fmt.Iso(start), 650_000_000m, 185_000_000m, new (string, string, decimal, decimal)[]
        {
            ("KYU-JTI", Catalog.RawStore, 4m, 14_200_000m), ("KYU-MHN", Catalog.RawStore, 2.4m, 6_100_000m),
            ("RTN-SGA", Catalog.RawStore, 300m, 37_000m), ("PLY-18", Catalog.RawStore, 40m, 280_000m),
            ("OIL-TEK", Catalog.RawStore, 80m, 92_000m), ("KAN-JOK", Catalog.RawStore, 120m, 60_000m),
            ("BSA-D32", Catalog.RawStore, 40m, 140_000m), ("ENG-KNG", Catalog.RawStore, 60m, 47_000m),
            ("SKR-500", Catalog.RawStore, 25m, 54_000m),
            ("KRS-KTN", Catalog.GoodsStore, 30m, 960_000m), ("MJM-180", Catalog.GoodsStore, 5m, 4_600_000m),
            ("LMR-2P", Catalog.GoodsStore, 4m, 3_600_000m), ("BGK-150", Catalog.GoodsStore, 8m, 2_200_000m),
            ("KRS-RTN", Catalog.GoodsStore, 12m, 1_250_000m), ("DPN-UKR", Catalog.GoodsStore, 2m, 7_300_000m),
            ("KRS-KTN", Catalog.Showroom, 6m, 960_000m), ("BGK-150", Catalog.Showroom, 2m, 2_200_000m), ("KRS-RTN", Catalog.Showroom, 4m, 1_250_000m),
        });

        for (var day = start.AddDays(1); day < today; day = day.AddDays(1))
        {
            var date = Fmt.Iso(day);
            if (day.Day == 1) await ErpStore.RecordExpenseAsync(Books.Rent, 14_500_000m, $"Workshop rent and electricity, {day:MMMM}", date);
            if (day.Day == 25) await ErpStore.RecordExpenseAsync(Books.Salaries, 36_000_000m, $"Office salaries, {day:MMMM}", date);
            if (day.DayOfWeek == DayOfWeek.Sunday) continue;

            foreach (var po in ErpStore.Purchases.Where(p => p.Status == "ordered" && string.CompareOrdinal(p.Expected, date) <= 0).ToList())
                await ErpStore.ReceivePurchaseAsync(po, date);
            foreach (var po in ErpStore.Purchases.Where(p => p.Status == "received" && string.CompareOrdinal(p.Due, Fmt.Iso(day.AddDays(2))) <= 0).ToList())
                await ErpStore.PayPurchaseAsync(po, date);
            foreach (var so in ErpStore.Sales.Where(s => s.Status == "invoiced" && payDay.TryGetValue(s.Id, out var pd) && string.CompareOrdinal(pd, date) <= 0).ToList())
                await ErpStore.ReceivePaymentAsync(so, date);

            foreach (var wo in ErpStore.WorkOrders.Where(w => w.Status == "in progress").ToList())
                if (Fmt.DaysBetween(wo.Started, date) >= Catalog.BomOf(wo.Item).Days) await ErpStore.CompleteWorkOrderAsync(wo, date);

            foreach (var so in ErpStore.Sales.Where(s => s.Status == "confirmed" && Fmt.DaysBetween(s.Date, date) >= 2).ToList())
            {
                if (!await ErpStore.DeliverSaleAsync(so, date)) continue;
                await ErpStore.InvoiceSaleAsync(so, date);
                var terms = ErpStore.Partner(so.Customer).TermsDays;
                // Kafe Ruang Tengah pays late; the others roughly on terms.
                var delay = so.Customer == "C-KRT" ? terms + 22 : terms + rng.Next(-9, 8);
                payDay[so.Id] = Fmt.Iso(day.AddDays(delay));
                if (rng.NextDouble() < 0.5) await ErpStore.RecordExpenseAsync(Books.Freight, 450_000m + rng.Next(0, 8) * 250_000m, $"Freight for {so.DeliveryNo}", date);
            }

            if (day.DayOfWeek != DayOfWeek.Saturday && ErpStore.WorkOrders.Count(w => w.Status == "in progress") < 4) await PlanProduction(date);
            if (day.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Friday) await Restock(date, day);
            if (day.DayOfWeek == DayOfWeek.Wednesday && (int)(day - start).TotalDays % 14 < 7) await StockShowroom(date);

            if (rng.NextDouble() < 0.55) await NewOrder(rng, date);
        }

        // Where today starts: an order for the hotel's new wing that the
        // finished-goods store cannot fill yet, and a teak delivery on its way.
        var now = Fmt.Iso(today);
        await ErpStore.CreateSaleAsync("C-TBN", Catalog.GoodsStore, new()
        {
            new() { Item = "KRS-KTN", Qty = 36, Price = 1_780_000m },
            new() { Item = "MJM-180", Qty = 6, Price = 6_950_000m },
        }, now);
        await ErpStore.CreatePurchaseAsync("S-PHT", new() { new() { Item = "KYU-JTI", Qty = 3m, Price = Catalog.CostOf("KYU-JTI") } }, Fmt.Iso(today.AddDays(-2)), Fmt.Iso(today.AddDays(3)));
    }

    private static async Task NewOrder(Random rng, string date)
    {
        // Random draws are made one by one, in a fixed order, never inside a
        // LINQ selector: when a selector runs differs between runtimes.
        var pick = rng.NextDouble() * Buyers.Sum(b => b.Weight);
        var customer = Buyers[^1].Customer;
        foreach (var (who, weight) in Buyers)
        {
            if ((pick -= weight) > 0) continue;
            customer = who;
            break;
        }
        var count = rng.Next(1, 4);
        var shuffled = new List<(Item Item, int Key)>();
        foreach (var item in ErpStore.Items.Where(i => !i.IsRaw)) shuffled.Add((item, rng.Next()));
        var lines = new List<Line>();
        foreach (var (p, _) in shuffled.OrderBy(x => x.Key).Take(count))
        {
            var (min, max) = OrderSizes[p.Id];
            var qty = rng.Next(min, max + 1);
            // Trade buyers ordering six or more get 5% off the list price.
            lines.Add(new Line { Item = p.Id, Qty = qty, Price = qty >= 6 ? Fmt.Whole(p.Price * 0.95m) : p.Price });
        }
        var warehouse = customer is "C-GRY" or "C-KRT" && lines.All(l => ErpStore.Item(l.Item).In(Catalog.Showroom) >= l.Qty) ? Catalog.Showroom : Catalog.GoodsStore;
        await ErpStore.CreateSaleAsync(customer, warehouse, lines, date);
    }

    /// <summary>Starts a batch of whichever piece is furthest below what it should hold.</summary>
    private static async Task PlanProduction(string date)
    {
        var gap = ErpStore.Items.Where(i => !i.IsRaw)
            .Select(i => (Item: i, Gap: i.Reorder + ErpStore.Committed(i.Id) - i.In(Catalog.GoodsStore) - ErpStore.InProduction(i.Id)))
            .Where(x => x.Gap > 0).OrderByDescending(x => x.Gap / x.Item.Reorder).FirstOrDefault();
        if (gap.Item is null) return;
        var qty = Math.Max(gap.Gap, Math.Ceiling(gap.Item.Reorder / 2));
        var wo = await ErpStore.CreateWorkOrderAsync(gap.Item.Id, qty, date);
        await ErpStore.StartWorkOrderAsync(wo, date);
    }

    /// <summary>Orders raw materials that will not cover open work orders plus their reorder level.</summary>
    private static async Task Restock(string date, DateTime day)
    {
        var lines = new List<(string Supplier, Line Line)>();
        foreach (var item in ErpStore.Items.Where(i => i.IsRaw))
        {
            var needed = ErpStore.WorkOrders.Where(w => w.Status != "done").SelectMany(ErpStore.Needs).Where(n => n.Item == item).Sum(n => n.Need);
            var cover = item.OnHand + ErpStore.OnOrder(item.Id) - needed;
            if (cover >= item.Reorder) continue;
            var qty = item.Reorder * 2 - cover;
            qty = item.Unit == "m³" ? Math.Ceiling(qty * 2) / 2 : Math.Ceiling(qty / 5) * 5;
            lines.Add((item.Supplier, new Line { Item = item.Id, Qty = qty, Price = Catalog.CostOf(item.Id) }));
        }
        foreach (var group in lines.GroupBy(l => l.Supplier))
            await ErpStore.CreatePurchaseAsync(group.Key, group.Select(g => g.Line).ToList(), date, Fmt.Iso(day.AddDays(LeadDays[group.Key])));
    }

    private static async Task StockShowroom(string date)
    {
        foreach (var (id, target) in new[] { ("KRS-KTN", 6m), ("BGK-150", 2m), ("KRS-RTN", 4m), ("MJM-180", 1m) })
        {
            var item = ErpStore.Item(id);
            var qty = Math.Min(target - item.In(Catalog.Showroom), item.In(Catalog.GoodsStore) - item.Reorder / 2);
            if (qty > 0) await ErpStore.TransferAsync(item, Catalog.GoodsStore, Catalog.Showroom, Math.Floor(qty), date);
        }
    }
}
