// Rustzor.Browser.BackgroundSync — one-off and periodic sync, and an outbox.
export function install({ ops }) {
  const db = () =>
    new Promise((resolve, reject) => {
      const req = indexedDB.open("rustzor-outbox", 1);
      req.onupgradeneeded = () => req.result.createObjectStore("outbox", { autoIncrement: true });
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  ops["sync.supported"] = () => "serviceWorker" in navigator && "SyncManager" in globalThis;
  ops["sync.register"] = async ({ tag }) => {
    await (await navigator.serviceWorker.ready).sync.register(tag);
    return true;
  };
  ops["sync.tags"] = async () => (await navigator.serviceWorker.ready).sync.getTags();
  ops["sync.periodic"] = async ({ tag, minutes }) => {
    const reg = await navigator.serviceWorker.ready;
    if (!reg.periodicSync) throw new Error("Periodic Background Sync is not supported here");
    await reg.periodicSync.register(tag, { minInterval: minutes * 60 * 1000 });
    return true;
  };
  ops["sync.enqueue"] = async ({ url, method, body }) => {
    const d = await db();
    await new Promise((resolve, reject) => {
      const tx = d.transaction("outbox", "readwrite");
      tx.objectStore("outbox").add({ url, method, body, queued: Date.now() });
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
    });
    return true;
  };
  ops["sync.count"] = async () => {
    const d = await db();
    return new Promise((resolve) => {
      const req = d.transaction("outbox").objectStore("outbox").count();
      req.onsuccess = () => resolve(req.result);
    });
  };
}
