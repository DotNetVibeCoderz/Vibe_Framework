// Rustzor — the browser half of the runtime.
//
// rustzor.wasm is RustNetRuntime (the RustCLR interpreter and RustBCL) compiled
// to WebAssembly. This file loads it, feeds it the app's assemblies, and is the
// only place C# reaches the browser from: every Rustzor.Browser.* library is a
// set of named operations in the `ops` table below, called with one JSON
// argument through Js.Invoke(op, json).
//
// Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil.

const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** Named operations C# can call. Rustzor.Browser.* libraries add to this. */
export const ops = Object.create(null);

/** JS objects C# holds by number: sockets, canvases, peer connections... */
const handles = new Map();
let nextHandle = 1;
export function addHandle(obj) {
  if (obj === null || obj === undefined) return 0;
  const id = nextHandle++;
  handles.set(id, obj);
  return id;
}
export function getHandle(id) {
  return handles.get(id);
}
export function releaseHandle(id) {
  handles.delete(id);
}

class Runtime {
  constructor() {
    this.exports = null;
    this.memory = null;
    this.pending = null; // result bytes waiting for host_take
    this.busy = 0; // > 0 while C# is running
    this.queue = []; // calls into C# that arrived while busy
    this.aot = null; // the AOT module's instance, if one was loaded
    this.log = (level, text) => {
      const fn = level >= 2 ? console.error : level === 1 ? console.warn : console.log;
      fn(text);
    };
    this.stdout = [];
  }

  // ── memory helpers ──────────────────────────────────────────────────────

  bytes(ptr, len) {
    return new Uint8Array(this.memory.buffer, ptr, len);
  }
  str(ptr, len) {
    return len === 0 ? "" : decoder.decode(this.bytes(ptr, len));
  }
  /** Copies bytes into wasm memory; returns [ptr, len]. Caller frees. */
  put(data) {
    const bytes = typeof data === "string" ? encoder.encode(data) : data;
    const ptr = this.exports.rz_alloc(bytes.length);
    this.bytes(ptr, bytes.length).set(bytes);
    return [ptr, bytes.length];
  }
  result() {
    return this.str(this.exports.rz_result_ptr(), this.exports.rz_result_len());
  }

  // ── the imports rustzor.wasm needs ──────────────────────────────────────

  imports() {
    const rt = this;
    return {
      rustzor: {
        host_write(stream, ptr, len) {
          const text = rt.str(ptr, len);
          if (stream >= 10) rt.log(stream - 10, text);
          else {
            rt.stdout.push(text);
            rt.log(stream === 2 ? 2 : 0, text);
          }
        },
        host_now: () => performance.now(),
        host_wall: () => Date.now(),
        host_tz_offset: () => -new Date().getTimezoneOffset(),
        host_call(opPtr, opLen, argPtr, argLen) {
          const op = rt.str(opPtr, opLen);
          const arg = rt.str(argPtr, argLen);
          let value;
          try {
            value = rt.callOp(op, arg);
          } catch (e) {
            value = { $error: String(e && e.message ? e.message : e) };
          }
          if (value === undefined || value === null) {
            rt.pending = null;
            return -1;
          }
          rt.pending = encoder.encode(typeof value === "string" ? value : JSON.stringify(value));
          return rt.pending.length;
        },
        host_take(dst) {
          rt.bytes(dst, rt.pending.length).set(rt.pending);
          rt.pending = null;
        },
        host_aot_call(index, _args, _argc) {
          return rt.aot.exports["t" + index](16);
        },
        host_aot_copy(toAot, here, there, len) {
          const mem = rt.aot.exports.memory;
          const need = there + len;
          if (need > mem.buffer.byteLength) {
            mem.grow(Math.ceil((need - mem.buffer.byteLength) / 65536));
          }
          const aotBytes = new Uint8Array(mem.buffer, there, len);
          const ours = rt.bytes(here, len);
          if (toAot) aotBytes.set(ours);
          else ours.set(aotBytes);
        },
      },
    };
  }

  /**
   * Runs one operation. Results are JSON: a string op result is passed as a
   * JSON string, so C# always parses. `{ $error }` becomes an exception there.
   */
  callOp(op, arg) {
    const fn = ops[op];
    if (!fn) return { $error: `Rustzor: no browser operation named '${op}'` };
    const parsed = arg.length ? JSON.parse(arg) : null;
    const value = fn(parsed, this);
    if (value === undefined || value === null) return null;
    return JSON.stringify(value);
  }

  // ── lifecycle ───────────────────────────────────────────────────────────

  async instantiate(source) {
    let instance;
    if (source instanceof WebAssembly.Module) {
      instance = await WebAssembly.instantiate(source, this.imports());
    } else if (source instanceof ArrayBuffer || ArrayBuffer.isView(source)) {
      ({ instance } = await WebAssembly.instantiate(source, this.imports()));
    } else if (typeof WebAssembly.instantiateStreaming === "function") {
      try {
        ({ instance } = await WebAssembly.instantiateStreaming(fetch(source), this.imports()));
      } catch {
        const buf = await (await fetch(source)).arrayBuffer();
        ({ instance } = await WebAssembly.instantiate(buf, this.imports()));
      }
    } else {
      const buf = await (await fetch(source)).arrayBuffer();
      ({ instance } = await WebAssembly.instantiate(buf, this.imports()));
    }
    this.exports = instance.exports;
    this.memory = instance.exports.memory;
    this.natives = this.exports.rz_init(0);
    return this;
  }

  /** Loads one assembly's bytes. Libraries first. Returns the assembly id. */
  load(bytes) {
    const [ptr, len] = this.put(new Uint8Array(bytes));
    const id = this.exports.rz_load(ptr, len);
    this.exports.rz_free(ptr, len);
    if (id < 0) throw new Error(this.result());
    return id;
  }

  /** Installs the AOT module and its manifest. Returns methods installed. */
  async loadAot(moduleBytes, manifest) {
    const { instance } = await WebAssembly.instantiate(moduleBytes, {});
    this.aot = instance;
    const [ptr, len] = this.put(manifest);
    const count = this.exports.rz_aot_install(ptr, len);
    this.exports.rz_free(ptr, len);
    if (count < 0) throw new Error(this.result());
    return count;
  }

  runMain(assemblyId) {
    return this.enter(() => {
      const code = this.exports.rz_run_main(assemblyId);
      if (code < 0 && this.exports.rz_result_len() > 0) throw new Error(this.result());
      return code;
    });
  }

  /**
   * Calls `static string? Type.Method(string)` (or with no argument). Calls
   * that arrive while C# is already running are queued and run when it
   * returns; they cannot nest, and their results are discarded.
   */
  invoke(type, method, arg) {
    if (this.busy) {
      this.queue.push([type, method, arg]);
      return undefined;
    }
    return this.enter(() => this.invokeNow(type, method, arg));
  }

  invokeNow(type, method, arg) {
    const request = arg === undefined ? `${type}\u0001${method}` : `${type}\u0001${method}\u0001${arg}`;
    const [ptr, len] = this.put(request);
    const status = this.exports.rz_invoke(ptr, len);
    this.exports.rz_free(ptr, len);
    if (status === 1) return null;
    const text = this.result();
    if (status < 0) throw new Error(text);
    return text;
  }

  enter(fn) {
    this.busy++;
    try {
      return fn();
    } finally {
      this.busy--;
      if (this.busy === 0) this.drain();
    }
  }

  drain() {
    while (this.queue.length && this.busy === 0) {
      const [type, method, arg] = this.queue.shift();
      try {
        this.enter(() => this.invokeNow(type, method, arg));
      } catch (e) {
        this.log(2, String(e.message || e));
      }
    }
  }

  stats() {
    this.exports.rz_stats();
    return JSON.parse(this.result());
  }

  collect() {
    if (!this.busy) this.exports.rz_collect();
  }
}

/** The one runtime on the page. */
export let runtime = null;

/** Sends a callback result into C#: Rustzor.Interop.Callbacks.Dispatch. */
export function dispatch(id, payload) {
  const json = payload === undefined ? "null" : JSON.stringify(payload);
  return runtime.invoke("Rustzor.Interop.Callbacks", "Dispatch", `${id}|${json}`);
}

// ── core operations every app uses ──────────────────────────────────────────

// Async operations: C# asks for `op` and gets told later through Complete.
ops["async"] = ({ id, op, arg }, rt) => {
  Promise.resolve()
    .then(() => {
      const fn = ops[op];
      if (!fn) throw new Error(`Rustzor: no browser operation named '${op}'`);
      return fn(arg, rt);
    })
    .then(
      (value) => complete(id, true, value),
      (err) => complete(id, false, String((err && err.message) || err)),
    );
  return null;
};

function complete(id, ok, value) {
  const json = value === undefined ? "null" : JSON.stringify(value);
  runtime.invoke("Rustzor.Interop.Callbacks", "Complete", `${id}|${ok ? 1 : 0}|${json}`);
}

ops["timer.set"] = ({ id, ms, repeat }) => {
  const fire = () => dispatch(id, null);
  return repeat ? setInterval(fire, ms) : setTimeout(fire, ms);
};
ops["timer.clear"] = ({ handle }) => {
  clearTimeout(handle);
  clearInterval(handle);
};
ops["timer.raf"] = ({ id }) => requestAnimationFrame((t) => dispatch(id, t));

// Generic interop: reach any browser API by path, for what no typed library
// covers yet. Objects come back as handles, primitives as values.
function resolvePath(path) {
  let target = globalThis;
  for (const part of path.split(".")) {
    if (part) target = target[part];
  }
  return target;
}
function toWire(value) {
  if (value === null || value === undefined) return null;
  const t = typeof value;
  if (t === "string" || t === "number" || t === "boolean") return value;
  if (Array.isArray(value) && value.every((v) => v === null || typeof v !== "object")) return value;
  return { $h: addHandle(value) };
}
function fromWire(value) {
  if (value && typeof value === "object" && "$h" in value) return getHandle(value.$h);
  return value;
}
ops["js.get"] = ({ h, path }) => {
  const base = h ? getHandle(h) : globalThis;
  let v = base;
  for (const p of path.split(".")) if (p) v = v == null ? undefined : v[p];
  return toWire(v);
};
ops["js.set"] = ({ h, path, value }) => {
  const parts = path.split(".");
  const last = parts.pop();
  let base = h ? getHandle(h) : globalThis;
  for (const p of parts) if (p) base = base[p];
  base[last] = fromWire(value);
};
ops["js.call"] = ({ h, path, args }) => {
  const parts = path.split(".");
  const name = parts.pop();
  let base = h ? getHandle(h) : globalThis;
  for (const p of parts) if (p) base = base[p];
  const result = base[name](...(args || []).map(fromWire));
  if (result && typeof result.then === "function") return result.then(toWire);
  return toWire(result);
};
ops["js.new"] = ({ ctor, args }) => {
  const C = resolvePath(ctor);
  return toWire(new C(...(args || []).map(fromWire)));
};
ops["js.release"] = ({ h }) => releaseHandle(h);
ops["js.eval"] = ({ code }) => toWire((0, eval)(code));
ops["js.has"] = ({ path }) => {
  try {
    return resolvePath(path) !== undefined;
  } catch {
    return false;
  }
};
ops["js.on"] = ({ h, event, id }) => {
  const target = h ? getHandle(h) : globalThis;
  const fn = (e) => dispatch(id, eventToWire(e));
  target.addEventListener(event, fn);
  return addHandle(() => target.removeEventListener(event, fn));
};
ops["js.off"] = ({ token }) => {
  const off = getHandle(token);
  if (off) off();
  releaseHandle(token);
};

/** A plain-data picture of an event, for handlers on the C# side. */
export function eventToWire(e) {
  if (!e || typeof e !== "object") return e;
  const out = { type: e.type };
  const t = e.target;
  if (t && typeof t === "object") {
    if ("value" in t) out.value = t.value;
    if ("checked" in t) out.checked = t.checked;
  }
  for (const k of ["key", "code", "ctrlKey", "shiftKey", "altKey", "metaKey", "repeat"]) if (k in e) out[k] = e[k];
  for (const k of ["clientX", "clientY", "offsetX", "offsetY", "button", "buttons", "deltaX", "deltaY", "pointerId"])
    if (k in e) out[k] = e[k];
  if ("data" in e && typeof e.data !== "object") out.data = e.data;
  if (typeof e.data === "object" && e.data !== null && !(e.data instanceof ArrayBuffer)) {
    try {
      out.data = JSON.stringify(e.data);
    } catch {}
  }
  if (e.touches && e.touches.length) out.touches = Array.from(e.touches, (p) => [p.clientX, p.clientY]);
  return out;
}

// ── the component renderer's DOM operations ─────────────────────────────────
//
// Rustzor.Components diffs virtual trees in C# and sends one batch per update:
// an array of [code, ...args]. Node ids are the renderer's; this table maps
// them to DOM nodes. The codes match Renderer.cs.

const nodes = new Map();
const RZ = "__rzid";

function forget(node) {
  if (node[RZ]) nodes.delete(node[RZ]);
  if (node.__rzListeners) node.__rzListeners = null;
  for (let c = node.firstChild; c; c = c.nextSibling) forget(c);
}

function domOf(id) {
  const n = nodes.get(id);
  return Array.isArray(n) ? n[0] : n;
}

// Properties that must be set as properties, not attributes, to take effect.
const PROPS = { value: 1, checked: 1, selected: 1, indeterminate: 1 };

function setAttr(el, name, value) {
  if (PROPS[name] && name in el) {
    if (name === "value") {
      if (el.value !== value) el.value = value;
      if (el.tagName === "SELECT") queueMicrotask(() => (el.value = value));
    } else el[name] = value !== "false";
    if (name !== "value") return;
  }
  try {
    el.setAttribute(name, value);
  } catch {}
}

function removeAttr(el, name) {
  if (PROPS[name] && name in el) {
    if (name === "value") el.value = "";
    else el[name] = false;
  }
  el.removeAttribute(name);
}

function listen(el, event, handlerId, flags) {
  const ls = (el.__rzListeners ||= {});
  const old = ls[event];
  if (old) el.removeEventListener(event, old.fn);
  const fn = (e) => {
    if (flags & 1) e.preventDefault();
    if (flags & 2) e.stopPropagation();
    // Handler 0 carries only modifiers (`@onclick:stopPropagation` with no `@onclick`).
    if (!handlerId) return;
    const payload = eventToWire(e);
    const t = e.target;
    if (t && t.type === "checkbox" && (event === "change" || event === "input")) payload.value = t.checked;
    for (const k of ["pageX", "pageY", "movementX", "movementY", "detail", "pointerType", "pressure", "isPrimary"])
      if (k in e) payload[k] = e[k];
    runtime.invoke("Rustzor.Components.RendererInterop", "DispatchEvent", `${handlerId}|${event}|${JSON.stringify(payload)}`);
  };
  ls[event] = { fn, handlerId };
  el.addEventListener(event, fn, event === "scroll" || event === "wheel" || event.startsWith("touch") ? { passive: !(flags & 1) } : false);
}

ops["dom.patch"] = (batch) => {
  for (const op of batch) {
    switch (op[0]) {
      case 1: {
        const tag = op[2];
        const svgParent = tag === "svg" || SVG_TAGS.has(tag);
        nodes.set(op[1], svgParent ? document.createElementNS("http://www.w3.org/2000/svg", tag) : document.createElement(tag));
        nodes.get(op[1])[RZ] = op[1];
        break;
      }
      case 2: {
        const t = document.createTextNode(op[2]);
        t[RZ] = op[1];
        nodes.set(op[1], t);
        break;
      }
      case 3: {
        const c = document.createComment("");
        c[RZ] = op[1];
        nodes.set(op[1], c);
        break;
      }
      case 4: {
        const tpl = document.createElement("template");
        tpl.innerHTML = op[2];
        const list = Array.from(tpl.content.childNodes);
        if (list.length === 0) list.push(document.createTextNode(""));
        list.html = op[2];
        nodes.set(op[1], list);
        break;
      }
      case 5: {
        const parent = domOf(op[1]);
        let n = nodes.get(op[2]);
        const before = op[3] ? domOf(op[3]) : null;
        // Markup under an <svg> must be parsed as SVG, or <text> and <path>
        // become HTML elements that draw nothing.
        if (Array.isArray(n) && n.html !== undefined && parent.namespaceURI === SVG_NS && n[0]?.namespaceURI !== SVG_NS) {
          const holder = document.createElementNS(SVG_NS, "svg");
          holder.innerHTML = n.html;
          const list = Array.from(holder.childNodes);
          list.html = n.html;
          n = list;
          nodes.set(op[2], list);
        }
        if (Array.isArray(n)) for (const x of n) parent.insertBefore(x, before);
        else parent.insertBefore(n, before);
        break;
      }
      case 6: {
        const n = nodes.get(op[1]);
        if (Array.isArray(n)) for (const x of n) x.remove();
        else if (n) {
          forget(n);
          n.remove();
        }
        nodes.delete(op[1]);
        break;
      }
      case 7:
        setAttr(nodes.get(op[1]), op[2], op[3]);
        break;
      case 8:
        removeAttr(nodes.get(op[1]), op[2]);
        break;
      case 9:
        nodes.get(op[1]).textContent = op[2];
        break;
      case 10:
        listen(nodes.get(op[1]), op[2], op[3], op[4]);
        break;
      case 11: {
        const el = nodes.get(op[1]);
        const l = el && el.__rzListeners && el.__rzListeners[op[2]];
        if (l) {
          el.removeEventListener(op[2], l.fn);
          delete el.__rzListeners[op[2]];
        }
        break;
      }
      case 12: {
        const host = document.querySelector(op[2]);
        if (!host) throw new Error(`Rustzor: no element matches '${op[2]}'`);
        host.textContent = "";
        host[RZ] = op[1];
        nodes.set(op[1], host);
        break;
      }
    }
  }
  return null;
};

const SVG_NS = "http://www.w3.org/2000/svg";
const SVG_TAGS = new Set([
  "path", "circle", "rect", "line", "polyline", "polygon", "ellipse", "g", "text", "tspan", "defs",
  "linearGradient", "radialGradient", "stop", "clipPath", "mask", "pattern", "use", "symbol", "foreignObject", "title",
  "image", "marker", "textPath", "desc", "switch", "view", "filter", "feGaussianBlur", "feOffset", "feBlend", "feFlood",
  "feComposite", "feColorMatrix", "feMerge", "feMergeNode", "feDropShadow", "animate", "animateTransform", "set",
]);

/** The DOM element behind an ElementReference id (C# sends `{ $el: "42" }`). */
export function elementOf(ref) {
  if (ref && typeof ref === "object" && "$el" in ref) return domOf(Number(ref.$el));
  if (typeof ref === "string" || typeof ref === "number") return domOf(Number(ref));
  return null;
}

// `ElementReference.FocusAsync()`.
ops["el.focus"] = ({ el, preventScroll }) => {
  elementOf(el)?.focus({ preventScroll: !!preventScroll });
};

ops["doc.title"] = ({ title }) => {
  document.title = title;
};
ops["app.ready"] = () => {
  document.documentElement.classList.add("rz-ready");
  const splash = document.getElementById("rz-splash");
  if (splash) splash.remove();
};

// IJSRuntime.InvokeAsync("path.to.fn", args): ElementReferences become nodes.
ops["js.invoke"] = ({ path, args }) => {
  const parts = path.split(".");
  const name = parts.pop();
  let base = globalThis;
  for (const p of parts) base = base[p];
  const real = (args || []).map((a) => (a && typeof a === "object" && "$el" in a ? elementOf(a) : a));
  return base[name](...real);
};

// ── navigation ──────────────────────────────────────────────────────────────

ops["nav.init"] = () => {
  const base = document.baseURI;
  // Back/forward.
  window.addEventListener("popstate", () =>
    runtime.invoke("Rustzor.Components.RendererInterop", "LocationChanged", `0|${location.href}`),
  );
  // Same-app link clicks become pushState navigations, as in Blazor.
  document.addEventListener("click", (e) => {
    if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
    const a = e.target.closest && e.target.closest("a[href]");
    if (!a || a.target === "_blank" || a.hasAttribute("download") || a.getAttribute("href").startsWith("#")) return;
    const url = new URL(a.href, base);
    if (!url.href.startsWith(base)) return;
    e.preventDefault();
    if (url.href !== location.href) history.pushState(null, "", url.href);
    runtime.invoke("Rustzor.Components.RendererInterop", "LocationChanged", `1|${url.href}`);
  });
  return { base, href: location.href };
};
ops["nav.push"] = ({ url, replace }) => {
  if (replace) history.replaceState(null, "", url);
  else history.pushState(null, "", url);
};
ops["nav.load"] = ({ url, replace }) => {
  if (replace) location.replace(url);
  else location.href = url;
};
ops["nav.scrollTop"] = () => window.scrollTo(0, 0);

// ── startup ─────────────────────────────────────────────────────────────────

/**
 * Starts an app from its boot manifest (written by `dotnet build` through the
 * Rustzor SDK): `{ runtime, assemblies: [...], entry, aot? }`.
 */
export async function start(options = {}) {
  const base = options.base ?? "_rustzor/";
  const t0 = performance.now();
  const boot = options.boot ?? (await (await fetch(base + "boot.json", { cache: "no-cache" })).json());
  const rt = new Runtime();
  if (options.log) rt.log = options.log;
  runtime = rt;
  globalThis.Rustzor = Object.assign(globalThis.Rustzor || {}, { runtime: rt, ops, dispatch, addHandle, getHandle, elementOf });

  const fetchBytes = async (name) => {
    const res = await fetch(base + name);
    if (!res.ok) throw new Error(`Rustzor: could not fetch ${name} (${res.status})`);
    return new Uint8Array(await res.arrayBuffer());
  };

  // Start every download at once; load them in manifest order.
  const wasm = rt.instantiate(base + boot.runtime);
  const assemblies = boot.assemblies.map(fetchBytes);
  const libs = await Promise.all((boot.modules || []).map((m) => import(new URL(base + m, document.baseURI).href)));
  // Library styles go before the page's own stylesheets (app.css and the
  // like), so an app's rule beats a library rule of the same specificity.
  // Cross-origin links such as web fonts are not the page's own.
  const ownStyles = [...document.head.querySelectorAll('link[rel="stylesheet"], style')].find(
    (el) => el.tagName === "STYLE" || new URL(el.href, document.baseURI).origin === location.origin,
  );
  for (const href of boot.styles || []) {
    const link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = new URL(base + href, document.baseURI).href;
    document.head.insertBefore(link, ownStyles ?? null);
  }
  const context = { ops, runtime: rt, dispatch, complete, addHandle, getHandle, releaseHandle, eventToWire, elementOf };
  for (const lib of libs) if (lib.install) lib.install(context);
  await wasm;
  let entryId = -1;
  const loaded = await Promise.all(assemblies);
  loaded.forEach((bytes, i) => {
    const id = rt.load(bytes);
    if (boot.assemblies[i].split("?")[0] === boot.entry + ".dll") entryId = id;
  });
  if (boot.aot && !options.interpretOnly) {
    const [mod, manifest] = await Promise.all([fetchBytes(boot.aot.module), fetch(base + boot.aot.manifest).then((r) => r.text())]);
    rt.aotMethods = await rt.loadAot(mod, manifest);
  }
  rt.bootMillis = performance.now() - t0;
  if (entryId < 0) throw new Error(`Rustzor: entry assembly ${boot.entry}.dll is not in the manifest`);
  rt.runMain(entryId);
  if (!options.quiet) {
    console.info(
      `%cRustzor%c started in ${rt.bootMillis.toFixed(0)} ms (${boot.assemblies.length} assemblies${rt.aotMethods ? `, ${rt.aotMethods} AOT methods` : ""}). Built by Gravicode Studios, led by Kang Fadhil.`,
      "background:#f74c00;color:#fff;padding:2px 6px;border-radius:3px;font-weight:600",
      "",
    );
  }
  return rt;
}

/** For tests and tools: a runtime without a page. */
export async function createRuntime(wasmBytes, options = {}) {
  const rt = new Runtime();
  if (options.log) rt.log = options.log;
  runtime = rt;
  await rt.instantiate(wasmBytes);
  return rt;
}
