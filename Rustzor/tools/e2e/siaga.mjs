// Drives the Siaga Citarum sample (samples/Siaga) with the mouse and keyboard
// and checks what the map and the panels say against numbers worked out here
// from the same GeoJSON files: projection, pan and zoom, identify, the
// attribute table in step with the map, geodesic area, a buffer and a
// select-by-area query, GeoJSON import and export, drawing that survives a
// reload, reset, and how long a frame takes while panning and zooming.
// usage: node siaga.mjs <base-url>   (the built site served, e.g. by tools/dev/serve.mjs)
import { readFileSync } from "fs";
import { dirname, join } from "path";
import { fileURLToPath } from "url";
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5330").replace(/\/$/, "");
const data = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "samples", "Siaga", "wwwroot", "data");
const load = (name) => JSON.parse(readFileSync(join(data, name), "utf8")).features;

const browser = await launch();
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();
const errors = [];
page.on("console", (m) => m.type() === "error" && !m.text().includes("Failed to load resource") && errors.push(m.text()));
page.on("pageerror", (e) => errors.push(e.message));

let failed = 0;
const check = (name, ok, detail = "") => {
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  (" + detail + ")" : ""}`);
  if (!ok) failed++;
};
const R = 6378137, RM = 6371008.8, D2R = Math.PI / 180;
const hav = ([lo1, la1], [lo2, la2]) => {
  const a = Math.sin(((la2 - la1) * D2R) / 2) ** 2 + Math.cos(la1 * D2R) * Math.cos(la2 * D2R) * Math.sin(((lo2 - lo1) * D2R) / 2) ** 2;
  return 2 * RM * Math.asin(Math.sqrt(a));
};
const pip = ([x, y], r) => {
  let c = false;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) if (r[i][1] > y !== r[j][1] > y && x < ((r[j][0] - r[i][0]) * (y - r[i][1])) / (r[j][1] - r[i][1]) + r[i][0]) c = !c;
  return c;
};
const view = () => page.evaluate(() => globalThis.__sg.view());
const setView = async (lon, lat, zoom) => {
  await page.evaluate(([lon, lat, zoom]) => {
    const v = globalThis.__sg;
    [v.M.cx, v.M.cy] = v.toLocal(lon, lat);
    v.M.s = (256 * 2 ** zoom) / (2 * Math.PI * 6378137);
    v.M.inertia = null;
  }, [lon, lat, zoom]);
  // A no-op wheel makes the engine redraw through its own path.
  await page.locator(".sg-map").hover({ position: { x: 300, y: 400 } });
  await page.mouse.wheel(0, -0.0001);
  await page.waitForTimeout(350);
};
const at = (lon, lat) => page.evaluate(([lon, lat]) => globalThis.__sg.toScreen(lon, lat), [lon, lat]);
const tool = async (id) => {
  await page.click(`.sg-tool[data-tool="${id}"]`);
  await page.waitForTimeout(150);
};
const selected = () => page.evaluate(() => globalThis.__sg.selected());

// A fresh profile: no drawn or imported layers, the default view.
await page.goto(base + "/");
await page.waitForFunction(() => globalThis.__sg && document.querySelector(".sg-gauge"), null, { timeout: 30000 });
await page.waitForTimeout(600);

// ── projection ─────────────────────────────────────────────────────
{
  const r = await page.evaluate(() => {
    const v = globalThis.__sg;
    const [x, y] = v.merc(107.62, -6.99);
    let worst = 0;
    for (const [lon, lat] of [[107.3, -7.28], [107.62, -6.99], [107.98, -6.72], [0, 0], [-122.4, 37.8]]) {
      const back = v.toLonLat(...v.toLocal(lon, lat));
      worst = Math.max(worst, Math.abs(back[0] - lon), Math.abs(back[1] - lat));
    }
    return { x, y, worst };
  });
  const ex = R * 107.62 * D2R, ey = R * Math.log(Math.tan(Math.PI / 4 + (-6.99 * D2R) / 2));
  check("Web Mercator matches the EPSG:3857 formula", Math.abs(r.x - ex) < 1e-6 && Math.abs(r.y - ey) < 1e-6, `x ${r.x.toFixed(2)}, y ${r.y.toFixed(2)}`);
  check("projection round-trips to 1e-9°", r.worst < 1e-9, `worst ${r.worst.toExponential(1)}`);
}

// ── pan and zoom ───────────────────────────────────────────────────
await setView(107.64, -6.99, 11);
{
  const before = await view();
  const box = await page.locator(".sg-map").boundingBox();
  const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
  await page.mouse.move(cx, cy);
  await page.mouse.down();
  for (let i = 1; i <= 10; i++) await page.mouse.move(cx - i * 20, cy - i * 10);
  await page.mouse.up();
  await page.waitForTimeout(900);
  const panned = await view();
  check("dragging pans the map", panned.lon > before.lon + 0.01 && panned.lat < before.lat - 0.003, `${before.lon},${before.lat} → ${panned.lon},${panned.lat}`);
  await page.mouse.move(cx, cy);
  await page.mouse.wheel(0, -400);
  await page.waitForTimeout(300);
  const zoomed = await view();
  check("the wheel zooms in", zoomed.zoom > panned.zoom + 0.5, `z ${panned.zoom} → ${zoomed.zoom}`);
  const scale = await page.textContent(".sg-scale [data-text]");
  check("the scale bar follows", /\d+ (m|km)/.test(scale), scale);
}

// ── identify ───────────────────────────────────────────────────────
const gauges = load("gauges.json");
const dk = gauges.find((g) => g.properties.name.includes("Dayeuhkolot"));
await setView(dk.geometry.coordinates[0], dk.geometry.coordinates[1], 14);
{
  const p = await at(...dk.geometry.coordinates);
  await page.mouse.click(p.x, p.y);
  await page.waitForTimeout(300);
  const id = await page.getAttribute(".sg-feature", "data-feature").catch(() => null);
  const title = await page.textContent(".sg-feature .sg-title").catch(() => "");
  check("identify finds the gauge under the pointer", id === "pda/" + dk.id && title === dk.properties.name, `${id}: ${title}`);
  const status = await page.textContent(".sg-staff-status").catch(() => "");
  check("the gauge shows its alert level", status === dk.properties.status, status);
}

// ── the attribute table and the map stay in step ───────────────────
{
  await page.click('.sg-rail-group.end .sg-tool');
  await page.waitForSelector(".sg-table");
  await page.selectOption(".sg-table-head select", "kecamatan");
  await page.waitForTimeout(200);
  const rows = await page.locator(".sg-table tbody tr").count();
  check("the table lists every kecamatan", rows === load("kecamatan.json").length, `${rows} rows`);
  await page.fill('.sg-table-head input[type="search"]', "Bojongsoang");
  await page.waitForTimeout(200);
  await page.click(".sg-table tbody tr");
  await page.waitForTimeout(400);
  const kec = load("kecamatan.json").find((f) => f.properties.name === "Bojongsoang");
  const sel = await selected();
  check("a clicked row selects the feature on the map", sel.length === 1 && sel[0].layer === "kecamatan" && sel[0].ids[0] === kec.id, JSON.stringify(sel));
  const v = await view();
  const ring = kec.geometry.coordinates[0];
  check("and zooms to it", pip([v.lon, v.lat], ring) || hav([v.lon, v.lat], ring[0]) < 6000, `${v.lon}, ${v.lat}`);
  // The other way: pick a kecamatan on the map, its row lights up.
  await page.fill('.sg-table-head input[type="search"]', "");
  const dayeuh = load("kecamatan.json").find((f) => f.properties.name === "Dayeuhkolot");
  await setView(107.6, -6.975, 12.5);
  // A point inside Dayeuhkolot that no gauge, posko or road covers: walk the ring's samples.
  const inside = await page.evaluate((ring) => {
    const v = globalThis.__sg;
    const xs = ring.map((p) => p[0]), ys = ring.map((p) => p[1]);
    for (let a = 0.2; a < 0.8; a += 0.05)
      for (let b = 0.2; b < 0.8; b += 0.05) {
        const lon = Math.min(...xs) + a * (Math.max(...xs) - Math.min(...xs)), lat = Math.min(...ys) + b * (Math.max(...ys) - Math.min(...ys));
        let c = false;
        for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) if (ring[i][1] > lat !== ring[j][1] > lat && lon < ((ring[j][0] - ring[i][0]) * (lat - ring[i][1])) / (ring[j][1] - ring[i][1]) + ring[i][0]) c = !c;
        if (c) return [lon, lat];
      }
    return null;
  }, dayeuh.geometry.coordinates[0]);
  // Hide the layers above, so the click lands on the kecamatan itself.
  for (const id of ["pda", "posko", "kesehatan", "infrastruktur", "laporan", "jalan", "sungai", "genangan", "air"])
    await page.click(`[data-layer="${id}"] .sg-eye`);
  await page.waitForTimeout(200);
  const pp = await at(...inside);
  await page.mouse.click(pp.x, pp.y);
  await page.waitForTimeout(400);
  const rowSel = await page.locator(`.sg-table tbody tr.sel[data-id="${dayeuh.id}"]`).count();
  check("a feature picked on the map is the highlighted row", rowSel === 1);
  for (const id of ["pda", "posko", "kesehatan", "infrastruktur", "laporan", "jalan", "sungai", "genangan", "air"])
    await page.click(`[data-layer="${id}"] .sg-eye`);
  await page.click('.sg-table-head .sg-icon[aria-label="Close the table"]');
}

// ── geodesic area ─────────────────────────────────────────────────
{
  const [w, s, e, n] = [107.6, -6.98, 107.605, -6.975];
  await setView((w + e) / 2, (s + n) / 2, 16.2);
  await tool("measure-area");
  for (const [lon, lat] of [[w, n], [e, n], [e, s], [w, s]]) {
    const p = await at(lon, lat);
    await page.mouse.click(p.x, p.y);
    await page.waitForTimeout(90);
  }
  await page.keyboard.press("Enter");
  await page.waitForTimeout(300);
  const m2 = Number(await page.getAttribute(".sg-measure", "data-area-m2"));
  const expected = R * R * (e - w) * D2R * Math.abs(Math.sin(n * D2R) - Math.sin(s * D2R));
  check("a drawn square's geodesic area matches the sphere", Math.abs(m2 - expected) / expected < 0.01, `${Math.round(m2)} m² vs ${Math.round(expected)} m²`);
  const perim = Number(await page.getAttribute(".sg-measure", "data-length-m"));
  const ep = hav([w, n], [e, n]) + hav([e, n], [e, s]) + hav([e, s], [w, s]) + hav([w, s], [w, n]);
  check("its perimeter by Haversine", Math.abs(perim - ep) / ep < 0.01, `${Math.round(perim)} m vs ${Math.round(ep)} m`);
  await tool("measure-area");
}

// ── buffer: everything within 3 km of the Dayeuhkolot gauge ───────────
{
  await setView(dk.geometry.coordinates[0], dk.geometry.coordinates[1], 13.5);
  const p = await at(...dk.geometry.coordinates);
  await page.mouse.click(p.x, p.y);
  await page.waitForTimeout(250);
  await page.click(".sg-feature .sg-actions button:has-text('Buffer')");
  await page.waitForTimeout(200);
  await page.click(".sg-chips button:has-text('3,00 km')");
  await page.waitForTimeout(400);
  const sel = await selected();
  for (const [layer, file] of [["posko", "shelters.json"], ["kesehatan", "health.json"], ["infrastruktur", "facilities.json"]]) {
    const expected = load(file).filter((f) => hav(f.geometry.coordinates, dk.geometry.coordinates) <= 3000).map((f) => f.id).sort();
    const got = (sel.find((s) => s.layer === layer)?.ids ?? []).sort();
    check(`buffer finds the ${layer} within 3 km`, JSON.stringify(got) === JSON.stringify(expected), `${got.length}: ${got.join(",")}`);
  }
  await tool("buffer");
}

// ── select by area ───────────────────────────────────────────────────
{
  const ring = [[107.6, -6.968], [107.66, -6.968], [107.66, -7.004], [107.6, -7.004]];
  await setView(107.63, -6.986, 13.2);
  await tool("select-area");
  for (const c of ring) {
    const p = await at(...c);
    await page.mouse.click(p.x, p.y);
    await page.waitForTimeout(90);
  }
  await page.keyboard.press("Enter");
  await page.waitForTimeout(400);
  const sel = await selected();
  const expected = load("shelters.json").filter((f) => pip(f.geometry.coordinates, ring)).map((f) => f.id).sort();
  const got = (sel.find((s) => s.layer === "posko")?.ids ?? []).sort();
  check("select by area finds the posko inside", expected.length > 3 && JSON.stringify(got) === JSON.stringify(expected), `${got.length} of ${expected.length}`);
  const hits = Number(await page.getAttribute(".sg-query", "data-hits"));
  check("and lists them in the panel", hits >= expected.length, `${hits} features`);
  await page.keyboard.press("Escape");
}

// ── GeoJSON in, GeoJSON out ─────────────────────────────────────────
const sample = {
  type: "FeatureCollection",
  features: [
    { type: "Feature", properties: { name: "Dapur umum Cieunteung", team: "Tagana", meals: 450 }, geometry: { type: "Point", coordinates: [107.6118, -7.0024] } },
    { type: "Feature", properties: { name: "Jalur evakuasi 2", team: "Basarnas", meals: 0 }, geometry: { type: "LineString", coordinates: [[107.6201, -6.9874], [107.6255, -6.9931], [107.6302, -7.0052]] } },
    { type: "Feature", properties: { name: "Zona tanggul kritis", team: "BBWS", meals: 0 }, geometry: { type: "Polygon", coordinates: [[[107.6302, -6.9921], [107.6381, -6.9902], [107.6399, -6.9963], [107.6318, -6.9989], [107.6302, -6.9921]]] } },
  ],
};
{
  await page.click(".sg-import .sg-btn.ghost");
  await page.fill(".sg-paste", JSON.stringify(sample));
  await page.fill(".rz-modal .sg-field input", "Posko lapangan Tagana");
  await page.click("[data-import]");
  await page.waitForTimeout(600);
  const count = await page.evaluate(() => [...globalThis.__sg.M.layers.values()].find((l) => l.id.startsWith("u-") && l.id !== "u-catatan")?.features.length ?? 0);
  check("pasted GeoJSON becomes a layer on the map", count === 3, `${count} features`);
  await page.click('.sg-rail-group.end .sg-tool');
  await page.waitForSelector(".sg-table");
  await page.click(".sg-table-head .sg-btn:has-text('GeoJSON')");
  const text = await page.inputValue("textarea[data-export]");
  const out = JSON.parse(text);
  const same = out.features.length === 3 && out.features.every((f, i) => {
    const a = sample.features[i];
    return f.geometry.type === a.geometry.type && JSON.stringify(f.geometry.coordinates) === JSON.stringify(a.geometry.coordinates) &&
      f.properties.name === a.properties.name && f.properties.team === a.properties.team && f.properties.meals === a.properties.meals;
  });
  check("exporting it gives back the same GeoJSON", same, `${out.features.length} features`);
  await page.click(".rz-modal .sg-btn.ghost");
  await page.click('.sg-table-head .sg-icon[aria-label="Close the table"]');
}

// ── draw, edit, keep across a reload ─────────────────────────────────────
{
  await setView(107.64, -6.99, 14);
  await tool("polygon");
  for (const [lon, lat] of [[107.636, -6.986], [107.644, -6.986], [107.644, -6.993], [107.636, -6.993]]) {
    const p = await at(lon, lat);
    await page.mouse.click(p.x, p.y);
    await page.waitForTimeout(90);
  }
  await page.keyboard.press("Enter");
  await page.waitForTimeout(500);
  const drawn = await page.evaluate(() => globalThis.__sg.layerCount("u-catatan"));
  check("a drawn area lands in Catatan lapangan", drawn === 1, `${drawn}`);
  const area1 = Number(await page.getAttribute(".sg-feature [data-area-m2]", "data-area-m2"));
  // Drag one corner outwards with the vertex editor: the area grows.
  await page.click(".sg-feature .sg-actions button:has-text('Edit vertices')");
  await page.waitForTimeout(200);
  const c = await at(107.644, -6.993);
  await page.mouse.move(c.x, c.y);
  await page.mouse.down();
  for (let i = 1; i <= 8; i++) await page.mouse.move(c.x + i * 8, c.y + i * 8);
  await page.mouse.up();
  await page.waitForTimeout(500);
  const area2 = Number(await page.getAttribute(".sg-feature [data-area-m2]", "data-area-m2"));
  check("moving a vertex changes the area", area2 > area1 * 1.05, `${Math.round(area1)} → ${Math.round(area2)} m²`);
  await page.keyboard.press("Escape");
  await page.reload();
  await page.waitForFunction(() => globalThis.__sg && document.querySelector(".sg-gauge"), null, { timeout: 30000 });
  await page.waitForTimeout(800);
  const kept = await page.evaluate(() => [globalThis.__sg.layerCount("u-catatan"), [...globalThis.__sg.M.layers.keys()].filter((k) => k.startsWith("u-")).length]);
  check("after a reload the drawing and the imported layer are still there", kept[0] === 1 && kept[1] === 2, JSON.stringify(kept));
}

// ── location without permission ─────────────────────────────────────────
{
  await page.click('.sg-mbtn[aria-label="Show my location"]');
  await page.waitForTimeout(1500);
  const toast = await page.textContent(".rz-toast").catch(() => "");
  check("location without permission explains itself", /location|Location/.test(toast ?? ""), (toast ?? "").slice(0, 80));
}

// ── reset ─────────────────────────────────────────────────────────────
{
  await page.click(".sg-credit .sg-link");
  await page.click("[data-reset]");
  await page.waitForTimeout(1500);
  await page.waitForFunction(() => globalThis.__sg && document.querySelector(".sg-gauge"), null, { timeout: 30000 });
  await page.waitForTimeout(800);
  const left = await page.evaluate(() => [globalThis.__sg.layerCount("u-catatan"), [...globalThis.__sg.M.layers.keys()].filter((k) => k.startsWith("u-")).length]);
  check("reset clears what this browser kept", left[0] === 0 && left[1] === 1, JSON.stringify(left));
}

// ── frame time while panning and zooming ────────────────────────────────
{
  await setView(107.64, -6.99, 11.2);
  await page.evaluate(() => {
    globalThis.__sgStats.frames.length = 0;
    globalThis.__sgStats.intervals.length = 0;
    globalThis.__sgStats.last = 0;
  });
  const box = await page.locator(".sg-map").boundingBox();
  const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
  await page.mouse.move(cx, cy);
  await page.mouse.down();
  for (let i = 0; i < 90; i++) {
    await page.mouse.move(cx + Math.sin(i / 9) * 160, cy + Math.cos(i / 11) * 110);
    await page.waitForTimeout(16);
  }
  await page.mouse.up();
  for (let i = 0; i < 24; i++) {
    await page.mouse.wheel(0, i < 12 ? -60 : 60);
    await page.waitForTimeout(16);
  }
  await page.waitForTimeout(400);
  const s = await page.evaluate(() => ({ frames: [...globalThis.__sgStats.frames], vertices: [...globalThis.__sg.M.layers.values()].reduce((n, L) => n + L.features.reduce((m, f) => m + (f.geo.t === "pt" ? 1 : f.geo.t === "ln" ? f.geo.parts.reduce((k, p) => k + p.length, 0) : f.geo.polys.reduce((k, p) => k + p.reduce((q, r) => q + r.length, 0), 0)), 0), 0) }));
  const sorted = s.frames.sort((a, b) => a - b);
  const q = (p) => sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))];
  check("a frame draws well inside 16 ms while panning and zooming", q(0.5) < 16 && sorted.length > 40,
    `${sorted.length} frames, ${s.vertices.toLocaleString("en")} vertices: median ${q(0.5).toFixed(1)} ms, p95 ${q(0.95).toFixed(1)} ms`);
}

check("no console errors", errors.length === 0, errors.slice(0, 3).join(" | "));
await browser.close();
console.log(failed ? `${failed} check(s) failed` : "all checks passed");
process.exit(failed ? 1 : 0);
