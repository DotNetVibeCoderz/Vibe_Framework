using System.Globalization;

namespace Erp.Data;

/// <summary>
/// Something the workshop buys (raw: timber, rattan, oil, fittings) or makes
/// and sells (finished: chairs, tables, cabinets). Stock is kept per
/// warehouse; the value is kept for the item as a whole, so the cost of what
/// leaves is the running average (moving-average costing).
/// </summary>
public sealed class Item
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"raw" or "finished".</summary>
    public string Kind { get; set; } = "raw";
    public string Unit { get; set; } = "pcs";
    /// <summary>Below this the overview asks for a purchase (raw) or a work order (finished).</summary>
    public decimal Reorder { get; set; }
    /// <summary>List price per unit, before PPN (finished goods).</summary>
    public decimal Price { get; set; }
    /// <summary>Usual supplier for raw materials.</summary>
    public string Supplier { get; set; } = "";
    /// <summary>Quantity per warehouse id.</summary>
    public Dictionary<string, decimal> Stock { get; set; } = new();
    /// <summary>Total value of all units on hand, whole rupiah. Equals the inventory account.</summary>
    public decimal Value { get; set; }

    public decimal OnHand => Stock.Values.Sum();
    public decimal In(string warehouse) => Stock.TryGetValue(warehouse, out var q) ? q : 0m;
    public decimal AvgCost => OnHand > 0 ? Value / OnHand : 0m;
    public bool IsRaw => Kind == "raw";
}

public sealed record Warehouse(string Id, string Name, string Place, string Holds);

public sealed class Partner
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"supplier" or "customer".</summary>
    public string Kind { get; set; } = "customer";
    public string City { get; set; } = "";
    public int TermsDays { get; set; } = 30;
}

/// <summary>A line on a purchase or sales order.</summary>
public sealed class Line
{
    public string Item { get; set; } = "";
    public decimal Qty { get; set; }
    public decimal Price { get; set; }
    public decimal Amount => Fmt.Whole(Qty * Price);
}

public sealed class PurchaseOrder
{
    public string Id { get; set; } = "";
    public string Supplier { get; set; } = "";
    public string Date { get; set; } = "";
    public string Expected { get; set; } = "";
    public List<Line> Lines { get; set; } = new();
    /// <summary>Goods receipt number and date, once the delivery is counted in.</summary>
    public string Receipt { get; set; } = "";
    public string Received { get; set; } = "";
    /// <summary>The supplier's bill falls due; paid when <see cref="Paid"/> is set.</summary>
    public string Due { get; set; } = "";
    public string Paid { get; set; } = "";
    public bool Cancelled { get; set; }

    public decimal Subtotal => Lines.Sum(l => l.Amount);
    public decimal Tax => Fmt.Ppn(Subtotal);
    public decimal Total => Subtotal + Tax;
    public string Status => Cancelled ? "cancelled" : Paid.Length > 0 ? "paid" : Received.Length > 0 ? "received" : "ordered";
}

public sealed class SalesOrder
{
    public string Id { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Date { get; set; } = "";
    /// <summary>Where the goods ship from.</summary>
    public string Warehouse { get; set; } = "GBJ";
    public List<Line> Lines { get; set; } = new();
    public string DeliveryNo { get; set; } = "";
    public string Delivered { get; set; } = "";
    public string InvoiceNo { get; set; } = "";
    public string Invoiced { get; set; } = "";
    public string Due { get; set; } = "";
    public string Paid { get; set; } = "";
    public bool Cancelled { get; set; }

    public decimal Subtotal => Lines.Sum(l => l.Amount);
    public decimal Tax => Fmt.Ppn(Subtotal);
    public decimal Total => Subtotal + Tax;
    public string Status => Cancelled ? "cancelled" : Paid.Length > 0 ? "paid" : Invoiced.Length > 0 ? "invoiced" : Delivered.Length > 0 ? "delivered" : "confirmed";
    public bool Overdue(string today) => Invoiced.Length > 0 && Paid.Length == 0 && string.CompareOrdinal(Due, today) < 0;
}

/// <summary>What one unit of a finished item takes: materials and the craftsmen's wages.</summary>
public sealed record Bom(string Item, (string Item, decimal Qty)[] Parts, decimal Labour, int Days);

public sealed class WorkOrder
{
    public string Id { get; set; } = "";
    public string Item { get; set; } = "";
    public decimal Qty { get; set; }
    public string Planned { get; set; } = "";
    public string Started { get; set; } = "";
    public string Completed { get; set; } = "";
    /// <summary>Cost of materials issued and wages paid, set on completion.</summary>
    public decimal MaterialCost { get; set; }
    public decimal LabourCost { get; set; }

    public string Status => Completed.Length > 0 ? "done" : Started.Length > 0 ? "in progress" : "planned";
}

/// <summary>Every change in stock, with the cost it moved at: the stock card.</summary>
public sealed class StockMove
{
    public string Id { get; set; } = "";
    public string Date { get; set; } = "";
    public string Item { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public decimal Qty { get; set; }
    /// <summary>Total cost of the movement, whole rupiah; negative when stock leaves.</summary>
    public decimal Cost { get; set; }
    public string Kind { get; set; } = "";
    public string Ref { get; set; } = "";
}

public sealed class JournalLine
{
    public string Account { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}

public sealed class JournalEntry
{
    public string Id { get; set; } = "";
    public string Date { get; set; } = "";
    public string Ref { get; set; } = "";
    public string Memo { get; set; } = "";
    public List<JournalLine> Lines { get; set; } = new();
    public decimal Amount => Lines.Sum(l => l.Debit);
}

public sealed record Account(string Code, string Name, string Type)
{
    /// <summary>Assets and expenses grow on the debit side; the rest on the credit side.</summary>
    public bool DebitNormal => Type is "asset" or "expense";
}

/// <summary>Figures the way an Indonesian ledger writes them.</summary>
public static class Fmt
{
    public const decimal PpnRate = 0.11m;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static decimal Whole(decimal v) => Math.Round(v, 0, MidpointRounding.AwayFromZero);

    public static decimal Ppn(decimal subtotal) => Whole(subtotal * PpnRate);

    /// <summary>"12.500.000" (dots between thousands), with a true minus sign.</summary>
    public static string Num(decimal v) =>
        (v < 0 ? "−" : "") + Math.Abs(Whole(v)).ToString("N0", Inv).Replace(',', '.');

    public static string Rp(decimal v) => (v < 0 ? "−Rp " : "Rp ") + Num(Math.Abs(v));

    /// <summary>Short form for cards: "Rp 1,2 M", "Rp 845 jt".</summary>
    public static string Short(decimal v)
    {
        var a = Math.Abs(v);
        var sign = v < 0 ? "−" : "";
        if (a >= 1_000_000_000m) return $"{sign}Rp {(a / 1_000_000_000m).ToString("0.00", Inv).Replace('.', ',')} M";
        if (a >= 1_000_000m) return $"{sign}Rp {(a / 1_000_000m).ToString("0.#", Inv).Replace('.', ',')} jt";
        return sign + Rp(a);
    }

    /// <summary>Quantities: whole numbers plain, fractions with up to three places ("0,045").</summary>
    public static string Qty(decimal v) => v.ToString(v == Math.Truncate(v) ? "N0" : "#,##0.###", Inv).Replace(',', ' ').Replace('.', ',').Replace(' ', '.');

    public static string Date(string iso) =>
        iso.Length < 10 ? "—" : DateTime.ParseExact(iso.Substring(0, 10), "yyyy-MM-dd", Inv).ToString("d MMM yyyy", Inv);

    public static string DateShort(string iso) =>
        iso.Length < 10 ? "—" : DateTime.ParseExact(iso.Substring(0, 10), "yyyy-MM-dd", Inv).ToString("d MMM", Inv);

    public static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", Inv);

    public static string Today => Iso(DateTime.Today);

    public static int DaysBetween(string from, string to) =>
        (int)(DateTime.ParseExact(to, "yyyy-MM-dd", Inv) - DateTime.ParseExact(from, "yyyy-MM-dd", Inv)).TotalDays;
}
