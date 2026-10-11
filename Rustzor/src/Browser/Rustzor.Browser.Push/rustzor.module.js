// Rustzor.Browser.Push — Web Push subscriptions.
export function install({ ops }) {
  const key = (base64) => {
    const pad = "=".repeat((4 - (base64.length % 4)) % 4);
    const raw = atob((base64 + pad).replace(/-/g, "+").replace(/_/g, "/"));
    return Uint8Array.from(raw, (c) => c.charCodeAt(0));
  };
  const registration = async () => {
    const reg = await navigator.serviceWorker.ready;
    if (!reg.pushManager) throw new Error("Push is not supported here");
    return reg;
  };
  ops["push.supported"] = () => "PushManager" in globalThis && "serviceWorker" in navigator;
  ops["push.subscribe"] = async ({ key: vapid }) => {
    const reg = await registration();
    const sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key(vapid) });
    return JSON.stringify(sub);
  };
  ops["push.get"] = async () => {
    const sub = await (await registration()).pushManager.getSubscription();
    return sub ? JSON.stringify(sub) : null;
  };
  ops["push.unsubscribe"] = async () => {
    const sub = await (await registration()).pushManager.getSubscription();
    return sub ? sub.unsubscribe() : false;
  };
}
