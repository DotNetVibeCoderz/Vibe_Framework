// Rustzor.Browser.Storage — localStorage, sessionStorage, quota, cookies.
export function install({ ops }) {
  const area = (name) => (name === "sessionStorage" ? sessionStorage : localStorage);
  ops["storage.get"] = ({ area: a, key }) => area(a).getItem(key);
  ops["storage.set"] = ({ area: a, key, value }) => area(a).setItem(key, value);
  ops["storage.remove"] = ({ area: a, key }) => area(a).removeItem(key);
  ops["storage.clear"] = ({ area: a }) => area(a).clear();
  ops["storage.count"] = ({ area: a }) => area(a).length;
  ops["storage.keys"] = ({ area: a }) => {
    const s = area(a);
    const keys = [];
    for (let i = 0; i < s.length; i++) keys.push(s.key(i));
    return keys;
  };
  ops["storage.estimate"] = async () => {
    const e = navigator.storage?.estimate ? await navigator.storage.estimate() : { usage: 0, quota: 0 };
    return { usage: e.usage ?? 0, quota: e.quota ?? 0 };
  };
  ops["storage.persist"] = async () => (navigator.storage?.persist ? navigator.storage.persist() : false);
  ops["storage.cookieGet"] = ({ name }) => {
    const hit = document.cookie.split("; ").find((c) => c.startsWith(encodeURIComponent(name) + "="));
    return hit ? decodeURIComponent(hit.split("=").slice(1).join("=")) : null;
  };
  ops["storage.cookieSet"] = ({ name, value, days, path }) => {
    const expires = new Date(Date.now() + days * 864e5).toUTCString();
    document.cookie = `${encodeURIComponent(name)}=${encodeURIComponent(value)}; expires=${expires}; path=${path}; SameSite=Lax`;
  };
}
