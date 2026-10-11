// Rustzor.Data — IndexedDB, SQLite (sql.js) and DuckDB (duckdb-wasm) for C#.
// The two engines load from a CDN on first use; window.RustzorCdn.sqljs /
// .duckdb point them at a mirror.
export function install({ ops, addHandle, getHandle, releaseHandle }) {
  const cdn = globalThis.RustzorCdn ?? {};
  const req = (r) =>
    new Promise((resolve, reject) => {
      r.onsuccess = () => resolve(r.result);
      r.onerror = () => reject(r.error);
    });
  const toB64 = (bytes) => {
    let s = "";
    for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(s);
  };
  const fromB64 = (b64) => Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
  // Values a JSON reply can carry: BigInt and typed arrays are not among them.
  const plain = (v) => (typeof v === "bigint" ? Number(v) : v instanceof Uint8Array ? toB64(v) : v instanceof Date ? v.toISOString() : v);

  // ── IndexedDB ────────────────────────────────────────────────────────────
  ops["idb.open"] = async ({ name, version, stores }) => {
    const open = indexedDB.open(name, version);
    open.onupgradeneeded = () => {
      const db = open.result;
      for (const s of stores ?? []) {
        const store = db.objectStoreNames.contains(s.name)
          ? open.transaction.objectStore(s.name)
          : db.createObjectStore(s.name, { keyPath: s.keyPath || undefined, autoIncrement: !!s.autoIncrement });
        for (const ix of s.indexes ?? []) if (!store.indexNames.contains(ix.name)) store.createIndex(ix.name, ix.keyPath, { unique: !!ix.unique });
      }
    };
    return addHandle(await req(open));
  };
  const store = (h, name, mode = "readonly") => getHandle(h).transaction(name, mode).objectStore(name);
  ops["idb.put"] = ({ h, store: s, value, key }) => req(key === undefined || key === null ? store(h, s, "readwrite").put(value) : store(h, s, "readwrite").put(value, key));
  ops["idb.putMany"] = ({ h, store: s, values }) =>
    new Promise((resolve, reject) => {
      const tx = getHandle(h).transaction(s, "readwrite");
      const os = tx.objectStore(s);
      for (const v of values) os.put(v);
      tx.oncomplete = () => resolve(values.length);
      tx.onerror = () => reject(tx.error);
    });
  ops["idb.get"] = async ({ h, store: s, key }) => (await req(store(h, s).get(key))) ?? null;
  ops["idb.getAll"] = ({ h, store: s, index, value, limit }) => {
    const os = store(h, s);
    const source = index ? os.index(index) : os;
    return req(source.getAll(value === undefined || value === null ? undefined : IDBKeyRange.only(value), limit || undefined));
  };
  ops["idb.range"] = ({ h, store: s, index, lower, upper, limit }) => {
    const os = store(h, s);
    const source = index ? os.index(index) : os;
    const range = lower != null && upper != null ? IDBKeyRange.bound(lower, upper) : lower != null ? IDBKeyRange.lowerBound(lower) : IDBKeyRange.upperBound(upper);
    return req(source.getAll(range, limit || undefined));
  };
  ops["idb.delete"] = async ({ h, store: s, key }) => {
    await req(store(h, s, "readwrite").delete(key));
    return true;
  };
  ops["idb.clear"] = async ({ h, store: s }) => {
    await req(store(h, s, "readwrite").clear());
    return true;
  };
  ops["idb.count"] = ({ h, store: s }) => req(store(h, s).count());
  ops["idb.keys"] = ({ h, store: s }) => req(store(h, s).getAllKeys());
  ops["idb.close"] = ({ h }) => {
    getHandle(h)?.close();
    releaseHandle(h);
  };
  ops["idb.drop"] = ({ name }) => req(indexedDB.deleteDatabase(name)).then(() => true);

  // A tiny key-value area for engine snapshots (SQLite files).
  const blobStore = async () => {
    const open = indexedDB.open("rustzor-data", 1);
    open.onupgradeneeded = () => open.result.createObjectStore("files");
    return req(open);
  };
  const saveBlob = async (key, bytes) => {
    const db = await blobStore();
    await req(db.transaction("files", "readwrite").objectStore("files").put(bytes, key));
    db.close();
  };
  const loadBlob = async (key) => {
    const db = await blobStore();
    const v = await req(db.transaction("files").objectStore("files").get(key));
    db.close();
    return v ?? null;
  };

  // ── SQLite (sql.js) ──────────────────────────────────────────────────────
  const SQLJS = cdn.sqljs ?? "https://cdn.jsdelivr.net/npm/sql.js@1.10.3/dist/";
  let sqlLoading = null;
  const loadSql = () =>
    (sqlLoading ??= new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = SQLJS + "sql-wasm.js";
      s.onload = () => globalThis.initSqlJs({ locateFile: (f) => SQLJS + f }).then(resolve, reject);
      s.onerror = () => reject(new Error("could not load sql.js from " + SQLJS));
      document.head.appendChild(s);
    }));
  ops["sql.open"] = async ({ data, persistKey }) => {
    const SQL = await loadSql();
    let bytes = data ? fromB64(data) : null;
    if (!bytes && persistKey) bytes = await loadBlob("sqlite:" + persistKey);
    const db = new SQL.Database(bytes ?? undefined);
    return addHandle({ db, persistKey });
  };
  const rowsOf = (results) => {
    const last = results[results.length - 1];
    if (!last) return { columns: [], rows: [] };
    return { columns: last.columns, rows: last.values.map((r) => r.map(plain)) };
  };
  ops["sql.query"] = ({ h, sql, params }) => {
    const { db } = getHandle(h);
    if (params && params.length) {
      const stmt = db.prepare(sql);
      stmt.bind(params);
      const columns = stmt.getColumnNames();
      const rows = [];
      while (stmt.step()) rows.push(stmt.get().map(plain));
      stmt.free();
      return { columns, rows };
    }
    return rowsOf(db.exec(sql));
  };
  ops["sql.run"] = ({ h, sql, params }) => {
    const { db } = getHandle(h);
    db.run(sql, params && params.length ? params : undefined);
    return { changes: db.getRowsModified(), lastId: Number(db.exec("select last_insert_rowid()")[0]?.values[0][0] ?? 0) };
  };
  ops["sql.export"] = ({ h }) => toB64(getHandle(h).db.export());
  ops["sql.save"] = async ({ h }) => {
    const box = getHandle(h);
    if (!box.persistKey) throw new Error("this database was opened without a persistKey");
    await saveBlob("sqlite:" + box.persistKey, box.db.export());
    return true;
  };
  ops["sql.close"] = ({ h }) => {
    getHandle(h)?.db.close();
    releaseHandle(h);
  };

  // ── DuckDB (duckdb-wasm) ─────────────────────────────────────────────────
  const DUCK = cdn.duckdb ?? "https://cdn.jsdelivr.net/npm/@duckdb/duckdb-wasm@1.29.0/+esm";
  let duckLoading = null;
  const loadDuck = () =>
    (duckLoading ??= (async () => {
      const duckdb = await import(/* @vite-ignore */ DUCK);
      const bundle = await duckdb.selectBundle(duckdb.getJsDelivrBundles());
      const workerUrl = URL.createObjectURL(new Blob([`importScripts("${bundle.mainWorker}");`], { type: "text/javascript" }));
      const worker = new Worker(workerUrl);
      const db = new duckdb.AsyncDuckDB(new duckdb.ConsoleLogger(duckdb.LogLevel.WARNING), worker);
      await db.instantiate(bundle.mainModule, bundle.pthreadWorker);
      URL.revokeObjectURL(workerUrl);
      return { duckdb, db };
    })());
  const tableRows = (table) => {
    const columns = table.schema.fields.map((f) => f.name);
    const rows = table.toArray().map((r) => columns.map((c) => plain(r[c])));
    return { columns, rows };
  };
  ops["duck.open"] = async () => {
    const { db } = await loadDuck();
    return addHandle({ conn: await db.connect() });
  };
  ops["duck.query"] = async ({ h, sql }) => tableRows(await getHandle(h).conn.query(sql));
  ops["duck.registerUrl"] = async ({ name, url }) => {
    const { duckdb, db } = await loadDuck();
    await db.registerFileURL(name, url, duckdb.DuckDBDataProtocol.HTTP, false);
    return true;
  };
  ops["duck.registerText"] = async ({ name, text }) => {
    const { db } = await loadDuck();
    await db.registerFileText(name, text);
    return true;
  };
  ops["duck.insertJson"] = async ({ h, table, rows }) => {
    const { db } = await loadDuck();
    const file = `${table}_${Date.now()}.json`;
    await db.registerFileText(file, JSON.stringify(rows));
    await getHandle(h).conn.insertJSONFromPath(file, { name: table });
    return rows.length;
  };
  ops["duck.close"] = async ({ h }) => {
    await getHandle(h)?.conn.close();
    releaseHandle(h);
  };
}
