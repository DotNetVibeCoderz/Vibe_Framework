# Embedded SQL database

`rustnet-db` is a dependency-free SQL engine built for MCU footprints. It
speaks a practical subset of SQLite's dialect and stores data either in
RAM or as a snapshot file on any mounted filesystem — the path picks the
medium:

| Path | Medium |
|---|---|
| `""` or `:memory:` | RAM only |
| `/data/app.db` | internal flash |
| `/sd/app.db` | SD card |
| `/usb/app.db` | USB drive |

## Supported SQL

- `CREATE TABLE [IF NOT EXISTS] t (col INTEGER|REAL|TEXT|BLOB, ...)`
  (`PRIMARY KEY`/`NOT NULL` accepted and ignored), `DROP TABLE [IF EXISTS]`
- `INSERT INTO t [(cols)] VALUES (...), (...)` — multi-row, `?` parameters
- `SELECT cols|* FROM t [WHERE expr] [ORDER BY col [DESC]] [LIMIT n]`
- Aggregates: `COUNT(*|col)`, `SUM`, `AVG`, `MIN`, `MAX`
- `UPDATE t SET col = expr [WHERE expr]`, `DELETE FROM t [WHERE expr]`
- `CREATE INDEX [IF NOT EXISTS] name ON t (col)`, `DROP INDEX [IF EXISTS] name`
- Expressions: `= != < <= > >=`, `AND OR NOT`, `+ -`, `LIKE` (`%`, `_`,
  case-insensitive), `IS [NOT] NULL`, parentheses, `'strings'` with `''`
  escape

**Secondary indexes** speed up equality lookups: a `WHERE col = value`
(including `?` parameters and a single `AND` conjunct) on an indexed column is
served from the index and the full predicate re-checked, instead of scanning
every row. Index maps are rebuilt after each mutation and their definitions are
saved in the snapshot, so they survive reopen.

**Persistence** uses a write-ahead log where the storage backend supports one
(the device VFS does, via a sibling `<db>.wal` file): each mutating statement is
appended to the WAL and folded into a full snapshot at a periodic checkpoint
(and on open, after replaying the log). Backends that only implement
`load`/`save` fall back to rewriting the whole snapshot after each mutation.
Either way this suits small-N embedded workloads (keep row counts in the
thousands).

## Managed API (`RustNet.Data`)

```csharp
Database db = Database.Open("/data/sensors.db");    // or Database.OpenInMemory()
db.Execute("CREATE TABLE IF NOT EXISTS readings (at INTEGER, temp REAL)");
db.Execute($"INSERT INTO readings VALUES ({Rtc.Epoch()}, 21.5)");

JsonValue result = db.Query("SELECT * FROM readings ORDER BY at DESC LIMIT 10");
JsonValue rows = result.Get("rows");                // array of row arrays
string hottest = db.Scalar("SELECT MAX(temp) FROM readings");
string quoted = Database.Quote(userInput);          // safe string literal
db.Close();
```

`Query` returns a parsed JSON document (`{"columns":[...],"rows":[[...]]}`,
BLOBs as `"0x..."` hex). Errors ("no such table", "modbus-style" messages)
surface as managed exceptions.

The engine lives in `runtime/rustnet-db` (tokenizer → recursive-descent
parser → row executor; `RNDB` snapshot format) with its own unit tests.
Template: `rustnet new datalogger-db <name>`.
