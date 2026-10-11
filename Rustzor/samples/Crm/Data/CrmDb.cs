using Rustzor.Data;

namespace Crm.Data;

/// <summary>
/// The CRM's database: SQLite in the browser (sql.js), saved to IndexedDB
/// under one key, so the data survives reloads. Every page talks to it through
/// these methods, which keeps SQL in one place.
/// </summary>
public static class CrmDb
{
    private static Task<SqliteDb>? _db;

    public static Task<SqliteDb> Db => _db ??= OpenAsync();

    public static event Action? Changed;

    private static async Task<SqliteDb> OpenAsync()
    {
        var db = await SqliteDb.OpenAsync(persistKey: "rustzor-crm-sample");
        await db.RunAsync("create table if not exists companies (id integer primary key, name text not null, industry text, city text, owner text, website text, tier text)");
        await db.RunAsync("create table if not exists contacts (id integer primary key, companyId integer, name text not null, title text, email text, phone text)");
        await db.RunAsync("create table if not exists deals (id integer primary key, companyId integer, title text not null, value real, stage text, owner text, source text, created text, closeDate text)");
        await db.RunAsync("create table if not exists activities (id integer primary key, companyId integer, dealId integer, kind text, subject text, due text, done integer default 0, owner text)");
        if ((await db.QueryAsync("select count(*) from companies")).Scalar.AsInt() == 0) await Seed.RunAsync(db);
        return db;
    }

    private static async Task<List<T>> Query<T>(string sql, params object?[] args)
    {
        var db = await Db;
        return (await db.QueryAsync(sql, args)).As<T>();
    }

    private static async Task<long> Run(string sql, params object?[] args)
    {
        var db = await Db;
        var (_, id) = await db.RunAsync(sql, args);
        await db.SaveAsync();
        Changed?.Invoke();
        return id;
    }

    // ── companies ──────────────────────────────────────────────────────────

    public static Task<List<Company>> CompaniesAsync() => Query<Company>("""
        select c.*, coalesce(sum(case when d.stage not in ('won','lost') then d.value end), 0) openValue, count(d.id) deals
        from companies c left join deals d on d.companyId = c.id
        group by c.id order by c.name
        """);

    public static async Task<Company?> CompanyAsync(long id) =>
        (await Query<Company>("select c.*, 0 openValue, 0 deals from companies c where c.id = ?", id)).FirstOrDefault();

    public static Task<long> AddCompanyAsync(Company c) =>
        Run("insert into companies (name, industry, city, owner, website, tier) values (?, ?, ?, ?, ?, ?)", c.Name, c.Industry, c.City, c.Owner, c.Website, c.Tier);

    // ── contacts ───────────────────────────────────────────────────────────

    public static Task<List<Contact>> ContactsAsync(long? companyId = null) => companyId is { } id
        ? Query<Contact>("select p.*, c.name company from contacts p join companies c on c.id = p.companyId where p.companyId = ? order by p.name", id)
        : Query<Contact>("select p.*, c.name company from contacts p join companies c on c.id = p.companyId order by p.name");

    public static Task<long> AddContactAsync(Contact p) =>
        Run("insert into contacts (companyId, name, title, email, phone) values (?, ?, ?, ?, ?)", p.CompanyId, p.Name, p.Title, p.Email, p.Phone);

    // ── deals ──────────────────────────────────────────────────────────────

    public static Task<List<Deal>> DealsAsync(long? companyId = null) => companyId is { } id
        ? Query<Deal>("select d.*, c.name company from deals d join companies c on c.id = d.companyId where d.companyId = ? order by d.closeDate", id)
        : Query<Deal>("select d.*, c.name company from deals d join companies c on c.id = d.companyId order by d.closeDate");

    public static Task<long> AddDealAsync(Deal d) =>
        Run("insert into deals (companyId, title, value, stage, owner, source, created, closeDate) values (?, ?, ?, ?, ?, ?, ?, ?)",
            d.CompanyId, d.Title, (double)d.Value, d.Stage, d.Owner, d.Source, DateTime.Today.ToString("yyyy-MM-dd"), d.CloseDate);

    public static Task MoveDealAsync(long id, string stage) =>
        Run("update deals set stage = ? where id = ?", stage, id);

    // ── activities ─────────────────────────────────────────────────────────

    public static Task<List<Activity>> ActivitiesAsync(long? companyId = null) => companyId is { } id
        ? Query<Activity>("select a.*, c.name company from activities a join companies c on c.id = a.companyId where a.companyId = ? order by a.done, a.due", id)
        : Query<Activity>("select a.*, c.name company from activities a join companies c on c.id = a.companyId order by a.done, a.due");

    public static Task<long> AddActivityAsync(Activity a) =>
        Run("insert into activities (companyId, dealId, kind, subject, due, done, owner) values (?, ?, ?, ?, ?, 0, ?)", a.CompanyId, a.DealId, a.Kind, a.Subject, a.Due, a.Owner);

    public static Task CompleteActivityAsync(long id, bool done) =>
        Run("update activities set done = ? where id = ?", done ? 1 : 0, id);

    // ── reporting ──────────────────────────────────────────────────────────

    public static async Task<QueryResult> SqlAsync(string sql)
    {
        var db = await Db;
        return await db.QueryAsync(sql);
    }

    public static async Task ResetAsync()
    {
        var db = await Db;
        foreach (var table in new[] { "activities", "deals", "contacts", "companies" }) await db.RunAsync($"delete from {table}");
        await Seed.RunAsync(db);
        Changed?.Invoke();
    }
}
