// Siaga Citarum's map engine. C# owns the layers (which, in what order, how
// styled), the attributes, the user's own features, the tools and every
// measurement and query; this module owns the drawing: it loads GeoJSON,
// projects it once to Web Mercator, keeps each layer as Path2D objects in
// metres, and draws them with one transform per frame, so panning and zooming
// never cross into C#. It also does what must answer within a frame: hit
// testing under the pointer, the sketch rubber band, vertex handles, inertia.
// Embedded in the app's assembly; Rustzor's bundler ships it.
export function install({ ops, dispatch, addHandle, getHandle, elementOf }) {
  const R = 6378137;
  const D2R = Math.PI / 180;
  const ORIGIN = merc(107.64, -6.99);
  const stats = (globalThis.__sgStats = { frames: [], intervals: [], last: 0 });

  /** Longitude/latitude to Web Mercator metres. */
  function merc(lon, lat) {
    const la = Math.max(-85.05112878, Math.min(85.05112878, lat));
    return [R * lon * D2R, R * Math.log(Math.tan(Math.PI / 4 + (la * D2R) / 2))];
  }
  /** Longitude/latitude to the map's local metres (origin at Dayeuhkolot, y down). */
  function toLocal(lon, lat) {
    const [x, y] = merc(lon, lat);
    return [x - ORIGIN[0], ORIGIN[1] - y];
  }
  function toLonLat(X, Y) {
    const x = X + ORIGIN[0], y = ORIGIN[1] - Y;
    return [x / R / D2R, (2 * Math.atan(Math.exp(y / R)) - Math.PI / 2) / D2R];
  }
  const haversine = (a, b) => {
    const s = Math.sin(((b[1] - a[1]) * D2R) / 2) ** 2 + Math.cos(a[1] * D2R) * Math.cos(b[1] * D2R) * Math.sin(((b[0] - a[0]) * D2R) / 2) ** 2;
    return 2 * 6371008.8 * Math.asin(Math.sqrt(s));
  };

  // ── state ────────────────────────────────────────────────────────────
  const M = {
    host: null, canvas: null, ctx: null, w: 0, h: 0, dpr: 1,
    cx: 0, cy: 0, s: 0.05, // view centre (local metres) and pixels per metre
    layers: new Map(), // id -> { id, kind, features, style, visible, opacity, groups, labels }
    order: [],
    sel: new Map(), // layer -> Set(ids)
    mode: "identify",
    sketch: [], cursor: null,
    edit: null, // { layer, id, kind, pts: [[X,Y]], drag }
    buffer: null, // { kind, pts, meters }
    locate: null,
    tiles: { on: false, cache: new Map(), failed: 0, loaded: 0 },
    cb: 0, coordsEl: null, scaleEl: null, ratioEl: null, zoomEl: null,
    dirty: false, colors: new Map(),
    inertia: null,
  };
  globalThis.__sg = {
    M,
    toLocal, toLonLat, merc,
    toScreen: (lon, lat) => {
      const [X, Y] = toLocal(lon, lat);
      const r = M.canvas.getBoundingClientRect();
      return { x: r.left + (X - M.cx) * M.s + M.w / 2, y: r.top + (Y - M.cy) * M.s + M.h / 2 };
    },
    view: () => viewInfo(),
    layerCount: (id) => M.layers.get(id)?.features.length ?? -1,
    selected: () => [...M.sel].map(([layer, ids]) => ({ layer, ids: [...ids] })),
    stats,
  };
  const send = (m) => M.cb && dispatch(M.cb, m);
  const invalidate = () => {
    if (M.dirty || !M.ctx) return;
    M.dirty = true;
    requestAnimationFrame(draw);
  };

  // ── colours: C# styles may use CSS custom properties ─────────────────
  function color(c) {
    if (!c) return "transparent";
    if (!c.startsWith("var(")) return c;
    let v = M.colors.get(c);
    if (v === undefined) {
      const name = c.slice(4, -1).split(",")[0].trim();
      v = getComputedStyle(M.host).getPropertyValue(name).trim() || "#888";
      M.colors.set(c, v);
    }
    return v;
  }
  function themeChanged() {
    M.colors.clear();
    for (const L of M.layers.values()) L.groups = null;
    invalidate();
  }

  // ── data ─────────────────────────────────────────────────────────────
  function geomToLocal(g) {
    const ring = (r) => r.map(([x, y]) => toLocal(x, y));
    switch (g.type) {
      case "Point": return { t: "pt", p: toLocal(g.coordinates[0], g.coordinates[1]) };
      case "MultiPoint": return { t: "pt", p: toLocal(g.coordinates[0][0], g.coordinates[0][1]) };
      case "LineString": return { t: "ln", parts: [ring(g.coordinates)] };
      case "MultiLineString": return { t: "ln", parts: g.coordinates.map(ring) };
      case "Polygon": return { t: "pg", polys: [g.coordinates.map(ring)] };
      case "MultiPolygon": return { t: "pg", polys: g.coordinates.map((p) => p.map(ring)) };
      default: return null;
    }
  }
  function bboxOf(geo) {
    let a = Infinity, b = Infinity, c = -Infinity, d = -Infinity;
    const eat = ([x, y]) => {
      if (x < a) a = x;
      if (y < b) b = y;
      if (x > c) c = x;
      if (y > d) d = y;
    };
    if (geo.t === "pt") eat(geo.p);
    else if (geo.t === "ln") geo.parts.forEach((p) => p.forEach(eat));
    else geo.polys.forEach((poly) => poly.forEach((r) => r.forEach(eat)));
    return [a, b, c, d];
  }
  /** A point inside the shape for its label: the centroid of the largest ring, or a vertex. */
  function labelPoint(geo) {
    if (geo.t === "pt") return geo.p;
    if (geo.t === "ln") {
      const longest = geo.parts.reduce((m, p) => (p.length > m.length ? p : m), geo.parts[0]);
      return longest[Math.floor(longest.length / 2)];
    }
    let best = null, bestA = 0;
    for (const poly of geo.polys) {
      const r = poly[0];
      let A = 0, x = 0, y = 0;
      for (let i = 0, j = r.length - 1; i < r.length; j = i++) {
        const f = r[j][0] * r[i][1] - r[i][0] * r[j][1];
        A += f;
        x += (r[j][0] + r[i][0]) * f;
        y += (r[j][1] + r[i][1]) * f;
      }
      if (Math.abs(A) > bestA) {
        bestA = Math.abs(A);
        best = [x / (3 * A), y / (3 * A)];
      }
    }
    return best ?? geo.polys[0][0][0];
  }
  function ingest(id, kind, fc) {
    const features = [];
    for (const f of fc.features ?? []) {
      if (!f.geometry) continue;
      const geo = geomToLocal(f.geometry);
      if (!geo) continue;
      const fid = String(f.id ?? f.properties?.id ?? features.length);
      features.push({ id: fid, props: f.properties ?? {}, geo, bbox: bboxOf(geo), lp: labelPoint(geo) });
    }
    let L = M.layers.get(id);
    if (!L) M.layers.set(id, (L = { id, kind, visible: true, opacity: 1, style: {}, features }));
    L.kind = kind || L.kind;
    L.features = features;
    L.byId = new Map(features.map((f) => [f.id, f]));
    L.groups = null;
    invalidate();
    return L;
  }
  function attributes(L) {
    return L.features.map((f) => {
      const [lon, lat] = toLonLat(f.lp[0], f.lp[1]);
      const [a, b] = toLonLat(f.bbox[0], f.bbox[3]);
      const [c, d] = toLonLat(f.bbox[2], f.bbox[1]);
      return { id: f.id, p: f.props, c: [round6(lon), round6(lat)], b: [round6(a), round6(b), round6(c), round6(d)] };
    });
  }
  const round6 = (v) => Math.round(v * 1e6) / 1e6;

  // ── styling: features grouped by fill so a layer is a few Path2D draws ──
  function styleOf(L, f) {
    const s = L.style;
    let fill = s.fill, stroke = s.stroke, width = s.width ?? 1;
    const v = s.field ? f.props[s.field] : undefined;
    if (s.mode === "categorized" && s.cats) {
      const c = s.cats[String(v)] ?? s.other;
      if (f.geo.t === "ln") stroke = c ?? stroke;
      else fill = c ?? fill;
    } else if (s.mode === "graduated" && s.breaks && s.colors) {
      let k = 0;
      const n = Number(v);
      while (k < s.breaks.length && n > s.breaks[k]) k++;
      const c = s.colors[Math.min(k, s.colors.length - 1)];
      if (f.geo.t === "ln") stroke = c;
      else fill = c;
    }
    let minor = !!s.emphasis;
    if (s.emphasis && f.props[s.emphasis.field] == s.emphasis.value) {
      width = s.emphasis.width;
      stroke = s.emphasis.stroke ?? stroke;
      minor = false;
    }
    return { fill, stroke, width, minor };
  }
  function buildGroups(L) {
    const groups = new Map();
    for (const f of L.features) {
      const st = styleOf(L, f);
      const key = st.fill + "|" + st.stroke + "|" + st.width + "|" + st.minor;
      let g = groups.get(key);
      if (!g) groups.set(key, (g = { ...st, poly: null, line: null, pts: [] }));
      if (f.geo.t === "pt") g.pts.push(f);
      else if (f.geo.t === "ln") for (const part of f.geo.parts) addLine((g.line ??= new Path2D()), part);
      else for (const poly of f.geo.polys) for (const r of poly) addLine((g.poly ??= new Path2D()), r, true);
    }
    L.groups = [...groups.values()];
  }
  function addLine(path, pts, close) {
    path.moveTo(pts[0][0], pts[0][1]);
    for (let i = 1; i < pts.length; i++) path.lineTo(pts[i][0], pts[i][1]);
    if (close) path.closePath();
  }

  // ── drawing ──────────────────────────────────────────────────────────
  function draw() {
    M.dirty = false;
    const t0 = performance.now();
    if (stats.last) {
      stats.intervals.push(t0 - stats.last);
      if (stats.intervals.length > 600) stats.intervals.shift();
    }
    stats.last = t0;
    const { ctx, w, h, dpr, s } = M;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = color("var(--map-ground)");
    ctx.fillRect(0, 0, w, h);
    if (M.tiles.on) drawTiles();
    const world = () => ctx.setTransform(dpr * s, 0, 0, dpr * s, dpr * (w / 2 - M.cx * s), dpr * (h / 2 - M.cy * s));
    const view = visibleBox();
    const zoom = zoomLevel();
    const labels = [];
    for (const id of M.order) {
      const L = M.layers.get(id);
      if (!L || !L.visible || !L.features.length) continue;
      if (L.style.minZoom && zoom < L.style.minZoom) continue;
      ctx.globalAlpha = L.opacity;
      if (L.style.mode === "heat") {
        drawHeat(L);
        ctx.globalAlpha = 1;
        continue;
      }
      if (!L.groups) buildGroups(L);
      world();
      ctx.lineJoin = "round";
      ctx.lineCap = "round";
      for (const g of L.groups) {
        if (g.minor && L.style.minorZoom && zoom < L.style.minorZoom) continue;
        if (g.poly) {
          if (g.fill && g.fill !== "none") {
            ctx.fillStyle = color(g.fill);
            ctx.fill(g.poly, "evenodd");
          }
          if (g.stroke && g.stroke !== "none") {
            ctx.strokeStyle = color(g.stroke);
            ctx.lineWidth = g.width / s;
            if (L.style.dash) ctx.setLineDash(L.style.dash.map((d) => d / s));
            ctx.stroke(g.poly);
            ctx.setLineDash([]);
          }
        }
        if (g.line) {
          if (L.style.casing) {
            ctx.strokeStyle = color(L.style.casing);
            ctx.lineWidth = (g.width + 2) / s;
            ctx.stroke(g.line);
          }
          ctx.strokeStyle = color(g.stroke);
          ctx.lineWidth = g.width / s;
          if (L.style.dash) ctx.setLineDash(L.style.dash.map((d) => d / s));
          ctx.stroke(g.line);
          ctx.setLineDash([]);
        }
      }
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      for (const g of L.groups) if (g.pts.length) drawPoints(L, g, view);
      ctx.globalAlpha = 1;
      if (L.style.label && zoom >= (L.style.labelZoom ?? 0)) collectLabels(L, view, labels);
    }
    ctx.globalAlpha = 1;
    drawSheetEdge();
    drawSelection();
    drawOverlays();
    placeLabels(labels);
    drawCollar();
    const dt = performance.now() - t0;
    stats.frames.push(dt);
    if (stats.frames.length > 600) stats.frames.shift();
    updateScale();
    if (M.inertia) stepInertia();
  }
  function visibleBox() {
    const hw = M.w / 2 / M.s, hh = M.h / 2 / M.s;
    return [M.cx - hw, M.cy - hh, M.cx + hw, M.cy + hh];
  }
  const inBox = (b, v, pad = 0) => !(b[2] < v[0] - pad || b[0] > v[2] + pad || b[3] < v[1] - pad || b[1] > v[3] + pad);
  const screen = (p) => [(p[0] - M.cx) * M.s + M.w / 2, (p[1] - M.cy) * M.s + M.h / 2];
  function symbol(ctx, shape, x, y, r) {
    ctx.beginPath();
    if (shape === "square") ctx.rect(x - r, y - r, r * 2, r * 2);
    else if (shape === "diamond") {
      ctx.moveTo(x, y - r * 1.3);
      ctx.lineTo(x + r * 1.3, y);
      ctx.lineTo(x, y + r * 1.3);
      ctx.lineTo(x - r * 1.3, y);
      ctx.closePath();
    } else if (shape === "triangle") {
      ctx.moveTo(x, y - r * 1.3);
      ctx.lineTo(x + r * 1.2, y + r * 0.9);
      ctx.lineTo(x - r * 1.2, y + r * 0.9);
      ctx.closePath();
    } else if (shape === "cross") {
      const t = r * 0.42;
      ctx.rect(x - t, y - r, t * 2, r * 2);
      ctx.rect(x - r, y - t, r * 2, t * 2);
    } else ctx.arc(x, y, r, 0, Math.PI * 2);
  }
  function drawPoints(L, g, view) {
    const ctx = M.ctx;
    const r = (L.style.size ?? 8) / 2;
    ctx.fillStyle = color(g.fill);
    ctx.strokeStyle = color(g.stroke ?? "var(--map-halo)");
    ctx.lineWidth = L.style.width ?? 1.5;
    const pad = r / M.s;
    for (const f of g.pts) {
      if (!inBox(f.bbox, view, pad)) continue;
      const [x, y] = screen(f.geo.p);
      symbol(ctx, L.style.shape, x, y, r);
      ctx.fill();
      if (g.stroke !== "none") ctx.stroke();
    }
  }
  function drawHeat(L) {
    const ctx = M.ctx;
    const q = 3; // a third of the resolution is plenty for a blur
    const w = Math.ceil(M.w / q), h = Math.ceil(M.h / q);
    if (!M.heat || M.heat.width !== w || M.heat.height !== h) {
      M.heat = Object.assign(document.createElement("canvas"), { width: w, height: h });
      M.heatCtx = M.heat.getContext("2d", { willReadFrequently: true });
    }
    const hc = M.heatCtx;
    hc.clearRect(0, 0, w, h);
    const radius = Math.max(5, Math.min(34, (L.style.radius ?? 400) * M.s)) / q;
    const field = L.style.field, max = L.style.max ?? 1;
    for (const f of L.features) {
      const [x, y] = screen(f.geo.p);
      const sx = x / q, sy = y / q;
      if (sx < -radius || sy < -radius || sx > w + radius || sy > h + radius) continue;
      const wgt = field ? Math.min(1, Number(f.props[field]) / max) : 0.5;
      const grad = hc.createRadialGradient(sx, sy, 0, sx, sy, radius);
      grad.addColorStop(0, `rgba(0,0,0,${0.25 + wgt * 0.5})`);
      grad.addColorStop(1, "rgba(0,0,0,0)");
      hc.fillStyle = grad;
      hc.fillRect(sx - radius, sy - radius, radius * 2, radius * 2);
    }
    const img = hc.getImageData(0, 0, w, h);
    const d = img.data;
    const ramp = heatRamp();
    for (let i = 3; i < d.length; i += 4) {
      const a = d[i];
      if (!a) continue;
      const k = Math.min(255, a) * 4;
      d[i - 3] = ramp[k];
      d[i - 2] = ramp[k + 1];
      d[i - 1] = ramp[k + 2];
      d[i] = Math.min(185, a * 1.5);
    }
    hc.putImageData(img, 0, 0);
    ctx.setTransform(M.dpr, 0, 0, M.dpr, 0, 0);
    ctx.imageSmoothingEnabled = true;
    ctx.drawImage(M.heat, 0, 0, w * q, h * q);
  }
  let rampCache = null;
  function heatRamp() {
    if (rampCache) return rampCache;
    const c = Object.assign(document.createElement("canvas"), { width: 256, height: 1 });
    const g = c.getContext("2d");
    const grad = g.createLinearGradient(0, 0, 256, 0);
    grad.addColorStop(0, "#2a7ab8");
    grad.addColorStop(0.35, "#39b3a6");
    grad.addColorStop(0.6, "#e8c43a");
    grad.addColorStop(0.8, "#ec7a1c");
    grad.addColorStop(1, "#c4231c");
    g.fillStyle = grad;
    g.fillRect(0, 0, 256, 1);
    return (rampCache = g.getImageData(0, 0, 256, 1).data);
  }

  // ── selection, sketch, edit handles, buffer, my location ────────────────
  function drawSelection() {
    const ctx = M.ctx;
    for (const [lid, ids] of M.sel) {
      const L = M.layers.get(lid);
      if (!L || !L.visible) continue;
      for (const id of ids) {
        const f = L.byId?.get(id);
        if (!f) continue;
        outline(f.geo, color("var(--map-select)"), true);
      }
    }
    void ctx;
  }
  function outline(geo, col, fillToo) {
    const ctx = M.ctx;
    ctx.setTransform(M.dpr, 0, 0, M.dpr, 0, 0);
    ctx.strokeStyle = col;
    ctx.lineJoin = "round";
    if (geo.t === "pt") {
      const [x, y] = screen(geo.p);
      ctx.lineWidth = 3;
      ctx.beginPath();
      ctx.arc(x, y, 11, 0, Math.PI * 2);
      ctx.stroke();
      return;
    }
    const path = new Path2D();
    if (geo.t === "ln") geo.parts.forEach((p) => addScreen(path, p));
    else geo.polys.forEach((poly) => poly.forEach((r) => addScreen(path, r, true)));
    if (fillToo && geo.t === "pg") {
      ctx.fillStyle = col + "33";
      ctx.fill(path, "evenodd");
    }
    ctx.lineWidth = geo.t === "ln" ? 5 : 3;
    ctx.stroke(path);
  }
  function addScreen(path, pts, close) {
    const [x0, y0] = screen(pts[0]);
    path.moveTo(x0, y0);
    for (let i = 1; i < pts.length; i++) {
      const [x, y] = screen(pts[i]);
      path.lineTo(x, y);
    }
    if (close) path.closePath();
  }
  function drawOverlays() {
    const ctx = M.ctx;
    ctx.setTransform(M.dpr, 0, 0, M.dpr, 0, 0);
    const acc = color("var(--map-sketch)");
    if (M.buffer) {
      const b = M.buffer;
      // The buffer is the line drawn as wide as the distance on each side,
      // with round ends: exactly the set of points within that distance.
      const lat = toLonLat(b.pts[0][0], b.pts[0][1])[1];
      const wpx = (2 * b.meters * M.s) / Math.cos(lat * D2R);
      ctx.save();
      ctx.globalAlpha = 0.22;
      ctx.strokeStyle = acc;
      ctx.fillStyle = acc;
      ctx.lineCap = ctx.lineJoin = "round";
      ctx.lineWidth = wpx;
      const p = new Path2D();
      addScreen(p, b.pts.length === 1 ? [b.pts[0], b.pts[0]] : b.pts, b.kind === "polygon");
      if (b.kind === "polygon") ctx.fill(p);
      ctx.stroke(p);
      ctx.restore();
    }
    if (M.sketch.length) {
      const pts = M.cursor && M.mode !== "point" ? [...M.sketch, M.cursor] : M.sketch;
      const closed = M.mode === "polygon" || M.mode === "measure-area" || M.mode === "select-area";
      const p = new Path2D();
      addScreen(p, pts, closed);
      if (closed && pts.length > 2) {
        ctx.fillStyle = acc + "2e";
        ctx.fill(p);
      }
      ctx.strokeStyle = acc;
      ctx.lineWidth = 2;
      ctx.setLineDash([6, 4]);
      ctx.stroke(p);
      ctx.setLineDash([]);
      for (const q of M.sketch) handle(screen(q), acc, 4.5);
      if (M.cursor && M.sketch.length) {
        // A live distance for the segment being drawn; C# measures the result.
        const a = toLonLat(...M.sketch[M.sketch.length - 1]), b = toLonLat(...M.cursor);
        const [x, y] = screen(M.cursor);
        tag(fmtLen(haversine(a, b)), x + 14, y - 12);
      }
    }
    if (M.edit) {
      const e = M.edit;
      const p = new Path2D();
      addScreen(p, e.pts, e.kind === "polygon");
      ctx.strokeStyle = acc;
      ctx.lineWidth = 2;
      ctx.stroke(p);
      for (const m of midpoints(e)) handle(screen(m.p), acc, 3.5, true);
      e.pts.forEach((q) => handle(screen(q), acc, 5.5));
    }
    if (M.locate) {
      const [x, y] = screen(M.locate.p);
      const lat = toLonLat(...M.locate.p)[1];
      const r = (M.locate.acc * M.s) / Math.cos(lat * D2R);
      ctx.fillStyle = "rgba(47,111,181,0.15)";
      ctx.beginPath();
      ctx.arc(x, y, Math.max(r, 8), 0, Math.PI * 2);
      ctx.fill();
      handle([x, y], "#2f6fb5", 6);
    }
  }
  function handle([x, y], col, r, hollow) {
    const ctx = M.ctx;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fillStyle = hollow ? color("var(--map-halo)") : col;
    ctx.fill();
    ctx.lineWidth = 2;
    ctx.strokeStyle = hollow ? col : color("var(--map-halo)");
    ctx.stroke();
  }
  function tag(text, x, y) {
    const ctx = M.ctx;
    ctx.font = "600 11px 'IBM Plex Mono', monospace";
    const w = ctx.measureText(text).width + 10;
    ctx.fillStyle = color("var(--map-ink)");
    ctx.fillRect(x, y - 9, w, 18);
    ctx.fillStyle = color("var(--map-ground)");
    ctx.textBaseline = "middle";
    ctx.fillText(text, x + 5, y);
  }
  const fmtLen = (m) => (m >= 1000 ? (m / 1000).toFixed(2) + " km" : Math.round(m) + " m");
  function midpoints(e) {
    const out = [];
    const n = e.pts.length;
    const last = e.kind === "polygon" ? n : n - 1;
    for (let i = 0; i < last; i++) {
      const a = e.pts[i], b = e.pts[(i + 1) % n];
      out.push({ i: i + 1, p: [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2] });
    }
    return out;
  }

  // ── labels, with a simple collision check ──────────────────────────────
  function collectLabels(L, view, out) {
    const s = L.style;
    for (const f of L.features) {
      if (!inBox(f.bbox, view)) continue;
      const text = f.props[s.label];
      if (text === undefined || text === null || text === "") continue;
      const [x, y] = screen(f.lp);
      if (x < -50 || y < -20 || x > M.w + 50 || y > M.h + 20) continue;
      out.push({ text: String(text), x, y, kind: L.kind, font: s.labelFont ?? "", color: s.labelColor ?? "var(--map-ink)", prio: s.labelPrio ?? 1, dx: L.kind === "point" ? (s.size ?? 8) / 2 + 4 : 0 });
    }
  }
  function placeLabels(labels) {
    const ctx = M.ctx;
    ctx.setTransform(M.dpr, 0, 0, M.dpr, 0, 0);
    labels.sort((a, b) => b.prio - a.prio);
    const placed = [];
    ctx.textBaseline = "middle";
    ctx.lineJoin = "round";
    for (const l of labels) {
      ctx.font = l.font || "600 11px 'Public Sans', sans-serif";
      const w = ctx.measureText(l.text).width;
      const x = l.kind === "point" ? l.x + l.dx : l.x - w / 2;
      const box = [x - 2, l.y - 8, x + w + 2, l.y + 8];
      if (placed.some((p) => !(box[2] < p[0] || box[0] > p[2] || box[3] < p[1] || box[1] > p[3]))) continue;
      placed.push(box);
      ctx.strokeStyle = color("var(--map-halo)");
      ctx.lineWidth = 3.5;
      ctx.strokeText(l.text, x, l.y);
      ctx.fillStyle = color(l.color);
      ctx.fillText(l.text, x, l.y);
    }
  }

  // ── the sheet's edge: what lies outside the mapped area is shaded, with a neatline ──
  const SHEET = [107.3, -7.28, 107.98, -6.72];
  function drawSheetEdge() {
    const ctx = M.ctx;
    ctx.setTransform(M.dpr, 0, 0, M.dpr, 0, 0);
    const [x0, y0] = screen(toLocal(SHEET[0], SHEET[3]));
    const [x1, y1] = screen(toLocal(SHEET[2], SHEET[1]));
    if (x0 <= 0 && y0 <= 0 && x1 >= M.w && y1 >= M.h) return;
    ctx.fillStyle = color("var(--map-outside)");
    ctx.beginPath();
    ctx.rect(0, 0, M.w, M.h);
    ctx.rect(x1, y0, x0 - x1, y1 - y0); // the other way round: a hole
    ctx.fill("evenodd");
    ctx.strokeStyle = color("var(--map-ink)");
    ctx.lineWidth = 1.5;
    ctx.strokeRect(x0, y0, x1 - x0, y1 - y0);
    ctx.lineWidth = 0.6;
    ctx.strokeRect(x0 - 4, y0 - 4, x1 - x0 + 8, y1 - y0 + 8);
  }

  // ── the map collar: graticule ticks in degrees and minutes ────────────
  function drawCollar() {
    const ctx = M.ctx;
    ctx.setTransform(M.dpr, 0, 0, M.dpr, 0, 0);
    const band = 18;
    const ink = color("var(--map-ink)"), paper = color("var(--map-collar)");
    ctx.fillStyle = paper;
    ctx.fillRect(0, 0, M.w, band);
    ctx.fillRect(0, 0, band, M.h);
    ctx.strokeStyle = ink;
    ctx.lineWidth = 1;
    ctx.strokeRect(band + 0.5, band + 0.5, M.w - band - 1, M.h - band - 1);
    const [lonL, latT] = toLonLat(M.cx - (M.w / 2 - band) / M.s, M.cy - (M.h / 2 - band) / M.s);
    const [lonR, latB] = toLonLat(M.cx + M.w / 2 / M.s, M.cy + M.h / 2 / M.s);
    const spanMin = (lonR - lonL) * 60;
    const steps = [0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
    const step = steps.find((st) => spanMin / st <= Math.max(3, M.w / 130)) ?? 600;
    ctx.font = "500 10px 'IBM Plex Mono', monospace";
    ctx.fillStyle = ink;
    ctx.textBaseline = "middle";
    for (let m = Math.ceil((lonL * 60) / step) * step; m <= lonR * 60; m += step) {
      const x = (toLocal(m / 60, latT)[0] - M.cx) * M.s + M.w / 2;
      if (x < band + 4) continue;
      ctx.fillRect(x, band - 6, 1, 6);
      ctx.fillText(dm(m / 60, "E"), x + 3, band / 2 - 1);
    }
    ctx.save();
    ctx.rotate(-Math.PI / 2);
    for (let m = Math.ceil((latB * 60) / step) * step; m <= latT * 60; m += step) {
      const y = (toLocal(lonL, m / 60)[1] - M.cy) * M.s + M.h / 2;
      if (y < band + 4) continue;
      ctx.fillRect(-y, band - 6, 1, 6);
      ctx.fillText(dm(m / 60, "N"), -y + 3, band / 2 - 1);
    }
    ctx.restore();
  }
  function dm(v, pos) {
    const hemi = v < 0 ? (pos === "E" ? "W" : "S") : pos;
    const a = Math.abs(v);
    const d = Math.floor(a + 1e-9);
    const m = (a - d) * 60;
    return `${d}°${m.toFixed(m % 1 ? 1 : 0).padStart(2, "0")}′${hemi}`;
  }
  function dms(v, pos) {
    const hemi = v < 0 ? (pos === "E" ? "W" : "S") : pos;
    let a = Math.abs(v);
    const d = Math.floor(a);
    a = (a - d) * 60;
    const m = Math.floor(a);
    const s = (a - m) * 60;
    return `${d}°${String(m).padStart(2, "0")}′${s.toFixed(1).padStart(4, "0")}″ ${hemi}`;
  }

  // ── tiles (optional, online) ───────────────────────────────────────────
  function drawTiles() {
    const ctx = M.ctx;
    const z = Math.max(0, Math.min(19, Math.round(zoomLevel())));
    const world = 2 * Math.PI * R;
    const n = 2 ** z;
    const tileM = world / n; // metres per tile
    const [x0, y0, x1, y1] = visibleBox();
    const gx = (X) => Math.floor((X + ORIGIN[0] + world / 2) / tileM);
    const gy = (Y) => Math.floor((world / 2 - (ORIGIN[1] - Y)) / tileM);
    ctx.setTransform(M.dpr, 0, 0, M.dpr, 0, 0);
    for (let ty = gy(y0); ty <= gy(y1); ty++)
      for (let tx = gx(x0); tx <= gx(x1); tx++) {
        if (ty < 0 || ty >= n) continue;
        const key = `${z}/${((tx % n) + n) % n}/${ty}`;
        let t = M.tiles.cache.get(key);
        if (!t) {
          t = new Image();
          t.crossOrigin = "anonymous";
          t.onload = () => {
            M.tiles.loaded++;
            invalidate();
          };
          t.onerror = () => {
            t.failed = true;
            M.tiles.failed++;
            if (M.tiles.failed === 4 && !M.tiles.loaded) send({ type: "tiles", ok: false });
          };
          t.src = `https://tile.openstreetmap.org/${key}.png`;
          M.tiles.cache.set(key, t);
        }
        if (!t.complete || t.failed || !t.naturalWidth) continue;
        const X = tx * tileM - world / 2 - ORIGIN[0];
        const Y = ORIGIN[1] - (world / 2 - ty * tileM);
        const [sx, sy] = screen([X, Y]);
        ctx.globalAlpha = 0.85;
        ctx.drawImage(t, sx, sy, tileM * M.s + 0.5, tileM * M.s + 0.5);
        ctx.globalAlpha = 1;
      }
  }

  // ── view ─────────────────────────────────────────────────────────────
  const zoomLevel = () => Math.log2((M.s * 2 * Math.PI * R) / 256);
  const scaleFor = (z) => (256 * 2 ** z) / (2 * Math.PI * R);
  function viewInfo() {
    const [lon, lat] = toLonLat(M.cx, M.cy);
    return { lon: round6(lon), lat: round6(lat), zoom: Math.round(zoomLevel() * 1000) / 1000, w: M.w, h: M.h };
  }
  function clampZoom() {
    M.s = Math.max(scaleFor(8), Math.min(scaleFor(19), M.s));
  }
  function zoomAt(px, py, factor) {
    const X = M.cx + (px - M.w / 2) / M.s, Y = M.cy + (py - M.h / 2) / M.s;
    M.s *= factor;
    clampZoom();
    M.cx = X - (px - M.w / 2) / M.s;
    M.cy = Y - (py - M.h / 2) / M.s;
    viewChanged();
  }
  let viewTimer = 0;
  function viewChanged() {
    invalidate();
    clearTimeout(viewTimer);
    viewTimer = setTimeout(() => send({ type: "view", ...viewInfo() }), 250);
  }
  function updateScale() {
    if (!M.scaleEl) return;
    const lat = toLonLat(M.cx, M.cy)[1];
    const mPerPx = Math.cos(lat * D2R) / M.s;
    const target = mPerPx * 110;
    const mag = 10 ** Math.floor(Math.log10(target));
    const nice = [5, 2, 1].map((k) => k * mag).find((v) => v <= target) ?? mag;
    const bar = M.scaleEl.querySelector("[data-bar]"), text = M.scaleEl.querySelector("[data-text]");
    if (bar) bar.style.width = (nice / mPerPx).toFixed(1) + "px";
    if (text) text.textContent = nice >= 1000 ? nice / 1000 + " km" : nice + " m";
    if (M.ratioEl) {
      const ratio = mPerPx / 0.00026458; // a 96 dpi screen pixel is 0.2646 mm
      const r = ratio > 10000 ? Math.round(ratio / 1000) * 1000 : Math.round(ratio / 100) * 100;
      M.ratioEl.textContent = "1 : " + r.toLocaleString("id-ID");
    }
    if (M.zoomEl) M.zoomEl.textContent = "z " + zoomLevel().toFixed(1);
  }

  // ── hit testing ────────────────────────────────────────────────────────
  function hitTest(px, py) {
    const X = M.cx + (px - M.w / 2) / M.s, Y = M.cy + (py - M.h / 2) / M.s;
    const tol = 6 / M.s;
    for (let k = M.order.length - 1; k >= 0; k--) {
      const L = M.layers.get(M.order[k]);
      if (!L || !L.visible || L.style.noHit || L.style.mode === "heat" || !L.features.length) continue;
      if (L.style.minZoom && zoomLevel() < L.style.minZoom) continue;
      // Points first, then lines, then areas: the smaller target wins.
      const r = ((L.style.size ?? 8) / 2 + 5) / M.s;
      let best = null, bd = r * r;
      for (const f of L.features) {
        if (f.geo.t !== "pt") continue;
        const d = (f.geo.p[0] - X) ** 2 + (f.geo.p[1] - Y) ** 2;
        if (d <= bd) (bd = d), (best = f);
      }
      if (best) return { layer: L.id, id: best.id };
      for (const f of L.features) {
        if (f.geo.t !== "ln" || !inBox(f.bbox, [X, Y, X, Y], tol)) continue;
        for (const part of f.geo.parts) for (let i = 1; i < part.length; i++) if (segDist(X, Y, part[i - 1], part[i]) <= tol) return { layer: L.id, id: f.id };
      }
      for (let i = L.features.length - 1; i >= 0; i--) {
        const f = L.features[i];
        if (f.geo.t !== "pg" || !inBox(f.bbox, [X, Y, X, Y])) continue;
        if (f.geo.polys.some((poly) => poly.reduce((inside, r) => inside !== pip(X, Y, r), false))) return { layer: L.id, id: f.id };
      }
    }
    return null;
  }
  function segDist(x, y, a, b) {
    const dx = b[0] - a[0], dy = b[1] - a[1];
    const l2 = dx * dx + dy * dy;
    const t = l2 ? Math.max(0, Math.min(1, ((x - a[0]) * dx + (y - a[1]) * dy) / l2)) : 0;
    return Math.hypot(x - a[0] - t * dx, y - a[1] - t * dy);
  }
  function pip(x, y, r) {
    let c = false;
    for (let i = 0, j = r.length - 1; i < r.length; j = i++)
      if (r[i][1] > y !== r[j][1] > y && x < ((r[j][0] - r[i][0]) * (y - r[i][1])) / (r[j][1] - r[i][1]) + r[i][0]) c = !c;
    return c;
  }

  // ── input ─────────────────────────────────────────────────────────────
  function attach(host, id) {
    M.host = host;
    M.cb = id;
    const canvas = document.createElement("canvas");
    canvas.className = "sg-canvas";
    canvas.setAttribute("aria-hidden", "true");
    host.prepend(canvas);
    M.canvas = canvas;
    M.ctx = canvas.getContext("2d");
    const resize = () => {
      const r = host.getBoundingClientRect();
      M.dpr = Math.min(2, window.devicePixelRatio || 1);
      M.w = Math.max(1, r.width);
      M.h = Math.max(1, r.height);
      canvas.width = Math.round(M.w * M.dpr);
      canvas.height = Math.round(M.h * M.dpr);
      canvas.style.width = M.w + "px";
      canvas.style.height = M.h + "px";
      M.dirty = false;
      draw();
    };
    const ro = new ResizeObserver(resize);
    ro.observe(host);
    const mo = new MutationObserver(themeChanged);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "class"] });
    const mq = matchMedia("(prefers-color-scheme: dark)");
    mq.addEventListener("change", themeChanged);

    const pointers = new Map();
    let down = null, pinch = null, samples = [];
    const local = (e) => {
      const r = canvas.getBoundingClientRect();
      return [e.clientX - r.left, e.clientY - r.top];
    };
    const toWorld = ([px, py]) => [M.cx + (px - M.w / 2) / M.s, M.cy + (py - M.h / 2) / M.s];
    const sketching = () => ["point", "line", "polygon", "measure-line", "measure-area", "select-area"].includes(M.mode);

    const onDown = (e) => {
      if (e.button === 1 || e.button === 0 || e.pointerType !== "mouse") {
        host.focus({ preventScroll: true });
        canvas.setPointerCapture(e.pointerId);
        pointers.set(e.pointerId, local(e));
        M.inertia = null;
        if (pointers.size === 2) {
          const [a, b] = [...pointers.values()];
          pinch = { d: Math.hypot(a[0] - b[0], a[1] - b[1]), mid: [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2] };
          down = null;
          return;
        }
        const p = local(e);
        down = { p, last: p, moved: false, button: e.button, alt: e.altKey, vertex: null };
        samples = [{ t: performance.now(), p }];
        if (M.mode === "edit" && M.edit) {
          const hit = editHit(p);
          if (hit) {
            if (e.altKey && hit.kind === "v") {
              const min = M.edit.kind === "polygon" ? 3 : M.edit.kind === "line" ? 2 : 1;
              if (M.edit.pts.length > min) {
                M.edit.pts.splice(hit.i, 1);
                sendEdit();
              }
              down = null;
              invalidate();
              return;
            }
            if (hit.kind === "m") M.edit.pts.splice(hit.i, 0, toWorld(p));
            down.vertex = hit.kind === "m" ? hit.i : hit.i;
          }
        }
      }
    };
    const onMove = (e) => {
      const p = local(e);
      if (M.coordsEl) {
        const [lon, lat] = toLonLat(...toWorld(p));
        M.coordsEl.textContent = dms(lat, "N") + "   " + dms(lon, "E");
      }
      if (pointers.has(e.pointerId)) pointers.set(e.pointerId, p);
      if (pinch && pointers.size === 2) {
        const [a, b] = [...pointers.values()];
        const d = Math.hypot(a[0] - b[0], a[1] - b[1]);
        const mid = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
        M.cx -= (mid[0] - pinch.mid[0]) / M.s;
        M.cy -= (mid[1] - pinch.mid[1]) / M.s;
        zoomAt(mid[0], mid[1], d / pinch.d);
        pinch = { d, mid };
        return;
      }
      if (sketching()) {
        M.cursor = toWorld(p);
        if (M.sketch.length) invalidate();
      }
      if (!down) return;
      const dx = p[0] - down.last[0], dy = p[1] - down.last[1];
      if (!down.moved && Math.hypot(p[0] - down.p[0], p[1] - down.p[1]) > 4) down.moved = true;
      if (!down.moved) return;
      if (down.vertex !== null && M.edit) {
        M.edit.pts[down.vertex] = toWorld(p);
        down.last = p;
        invalidate();
        return;
      }
      M.cx -= dx / M.s;
      M.cy -= dy / M.s;
      down.last = p;
      const t = performance.now();
      samples.push({ t, p });
      while (samples.length > 2 && t - samples[0].t > 90) samples.shift();
      viewChanged();
    };
    const onUp = (e) => {
      pointers.delete(e.pointerId);
      if (pinch) {
        if (pointers.size < 2) pinch = null;
        return;
      }
      if (!down) return;
      const d = down;
      down = null;
      if (d.vertex !== null && M.edit) {
        if (d.moved || true) sendEdit();
        return;
      }
      if (d.moved) {
        const a = samples[0], b = samples[samples.length - 1];
        const dt = b.t - a.t;
        if (dt > 0 && performance.now() - b.t < 60) {
          const vx = (b.p[0] - a.p[0]) / dt, vy = (b.p[1] - a.p[1]) / dt;
          if (Math.hypot(vx, vy) > 0.25) M.inertia = { vx, vy, t: performance.now() };
          invalidate();
        }
        return;
      }
      click(d.p, e);
    };
    function click(p, e) {
      const w = toWorld(p);
      const [lon, lat] = toLonLat(...w);
      if (sketching()) {
        const lastPt = M.sketch[M.sketch.length - 1];
        if (lastPt && Math.hypot(lastPt[0] - w[0], lastPt[1] - w[1]) * M.s < 3) return;
        M.sketch.push(w);
        invalidate();
        send({ type: "sketchStep", mode: M.mode, coords: M.sketch.map((q) => toLonLat(...q).map(round6)) });
        if (M.mode === "point") finishSketch();
        return;
      }
      if (M.mode === "edit") return;
      const hit = hitTest(p[0], p[1]);
      send({ type: "click", lon: round6(lon), lat: round6(lat), hit, shift: !!e.shiftKey, x: Math.round(p[0]), y: Math.round(p[1]) });
    }
    const onDbl = (e) => {
      if (sketching() && M.sketch.length) {
        e.preventDefault();
        finishSketch();
        return;
      }
      if (M.mode === "identify") {
        const p = local(e);
        zoomAt(p[0], p[1], 2);
      }
    };
    const onWheel = (e) => {
      e.preventDefault();
      const p = local(e);
      const k = e.deltaMode === 1 ? 33 : e.deltaMode === 2 ? 400 : 1;
      zoomAt(p[0], p[1], Math.exp((-e.deltaY * k) * (e.ctrlKey ? 0.01 : 0.0018)));
    };
    const onKey = (e) => {
      if (e.target !== host) return;
      if (e.key === "Enter" && M.sketch.length) finishSketch();
      else if (e.key === "Escape") {
        if (M.sketch.length) {
          M.sketch = [];
          send({ type: "sketchCancel" });
        } else if (M.edit) send({ type: "editEnd" });
        invalidate();
      } else if (e.key === "Backspace" && M.sketch.length) {
        M.sketch.pop();
        send({ type: "sketchStep", mode: M.mode, coords: M.sketch.map((q) => toLonLat(...q).map(round6)) });
        invalidate();
      } else if (e.key === "+" || e.key === "=") zoomAt(M.w / 2, M.h / 2, 1.6);
      else if (e.key === "-") zoomAt(M.w / 2, M.h / 2, 1 / 1.6);
      else if (e.key.startsWith("Arrow")) {
        const step = 80 / M.s;
        if (e.key === "ArrowLeft") M.cx -= step;
        if (e.key === "ArrowRight") M.cx += step;
        if (e.key === "ArrowUp") M.cy -= step;
        if (e.key === "ArrowDown") M.cy += step;
        viewChanged();
      } else return;
      e.preventDefault();
    };
    const onLeave = () => {
      M.cursor = null;
      if (M.sketch.length) invalidate();
    };
    canvas.addEventListener("pointerdown", onDown);
    canvas.addEventListener("pointermove", onMove);
    canvas.addEventListener("pointerup", onUp);
    canvas.addEventListener("pointercancel", onUp);
    canvas.addEventListener("pointerleave", onLeave);
    canvas.addEventListener("dblclick", onDbl);
    canvas.addEventListener("wheel", onWheel, { passive: false });
    canvas.addEventListener("contextmenu", (e) => e.preventDefault());
    host.addEventListener("keydown", onKey);
    resize();
    return addHandle(() => {
      ro.disconnect();
      mo.disconnect();
      mq.removeEventListener("change", themeChanged);
      host.removeEventListener("keydown", onKey);
      canvas.remove();
      M.ctx = null;
      M.cb = 0;
    });
  }
  function stepInertia() {
    const now = performance.now();
    const I = M.inertia;
    const dt = now - I.t;
    I.t = now;
    const decay = Math.exp(-dt / 260);
    M.cx -= (I.vx * dt) / M.s;
    M.cy -= (I.vy * dt) / M.s;
    I.vx *= decay;
    I.vy *= decay;
    if (Math.hypot(I.vx, I.vy) < 0.02) M.inertia = null;
    viewChanged();
  }
  function finishSketch() {
    const min = M.mode === "point" ? 1 : M.mode === "line" || M.mode === "measure-line" ? 2 : 3;
    const pts = M.sketch;
    M.sketch = [];
    invalidate();
    if (pts.length < min) {
      send({ type: "sketchCancel" });
      return;
    }
    send({ type: "sketch", mode: M.mode, coords: pts.map((q) => toLonLat(...q).map(round6)) });
  }
  function editHit(p) {
    const e = M.edit;
    for (let i = 0; i < e.pts.length; i++) {
      const [x, y] = screen(e.pts[i]);
      if (Math.hypot(x - p[0], y - p[1]) <= 9) return { kind: "v", i };
    }
    for (const m of midpoints(e)) {
      const [x, y] = screen(m.p);
      if (Math.hypot(x - p[0], y - p[1]) <= 8) return { kind: "m", i: m.i };
    }
    return null;
  }
  function sendEdit() {
    invalidate();
    send({ type: "edited", layer: M.edit.layer, id: M.edit.id, coords: M.edit.pts.map((q) => toLonLat(...q).map(round6)) });
  }

  // ── ops ─────────────────────────────────────────────────────────────────
  ops["sg.attach"] = ({ host, coords, scale, ratio, zoom, id }) => {
    const h = attach(elementOf(host), id);
    M.coordsEl = elementOf(coords);
    M.scaleEl = elementOf(scale);
    M.ratioEl = elementOf(ratio);
    M.zoomEl = elementOf(zoom);
    return h;
  };
  ops["sg.detach"] = ({ h }) => {
    const f = getHandle(h);
    if (typeof f === "function") f();
  };
  ops["sg.load"] = async ({ id, kind, url, attrs }) => {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`${url}: ${res.status}`);
    const L = ingest(id, kind, await res.json());
    return attrs === false ? { count: L.features.length, vertices: countVertices(L) } : { count: L.features.length, vertices: countVertices(L), features: attributes(L) };
  };
  function countVertices(L) {
    let n = 0;
    for (const f of L.features) {
      if (f.geo.t === "pt") n++;
      else if (f.geo.t === "ln") for (const p of f.geo.parts) n += p.length;
      else for (const poly of f.geo.polys) for (const r of poly) n += r.length;
    }
    return n;
  }
  ops["sg.data"] = ({ id, kind, geojson }) => {
    const L = ingest(id, kind, typeof geojson === "string" ? JSON.parse(geojson) : geojson);
    return { count: L.features.length };
  };
  ops["sg.drop"] = ({ id }) => {
    M.layers.delete(id);
    M.order = M.order.filter((x) => x !== id);
    M.sel.delete(id);
    invalidate();
  };
  ops["sg.layers"] = ({ layers }) => {
    M.order = layers.map((l) => l.id);
    for (const l of layers) {
      let L = M.layers.get(l.id);
      if (!L) M.layers.set(l.id, (L = { id: l.id, kind: l.kind, features: [], byId: new Map(), style: {}, visible: true, opacity: 1 }));
      const styleKey = JSON.stringify(l.style ?? {});
      if (styleKey !== L.styleKey) {
        L.style = l.style ?? {};
        L.styleKey = styleKey;
        L.groups = null;
      }
      L.visible = !!l.visible;
      L.opacity = l.opacity ?? 1;
    }
    invalidate();
  };
  ops["sg.select"] = ({ sel }) => {
    M.sel = new Map((sel ?? []).map((s) => [s.layer, new Set(s.ids.map(String))]));
    invalidate();
  };
  ops["sg.view"] = (a) => {
    if (a && a.lon !== undefined) {
      [M.cx, M.cy] = toLocal(a.lon, a.lat);
      if (a.zoom !== undefined) M.s = scaleFor(a.zoom);
      clampZoom();
      M.inertia = null;
      viewChanged();
    }
    return viewInfo();
  };
  ops["sg.zoomBy"] = ({ factor }) => zoomAt(M.w / 2, M.h / 2, factor);
  ops["sg.fit"] = ({ bbox, pad = 40, maxZoom = 17 }) => {
    const [a, b] = toLocal(bbox[0], bbox[3]);
    const [c, d] = toLocal(bbox[2], bbox[1]);
    M.cx = (a + c) / 2;
    M.cy = (b + d) / 2;
    const sx = (M.w - pad * 2) / Math.max(1, c - a), sy = (M.h - pad * 2) / Math.max(1, d - b);
    M.s = Math.min(sx, sy, scaleFor(maxZoom));
    clampZoom();
    M.inertia = null;
    viewChanged();
    return viewInfo();
  };
  ops["sg.tool"] = ({ mode }) => {
    M.mode = mode;
    M.sketch = [];
    if (mode !== "edit") M.edit = null;
    if (M.canvas) M.canvas.dataset.mode = mode;
    invalidate();
  };
  ops["sg.edit"] = (a) => {
    M.edit = a && a.coords ? { layer: a.layer, id: a.id, kind: a.kind, pts: a.coords.map(([x, y]) => toLocal(x, y)) } : null;
    invalidate();
  };
  ops["sg.buffer"] = (a) => {
    M.buffer = a && a.coords ? { kind: a.kind, pts: a.coords.map(([x, y]) => toLocal(x, y)), meters: a.meters } : null;
    invalidate();
  };
  ops["sg.locate"] = (a) => {
    M.locate = a && a.lon !== undefined ? { p: toLocal(a.lon, a.lat), acc: a.acc ?? 30 } : null;
    invalidate();
  };
  ops["sg.tiles"] = ({ on }) => {
    M.tiles.on = !!on;
    M.tiles.failed = 0;
    invalidate();
  };
  ops["sg.geometry"] = ({ layer, id }) => {
    const f = M.layers.get(layer)?.byId?.get(String(id));
    if (!f) return null;
    const back = (pts) => pts.map((q) => toLonLat(q[0], q[1]).map(round6));
    if (f.geo.t === "pt") return { type: "Point", coordinates: toLonLat(...f.geo.p).map(round6) };
    if (f.geo.t === "ln") return f.geo.parts.length === 1 ? { type: "LineString", coordinates: back(f.geo.parts[0]) } : { type: "MultiLineString", coordinates: f.geo.parts.map(back) };
    return f.geo.polys.length === 1 ? { type: "Polygon", coordinates: f.geo.polys[0].map(back) } : { type: "MultiPolygon", coordinates: f.geo.polys.map((p) => p.map(back)) };
  };
  ops["sg.project"] = ({ lon, lat }) => {
    const [x, y] = merc(lon, lat);
    const [lon2, lat2] = toLonLat(...toLocal(lon, lat));
    return { x, y, lon: lon2, lat: lat2 };
  };
  ops["sg.focus"] = () => M.host?.focus({ preventScroll: true });
}
