// Rupa's browser side: canvas input folded to one message per frame, text
// measuring, image sizes, and export to SVG and PNG. Embedded in the app's
// assembly; Rustzor's bundler ships it like a library's module.
export function install({ ops, dispatch, addHandle, getHandle, elementOf }) {
  const stats = (globalThis.__rupaStats = { moves: [], frames: [], last: null });
  const keep = (list, v) => {
    list.push(v);
    if (list.length > 600) list.shift();
  };

  // ── canvas input ──────────────────────────────────────────────────────
  ops["rupa.attach"] = ({ el, id }) => {
    const host = elementOf(el);
    const pointers = new Map();
    let pending = null; // the newest move, sent once per animation frame
    let wheel = null;
    let pinch = null;
    let raf = 0;
    let lastFrame = 0;
    // Pointer events carry no click count (detail is 0), so double clicks are counted here.
    let lastDown = { t: 0, x: 0, y: 0, n: 0 };
    const rect = () => host.getBoundingClientRect();
    const pos = (e) => {
      const r = rect();
      return { x: e.clientX - r.left, y: e.clientY - r.top };
    };
    const mods = (e) => ({ s: e.shiftKey, c: e.ctrlKey || e.metaKey, a: e.altKey });
    const send = (msg) => dispatch(id, msg);
    const flush = (t) => {
      raf = 0;
      if (pointers.size > 0 && lastFrame) keep(stats.frames, t - lastFrame);
      lastFrame = pointers.size > 0 ? t : 0;
      if (wheel) {
        const w = wheel;
        wheel = null;
        send(w);
      }
      if (pending) {
        const m = pending;
        pending = null;
        const t0 = performance.now();
        send(m);
        if (m.b) keep(stats.moves, performance.now() - t0);
      }
    };
    const schedule = () => {
      if (!raf) raf = requestAnimationFrame(flush);
    };
    const now = () => {
      if (raf) {
        cancelAnimationFrame(raf);
        flush(performance.now());
      }
    };

    const down = (e) => {
      if (e.target.closest("[data-ui]")) return;
      e.preventDefault();
      host.focus({ preventScroll: true });
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY, type: e.pointerType });
      try {
        host.setPointerCapture(e.pointerId);
      } catch {}
      if (pointers.size === 2) {
        // Two fingers: the first finger's gesture is cancelled and the pair pinches.
        now();
        send({ t: "cancel" });
        const [a, b] = [...pointers.values()];
        pinch = { d: Math.hypot(a.x - b.x, a.y - b.y), cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2 };
        return;
      }
      if (pointers.size > 2) return;
      now();
      const p = pos(e);
      const quick = e.timeStamp - lastDown.t < 450 && Math.abs(p.x - lastDown.x) < 6 && Math.abs(p.y - lastDown.y) < 6;
      lastDown = { t: e.timeStamp, x: p.x, y: p.y, n: quick ? lastDown.n + 1 : 1 };
      send({ t: "down", ...p, b: e.button, n: lastDown.n, p: e.pointerType, ...mods(e) });
    };
    const move = (e) => {
      const p = pointers.get(e.pointerId);
      if (p) {
        p.x = e.clientX;
        p.y = e.clientY;
      }
      if (pinch && pointers.size === 2) {
        const [a, b] = [...pointers.values()];
        const d = Math.hypot(a.x - b.x, a.y - b.y);
        const cx = (a.x + b.x) / 2;
        const cy = (a.y + b.y) / 2;
        const r = rect();
        const msg = { t: "pinch", k: d / (pinch.d || 1), x: cx - r.left, y: cy - r.top, dx: cx - pinch.cx, dy: cy - pinch.cy };
        pinch = { d, cx, cy };
        if (wheel && wheel.t === "pinch") {
          msg.k *= wheel.k;
          msg.dx += wheel.dx;
          msg.dy += wheel.dy;
        }
        wheel = msg;
        schedule();
        return;
      }
      if (pinch) return;
      pending = { t: "move", ...pos(e), b: pointers.size > 0, ...mods(e) };
      schedule();
    };
    const up = (e) => {
      if (!pointers.has(e.pointerId)) return;
      pointers.delete(e.pointerId);
      try {
        host.releasePointerCapture(e.pointerId);
      } catch {}
      if (pinch) {
        if (pointers.size === 0) pinch = null;
        return;
      }
      now();
      send({ t: "up", ...pos(e), ...mods(e) });
    };
    const onWheel = (e) => {
      e.preventDefault();
      const k = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? 400 : 1;
      const p = pos(e);
      const ctrl = e.ctrlKey || e.metaKey;
      if (wheel && wheel.t === "wheel" && wheel.c === ctrl) {
        wheel.dx += e.deltaX * k;
        wheel.dy += e.deltaY * k;
        wheel.x = p.x;
        wheel.y = p.y;
      } else {
        if (wheel) now();
        wheel = { t: "wheel", dx: e.deltaX * k, dy: e.deltaY * k, ...p, c: ctrl };
      }
      schedule();
    };
    const leave = () => {
      if (pointers.size === 0) {
        pending = null;
        send({ t: "leave" });
      }
    };
    const over = (e) => {
      if ([...(e.dataTransfer?.items || [])].some((i) => i.kind === "file")) {
        e.preventDefault();
        host.classList.add("dropping");
      }
    };
    const out = () => host.classList.remove("dropping");
    const drop = async (e) => {
      const files = [...(e.dataTransfer?.files || [])].filter((f) => f.type.startsWith("image/"));
      if (!files.length) return;
      e.preventDefault();
      out();
      const p = pos(e);
      send({ t: "drop", ...p, files: await Promise.all(files.map(readImage)) });
    };
    const block = (e) => e.preventDefault();
    host.addEventListener("pointerdown", down);
    host.addEventListener("pointermove", move);
    host.addEventListener("pointerup", up);
    host.addEventListener("pointercancel", up);
    host.addEventListener("pointerleave", leave);
    host.addEventListener("wheel", onWheel, { passive: false });
    host.addEventListener("contextmenu", block);
    host.addEventListener("dragover", over);
    host.addEventListener("dragleave", out);
    host.addEventListener("drop", drop);
    // Safari's own pinch events would zoom the page.
    host.addEventListener("gesturestart", block);
    const ro = new ResizeObserver(() => {
      const r = rect();
      send({ t: "size", w: r.width, h: r.height });
    });
    ro.observe(host);
    const r = rect();
    send({ t: "size", w: r.width, h: r.height });
    return addHandle(() => {
      ro.disconnect();
      host.removeEventListener("pointerdown", down);
      host.removeEventListener("pointermove", move);
      host.removeEventListener("pointerup", up);
      host.removeEventListener("pointercancel", up);
      host.removeEventListener("pointerleave", leave);
      host.removeEventListener("wheel", onWheel);
      host.removeEventListener("contextmenu", block);
      host.removeEventListener("dragover", over);
      host.removeEventListener("dragleave", out);
      host.removeEventListener("drop", drop);
      host.removeEventListener("gesturestart", block);
    });
  };
  ops["rupa.detach"] = ({ h }) => {
    const off = getHandle(h);
    if (off) off();
  };

  // ── keyboard and paste, page-wide while the editor is open ─────────────
  // Shortcuts the browser would otherwise take (Ctrl+D bookmarks, Ctrl+G finds).
  const taken = new Set(["KeyZ", "KeyY", "KeyG", "KeyD", "KeyA", "KeyS", "KeyO", "KeyK", "KeyH", "KeyL", "KeyE", "BracketLeft", "BracketRight", "Equal", "Minus", "Digit0"]);
  const editable = (t) => t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName));
  ops["rupa.keys"] = ({ id }) => {
    const send = (msg) => dispatch(id, msg);
    const down = (e) => {
      if (editable(e.target)) return;
      const ctrl = e.ctrlKey || e.metaKey;
      if ((ctrl && taken.has(e.code)) || e.key === " " || e.key.startsWith("Arrow") || e.key === "Backspace" || (e.key === "Tab" && !ctrl)) e.preventDefault();
      if (ctrl && (e.code === "KeyC" || e.code === "KeyX" || e.code === "KeyV")) {
        // Leave these to copy/cut/paste events when text is selected on the page.
        if (String(getSelection() || "").length) return;
        if (e.code === "KeyV") return; // the paste event below carries images too
      }
      if (e.repeat && e.key === " ") return;
      send({ t: "kd", k: e.key, c: e.code, ctrl, s: e.shiftKey, a: e.altKey });
    };
    const up = (e) => {
      if (e.key === " ") send({ t: "ku", k: e.key });
    };
    const paste = async (e) => {
      if (editable(e.target)) return;
      const files = [...(e.clipboardData?.files || [])].filter((f) => f.type.startsWith("image/"));
      e.preventDefault();
      if (files.length) send({ t: "image", files: await Promise.all(files.map(readImage)) });
      else send({ t: "kd", k: "v", c: "KeyV", ctrl: true, s: false, a: false });
    };
    const blur = () => send({ t: "ku", k: " " });
    addEventListener("keydown", down);
    addEventListener("keyup", up);
    addEventListener("paste", paste);
    addEventListener("blur", blur);
    return addHandle(() => {
      removeEventListener("keydown", down);
      removeEventListener("keyup", up);
      removeEventListener("paste", paste);
      removeEventListener("blur", blur);
    });
  };

  // ── dragging ──────────────────────────────────────────────────────────
  // While layers are moved, the browser shifts them itself, one attribute per
  // layer per frame; C# keeps the model and redraws the views once, on release.
  let dragged = [];
  ops["rupa.dragBegin"] = ({ ids }) => {
    const world = document.querySelector(".rp-world");
    dragged = [];
    for (const id of ids) {
      const el = world && world.querySelector(`[data-n="${id}"]:not([data-i])`);
      if (el) dragged.push([el, el.getAttribute("transform") || ""]);
    }
  };
  ops["rupa.drag"] = ({ dx, dy }) => {
    const t = `translate(${dx} ${dy}) `;
    for (const [el, base] of dragged) el.setAttribute("transform", t + base);
  };
  ops["rupa.dragEnd"] = ({ reset }) => {
    if (reset) for (const [el, base] of dragged) el.setAttribute("transform", base);
    dragged = [];
  };

  // The editor's state for scripted checks (tools/e2e/rupa.mjs): the call goes to
  // C# and back synchronously, so the answer is there when it returns.
  ops["rupa.inspect"] = ({ id }) => {
    globalThis.__rupaInspect = () => {
      globalThis.__rupaAnswer = null;
      dispatch(id, { t: "inspect" });
      return globalThis.__rupaAnswer;
    };
  };
  ops["rupa.answer"] = ({ json }) => {
    globalThis.__rupaAnswer = JSON.parse(json);
  };

  ops["rupa.focusRename"] = () => {
    const input = document.querySelector(".rp-rename");
    if (input) {
      input.focus();
      input.select();
    }
  };

  // ── text and images ───────────────────────────────────────────────────
  const ctx = document.createElement("canvas").getContext("2d");
  ops["rupa.measure"] = ({ font, size, weight, tracking, text }) => {
    ctx.font = `${weight} ${size}px "${font}"`;
    if ("letterSpacing" in ctx) ctx.letterSpacing = `${tracking}px`;
    const lines = String(text).split("\n");
    let w = 0;
    for (const l of lines) w = Math.max(w, ctx.measureText(l).width);
    return { w, n: lines.length };
  };
  ops["rupa.fonts"] = async () => {
    await document.fonts.ready;
    const want = ["Plus Jakarta Sans", "Instrument Sans", "Fraunces", "Geist Mono"];
    await Promise.all(
      want.flatMap((f) => [400, 500, 600, 700, 800].map((w) => document.fonts.load(`${w} 16px "${f}"`).catch(() => null)))
    );
    return true;
  };
  const readImage = (file) =>
    new Promise((resolve) => {
      const r = new FileReader();
      r.onload = () => {
        const img = new Image();
        img.onload = () => resolve({ name: file.name, url: r.result, w: img.naturalWidth, h: img.naturalHeight });
        img.onerror = () => resolve({ name: file.name, url: r.result, w: 200, h: 200 });
        img.src = r.result;
      };
      r.readAsDataURL(file);
    });
  ops["rupa.imageSize"] = ({ url }) =>
    new Promise((resolve) => {
      const img = new Image();
      img.onload = () => resolve({ w: img.naturalWidth, h: img.naturalHeight });
      img.onerror = () => resolve({ w: 200, h: 200 });
      img.src = url;
    });

  // ── export ────────────────────────────────────────────────────────────
  // A layer's drawing is the same SVG the canvas shows: cloned from the page,
  // placed by its parent's transform, cropped to its bounds.
  const SVGNS = "http://www.w3.org/2000/svg";
  const fontCss = new Map();
  ops["rupa.svg"] = ({ ids, matrices, x, y, w, h, fonts }) => {
    const world = document.querySelector(".rp-world");
    const svg = document.createElementNS(SVGNS, "svg");
    svg.setAttribute("xmlns", SVGNS);
    svg.setAttribute("width", String(w));
    svg.setAttribute("height", String(h));
    svg.setAttribute("viewBox", `${x} ${y} ${w} ${h}`);
    svg.setAttribute("fill", "none");
    if (fonts && fonts.length) {
      const style = document.createElementNS(SVGNS, "style");
      style.textContent = `@import url("https://fonts.googleapis.com/css2?${fonts.map((f) => "family=" + f.replace(/ /g, "+") + ":wght@400;500;600;700;800").join("&")}&display=swap");`;
      svg.appendChild(style);
    }
    ids.forEach((id, i) => {
      const src = world.querySelector(`[data-n="${id}"]:not([data-i])`);
      if (!src) return;
      const g = document.createElementNS(SVGNS, "g");
      if (matrices[i]) g.setAttribute("transform", matrices[i]);
      const copy = src.cloneNode(true);
      for (const el of [copy, ...copy.querySelectorAll("*")]) {
        el.removeAttribute("data-n");
        el.removeAttribute("data-i");
        el.removeAttribute("class");
      }
      g.appendChild(copy);
      svg.appendChild(g);
    });
    const text = new XMLSerializer().serializeToString(svg);
    stats.last = { kind: "svg", size: text.length, text };
    return text;
  };

  // Fonts are inlined so the PNG draws with them, not a fallback.
  async function inlineFonts(svgText, fonts) {
    if (!fonts.length) return svgText;
    let css = "";
    for (const f of fonts) {
      if (!fontCss.has(f)) {
        try {
          const url = `https://fonts.googleapis.com/css2?family=${f.replace(/ /g, "+")}:wght@400;500;600;700;800&display=swap`;
          let sheet = await (await fetch(url)).text();
          const files = [...sheet.matchAll(/url\((https:[^)]+)\)/g)].map((m) => m[1]);
          for (const file of new Set(files)) {
            const blob = await (await fetch(file)).blob();
            const data = await new Promise((r) => {
              const fr = new FileReader();
              fr.onload = () => r(fr.result);
              fr.readAsDataURL(blob);
            });
            sheet = sheet.split(file).join(data);
          }
          fontCss.set(f, sheet);
        } catch {
          fontCss.set(f, "");
        }
      }
      css += fontCss.get(f);
    }
    return svgText.replace(/<style>[^<]*<\/style>/, `<style>${css.replace(/</g, "")}</style>`);
  }

  ops["rupa.png"] = async ({ svg, w, h, scale, fonts }) => {
    const text = await inlineFonts(svg, fonts || []);
    const url = URL.createObjectURL(new Blob([text], { type: "image/svg+xml" }));
    try {
      const img = new Image();
      await new Promise((resolve, reject) => {
        img.onload = resolve;
        img.onerror = () => reject(new Error("The drawing could not be rasterised."));
        img.src = url;
      });
      const c = document.createElement("canvas");
      c.width = Math.max(1, Math.round(w * scale));
      c.height = Math.max(1, Math.round(h * scale));
      const g = c.getContext("2d");
      g.drawImage(img, 0, 0, c.width, c.height);
      const data = c.toDataURL("image/png");
      stats.last = { kind: "png", size: Math.round((data.length - 22) * 0.75), width: c.width, height: c.height, data };
      return { url: data, bytes: stats.last.size, width: c.width, height: c.height };
    } finally {
      URL.revokeObjectURL(url);
    }
  };
}
