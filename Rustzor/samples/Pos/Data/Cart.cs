namespace Pos.Data;

/// <summary>
/// The open ticket. It is static so a half-rung order survives a trip to
/// another screen, the way a paper ticket stays on the counter.
/// </summary>
public static class Cart
{
    public static List<CartLine> Lines { get; } = new();
    public static string Service { get; set; } = "dine-in";
    public static string Table { get; set; } = "";
    public static string Promo { get; private set; } = "";

    public static event Action? Changed;

    public static void Add(Product p, string options, long extra)
    {
        var same = Lines.FirstOrDefault(l => l.ProductId == p.Id && l.Options == options);
        if (same is not null) same.Qty++;
        else Lines.Add(new CartLine { ProductId = p.Id, Name = p.Name, Category = p.Category, Options = options, Unit = p.Price + extra });
        Changed?.Invoke();
    }

    public static void Step(CartLine line, int by)
    {
        line.Qty += by;
        if (line.Qty <= 0) Lines.Remove(line);
        Changed?.Invoke();
    }

    /// <summary>Applies a promo code; returns false when the code is unknown.</summary>
    public static bool ApplyPromo(string code)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length > 0 && !Menu.Promos.ContainsKey(code)) return false;
        Promo = code;
        Changed?.Invoke();
        return true;
    }

    public static void Clear()
    {
        Lines.Clear();
        Promo = "";
        Table = "";
        Service = "dine-in";
        Changed?.Invoke();
    }

    public static int Items => Lines.Sum(l => l.Qty);
    public static long Subtotal => Lines.Sum(l => l.Total);
    public static long Discount => Promo.Length == 0 ? 0 : (long)Math.Round(Subtotal * Menu.Promos[Promo].Rate, MidpointRounding.AwayFromZero);
    public static long Tax => (long)Math.Round((Subtotal - Discount) * Menu.TaxRate, MidpointRounding.AwayFromZero);

    /// <summary>Totals are rounded down to the nearest Rp 100, as most tills do.</summary>
    public static long Rounding => -((Subtotal - Discount + Tax) % 100);
    public static long Total => Subtotal - Discount + Tax + Rounding;

    public static Order ToOrder(string method, long tendered, string reference, Shift shift) => new()
    {
        Time = Menu.Now(),
        ShiftId = shift.Id,
        Cashier = shift.Cashier,
        Service = Service,
        Table = Service == "dine-in" ? Table : "",
        Lines = Lines.Select(l => new OrderLine { ProductId = l.ProductId, Name = l.Name, Category = l.Category, Options = l.Options, Unit = l.Unit, Qty = l.Qty }).ToList(),
        Subtotal = Subtotal,
        Promo = Promo,
        Discount = Discount,
        Tax = Tax,
        Rounding = Rounding,
        Total = Total,
        Method = method,
        Tendered = method == "cash" ? tendered : Total,
        Change = method == "cash" ? tendered - Total : 0,
        Reference = reference,
    };
}
