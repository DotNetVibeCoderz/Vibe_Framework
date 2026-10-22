// Generates Siaga Citarum's map data into ../wwwroot/data as GeoJSON.
// Every shape here is generated: a made-up elevation surface shaped like the
// Bandung basin (a lake-bed floor near 660 m ringed by volcanoes) gives the
// contours, the low ground that floods and each kecamatan's risk; rivers and
// roads follow real places by their approximate coordinates; kecamatan are a
// jittered mesh named after real kecamatan near each cell. Not survey data.
// usage: node samples/Siaga/data/make-data.mjs
import { mkdirSync, writeFileSync } from "fs";
import { dirname, join } from "path";
import { fileURLToPath } from "url";

const out = join(dirname(fileURLToPath(import.meta.url)), "..", "wwwroot", "data");
mkdirSync(out, { recursive: true });

function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const R = rng(20261022);
const r5 = (v) => Math.round(v * 1e5) / 1e5;
const pt = ([x, y]) => [r5(x), r5(y)];

// ── elevation ──────────────────────────────────────────────────────────
const peaks = [
  // lon, lat, height above the floor (m), spread (deg)
  [107.61, -6.76, 1420, 0.07], // Tangkuban Perahu
  [107.53, -6.77, 1380, 0.06], // Burangrang
  [107.72, -6.82, 1500, 0.07], // Bukit Tunggul
  [107.76, -6.88, 1150, 0.045], // Manglayang
  [107.6, -7.13, 1650, 0.07], // Malabar
  [107.65, -7.21, 1500, 0.07], // Wayang-Windu
  [107.4, -7.16, 1750, 0.08], // Patuha
  [107.78, -7.12, 1280, 0.06], // Rakutak
  [107.86, -7.14, 1560, 0.06], // Guntur
  [107.47, -7.05, 900, 0.07], // Soreang hills
  [107.42, -6.88, 380, 0.06], // Saguling hills
  [107.9, -6.97, 800, 0.06], // Cicalengka hills
];
const gridNoise = (() => {
  const g = new Map();
  const at = (i, j) => {
    const k = i * 7919 + j;
    if (!g.has(k)) g.set(k, rng(k * 2654435761)());
    return g.get(k);
  };
  return (x, y) => {
    const i = Math.floor(x), j = Math.floor(y);
    const fx = x - i, fy = y - j;
    const sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
    const a = at(i, j), b = at(i + 1, j), c = at(i, j + 1), d = at(i + 1, j + 1);
    return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
  };
})();
const fbm = (x, y) => {
  let v = 0, amp = 1, f = 1;
  for (let o = 0; o < 4; o++) {
    v += amp * (gridNoise(x * f, y * f) - 0.5);
    amp *= 0.5;
    f *= 2.03;
  }
  return v;
};
export function elevation(lon, lat) {
  // The floor: an ellipse round Dayeuhkolot, rising gently, then the peaks.
  const dx = (lon - 107.67) / 0.18, dy = (lat + 6.995) / 0.08;
  const d = Math.sqrt(dx * dx + dy * dy);
  let h = 658 + 26 * d * d + Math.max(0, d - 1) * 150;
  for (const [px, py, ph, s] of peaks) {
    const q = ((lon - px) ** 2 + (lat - py) ** 2) / (s * s);
    h += ph * 0.82 * Math.exp(-q);
  }
  h += fbm(lon * 60, lat * 60) * Math.min(220, 20 + Math.max(0, h - 700) * 0.35);
  return h;
}

// ── contours by marching squares ──────────────────────────────────────
const BBOX = [107.3, -7.28, 107.98, -6.72];
const STEP = 0.0035;
const nx = Math.round((BBOX[2] - BBOX[0]) / STEP), ny = Math.round((BBOX[3] - BBOX[1]) / STEP);
const H = new Float64Array((nx + 1) * (ny + 1));
for (let j = 0; j <= ny; j++) for (let i = 0; i <= nx; i++) H[j * (nx + 1) + i] = elevation(BBOX[0] + i * STEP, BBOX[1] + j * STEP);
const hAt = (i, j) => H[j * (nx + 1) + i];

function contour(level) {
  const segs = [];
  const lerp = (i0, j0, i1, j1) => {
    const a = hAt(i0, j0), b = hAt(i1, j1);
    const t = (level - a) / (b - a);
    return [BBOX[0] + (i0 + (i1 - i0) * t) * STEP, BBOX[1] + (j0 + (j1 - j0) * t) * STEP];
  };
  for (let j = 0; j < ny; j++)
    for (let i = 0; i < nx; i++) {
      const c = (hAt(i, j) > level ? 1 : 0) | (hAt(i + 1, j) > level ? 2 : 0) | (hAt(i + 1, j + 1) > level ? 4 : 0) | (hAt(i, j + 1) > level ? 8 : 0);
      if (c === 0 || c === 15) continue;
      const e = {
        b: () => lerp(i, j, i + 1, j),
        r: () => lerp(i + 1, j, i + 1, j + 1),
        t: () => lerp(i, j + 1, i + 1, j + 1),
        l: () => lerp(i, j, i, j + 1),
      };
      const key = (s) => s + ":" + i + ":" + j;
      const edgeKey = { b: `h${i},${j}`, t: `h${i},${j + 1}`, l: `v${i},${j}`, r: `v${i + 1},${j}` };
      const pairs = {
        1: [["l", "b"]], 2: [["b", "r"]], 3: [["l", "r"]], 4: [["r", "t"]], 5: [["l", "t"], ["b", "r"]],
        6: [["b", "t"]], 7: [["l", "t"]], 8: [["t", "l"]], 9: [["t", "b"]], 10: [["t", "r"], ["l", "b"]],
        11: [["t", "r"]], 12: [["r", "l"]], 13: [["r", "b"]], 14: [["b", "l"]],
      }[c];
      for (const [p, q] of pairs) segs.push([edgeKey[p], e[p](), edgeKey[q], e[q]()]);
      void key;
    }
  // Chain the segments through their shared cell edges.
  const ends = new Map();
  const add = (k, s) => (ends.get(k) ?? ends.set(k, []).get(k)).push(s);
  segs.forEach((s, n) => {
    add(s[0], n);
    add(s[2], n);
  });
  const used = new Uint8Array(segs.length);
  const lines = [];
  for (let n = 0; n < segs.length; n++) {
    if (used[n]) continue;
    used[n] = 1;
    const line = [segs[n][1], segs[n][3]];
    for (const dir of [1, 0]) {
      let k = dir ? segs[n][2] : segs[n][0];
      for (;;) {
        const next = (ends.get(k) || []).find((m) => !used[m]);
        if (next === undefined) break;
        used[next] = 1;
        const s = segs[next];
        const [pk, pp] = s[0] === k ? [s[2], s[3]] : [s[0], s[1]];
        if (dir) line.push(pp);
        else line.unshift(pp);
        k = pk;
      }
    }
    if (line.length >= 4) lines.push(line);
  }
  // Drop every other vertex on long lines: the grid is finer than the drawing needs.
  return lines.map((l) => l.filter((_, n) => n % 2 === 0 || n === l.length - 1).map(pt));
}
const contourFeatures = [];
let contourVertices = 0;
for (let level = 675; level <= 2350; level += 25) {
  const lines = contour(level);
  if (!lines.length) continue;
  contourVertices += lines.reduce((s, l) => s + l.length, 0);
  contourFeatures.push({ type: "Feature", id: "c" + level, properties: { elev: level, index: level % 250 === 0 ? 1 : 0 }, geometry: { type: "MultiLineString", coordinates: lines } });
}

// ── rivers ────────────────────────────────────────────────────────────
function meander(points, perSeg, amp, seed) {
  const rr = rng(seed);
  const outp = [];
  for (let s = 0; s < points.length - 1; s++) {
    const [ax, ay] = points[s], [bx, by] = points[s + 1];
    const len = Math.hypot(bx - ax, by - ay);
    const nxv = -(by - ay) / len, nyv = (bx - ax) / len;
    const phase = rr() * 6.28, freq = 2 + rr() * 3;
    for (let k = 0; k < perSeg; k++) {
      const t = k / perSeg;
      const w = Math.sin(t * Math.PI) * amp * (Math.sin(t * freq * 6.28 + phase) + 0.4 * Math.sin(t * freq * 15 + phase * 2));
      outp.push([ax + (bx - ax) * t + nxv * w, ay + (by - ay) * t + nyv * w]);
    }
  }
  outp.push(points[points.length - 1]);
  return outp.map(pt);
}
const rivers = [
  ["Citarum", 1, [[107.657, -7.208], [107.7, -7.16], [107.735, -7.1], [107.755, -7.045], [107.73, -7.03], [107.705, -7.0], [107.67, -6.993], [107.64, -6.99], [107.622, -6.989], [107.59, -6.975], [107.555, -6.952], [107.528, -6.94], [107.5, -6.93], [107.46, -6.92], [107.42, -6.915]]],
  ["Cikapundung", 2, [[107.62, -6.8], [107.615, -6.85], [107.608, -6.9], [107.612, -6.94], [107.62, -6.97], [107.624, -6.986]]],
  ["Citarik", 2, [[107.86, -6.99], [107.82, -6.975], [107.78, -6.965], [107.74, -6.978], [107.705, -6.995]]],
  ["Cisangkuy", 2, [[107.57, -7.18], [107.58, -7.12], [107.59, -7.06], [107.605, -7.02], [107.62, -6.995]]],
  ["Cikeruh", 3, [[107.77, -6.92], [107.755, -6.945], [107.74, -6.975]]],
  ["Cimahi", 3, [[107.55, -6.82], [107.545, -6.87], [107.535, -6.91], [107.53, -6.938]]],
  ["Cirasea", 3, [[107.8, -7.08], [107.78, -7.05], [107.756, -7.046]]],
  ["Citepus", 3, [[107.58, -6.86], [107.585, -6.92], [107.595, -6.972]]],
];
const riverFeatures = rivers.map(([name, order, p], n) => ({
  type: "Feature",
  id: "r" + n,
  properties: { name, order, length_km: 0 },
  geometry: { type: "LineString", coordinates: meander(p, order === 1 ? 70 : 40, order === 1 ? 0.0035 : 0.002, 100 + n) },
}));
for (const f of riverFeatures) {
  let km = 0;
  const c = f.geometry.coordinates;
  for (let i = 1; i < c.length; i++) km += hav(c[i - 1], c[i]) / 1000;
  f.properties.length_km = Math.round(km * 10) / 10;
}
function hav([lo1, la1], [lo2, la2]) {
  const rad = Math.PI / 180, R0 = 6371008.8;
  const a = Math.sin(((la2 - la1) * rad) / 2) ** 2 + Math.cos(la1 * rad) * Math.cos(la2 * rad) * Math.sin(((lo2 - lo1) * rad) / 2) ** 2;
  return 2 * R0 * Math.asin(Math.sqrt(a));
}

// The Saguling reservoir and the flood extents are blobs: a noisy radius round a centre.
function blob(cx, cy, rx, ry, n, rough, seed) {
  const rr = rng(seed);
  const ph = [rr() * 6.28, rr() * 6.28, rr() * 6.28];
  const ring = [];
  for (let k = 0; k < n; k++) {
    const a = (k / n) * Math.PI * 2;
    const f = 1 + rough * (0.5 * Math.sin(3 * a + ph[0]) + 0.3 * Math.sin(7 * a + ph[1]) + 0.2 * Math.sin(13 * a + ph[2]));
    ring.push(pt([cx + Math.cos(a) * rx * f, cy + Math.sin(a) * ry * f]));
  }
  ring.push(ring[0]);
  return ring;
}
const water = [
  { type: "Feature", id: "w0", properties: { name: "Waduk Saguling", kind: "reservoir", area_ha: 0 }, geometry: { type: "Polygon", coordinates: [blob(107.37, -6.9, 0.055, 0.035, 160, 0.45, 7)] } },
  { type: "Feature", id: "w1", properties: { name: "Kolam Retensi Cieunteung", kind: "retention", area_ha: 0 }, geometry: { type: "Polygon", coordinates: [blob(107.613, -7.0, 0.0035, 0.0025, 40, 0.15, 8)] } },
  { type: "Feature", id: "w2", properties: { name: "Situ Cisanti", kind: "lake", area_ha: 0 }, geometry: { type: "Polygon", coordinates: [blob(107.657, -7.211, 0.003, 0.002, 36, 0.2, 9)] } },
];

// ── roads ─────────────────────────────────────────────────────────────
const roads = [
  ["Tol Padaleunyi", "toll", [[107.45, -6.85], [107.5, -6.875], [107.55, -6.905], [107.58, -6.925], [107.62, -6.94], [107.66, -6.945], [107.7, -6.935], [107.735, -6.935]]],
  ["Tol Soroja", "toll", [[107.565, -6.93], [107.55, -6.97], [107.535, -7.01], [107.525, -7.03]]],
  ["Jl. Raya Dayeuhkolot", "arterial", [[107.62, -6.94], [107.622, -6.965], [107.625, -6.99], [107.63, -7.02]]],
  ["Jl. Moh. Toha", "arterial", [[107.608, -6.93], [107.611, -6.96], [107.614, -6.985], [107.6, -7.03], [107.59, -7.07]]],
  ["Jl. Raya Banjaran", "arterial", [[107.6, -7.03], [107.59, -7.06], [107.585, -7.1], [107.575, -7.16]]],
  ["Jl. Raya Majalaya", "arterial", [[107.66, -6.99], [107.7, -7.01], [107.73, -7.03], [107.755, -7.05]]],
  ["Jl. Raya Bandung–Garut", "arterial", [[107.7, -6.935], [107.75, -6.95], [107.78, -6.96], [107.83, -6.98], [107.88, -7.03], [107.92, -7.05]]],
  ["Jl. Raya Lembang", "arterial", [[107.61, -6.88], [107.615, -6.85], [107.62, -6.81]]],
  ["Jl. Raya Cimahi–Padalarang", "arterial", [[107.58, -6.9], [107.55, -6.88], [107.51, -6.86], [107.47, -6.84]]],
  ["Jl. Raya Soreang", "arterial", [[107.565, -6.95], [107.55, -6.99], [107.53, -7.025]]],
  ["Jl. Raya Ciparay", "local", [[107.68, -7.0], [107.71, -7.03], [107.715, -7.06]]],
  ["Jl. Raya Rancaekek", "local", [[107.735, -6.935], [107.76, -6.955], [107.78, -6.965]]],
  ["Jl. Raya Pangalengan", "local", [[107.575, -7.16], [107.57, -7.18], [107.565, -7.2]]],
  ["Jl. Baleendah–Ciparay", "local", [[107.625, -7.0], [107.65, -7.01], [107.68, -7.0]]],
];
const roadFeatures = roads.map(([name, cls, p], n) => ({ type: "Feature", id: "j" + n, properties: { name, class: cls }, geometry: { type: "LineString", coordinates: meander(p, 14, 0.0008, 300 + n) } }));

// ── flood extents and points ────────────────────────────────────────────
const floods = [
  ["Dayeuhkolot–Baleendah", 107.622, -6.995, 0.018, 0.011, 110, 120],
  ["Bojongsoang", 107.655, -6.985, 0.013, 0.008, 70, 80],
  ["Andir–Rancamanyar", 107.6, -6.985, 0.009, 0.006, 60, 60],
  ["Rancaekek", 107.765, -6.962, 0.012, 0.007, 50, 50],
  ["Majalaya", 107.752, -7.04, 0.009, 0.006, 40, 45],
  ["Gedebage", 107.695, -6.955, 0.008, 0.005, 35, 35],
];
const floodFeatures = floods.map(([name, x, y, rx, ry, depth, n], k) => ({
  type: "Feature",
  id: "g" + k,
  properties: { name, max_depth_cm: depth, since: "2026-10-21 18:00" },
  geometry: { type: "Polygon", coordinates: [blob(x, y, rx, ry, n, 0.35, 50 + k)] },
}));

// ── kecamatan seats: real places, approximate coordinates ─────────────────
const names = [
  ["Kota Bandung", "Sukajadi", 107.59, -6.88], ["Kota Bandung", "Coblong", 107.615, -6.885], ["Kota Bandung", "Cibeunying Kidul", 107.645, -6.9], ["Kota Bandung", "Arcamanik", 107.68, -6.915],
  ["Kota Bandung", "Ujungberung", 107.705, -6.905], ["Kota Bandung", "Bandung Kulon", 107.565, -6.925], ["Kota Bandung", "Regol", 107.61, -6.935], ["Kota Bandung", "Buahbatu", 107.635, -6.95],
  ["Kota Bandung", "Gedebage", 107.69, -6.945], ["Kota Bandung", "Cibiru", 107.72, -6.925], ["Kota Cimahi", "Cimahi Tengah", 107.54, -6.88], ["Kota Cimahi", "Cimahi Selatan", 107.54, -6.91],
  ["Bandung Barat", "Lembang", 107.62, -6.82], ["Bandung Barat", "Parongpong", 107.57, -6.82], ["Bandung Barat", "Ngamprah", 107.5, -6.84], ["Bandung Barat", "Padalarang", 107.47, -6.85],
  ["Bandung Barat", "Batujajar", 107.48, -6.92], ["Bandung Barat", "Cihampelas", 107.45, -6.95], ["Bandung Barat", "Cililin", 107.45, -7.01], ["Kabupaten Bandung", "Cilengkrang", 107.73, -6.88],
  ["Kabupaten Bandung", "Cileunyi", 107.75, -6.93], ["Kabupaten Bandung", "Rancaekek", 107.77, -6.96], ["Kabupaten Bandung", "Cicalengka", 107.84, -6.98], ["Kabupaten Bandung", "Nagreg", 107.89, -7.02],
  ["Kabupaten Bandung", "Margaasih", 107.53, -6.95], ["Kabupaten Bandung", "Margahayu", 107.57, -6.97], ["Kabupaten Bandung", "Dayeuhkolot", 107.62, -6.985], ["Kabupaten Bandung", "Bojongsoang", 107.65, -6.98],
  ["Kabupaten Bandung", "Solokanjeruk", 107.73, -7.0], ["Kabupaten Bandung", "Paseh", 107.79, -7.02], ["Kabupaten Bandung", "Kutawaringin", 107.5, -6.99], ["Kabupaten Bandung", "Katapang", 107.55, -7.0],
  ["Kabupaten Bandung", "Baleendah", 107.62, -7.01], ["Kabupaten Bandung", "Ciparay", 107.7, -7.03], ["Kabupaten Bandung", "Majalaya", 107.755, -7.045], ["Kabupaten Bandung", "Ibun", 107.79, -7.09],
  ["Kabupaten Bandung", "Soreang", 107.52, -7.03], ["Kabupaten Bandung", "Pameungpeuk", 107.6, -7.02], ["Kabupaten Bandung", "Banjaran", 107.59, -7.05], ["Kabupaten Bandung", "Arjasari", 107.64, -7.06],
  ["Kabupaten Bandung", "Pacet", 107.73, -7.1], ["Kabupaten Bandung", "Cangkuang", 107.56, -7.07], ["Kabupaten Bandung", "Cimaung", 107.56, -7.1], ["Kabupaten Bandung", "Pangalengan", 107.58, -7.17],
  ["Kabupaten Bandung", "Kertasari", 107.68, -7.18], ["Kabupaten Bandung", "Pasirjambu", 107.47, -7.1], ["Kabupaten Bandung", "Ciwidey", 107.43, -7.12], ["Kabupaten Bandung", "Rancabali", 107.4, -7.16],
];
// ── kecamatan: a noisy Voronoi partition round each kecamatan's real seat ──
// Every cell of a fine grid goes to the nearest seat (distance bent by noise, so
// borders wander like real ones); each kecamatan's cells are traced into a ring
// and smoothed. Neighbours share the traced edges, so borders line up.
const STEPK = 0.0035, X0 = 107.37, X1 = 107.94, Y0 = -7.24, Y1 = -6.78;
const gw = Math.round((X1 - X0) / STEPK), gh = Math.round((Y1 - Y0) / STEPK);
const label = new Int16Array(gw * gh).fill(-1);
for (let j = 0; j < gh; j++)
  for (let i = 0; i < gw; i++) {
    const x = X0 + (i + 0.5) * STEPK, y = Y0 + (j + 0.5) * STEPK;
    let best = -1, bd = Infinity;
    names.forEach((n, k) => {
      const d = Math.hypot((x - n[2]) * 1.0, y - n[3]) + fbm(x * 45 + k * 0.7, y * 45) * 0.012;
      if (d < bd) (bd = d), (best = k);
    });
    // Beyond 0.075° from any seat is outside the map's kecamatan.
    if (bd < 0.075) label[j * gw + i] = best;
  }
const at = (i, j) => (i < 0 || j < 0 || i >= gw || j >= gh ? -1 : label[j * gw + i]);
function trace(k) {
  const next = new Map();
  const key = (i, j) => i + "," + j;
  const add = (a, b) => (next.get(key(...a)) ?? next.set(key(...a), []).get(key(...a))).push(b);
  for (let j = 0; j < gh; j++)
    for (let i = 0; i < gw; i++) {
      if (at(i, j) !== k) continue;
      if (at(i, j - 1) !== k) add([i, j], [i + 1, j]);
      if (at(i + 1, j) !== k) add([i + 1, j], [i + 1, j + 1]);
      if (at(i, j + 1) !== k) add([i + 1, j + 1], [i, j + 1]);
      if (at(i - 1, j) !== k) add([i, j + 1], [i, j]);
    }
  const rings = [];
  for (const [start] of next) {
    while (next.get(start)?.length) {
      const ring = [];
      let cur = start.split(",").map(Number);
      for (;;) {
        const outs = next.get(key(...cur));
        if (!outs || outs.length === 0) break;
        const nxt = outs.pop();
        ring.push(cur);
        cur = nxt;
        if (key(...cur) === start) break;
      }
      if (ring.length > 3) rings.push(ring);
    }
  }
  // The largest ring is the kecamatan; stray islands are dropped.
  rings.sort((a, b) => b.length - a.length);
  return rings[0] ?? [];
}
function simplify(ring) {
  // Drop points on a straight run, keeping corners.
  return ring.filter((p, n) => {
    const a = ring[(n - 1 + ring.length) % ring.length], b = ring[(n + 1) % ring.length];
    return (p[0] - a[0]) * (b[1] - p[1]) - (p[1] - a[1]) * (b[0] - p[0]) !== 0;
  });
}
function chaikin(ring, times) {
  for (let t = 0; t < times; t++) {
    const out = [];
    for (let n = 0; n < ring.length; n++) {
      const a = ring[n], b = ring[(n + 1) % ring.length];
      out.push([a[0] * 0.75 + b[0] * 0.25, a[1] * 0.75 + b[1] * 0.25], [a[0] * 0.25 + b[0] * 0.75, a[1] * 0.25 + b[1] * 0.75]);
    }
    ring = out;
  }
  return ring;
}
const inRing = (x, y, r) => {
  let inside = false;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++)
    if (r[i][1] > y !== r[j][1] > y && x < ((r[j][0] - r[i][0]) * (y - r[i][1])) / (r[j][1] - r[i][1]) + r[i][0]) inside = !inside;
  return inside;
};
const floodRings = floodFeatures.map((f) => f.geometry.coordinates[0]);
const citarum = riverFeatures[0].geometry.coordinates;
const kecFeatures = names.map((n, k) => {
  const grid = chaikin(simplify(trace(k)), 2);
  const ring = grid.map(([i, j]) => pt([X0 + i * STEPK, Y0 + j * STEPK]));
  // The kecamatan's own cells give its numbers: low ground (under 690 m),
  // the share under today's flood extents, and how much lies by the Citarum.
  let low = 0, wet = 0, tot = 0, sum = 0, nearMain = 0;
  for (let j = 0; j < gh; j++)
    for (let i = 0; i < gw; i++) {
      if (label[j * gw + i] !== k) continue;
      const x = X0 + (i + 0.5) * STEPK, y = Y0 + (j + 0.5) * STEPK;
      const h = elevation(x, y);
      tot++;
      sum += h;
      if (h < 690) low++;
      if (floodRings.some((r) => inRing(x, y, r))) wet++;
      if (h < 720 && citarum.some((p) => Math.abs(p[0] - x) + Math.abs(p[1] - y) < 0.01)) nearMain++;
    }
  tot = Math.max(1, tot);
  const lowShare = low / tot, wetShare = wet / tot, riverShare = nearMain / tot;
  const urban = n[0].startsWith("Kota") ? 2.2 : 1;
  const pop = Math.round((42000 + R() * 68000) * urban);
  const hh = Math.round(pop / 3.6);
  const affected = Math.round(hh * Math.min(1, wetShare * 1.8) * (0.6 + R() * 0.25) + (lowShare > 0.3 && wetShare === 0 ? hh * 0.01 * R() : 0));
  const score = lowShare * 0.7 + Math.min(1, riverShare * 4) * 0.4 + Math.min(1, wetShare * 3) * 0.5;
  const risk = score > 0.55 ? "Tinggi" : score > 0.28 ? "Sedang" : score > 0.08 ? "Rendah" : "Sangat rendah";
  return {
    type: "Feature",
    id: "k" + k,
    properties: { name: n[1], kabupaten: n[0], population: pop, households: hh, affected_hh: affected, flooded_pct: Math.round(wetShare * 1000) / 10, low_ground_pct: Math.round(lowShare * 100), mean_elev_m: Math.round(sum / tot), risk },
    geometry: { type: "Polygon", coordinates: [ring.concat([ring[0]])] },
  };
}).filter((f) => f.geometry.coordinates[0].length > 4);

const point = (id, props, x, y) => ({ type: "Feature", id, properties: props, geometry: { type: "Point", coordinates: pt([x, y]) } });
const gauges = [
  ["Pos Duga Air Dayeuhkolot", "Citarum", 107.6235, -6.9885, 352, [150, 250, 320]],
  ["Pos Duga Air Nanjung", "Citarum", 107.5285, -6.9405, 298, [180, 280, 350]],
  ["Pos Duga Air Sapan", "Citarum", 107.7005, -6.9965, 214, [140, 230, 300]],
  ["Pos Duga Air Majalaya", "Citarum", 107.7545, -7.0455, 168, [120, 200, 260]],
  ["Pos Duga Air Gandok", "Cikapundung", 107.6125, -6.8655, 96, [110, 170, 230]],
  ["Pos Duga Air Citarik Cicalengka", "Citarik", 107.8205, -6.9755, 132, [100, 160, 220]],
].map(([name, river, x, y, level, [waspada, siaga, awas]], k) =>
  point("p" + k, { name, river, level_cm: level, waspada_cm: waspada, siaga_cm: siaga, awas_cm: awas, status: level >= awas ? "Awas" : level >= siaga ? "Siaga" : level >= waspada ? "Waspada" : "Normal", updated: "2026-10-22 05:40" }, x, y));

const shelterPlaces = [
  ["GOR Baleendah", 107.628, -7.012], ["Masjid Agung Dayeuhkolot", 107.618, -6.982], ["SDN Citeureup 3", 107.61, -6.975], ["Gedung Inkanas Bojongsoang", 107.648, -6.972],
  ["Kantor Kecamatan Baleendah", 107.623, -7.006], ["Pesantren Al-Ihsan", 107.606, -7.002], ["Balai Desa Andir", 107.596, -6.995], ["SMPN 1 Dayeuhkolot", 107.629, -6.976],
  ["Aula Desa Bojongsari", 107.662, -6.978], ["GOR Rancaekek", 107.77, -6.952], ["Masjid Raya Rancaekek", 107.776, -6.958], ["SDN Bojongloa", 107.758, -6.968],
  ["Balai Desa Majalaya", 107.751, -7.035], ["Lapangan Majalaya", 107.757, -7.043], ["Gedung Serbaguna Ciparay", 107.708, -7.028], ["SMAN 1 Baleendah", 107.638, -7.018],
  ["Kantor Desa Cangkuang Kulon", 107.616, -6.968], ["Posko Cieunteung", 107.612, -7.003], ["Aula Telkom University", 107.63, -6.974], ["GOR Gedebage", 107.69, -6.948],
  ["Masjid Al-Jabbar", 107.697, -6.952], ["Balai Desa Sapan", 107.703, -6.99], ["SDN Rancamanyar", 107.588, -6.988], ["Kantor Desa Solokanjeruk", 107.727, -7.004],
];
const shelters = shelterPlaces.map(([name, x, y], k) => {
  const capacity = 80 + Math.round(R() * 6) * 40;
  const occupied = Math.min(capacity, Math.round(capacity * (0.15 + R() * 0.95)));
  return point("s" + k, { name, capacity, occupied, status: occupied >= capacity * 0.95 ? "Penuh" : occupied >= capacity * 0.6 ? "Hampir penuh" : "Tersedia", contact: "BPBD posko " + (k + 1) }, x, y);
});
const pumps = [
  ["Rumah Pompa Cieunteung", "pump", 107.611, -7.0], ["Rumah Pompa Andir", "pump", 107.598, -6.992], ["Kolam Retensi Cieunteung", "retention", 107.613, -7.0],
  ["Terowongan Nanjung", "tunnel", 107.523, -6.937], ["Floodway Cisangkuy", "floodway", 107.612, -7.015], ["Rumah Pompa Bojongsoang", "pump", 107.652, -6.988],
  ["Kolam Retensi Andir", "retention", 107.594, -6.99], ["Rumah Pompa Rancaekek", "pump", 107.766, -6.964],
].map(([name, kind, x, y], k) => point("f" + k, { name, kind, state: k % 3 === 2 ? "Siaga" : "Beroperasi" }, x, y));
const health = [
  ["Puskesmas Dayeuhkolot", 107.62, -6.98], ["Puskesmas Baleendah", 107.627, -7.01], ["RSUD Al-Ihsan", 107.604, -7.003], ["Puskesmas Bojongsoang", 107.652, -6.97],
  ["Puskesmas Rancaekek", 107.772, -6.955], ["Puskesmas Majalaya", 107.754, -7.038], ["RSUD Majalaya", 107.749, -7.042], ["Puskesmas Ciparay", 107.711, -7.034],
  ["Puskesmas Margahayu", 107.573, -6.97], ["Puskesmas Banjaran", 107.59, -7.05], ["RS Immanuel", 107.596, -6.938], ["Puskesmas Gedebage", 107.692, -6.944],
].map(([name, x, y], k) => point("h" + k, { name, kind: name.startsWith("RS") ? "Rumah sakit" : "Puskesmas", beds: name.startsWith("RS") ? 150 + k * 20 : 10 + k }, x, y));

// Citizen reports: many near the flood extents, a few scattered.
const reports = [];
const blobs = floods.map((f) => f);
for (let k = 0; k < 420; k++) {
  let x, y, depth;
  if (R() < 0.85) {
    const [, fx, fy, rx, ry, d] = blobs[Math.floor(R() * blobs.length)];
    const a = R() * 6.28, rr = Math.sqrt(R()) * 1.2;
    x = fx + Math.cos(a) * rx * rr;
    y = fy + Math.sin(a) * ry * rr;
    depth = Math.max(5, Math.round(d * (1.1 - rr * 0.7) * (0.6 + R() * 0.5)));
  } else {
    x = 107.5 + R() * 0.35;
    y = -7.08 + R() * 0.2;
    depth = Math.round(5 + R() * 25);
  }
  const hour = 18 + Math.floor(R() * 12);
  reports.push(point("w" + k, { depth_cm: depth, time: `2026-10-${hour < 24 ? 21 : 22} ${String(hour % 24).padStart(2, "0")}:${String(Math.floor(R() * 60)).padStart(2, "0")}`, source: R() < 0.5 ? "Aplikasi warga" : R() < 0.5 ? "Relawan" : "Linmas" }, x, y));
}

const fc = (features) => ({ type: "FeatureCollection", features });
const files = {
  "contours.json": fc(contourFeatures),
  "rivers.json": fc(riverFeatures),
  "water.json": fc(water),
  "roads.json": fc(roadFeatures),
  "kecamatan.json": fc(kecFeatures),
  "flood.json": fc(floodFeatures),
  "gauges.json": fc(gauges),
  "shelters.json": fc(shelters),
  "facilities.json": fc(pumps),
  "health.json": fc(health),
  "reports.json": fc(reports),
};
let total = 0;
for (const [name, data] of Object.entries(files)) {
  const text = JSON.stringify(data);
  total += text.length;
  writeFileSync(join(out, name), text);
  console.log(name.padEnd(16), String(data.features.length).padStart(5), "features", (text.length / 1024).toFixed(0).padStart(6), "KB");
}
console.log("contour vertices", contourVertices, "· total", (total / 1024).toFixed(0), "KB");
