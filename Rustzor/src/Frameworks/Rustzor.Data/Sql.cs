using System.Globalization;
using System.Text;
using Rustzor.Interop;

namespace Rustzor.Data;

/// <summary>Rows from a query: column names and values in column order.</summary>
public sealed class QueryResult
{
    internal QueryResult(JsonValue v)
    {
        foreach (var c in v["columns"].Items) Columns.Add(c.AsString());
        foreach (var row in v["rows"].Items) Rows.Add(row.Items.ToArray());
    }

    public List<string> Columns { get; } = new();
    public List<JsonValue[]> Rows { get; } = new();
    public int Count => Rows.Count;

    public JsonValue this[int row, string column] => Rows[row][Columns.IndexOf(column)];

    /// <summary>The first column of the first row, for COUNT(*) and friends.</summary>
    public JsonValue Scalar => Rows.Count > 0 && Rows[0].Length > 0 ? Rows[0][0] : JsonValue.Null;

    /// <summary>Each row as an object keyed by column name.</summary>
    public JsonValue ToObjects()
    {
        var w = new JsonWriter().BeginArray();
        foreach (var row in Rows)
        {
            w.BeginObject();
            for (var i = 0; i < Columns.Count; i++) w.Name(Columns[i]).Value(row[i]);
            w.EndObject();
        }
        return JsonValue.Parse(w.EndArray().ToString());
    }

    /// <summary>Rows mapped onto <typeparamref name="T"/> by column name (case-insensitive).</summary>
    public List<T> As<T>() => Json.DeserializeList<T>(ToObjects());

    /// <summary>The result as CSV text.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns.Select(Csv)));
        foreach (var row in Rows) sb.AppendLine(string.Join(",", row.Select(v => Csv(v.IsNull ? "" : v.AsString()))));
        return sb.ToString();
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>
/// SQLite in the browser (sql.js). Open with a <c>persistKey</c> to keep the
/// database across reloads: <see cref="SaveAsync"/> writes it to IndexedDB.
/// <code>
/// var db = await SqliteDb.OpenAsync("pos");
/// await db.RunAsync("create table if not exists items(id integer primary key, name text, price real)");
/// await db.RunAsync("insert into items(name, price) values (?, ?)", "Coffee", 3.5);
/// var items = (await db.QueryAsync("select * from items where price &lt; ?", 10)).As&lt;Item&gt;();
/// await db.SaveAsync();
/// </code>
/// </summary>
public sealed class SqliteDb : IDisposable
{
    private readonly int _handle;

    private SqliteDb(int handle) => _handle = handle;

    public static async Task<SqliteDb> OpenAsync(string? persistKey = null, byte[]? file = null) =>
        new((await Js.CallAsync("sql.open", Js.Args("persistKey", persistKey, "data", file is null ? null : Convert.ToBase64String(file)))).AsInt());

    public async Task<QueryResult> QueryAsync(string sql, params object?[] parameters) =>
        new(await Js.CallAsync("sql.query", Js.Args("h", _handle, "sql", sql, "params", parameters)));

    /// <summary>Runs a statement; returns rows changed and the last inserted row id.</summary>
    public async Task<(int Changes, long LastId)> RunAsync(string sql, params object?[] parameters)
    {
        var r = await Js.CallAsync("sql.run", Js.Args("h", _handle, "sql", sql, "params", parameters));
        return (r["changes"].AsInt(), r["lastId"].AsLong());
    }

    /// <summary>Runs several statements in one transaction.</summary>
    public async Task TransactionAsync(Func<SqliteDb, Task> work)
    {
        await RunAsync("begin");
        try
        {
            await work(this);
            await RunAsync("commit");
        }
        catch
        {
            await RunAsync("rollback");
            throw;
        }
    }

    public async Task<byte[]> ExportAsync() => Convert.FromBase64String((await Js.CallAsync("sql.export", Js.Args("h", _handle))).AsString());

    public Task SaveAsync() => Js.CallAsync("sql.save", Js.Args("h", _handle));

    public void Dispose() => Js.Send("sql.close", Js.Args("h", _handle));
}

/// <summary>
/// DuckDB in the browser for analytics: SQL over CSV, Parquet and JSON files
/// fetched from URLs, or over rows you insert.
/// <code>
/// var duck = await DuckDb.OpenAsync();
/// await DuckDb.RegisterUrlAsync("sales.parquet", "https://example.com/sales.parquet");
/// var top = await duck.QueryAsync("select region, sum(amount) total from 'sales.parquet' group by 1 order by 2 desc");
/// </code>
/// </summary>
public sealed class DuckDb : IDisposable
{
    private readonly int _handle;

    private DuckDb(int handle) => _handle = handle;

    /// <summary>Starts DuckDB (about 35 MB of WebAssembly, fetched and cached on first use).</summary>
    public static async Task<DuckDb> OpenAsync() => new((await Js.CallAsync("duck.open")).AsInt());

    public async Task<QueryResult> QueryAsync(string sql) => new(await Js.CallAsync("duck.query", Js.Args("h", _handle, "sql", sql)));

    /// <summary>Makes a remote file queryable by name: <c>select * from 'name'</c>.</summary>
    public static Task RegisterUrlAsync(string name, string url) => Js.CallAsync("duck.registerUrl", Js.Args("name", name, "url", url));

    /// <summary>Makes text (CSV or JSON) queryable by name.</summary>
    public static Task RegisterTextAsync(string name, string text) => Js.CallAsync("duck.registerText", Js.Args("name", name, "text", text));

    /// <summary>Creates or appends to a table from objects.</summary>
    public async Task<int> InsertAsync<T>(string table, IEnumerable<T> rows) =>
        (await Js.CallAsync("duck.insertJson", Js.Args("h", _handle, "table", table, "rows", new JsonRaw(Json.Serialize(rows.ToList()))))).AsInt();

    public void Dispose() => Js.Send("duck.close", Js.Args("h", _handle));
}

/// <summary>Reads CSV text into rows, honouring quotes.</summary>
public static class Csv
{
    public static List<string[]> Parse(string text, char separator = ',')
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == separator) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }

    public static double Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
