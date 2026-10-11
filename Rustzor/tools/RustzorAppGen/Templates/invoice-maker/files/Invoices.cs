using System.Globalization;
using System.Text;
using Rustzor.Browser.Storage;
using Rustzor.Interop;

namespace {{Namespace}};

public sealed class LineItem
{
    public string Description { get; set; } = "";
    public double Quantity { get; set; } = 1;
    public double UnitPrice { get; set; }
    public double Amount => Math.Round(Quantity * UnitPrice, 2);
}

public sealed class Invoice
{
    public string Number { get; set; } = "";
    public string Issued { get; set; } = "";
    public string Due { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public string FromName { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string ToName { get; set; } = "";
    public string ToAddress { get; set; } = "";
    public List<LineItem> Lines { get; set; } = new();
    public double TaxRate { get; set; } = 11;
    public double Discount { get; set; }
    public string Notes { get; set; } = "";
    public string Status { get; set; } = "draft";

    public double Subtotal => Math.Round(Lines.Sum(l => l.Amount), 2);
    public double Taxable => Math.Max(0, Subtotal - Discount);
    public double Tax => Math.Round(Taxable * TaxRate / 100, 2);
    public double Total => Taxable + Tax;
}

public sealed record CurrencyInfo(string Code, string Symbol, int Decimals);

/// <summary>Invoices kept in localStorage, newest first.</summary>
public static class InvoiceBook
{
    private const string Key = "invoices";
    private const string ProfileKey = "invoice-profile";

    public static readonly CurrencyInfo[] Currencies =
    {
        new("USD", "$", 2), new("EUR", "€", 2), new("GBP", "£", 2), new("IDR", "Rp ", 0), new("SGD", "S$", 2), new("JPY", "¥", 0),
    };

    public static List<Invoice> All() => WebStorage.Local.GetList<Invoice>(Key);

    public static void Save(Invoice invoice)
    {
        var all = All();
        all.RemoveAll(i => i.Number == invoice.Number);
        all.Insert(0, invoice);
        WebStorage.Local.SetJson(Key, all);
        // Remember who is sending, so the next invoice starts filled in.
        WebStorage.Local.SetJson(ProfileKey, new Invoice { FromName = invoice.FromName, FromAddress = invoice.FromAddress, Currency = invoice.Currency, TaxRate = invoice.TaxRate });
    }

    public static void Delete(string number)
    {
        var all = All();
        all.RemoveAll(i => i.Number == number);
        WebStorage.Local.SetJson(Key, all);
    }

    public static Invoice New()
    {
        var profile = WebStorage.Local.GetObject<Invoice>(ProfileKey);
        var today = DateTime.Today;
        var invoice = new Invoice
        {
            Number = NextNumber(),
            Issued = today.ToString("yyyy-MM-dd"),
            Due = today.AddDays(14).ToString("yyyy-MM-dd"),
            FromName = profile?.FromName is { Length: > 0 } n ? n : "Rustworks Studio",
            FromAddress = profile?.FromAddress is { Length: > 0 } a ? a : "Jl. Braga 12\nBandung 40111",
            Currency = profile?.Currency ?? "USD",
            TaxRate = profile?.TaxRate ?? 11,
        };
        if (All().Count == 0)
        {
            invoice.ToName = "Northwind Traders";
            invoice.ToAddress = "88 Harbour Road\nSingapore 039594";
            invoice.Lines.Add(new LineItem { Description = "Discovery workshop (half day)", Quantity = 1, UnitPrice = 850 });
            invoice.Lines.Add(new LineItem { Description = "Dashboard design, per screen", Quantity = 6, UnitPrice = 240 });
            invoice.Lines.Add(new LineItem { Description = "Front-end build, hours", Quantity = 32, UnitPrice = 65 });
            invoice.Notes = "Payment by bank transfer within 14 days. Thank you for your business.";
        }
        else
        {
            invoice.Lines.Add(new LineItem());
        }
        return invoice;
    }

    public static Invoice Clone(Invoice source) => Json.Deserialize<Invoice>(Json.Serialize(source));

    private static string NextNumber()
    {
        var year = DateTime.Today.Year;
        var highest = All()
            .Select(i => i.Number.Split('-'))
            .Where(p => p.Length == 3 && p[1] == year.ToString())
            .Select(p => int.TryParse(p[2], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"INV-{year}-{highest + 1:000}";
    }

    public static string Money(double value, string currency)
    {
        var info = Currencies.FirstOrDefault(c => c.Code == currency) ?? Currencies[0];
        var text = value.ToString(info.Decimals == 0 ? "#,##0" : "#,##0.00", CultureInfo.InvariantCulture);
        return value < 0 ? "-" + info.Symbol + text.TrimStart('-') : info.Symbol + text;
    }

    public static string Csv(Invoice invoice)
    {
        static string Cell(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine("Description,Quantity,Unit price,Amount");
        foreach (var l in invoice.Lines)
            sb.AppendLine(string.Join(",", Cell(l.Description), l.Quantity.ToString(CultureInfo.InvariantCulture),
                l.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture), l.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
        sb.AppendLine($",,Subtotal,{invoice.Subtotal.ToString("0.00", CultureInfo.InvariantCulture)}");
        sb.AppendLine($",,Discount,{invoice.Discount.ToString("0.00", CultureInfo.InvariantCulture)}");
        sb.AppendLine($",,Tax {invoice.TaxRate.ToString(CultureInfo.InvariantCulture)}%,{invoice.Tax.ToString("0.00", CultureInfo.InvariantCulture)}");
        sb.AppendLine($",,Total {invoice.Currency},{invoice.Total.ToString("0.00", CultureInfo.InvariantCulture)}");
        return sb.ToString();
    }
}
