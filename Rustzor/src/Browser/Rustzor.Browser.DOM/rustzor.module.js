// Rustzor.Browser.DOM — document, elements, window, clipboard, downloads.
export function install({ ops, elementOf, addHandle, dispatch }) {
  const el = (id) => {
    const node = elementOf(id);
    if (!node) throw new Error(`no element ${id}`);
    return node;
  };
  ops["dom.title"] = () => document.title;
  ops["dom.rootAttr"] = ({ name, value }) =>
    value === null || value === undefined
      ? document.documentElement.removeAttribute(name)
      : document.documentElement.setAttribute(name, value);
  ops["dom.getRootAttr"] = ({ name }) => document.documentElement.getAttribute(name);
  ops["dom.cssVar"] = ({ name, value }) => document.documentElement.style.setProperty(name, value);
  ops["dom.addStyle"] = ({ href }) => {
    if (document.querySelector(`link[href="${href}"]`)) return;
    const link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = href;
    document.head.appendChild(link);
  };
  ops["dom.addScript"] = ({ src }) =>
    new Promise((resolve, reject) => {
      if (document.querySelector(`script[src="${src}"]`)) return resolve(true);
      const s = document.createElement("script");
      s.src = src;
      s.onload = () => resolve(true);
      s.onerror = () => reject(new Error(`could not load ${src}`));
      document.head.appendChild(s);
    });
  ops["dom.queryText"] = ({ selector }) => document.querySelector(selector)?.textContent ?? null;
  ops["dom.query"] = ({ selector }) => {
    const n = document.querySelector(selector);
    return n ? { $h: addHandle(n) } : null;
  };
  ops["dom.handle"] = ({ el: id }) => ({ $h: addHandle(el(id)) });
  ops["dom.visible"] = () => document.visibilityState === "visible";
  ops["dom.el"] = ({ el: id, call }) => el(id)[call]();
  ops["dom.scrollIntoView"] = ({ el: id, smooth, block }) => el(id).scrollIntoView({ behavior: smooth ? "smooth" : "auto", block: block || "nearest" });
  ops["dom.scrollBottom"] = ({ el: id }) => {
    const n = el(id);
    n.scrollTop = n.scrollHeight;
  };
  ops["dom.rect"] = ({ el: id }) => {
    const r = el(id).getBoundingClientRect();
    return { x: r.x, y: r.y, width: r.width, height: r.height };
  };
  ops["dom.style"] = ({ el: id, prop, value }) => {
    if (value === null || value === undefined) el(id).style.removeProperty(prop);
    else el(id).style.setProperty(prop, value);
  };
  ops["dom.toggleClass"] = ({ el: id, cls, on }) => el(id).classList.toggle(cls, on);
  ops["dom.value"] = ({ el: id }) => el(id).value ?? "";
  ops["dom.setValue"] = ({ el: id, value }) => {
    el(id).value = value;
  };
  ops["dom.fullscreen"] = ({ el: id }) => el(id).requestFullscreen();
  ops["dom.observeResize"] = ({ el: id, id: cb }) => {
    const ro = new ResizeObserver(([entry]) => dispatch(cb, { width: entry.contentRect.width, height: entry.contentRect.height }));
    ro.observe(el(id));
    return addHandle(() => ro.disconnect());
  };
  ops["dom.window"] = () => ({ w: innerWidth, h: innerHeight, dpr: devicePixelRatio });
  ops["dom.prefersDark"] = () => matchMedia("(prefers-color-scheme: dark)").matches;
  ops["dom.scrollTo"] = ({ x, y, smooth }) => scrollTo({ left: x, top: y, behavior: smooth ? "smooth" : "auto" });
  ops["dom.alert"] = ({ message }) => alert(message);
  ops["dom.confirm"] = ({ message }) => confirm(message);
  ops["dom.prompt"] = ({ message, value }) => prompt(message, value ?? "");
  ops["dom.open"] = ({ url, target }) => open(url, target);
  ops["dom.print"] = () => print();
  ops["dom.online"] = () => navigator.onLine;
  ops["dom.clipWrite"] = ({ text }) => navigator.clipboard.writeText(text).then(() => true);
  ops["dom.clipRead"] = () => navigator.clipboard.readText();
  ops["dom.download"] = ({ name, text, type, url }) => {
    const href = url ?? URL.createObjectURL(new Blob([text], { type: type || "text/plain" }));
    const a = document.createElement("a");
    a.href = href;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    if (!url) setTimeout(() => URL.revokeObjectURL(href), 10000);
  };
}
