using Rustzor.Data;

namespace Erp.Data;

/// <summary>Running document numbers, kept with the data.</summary>
public sealed class Meta
{
    public string Id { get; set; } = "counters";
    public Dictionary<string, int> Counters { get; set; } = new();
}

/// <summary>
/// The workshop's books in IndexedDB. Everything is loaded once and kept in
/// memory; each business action changes stock, documents and the journal
/// together and then writes what it touched. Every action that moves value
/// posts a balanced journal entry, so inventory, receivables and payables in
/// the ledger always equal the documents behind them.
/// </summary>
public static class ErpStore
{
    private const string DbName = "jati-lestari-erp";
    private static Task? _loading;
    private static IndexedDb? _db;
    private static bool _seeding;
    private static readonly List<object> _dirty = new();

    public static List<Item> Items { get; private set; } = new();
    public static List<Partner> Partners { get; private set; } = new();
    public static List<PurchaseOrder> Purchases { get; private set; } = new();
    public static List<SalesOrder> Sales { get; private set; } = new();
    public static List<WorkOrder> WorkOrders { get; private set; } = new();
    public static List<StockMove> Moves { get; private set; } = new();
    public static List<JournalEntry> Journal { get; private set; } = new();
    private static Meta _meta = new();

    /// <summary>Raised after any write, so open screens can refresh.</summary>
    public static event Action? Changed;

    public static Task ReadyAsync() => _loading ??= LoadAsync();

    private static async Task LoadAsync()
    {
        _db = await IndexedDb.OpenAsync(DbName, 1,
            new StoreSchema("items"), new StoreSchema("partners"), new StoreSchema("purchases"), new StoreSchema("sales"),
            new StoreSchema("workorders"), new StoreSchema("moves"), new StoreSchema("journal"), new StoreSchema("meta"));
        Items = await _db.Store<Item>("items").AllAsync();
        if (Items.Count == 0)
        {
            await SimulateAsync();
            await _db.Store<Item>("items").PutManyAsync(Items);
            await _db.Store<Partner>("partners").PutManyAsync(Partners);
            await _db.Store<PurchaseOrder>("purchases").PutManyAsync(Purchases);
            await _db.Store<SalesOrder>("sales").PutManyAsync(Sales);
            await _db.Store<WorkOrder>("workorders").PutManyAsync(WorkOrders);
            await _db.Store<StockMove>("moves").PutManyAsync(Moves);
            await _db.Store<JournalEntry>("journal").PutManyAsync(Journal);
            await _db.Store<Meta>("meta").PutAsync(_meta);
            return;
        }
        Partners = await _db.Store<Partner>("partners").AllAsync();
        Purchases = (await _db.Store<PurchaseOrder>("purchases").AllAsync()).OrderBy(p => p.Id).ToList();
        Sales = (await _db.Store<SalesOrder>("sales").AllAsync()).OrderBy(s => s.Id).ToList();
        WorkOrders = (await _db.Store<WorkOrder>("workorders").AllAsync()).OrderBy(w => w.Id).ToList();
        Moves = (await _db.Store<StockMove>("moves").AllAsync()).OrderBy(m => m.Id).ToList();
        Journal = (await _db.Store<JournalEntry>("journal").AllAsync()).OrderBy(j => j.Id).ToList();
        _meta = await _db.Store<Meta>("meta").GetAsync("counters") ?? new Meta();
        Items = Items.OrderBy(i => i.Kind == "finished" ? 0 : 1).ThenBy(i => i.Id).ToList();
    }

    /// <summary>Replays the seeded history in memory, writing nothing.</summary>
    internal static async Task SimulateAsync()
    {
        Clear();
        _seeding = true;
        try { await Seed.RunAsync(); }
        finally { _seeding = false; _dirty.Clear(); }
    }

    internal static void Clear()
    {
        Items = Catalog.NewItems().ToList();
        Partners = Catalog.NewPartners();
        Purchases = new(); Sales = new(); WorkOrders = new(); Moves = new(); Journal = new();
        _meta = new Meta();
    }

    /// <summary>Wipes the database and seeds the workshop's last eleven weeks again.</summary>
    public static async Task ResetAsync()
    {
        _db?.Dispose();
        await IndexedDb.DeleteAsync(DbName);
        _loading = null;
        await ReadyAsync();
        Changed?.Invoke();
    }

    // ── lookups ────────────────────────────────────────────────────────────

    public static Item Item(string id) => Items.First(i => i.Id == id);
    public static Item? FindItem(string id) => Items.FirstOrDefault(i => i.Id == id);
    public static Partner Partner(string id) => Partners.First(p => p.Id == id);
    public static string PartnerName(string id) => Partners.FirstOrDefault(p => p.Id == id)?.Name ?? id;
    public static IEnumerable<Partner> Suppliers => Partners.Where(p => p.Kind == "supplier");
    public static IEnumerable<Partner> Customers => Partners.Where(p => p.Kind == "customer");

    /// <summary>The entries posted for a document (its id, or a receipt, delivery or invoice number on it).</summary>
    public static List<JournalEntry> EntriesFor(params string[] refs) =>
        Journal.Where(j => refs.Any(r => r.Length > 0 && j.Ref == r)).ToList();

    /// <summary>Raw material still to arrive on open purchase orders.</summary>
    public static decimal OnOrder(string item) =>
        Purchases.Where(p => p.Status == "ordered").SelectMany(p => p.Lines).Where(l => l.Item == item).Sum(l => l.Qty);

    /// <summary>Finished goods promised on confirmed orders not yet delivered.</summary>
    public static decimal Committed(string item) =>
        Sales.Where(s => s.Status == "confirmed").SelectMany(s => s.Lines).Where(l => l.Item == item).Sum(l => l.Qty);

    public static decimal InProduction(string item) =>
        WorkOrders.Where(w => w.Status != "done" && w.Item == item).Sum(w => w.Qty);

    /// <summary>For each line, how much is missing in the warehouse (empty when all is there).</summary>
    public static List<(Item Item, decimal Need, decimal Have)> Shortages(IEnumerable<Line> lines, string warehouse) =>
        lines.GroupBy(l => l.Item)
            .Select(g => (Item: Item(g.Key), Need: g.Sum(l => l.Qty)))
            .Select(x => (x.Item, x.Need, Have: x.Item.In(warehouse)))
            .Where(x => x.Have < x.Need).ToList();

    /// <summary>The materials a work order takes, with what the raw-material store holds.</summary>
    public static List<(Item Item, decimal Need, decimal Have)> Needs(WorkOrder wo) =>
        Catalog.BomOf(wo.Item).Parts.Select(p => (Item: Item(p.Item), Need: p.Qty * wo.Qty, Have: Item(p.Item).In(Catalog.RawStore))).ToList();

    // ── purchasing ─────────────────────────────────────────────────────────

    public static async Task<PurchaseOrder> CreatePurchaseAsync(string supplier, List<Line> lines, string date, string expected)
    {
        var po = new PurchaseOrder { Id = Next("PO", date), Supplier = supplier, Date = date, Expected = expected, Lines = lines };
        Purchases.Add(po);
        await Flush(po);
        return po;
    }

    /// <summary>Counts the delivery in: raw stock and its value rise, the supplier's bill is owed.</summary>
    public static async Task ReceivePurchaseAsync(PurchaseOrder po, string date)
    {
        if (po.Status != "ordered") return;
        po.Receipt = Next("GR", date);
        po.Received = date;
        po.Due = Fmt.Iso(DateTime.Parse(date).AddDays(Partner(po.Supplier).TermsDays));
        foreach (var l in po.Lines)
        {
            var item = Item(l.Item);
            item.Stock[Catalog.RawStore] = item.In(Catalog.RawStore) + l.Qty;
            item.Value += l.Amount;
            Move(date, item, Catalog.RawStore, l.Qty, l.Amount, "receipt", po.Receipt);
        }
        Post(date, po.Receipt, $"Goods receipt for {po.Id}, {PartnerName(po.Supplier)}",
            (Books.RawStock, po.Subtotal, 0), (Books.VatIn, po.Tax, 0), (Books.Payable, 0, po.Total));
        await Flush(po);
    }

    public static async Task PayPurchaseAsync(PurchaseOrder po, string date)
    {
        if (po.Status != "received") return;
        po.Paid = date;
        Post(date, po.Id, $"Paid {PartnerName(po.Supplier)} for {po.Id}", (Books.Payable, po.Total, 0), (Books.Cash, 0, po.Total));
        await Flush(po);
    }

    public static async Task CancelPurchaseAsync(PurchaseOrder po)
    {
        if (po.Status != "ordered") return;
        po.Cancelled = true;
        await Flush(po);
    }

    // ── sales ──────────────────────────────────────────────────────────────

    public static async Task<SalesOrder> CreateSaleAsync(string customer, string warehouse, List<Line> lines, string date)
    {
        var so = new SalesOrder { Id = Next("SO", date), Customer = customer, Warehouse = warehouse, Date = date, Lines = lines };
        Sales.Add(so);
        await Flush(so);
        return so;
    }

    /// <summary>Ships the goods: stock leaves at average cost, which becomes cost of goods sold.</summary>
    public static async Task<bool> DeliverSaleAsync(SalesOrder so, string date)
    {
        if (so.Status != "confirmed" || Shortages(so.Lines, so.Warehouse).Count > 0) return false;
        so.DeliveryNo = Next("SJ", date);
        so.Delivered = date;
        decimal cost = 0;
        foreach (var l in so.Lines)
        {
            var item = Item(l.Item);
            var c = Issue(item, so.Warehouse, l.Qty);
            cost += c;
            Move(date, item, so.Warehouse, -l.Qty, -c, "delivery", so.DeliveryNo);
        }
        Post(date, so.DeliveryNo, $"Delivery for {so.Id}, {PartnerName(so.Customer)}", (Books.Cogs, cost, 0), (Books.GoodsStock, 0, cost));
        await Flush(so);
        return true;
    }

    /// <summary>Bills the customer: receivable for the total, sales and PPN keluaran.</summary>
    public static async Task InvoiceSaleAsync(SalesOrder so, string date)
    {
        if (so.Status != "delivered") return;
        so.InvoiceNo = Next("INV", date);
        so.Invoiced = date;
        so.Due = Fmt.Iso(DateTime.Parse(date).AddDays(Partner(so.Customer).TermsDays));
        Post(date, so.InvoiceNo, $"Invoice for {so.Id}, {PartnerName(so.Customer)}",
            (Books.Receivable, so.Total, 0), (Books.Sales, 0, so.Subtotal), (Books.VatOut, 0, so.Tax));
        await Flush(so);
    }

    public static async Task ReceivePaymentAsync(SalesOrder so, string date)
    {
        if (so.Status != "invoiced") return;
        so.Paid = date;
        Post(date, so.InvoiceNo, $"Payment from {PartnerName(so.Customer)} for {so.InvoiceNo}", (Books.Cash, so.Total, 0), (Books.Receivable, 0, so.Total));
        await Flush(so);
    }

    public static async Task CancelSaleAsync(SalesOrder so)
    {
        if (so.Status != "confirmed") return;
        so.Cancelled = true;
        await Flush(so);
    }

    // ── production ─────────────────────────────────────────────────────────

    public static async Task<WorkOrder> CreateWorkOrderAsync(string item, decimal qty, string planned)
    {
        var wo = new WorkOrder { Id = Next("WO", planned), Item = item, Qty = qty, Planned = planned };
        WorkOrders.Add(wo);
        await Flush(wo);
        return wo;
    }

    public static async Task StartWorkOrderAsync(WorkOrder wo, string date)
    {
        if (wo.Status != "planned") return;
        wo.Started = date;
        await Flush(wo);
    }

    /// <summary>
    /// Finishes the batch: materials leave the raw store at average cost, the
    /// craftsmen are paid, and the finished goods enter at the sum of both.
    /// </summary>
    public static async Task<bool> CompleteWorkOrderAsync(WorkOrder wo, string date)
    {
        if (wo.Status != "in progress" || Needs(wo).Any(n => n.Have < n.Need)) return false;
        decimal material = 0;
        foreach (var (item, need, _) in Needs(wo))
        {
            var c = Issue(item, Catalog.RawStore, need);
            material += c;
            Move(date, item, Catalog.RawStore, -need, -c, "consumed", wo.Id);
        }
        var labour = Fmt.Whole(Catalog.BomOf(wo.Item).Labour * wo.Qty);
        var product = Item(wo.Item);
        product.Stock[Catalog.GoodsStore] = product.In(Catalog.GoodsStore) + wo.Qty;
        product.Value += material + labour;
        Move(date, product, Catalog.GoodsStore, wo.Qty, material + labour, "produced", wo.Id);
        wo.Completed = date;
        wo.MaterialCost = material;
        wo.LabourCost = labour;
        Post(date, wo.Id, $"{Fmt.Qty(wo.Qty)} × {product.Name} finished",
            (Books.GoodsStock, material + labour, 0), (Books.RawStock, 0, material), (Books.Cash, 0, labour));
        await Flush(wo);
        return true;
    }

    // ── inventory and cash ─────────────────────────────────────────────────

    /// <summary>Moves stock between warehouses. Value stays with the item, so nothing is posted.</summary>
    public static async Task<bool> TransferAsync(Item item, string from, string to, decimal qty, string date)
    {
        if (qty <= 0 || from == to || item.In(from) < qty) return false;
        var id = Next("TR", date);
        var cost = item.OnHand == 0 ? 0 : Fmt.Whole(item.Value * qty / item.OnHand);
        item.Stock[from] = item.In(from) - qty;
        item.Stock[to] = item.In(to) + qty;
        Move(date, item, from, -qty, -cost, "transfer out", id);
        Move(date, item, to, qty, cost, "transfer in", id);
        await Flush(item);
        return true;
    }

    public static async Task<JournalEntry> RecordExpenseAsync(string account, decimal amount, string memo, string date)
    {
        var entry = Post(date, Next("BKK", date), memo, (account, amount, 0), (Books.Cash, 0, amount));
        await Flush();
        return entry;
    }

    /// <summary>The opening balances: owners' capital in cash, machines, and stock counted on day one.</summary>
    internal static void Open(string date, decimal cash, decimal equipment, IEnumerable<(string Item, string Warehouse, decimal Qty, decimal UnitCost)> stock)
    {
        decimal raw = 0, goods = 0;
        foreach (var (id, wh, qty, unit) in stock)
        {
            var item = Item(id);
            var value = Fmt.Whole(qty * unit);
            item.Stock[wh] = item.In(wh) + qty;
            item.Value += value;
            if (item.IsRaw) raw += value; else goods += value;
            Move(date, item, wh, qty, value, "opening", "OPEN");
        }
        Post(date, "OPEN", "Opening balances",
            (Books.Cash, cash, 0), (Books.Equipment, equipment, 0), (Books.RawStock, raw, 0), (Books.GoodsStock, goods, 0),
            (Books.Capital, 0, cash + equipment + raw + goods));
    }

    // ── internals ──────────────────────────────────────────────────────────

    /// <summary>Takes stock out of a warehouse at the item's average cost; returns that cost.</summary>
    private static decimal Issue(Item item, string warehouse, decimal qty)
    {
        var cost = qty >= item.OnHand ? item.Value : Fmt.Whole(item.Value * qty / item.OnHand);
        item.Stock[warehouse] = item.In(warehouse) - qty;
        item.Value -= cost;
        return cost;
    }

    private static void Move(string date, Item item, string warehouse, decimal qty, decimal cost, string kind, string reference)
    {
        var move = new StockMove { Id = $"M{Moves.Count + 1:000000}", Date = date, Item = item.Id, Warehouse = warehouse, Qty = qty, Cost = cost, Kind = kind, Ref = reference };
        Moves.Add(move);
        _dirty.Add(move);
        _dirty.Add(item);
    }

    private static JournalEntry Post(string date, string reference, string memo, params (string Account, decimal Debit, decimal Credit)[] lines)
    {
        var entry = new JournalEntry
        {
            Id = Next("JU", date),
            Date = date,
            Ref = reference,
            Memo = memo,
            Lines = lines.Where(l => l.Debit != 0 || l.Credit != 0).Select(l => new JournalLine { Account = l.Account, Debit = l.Debit, Credit = l.Credit }).ToList(),
        };
        if (entry.Lines.Sum(l => l.Debit) != entry.Lines.Sum(l => l.Credit))
            throw new InvalidOperationException($"Unbalanced entry for {reference}");
        Journal.Add(entry);
        _dirty.Add(entry);
        return entry;
    }

    /// <summary>"PO-2610-007": prefix, year and month of the document date, a running number per month.</summary>
    private static string Next(string prefix, string date)
    {
        var key = prefix + "-" + date.Substring(2, 2) + date.Substring(5, 2);
        var n = (_meta.Counters.TryGetValue(key, out var c) ? c : 0) + 1;
        _meta.Counters[key] = n;
        _dirty.Add(_meta);
        return $"{key}-{n:000}";
    }

    /// <summary>Writes the document and everything the action touched, then tells the screens.</summary>
    private static async Task Flush(object? document = null)
    {
        if (document is not null) _dirty.Add(document);
        if (_seeding || _db is null) return;
        var batch = _dirty.Distinct().ToList();
        _dirty.Clear();
        foreach (var o in batch)
        {
            switch (o)
            {
                case Item x: await _db.Store<Item>("items").PutAsync(x); break;
                case PurchaseOrder x: await _db.Store<PurchaseOrder>("purchases").PutAsync(x); break;
                case SalesOrder x: await _db.Store<SalesOrder>("sales").PutAsync(x); break;
                case WorkOrder x: await _db.Store<WorkOrder>("workorders").PutAsync(x); break;
                case StockMove x: await _db.Store<StockMove>("moves").PutAsync(x); break;
                case JournalEntry x: await _db.Store<JournalEntry>("journal").PutAsync(x); break;
                case Meta x: await _db.Store<Meta>("meta").PutAsync(x); break;
            }
        }
        Changed?.Invoke();
    }
}
