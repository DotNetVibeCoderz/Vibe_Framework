// Rustzor.Browser.Workers — Web Workers, service workers, PWA install.
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle }) {
  const wire = (worker, id) => {
    worker.onmessage = (e) => dispatch(id, { type: "message", data: e.data });
    worker.onerror = (e) => dispatch(id, { type: "error", message: e.message });
    return addHandle(worker);
  };
  ops["worker.start"] = ({ url, module, id }) => wire(new Worker(url, module ? { type: "module" } : {}), id);
  ops["worker.source"] = ({ code, id }) => wire(new Worker(URL.createObjectURL(new Blob([code], { type: "text/javascript" }))), id);
  ops["worker.post"] = ({ h, data }) => getHandle(h)?.postMessage(data);
  ops["worker.stop"] = ({ h }) => {
    getHandle(h)?.terminate();
    releaseHandle(h);
  };

  let deferred = null;
  addEventListener("beforeinstallprompt", (e) => {
    e.preventDefault();
    deferred = e;
    dispatchEvent(new Event("rustzor:installable"));
  });
  ops["sw.supported"] = () => "serviceWorker" in navigator;
  ops["sw.register"] = async ({ url, scope }) => (await navigator.serviceWorker.register(url, { scope })).scope;
  ops["sw.waiting"] = async () => !!(await navigator.serviceWorker.getRegistration())?.waiting;
  ops["sw.activate"] = async () => {
    const reg = await navigator.serviceWorker.getRegistration();
    reg?.waiting?.postMessage({ type: "SKIP_WAITING" });
    navigator.serviceWorker.addEventListener("controllerchange", () => location.reload(), { once: true });
  };
  ops["sw.install"] = async () => {
    if (!deferred) return false;
    deferred.prompt();
    const choice = await deferred.userChoice;
    deferred = null;
    return choice.outcome === "accepted";
  };
}
