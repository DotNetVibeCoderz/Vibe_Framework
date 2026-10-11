namespace Erp.Data;

/// <summary>
/// The chart of accounts and the reports read from the journal. Nothing here
/// keeps its own totals: every figure is summed from posted entries, so the
/// reports cannot disagree with the journal.
/// </summary>
public static class Books
{
    public const string Cash = "1100", Receivable = "1200", RawStock = "1300", GoodsStock = "1310", VatIn = "1400", Equipment = "1500";
    public const string Payable = "2100", VatOut = "2200";
    public const string Capital = "3100", Retained = "3200";
    public const string Sales = "4100";
    public const string Cogs = "5100";
    public const string Rent = "6100", Salaries = "6200", Freight = "6300", Other = "6900";

    public static readonly Account[] Accounts =
    {
        new(Cash, "Kas & Bank", "asset"),
        new(Receivable, "Piutang Usaha", "asset"),
        new(RawStock, "Persediaan Bahan Baku", "asset"),
        new(GoodsStock, "Persediaan Barang Jadi", "asset"),
        new(VatIn, "PPN Masukan", "asset"),
        new(Equipment, "Mesin & Peralatan Workshop", "asset"),
        new(Payable, "Utang Usaha", "liability"),
        new(VatOut, "PPN Keluaran", "liability"),
        new(Capital, "Modal Disetor", "equity"),
        new(Retained, "Laba Ditahan", "equity"),
        new(Sales, "Penjualan", "revenue"),
        new(Cogs, "Harga Pokok Penjualan", "expense"),
        new(Rent, "Sewa & Listrik Workshop", "expense"),
        new(Salaries, "Gaji Staf Kantor", "expense"),
        new(Freight, "Ongkos Kirim", "expense"),
        new(Other, "Beban Lain-lain", "expense"),
    };

    /// <summary>Accounts a cash expense can go to, for the "Record expense" form.</summary>
    public static readonly string[] ExpenseAccounts = { Rent, Salaries, Freight, Other };

    public static Account Of(string code) => Accounts.First(a => a.Code == code);

    public static string Name(string code) => Of(code).Name;

    public static readonly string[] TypeOrder = { "asset", "liability", "equity", "revenue", "expense" };

    public static string TypeName(string type) => type switch
    {
        "asset" => "Assets",
        "liability" => "Liabilities",
        "equity" => "Equity",
        "revenue" => "Revenue",
        _ => "Expenses",
    };

    public sealed record Row(Account Account, decimal Debit, decimal Credit)
    {
        /// <summary>The balance on the account's normal side.</summary>
        public decimal Balance => Account.DebitNormal ? Debit - Credit : Credit - Debit;
    }

    /// <summary>Debits and credits per account over entries dated in [from, to] (inclusive; empty means open).</summary>
    public static List<Row> Totals(IEnumerable<JournalEntry> journal, string from = "", string to = "")
    {
        var debit = new Dictionary<string, decimal>();
        var credit = new Dictionary<string, decimal>();
        foreach (var e in journal)
        {
            if (from.Length > 0 && string.CompareOrdinal(e.Date, from) < 0) continue;
            if (to.Length > 0 && string.CompareOrdinal(e.Date, to) > 0) continue;
            foreach (var l in e.Lines)
            {
                debit[l.Account] = (debit.TryGetValue(l.Account, out var d) ? d : 0m) + l.Debit;
                credit[l.Account] = (credit.TryGetValue(l.Account, out var c) ? c : 0m) + l.Credit;
            }
        }
        return Accounts.Select(a => new Row(a, debit.TryGetValue(a.Code, out var d) ? d : 0m, credit.TryGetValue(a.Code, out var c) ? c : 0m)).ToList();
    }

    public static decimal Balance(IEnumerable<JournalEntry> journal, string code, string to = "") =>
        Totals(journal, "", to).First(r => r.Account.Code == code).Balance;

    /// <summary>Revenue less expenses over a period: the profit and loss.</summary>
    public sealed record Pnl(decimal Revenue, decimal Cogs, List<Row> Expenses)
    {
        public decimal Gross => Revenue - Cogs;
        public decimal Operating => Expenses.Sum(e => e.Balance);
        public decimal Net => Gross - Operating;
        public double Margin => Revenue == 0 ? 0 : (double)(Gross / Revenue);
    }

    public static Pnl ProfitAndLoss(IEnumerable<JournalEntry> journal, string from, string to)
    {
        var rows = Totals(journal, from, to);
        return new Pnl(
            rows.Where(r => r.Account.Type == "revenue").Sum(r => r.Balance),
            rows.First(r => r.Account.Code == Cogs).Balance,
            rows.Where(r => r.Account.Type == "expense" && r.Account.Code != Cogs).ToList());
    }

    /// <summary>
    /// Assets against liabilities and equity at a date. The year is not closed,
    /// so profit to date is shown as its own equity line, as accountants do
    /// before the closing entry.
    /// </summary>
    public sealed record Sheet(List<Row> Assets, List<Row> Liabilities, List<Row> Equity, decimal Earnings)
    {
        public decimal TotalAssets => Assets.Sum(r => r.Balance);
        public decimal TotalLiabilities => Liabilities.Sum(r => r.Balance);
        public decimal TotalEquity => Equity.Sum(r => r.Balance) + Earnings;
        public bool Balances => TotalAssets == TotalLiabilities + TotalEquity;
    }

    public static Sheet BalanceSheet(IEnumerable<JournalEntry> journal, string to)
    {
        var rows = Totals(journal, "", to);
        var earnings = rows.Where(r => r.Account.Type == "revenue").Sum(r => r.Balance) - rows.Where(r => r.Account.Type == "expense").Sum(r => r.Balance);
        return new Sheet(
            rows.Where(r => r.Account.Type == "asset").ToList(),
            rows.Where(r => r.Account.Type == "liability").ToList(),
            rows.Where(r => r.Account.Type == "equity").ToList(),
            earnings);
    }
}
