// Rustzor.UI.Mui — Material UI 5 islands. Each MuiIsland renders one MUI
// component tree with React 18; the tree is described in JSON from C#:
//   { "$mui": "Button", "props": {...}, "children": "text" | node | [nodes] }
//   { "$slot": "name" }   → DOM that Rustzor renders and keeps updating
//   { "$event": "name" }  → a callback that reaches C# as MuiEvent(name, value)
// The theme follows Rustzor's data-theme and the Rustzor palette.
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle, elementOf }) {
  const cdn = globalThis.RustzorCdn ?? {};
  const SOURCES = [
    cdn.react ?? "https://cdn.jsdelivr.net/npm/react@18.3.1/umd/react.production.min.js",
    cdn.reactDom ?? "https://cdn.jsdelivr.net/npm/react-dom@18.3.1/umd/react-dom.production.min.js",
    cdn.mui ?? "https://cdn.jsdelivr.net/npm/@mui/material@5.16.7/umd/material-ui.production.min.js",
  ];
  const FONTS = cdn.muiFonts ?? "https://fonts.googleapis.com/icon?family=Material+Icons";
  const script = (src) =>
    new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = src;
      s.onload = resolve;
      s.onerror = () => reject(new Error("could not load " + src));
      document.head.appendChild(s);
    });
  let loading = null;
  const load = () =>
    (loading ??= (async () => {
      if (!document.querySelector("link[data-rz-mui]")) {
        const link = document.createElement("link");
        link.rel = "stylesheet";
        link.href = FONTS;
        link.dataset.rzMui = "";
        document.head.appendChild(link);
      }
      if (!globalThis.React) await script(SOURCES[0]);
      if (!globalThis.ReactDOM) await script(SOURCES[1]);
      if (!globalThis.MaterialUI) await script(SOURCES[2]);
      return { React: globalThis.React, ReactDOM: globalThis.ReactDOM, MUI: globalThis.MaterialUI };
    })());

  const merge = (a, b) => {
    for (const [k, v] of Object.entries(b ?? {})) {
      a[k] = v && typeof v === "object" && !Array.isArray(v) && a[k] && typeof a[k] === "object" ? merge(a[k], v) : v;
    }
    return a;
  };
  const root = document.documentElement;
  const darkQuery = matchMedia("(prefers-color-scheme: dark)");
  const mode = () => (root.dataset.theme === "dark" || (root.dataset.theme !== "light" && darkQuery.matches) ? "dark" : "light");
  let themeExtra = globalThis.RustzorMuiTheme ?? {};
  let theme = null;
  const makeTheme = (MUI) => {
    const dark = mode() === "dark";
    theme = MUI.createTheme(
      merge(
        {
          palette: {
            mode: dark ? "dark" : "light",
            primary: { main: dark ? "#f97316" : "#c2410c" },
            secondary: { main: dark ? "#2dd4bf" : "#0f766e" },
            ...(dark ? { background: { default: "#14110f", paper: "#1c1916" } } : { background: { default: "#faf8f5", paper: "#ffffff" } }),
          },
          shape: { borderRadius: 10 },
          typography: {
            fontFamily: '"IBM Plex Sans", "Roboto", system-ui, sans-serif',
            button: { textTransform: "none", fontWeight: 600 },
            h1: { fontFamily: '"Bricolage Grotesque", "IBM Plex Sans", sans-serif' },
            h2: { fontFamily: '"Bricolage Grotesque", "IBM Plex Sans", sans-serif' },
            h3: { fontFamily: '"Bricolage Grotesque", "IBM Plex Sans", sans-serif' },
            h4: { fontFamily: '"Bricolage Grotesque", "IBM Plex Sans", sans-serif' },
            h5: { fontFamily: '"Bricolage Grotesque", "IBM Plex Sans", sans-serif' },
            h6: { fontFamily: '"Bricolage Grotesque", "IBM Plex Sans", sans-serif' },
          },
        },
        structuredClone(themeExtra)
      )
    );
    return theme;
  };

  // Moves a Rustzor-owned element into the React tree. React never manages its
  // children, so Rustzor keeps patching it in place.
  let Slot = null;
  const islands = new Set();

  // The value an MUI callback reports: a plain second argument when there is one
  // (Slider, Tabs, Rating, Switch, ToggleButtonGroup), else the input's value.
  // Select passes a React element second, so it falls through to the target.
  const valueOf = (args) => {
    const e = args[0];
    const t = e && e.target;
    const prop = t && t.type === "checkbox" ? "checked" : "value";
    const second = args[1];
    if (args.length > 1 && (second === null || Array.isArray(second) || (typeof second !== "object" && typeof second !== "function" && second !== undefined)))
      return { value: second, prop };
    if (t && t.type === "checkbox") return { value: t.checked, prop };
    if (t && "value" in t) return { value: t.value, prop };
    return { value: e && typeof e === "object" ? null : e ?? null, prop: null };
  };

  const build = (box, React, MUI) => {
    let key = 0;
    const node = (v) => {
      if (Array.isArray(v)) return v.map(node);
      if (!v || typeof v !== "object") return v;
      if ("$slot" in v) return React.createElement(Slot, { key: "slot-" + v.$slot, el: box.slots.get(v.$slot) });
      if ("$mui" in v) {
        const C = MUI[v.$mui];
        if (!C) throw new Error(`Material UI has no component named ${v.$mui}`);
        const p = props(v.props);
        if (p.key === undefined) p.key = "k" + key++;
        return React.createElement(C, p, v.children === undefined ? undefined : node(v.children));
      }
      return v;
    };
    const handler = (name) => (...args) => {
      const { value, prop } = valueOf(args);
      if (prop) {
        box.over.set(name, { prop, value });
        box.last = Date.now();
        render(box);
      }
      dispatch(box.callback, { name, value });
    };
    // Elements, slots and callbacks may sit anywhere in a prop value:
    // InputProps.startAdornment, slotProps.input.onBlur, an array of actions.
    const deep = (v) => {
      if (Array.isArray(v)) return v.map(deep);
      if (!v || typeof v !== "object") return v;
      if ("$mui" in v || "$slot" in v) return node(v);
      if ("$event" in v) return handler(v.$event);
      const out = {};
      for (const [k, x] of Object.entries(v)) out[k] = deep(x);
      return out;
    };
    const props = (p) => {
      const out = {};
      let override = null;
      for (const [k, v] of Object.entries(p ?? {})) {
        if (v && typeof v === "object" && "$event" in v && box.over.has(v.$event)) override = box.over.get(v.$event);
        out[k] = deep(v);
      }
      // The value the user just typed or dragged to, until C# catches up.
      if (override) out[override.prop] = override.value;
      return out;
    };
    return node(box.spec);
  };

  const render = (box) => {
    const { React, MUI } = box.libs;
    box.root.render(React.createElement(MUI.ThemeProvider, { theme: theme ?? makeTheme(MUI) }, build(box, React, MUI)));
  };

  const rerenderAll = () => {
    if (!globalThis.MaterialUI) return;
    makeTheme(globalThis.MaterialUI);
    for (const box of islands) render(box);
  };
  new MutationObserver(rerenderAll).observe(root, { attributes: true, attributeFilter: ["data-theme"] });
  darkQuery.addEventListener("change", rerenderAll);

  const scanSlots = (box) => {
    for (const el of box.host.querySelectorAll(":scope > [data-rz-slot]")) box.slots.set(el.dataset.rzSlot, el);
  };

  ops["mui.create"] = async ({ el, spec, callback }) => {
    const libs = await load();
    const { React, ReactDOM } = libs;
    Slot ??= ({ el: target }) => {
      const ref = React.useRef(null);
      React.useLayoutEffect(() => {
        if (target && ref.current && target.parentNode !== ref.current) ref.current.appendChild(target);
      });
      return React.createElement("span", { ref, style: { display: "contents" } });
    };
    const host = elementOf(el);
    const mount = document.createElement("div");
    mount.style.display = "contents";
    host.appendChild(mount);
    const box = { host, mount, spec, callback, libs, slots: new Map(), over: new Map(), last: 0 };
    scanSlots(box);
    box.root = ReactDOM.createRoot(mount);
    islands.add(box);
    render(box);
    return addHandle(box);
  };
  ops["mui.update"] = ({ h, spec }) => {
    const box = getHandle(h);
    if (!box) return;
    box.spec = spec;
    // Keep the optimistic value while the user is still typing or dragging, so a
    // round trip that lags behind the latest keystroke cannot move the caret back.
    if (Date.now() - box.last > 400) box.over.clear();
    scanSlots(box);
    render(box);
  };
  ops["mui.dispose"] = ({ h }) => {
    const box = getHandle(h);
    if (!box) return;
    islands.delete(box);
    releaseHandle(h);
    for (const slot of box.slots.values()) box.host.appendChild(slot);
    queueMicrotask(() => box.root.unmount());
  };
  ops["mui.theme"] = ({ options }) => {
    themeExtra = merge(themeExtra, options);
    rerenderAll();
  };
}
