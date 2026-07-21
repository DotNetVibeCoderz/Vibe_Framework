using RustNet.Core;
using RustNet.Serialization;

namespace RustNet.Data;

/// <summary>Raw database host calls; prefer <see cref="Database"/>.</summary>
public static class Db
{
    /// <summary>Open a database. "" or ":memory:" = in-memory; otherwise a
    /// filesystem path decides the storage: "/data/x.db" (flash),
    /// "/sd/x.db" (SD card), "/usb/x.db" (USB drive).</summary>
    [InternalCall]
    public static int Open(string path) => throw new RuntimeOnlyException();

    /// <summary>Run a statement, returning affected row count.</summary>
    [InternalCall]
    public static int Exec(int handle, string sql) => throw new RuntimeOnlyException();

    /// <summary>Run a query, returning {"columns":[...],"rows":[[...]]} JSON.</summary>
    [InternalCall]
    public static string Query(int handle, string sql) => throw new RuntimeOnlyException();

    [InternalCall]
    public static void Close(int handle) => throw new RuntimeOnlyException();
}

/// <summary>
/// SQL database on the device (SQLite-style dialect: CREATE/DROP TABLE,
/// INSERT, SELECT with WHERE/ORDER BY/LIMIT and aggregates, UPDATE,
/// DELETE). The engine runs in the Rust runtime; results come back as a
/// parsed JSON document.
/// </summary>
public class Database
{
    private readonly int _handle;
    private bool _open;

    private Database(int handle)
    {
        _handle = handle;
        _open = true;
    }

    public static Database OpenInMemory() => new Database(Db.Open(":memory:"));

    /// <summary>Open at a path; the mount decides the medium (/data, /sd, /usb).</summary>
    public static Database Open(string path) => new Database(Db.Open(path));

    /// <summary>Execute a non-query statement; returns affected rows.</summary>
    public int Execute(string sql) => Db.Exec(_handle, sql);

    /// <summary>Run a SELECT; result.Get("rows") is an array of row arrays.</summary>
    public JsonValue Query(string sql) => Json.Parse(Db.Query(_handle, sql));

    /// <summary>First column of the first row, as a string ("" when empty).</summary>
    public string Scalar(string sql)
    {
        JsonValue result = Query(sql);
        JsonValue rows = result.Get("rows");
        if (rows.Count == 0 || rows.At(0).Count == 0)
        {
            return "";
        }
        return rows.At(0).At(0).AsString;
    }

    /// <summary>Escape and single-quote a string literal for SQL text.</summary>
    public static string Quote(string s)
    {
        return string.Concat("'", s.Replace("'", "''"), "'");
    }

    public void Close()
    {
        if (_open)
        {
            Db.Close(_handle);
            _open = false;
        }
    }
}
