using Rustzor.Data;

namespace Pos.Data;

/// <summary>
/// The till's records in IndexedDB: the menu, every order and every shift.
/// Everything is loaded once and kept in memory (a café sells hundreds of
/// orders a day, not millions); writes go straight through to the database.
/// </summary>
public static class PosStore
{
    private static Task? _loading;
    private static IndexedDb? _db;

    public static List<Product> Products { get; private set; } = new();
    public static List<Order> Orders { get; private set; } = new();
    public static List<Shift> Shifts { get; private set; } = new();

    /// <summary>Raised after any write, so open screens can refresh.</summary>
    public static event Action? Changed;

    public static Shift? CurrentShift => Shifts.LastOrDefault(s => s.IsOpen);

    public static Task ReadyAsync() => _loading ??= LoadAsync();

    private static async Task LoadAsync()
    {
        _db = await IndexedDb.OpenAsync("kedai-senja-pos", 1,
            new StoreSchema("products"),
            new StoreSchema("orders", "id", Indexes: new IndexSchema("byShift", "shiftId")),
            new StoreSchema("shifts"));
        Products = await _db.Store<Product>("products").AllAsync();
        if (Products.Count == 0)
        {
            await Seed.RunAsync(_db);
            Products = await _db.Store<Product>("products").AllAsync();
        }
        Orders = (await _db.Store<Order>("orders").AllAsync()).OrderBy(o => o.Time).ToList();
        Shifts = (await _db.Store<Shift>("shifts").AllAsync()).OrderBy(s => s.Opened).ToList();
        Products = Products.OrderBy(p => Array.IndexOf(Menu.Categories, p.Category)).ThenBy(p => p.Rank).ToList();
    }

    // ── orders ─────────────────────────────────────────────────────────────

    public static int NextNumber()
    {
        var today = Menu.Today();
        return Orders.Where(o => o.Day == today).Select(o => o.Number).DefaultIfEmpty(0).Max() + 1;
    }

    public static async Task<Order> RecordAsync(Order order)
    {
        order.Number = NextNumber();
        order.Id = $"KS-{order.Time.Substring(0, 10).Replace("-", "")}-{order.Number:000}";
        Orders.Add(order);
        await _db!.Store<Order>("orders").PutAsync(order);
        Changed?.Invoke();
        return order;
    }

    public static async Task VoidAsync(Order order, string reason)
    {
        order.Status = "void";
        order.VoidReason = reason;
        await _db!.Store<Order>("orders").PutAsync(order);
        Changed?.Invoke();
    }

    public static IEnumerable<Order> PaidIn(Shift shift) => Orders.Where(o => o.ShiftId == shift.Id && o.Status == "paid");

    // ── shifts ─────────────────────────────────────────────────────────────

    public static async Task<Shift> OpenShiftAsync(string cashier, long openingCash)
    {
        var shift = new Shift { Id = "shift-" + DateTime.Now.ToString("yyyyMMddHHmmss"), Cashier = cashier, Opened = Menu.Now(), OpeningCash = openingCash };
        Shifts.Add(shift);
        await _db!.Store<Shift>("shifts").PutAsync(shift);
        Changed?.Invoke();
        return shift;
    }

    public static async Task CloseShiftAsync(Shift shift, long counted)
    {
        shift.Closed = Menu.Now();
        shift.CountedCash = counted;
        await _db!.Store<Shift>("shifts").PutAsync(shift);
        Changed?.Invoke();
    }

    /// <summary>What should be in the drawer: the float plus cash taken, less change given.</summary>
    public static long ExpectedCash(Shift shift) =>
        shift.OpeningCash + PaidIn(shift).Where(o => o.Method == "cash").Sum(o => o.Total);

    // ── menu ───────────────────────────────────────────────────────────────

    public static async Task SaveProductAsync(Product p)
    {
        if (p.Id.Length == 0)
        {
            p.Id = "p" + DateTime.Now.Ticks.ToString().Substring(8);
            p.Rank = Products.Count(x => x.Category == p.Category) + 1;
            Products.Add(p);
        }
        await _db!.Store<Product>("products").PutAsync(p);
        Changed?.Invoke();
    }

    /// <summary>Wipes the database and seeds a fresh day.</summary>
    public static async Task ResetAsync()
    {
        _db?.Dispose();
        await IndexedDb.DeleteAsync("kedai-senja-pos");
        _loading = null;
        Cart.Clear();
        await ReadyAsync();
        Changed?.Invoke();
    }
}
