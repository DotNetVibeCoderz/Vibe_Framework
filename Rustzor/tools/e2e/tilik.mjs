// Drives the Tilik sample (samples/Tilik), a CAD design-review viewer, with the mouse and
// keyboard, and checks what it draws and reports against the model file itself: the part
// count, picking a known point, hide and isolate (the draw list and the pixels), the section
// plane, a vertex-snapped measurement against the GLB's own coordinates, the explode slider,
// a note that survives a reload, a saved view, the bill of materials CSV, an STL import, a
// PNG screenshot, and how long frames take while orbiting.
// usage: node tilik.mjs <base-url>   (the built site served, e.g. by tools/dev/serve.mjs)
import { readFileSync } from "fs";
import { dirname, join } from "path";
import { fileURLToPath } from "url";
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5340").replace(/\/$/, "");
const glbPath = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "samples", "Tilik", "wwwroot", "models", "rw63.glb");

// The GLB's own facts, read here independently of the app.
const glb = readFileSync(glbPath);
const gltf = JSON.parse(glb.subarray(20, 20 + glb.readUInt32LE(12)).toString("utf8"));
const meshNodes = gltf.nodes.filter((n) => n.mesh !== undefined);

const browser = await launch();
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, acceptDownloads: true });
const page = await context.newPage();
const errors = [];
page.on("console", (m) => m.type() === "error" && !m.text().includes("Failed to load resource") && errors.push(m.text()));
page.on("pageerror", (e) => errors.push(e.message));

let failed = 0;
const check = (name, ok, detail = "") => {
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  (" + detail + ")" : ""}`);
  if (!ok) failed++;
};
const ready = () => page.waitForFunction(() => globalThis.__tk?.M?.parts?.length > 0 && document.querySelector(".tk-count"), null, { timeout: 30000 });
const idx = (name) => page.evaluate((n) => globalThis.__tk.M.parts.findIndex((p) => p.name === n), name);
const drawn = () => page.evaluate(() => { globalThis.__tk.redraw(); return globalThis.__tk.drawn(); });
const hitAt = (x, y) => page.evaluate(([x, y]) => globalThis.__tk.hitAt(x, y), [x, y]);
const tool = async (id) => { await page.click(`.tk-tool[data-tool-id="${id}"]`); await page.waitForTimeout(120); };
const row = (name) => page.locator(`.tk-row[data-node="${name}"]`);
const expand = async (name) => {
  const caret = row(name).locator(".tk-caret");
  if (!(await caret.getAttribute("class")).includes("open")) await caret.click();
};
/** A point on a part's surface that the view shows: try its centre, then around it. */
const visiblePoint = async (name) => page.evaluate((name) => {
  const tk = globalThis.__tk, M = tk.M;
  const p = M.parts.find((q) => q.name === name);
  const r = M.canvas.getBoundingClientRect();
  const c = tk.partScreen(p.index);
  for (let ring = 0; ring < 8; ring++)
    for (let a = 0; a < 8; a++) {
      const x = r.left + c.x + Math.cos(a) * ring * 6, y = r.top + c.y + Math.sin(a) * ring * 6;
      const h = tk.hitAt(x, y);
      if (h && h.name === name) return { x, y };
    }
  return null;
}, name);

// 1. the model loads with every part
await page.goto(base + "/");
await ready();
const partCount = await page.evaluate(() => globalThis.__tk.M.parts.length);
check("the model loads with every part", partCount === meshNodes.length && partCount === 36, `${partCount} parts drawn, ${meshNodes.length} meshes in the file`);
check("the tree counts the parts", (await page.textContent(".tk-count")).trim() === "36 parts");
const loadMs = await page.evaluate(() => globalThis.__tk.stats.loadMs);
check("the GLB loads quickly", loadMs < 3000, `${Math.round(loadMs)} ms from fetch to first frame`);

// 2. picking a known point selects that part, and the tree follows
const breather = await visiblePoint("Breather");
check("the breather is visible from the opening view", !!breather);
await page.mouse.click(breather.x, breather.y);
await page.waitForTimeout(200);
check("a click on the breather selects it", (await page.textContent(".tk-part-name")).trim() === "Breather");
check("the tree opens the branch and marks the row", (await row("Breather").getAttribute("class")).includes("sel") && (await row("Accessories").getAttribute("aria-expanded")) === "true");
const sel = await page.evaluate(() => [...globalThis.__tk.M.selected]);
check("the engine draws it as selected", sel.length === 1 && sel[0] === (await idx("Breather")));

// 3. hide and isolate change what is drawn
await expand("RW-63 reducer");
await expand("Housing");
const coverPt = await visiblePoint("Housing cover");
const before = await page.evaluate(([x, y]) => globalThis.__tk.pixel(x, y), [coverPt.x, coverPt.y]);
await row("Housing cover").locator(".tk-row-act").first().click();
await page.waitForTimeout(250);
const afterHide = await drawn();
const pxHidden = await page.evaluate(([x, y]) => globalThis.__tk.pixel(x, y), [coverPt.x, coverPt.y]);
const diff = (a, b) => Math.abs(a[0] - b[0]) + Math.abs(a[1] - b[1]) + Math.abs(a[2] - b[2]);
check("hiding the cover takes it out of the draw list", !afterHide.includes("Housing cover") && afterHide.length === 35);
check("and out of the picture", diff(before, pxHidden) > 20, `pixel ${before.slice(0, 3)} -> ${pxHidden.slice(0, 3)}`);
const hitInside = await hitAt(coverPt.x, coverPt.y);
check("the point now hits what was under the cover", hitInside && hitInside.name !== "Housing cover", hitInside?.name);
await row("Output shaft assembly").locator(".tk-row-act.iso").click({ force: true });
await page.waitForTimeout(300);
const iso = await drawn();
const outputParts = ["Output shaft", "Gear z38", "Output bearing, front", "Output bearing, rear", "Output blind cap", "Output through cap", "Output oil seal", "Output key"];
check("isolating the output shaft assembly draws only its 8 parts", iso.length === 8 && outputParts.every((n) => iso.includes(n)), iso.join(", "));
await page.click(".tk-panel-head .tk-link"); // Show all
await page.waitForTimeout(300);
check("show all brings back all 36", (await drawn()).length === 36);
await page.click('.tk-cube-btns button[aria-label="Isometric view"]');
await page.waitForTimeout(600);

// 4. the section plane removes geometry on one side
const coverTop = await visiblePoint("Housing cover");
const beforeCut = await hitAt(coverTop.x, coverTop.y);
await tool("section");
await page.click('.tk-sectionbar .tk-seg button:has-text("Y")');
await page.waitForTimeout(300);
const afterCut = await hitAt(coverTop.x, coverTop.y);
const plane = await page.evaluate(() => { const M = globalThis.__tk.M; return M.bbox.lo[1] + (M.bbox.hi[1] - M.bbox.lo[1]) * M.section.offset; });
check("before the cut, the point is on the cover", beforeCut?.name === "Housing cover");
check("the cut removes everything above the plane", afterCut && (afterCut.cap || afterCut.point[1] <= plane + 1e-6) && afterCut.name !== "Housing cover" || (afterCut && afterCut.point[1] <= plane + 1e-6), afterCut ? `${afterCut.name}${afterCut.cap ? " (cap)" : ""} at y ${(afterCut.point[1] * 1000).toFixed(1)} mm, plane ${(plane * 1000).toFixed(1)} mm` : "nothing");
// dragging in the view moves the plane
const vp = await page.locator(".tk-vp").boundingBox();
const off0 = await page.evaluate(() => globalThis.__tk.M.section.offset);
await page.mouse.move(vp.x + vp.width * 0.3, vp.y + vp.height * 0.5);
await page.mouse.down();
for (let i = 1; i <= 10; i++) await page.mouse.move(vp.x + vp.width * 0.3, vp.y + vp.height * 0.5 - i * 8);
await page.mouse.up();
const off1 = await page.evaluate(() => globalThis.__tk.M.section.offset);
check("dragging in the view moves the section", off1 > off0 + 0.05, `${off0.toFixed(2)} -> ${off1.toFixed(2)}`);
await page.click('.tk-sectionbar button[aria-label="Remove the section"]');
await tool("select");

// 5. a measurement between two known vertices: the same corner of two bolt heads, 140 mm apart in the model
await page.click('.tk-tabs button[data-tab="views"]');
await page.click('.tk-presets button:has-text("Front")');
await page.waitForTimeout(600);
const corners = await page.evaluate(() => {
  const M = globalThis.__tk.M;
  const corner = (name) => {
    const p = M.parts.find((q) => q.name === name), V = p.weld.verts;
    let best = null;
    for (let k = 0; k < V.length; k += 3) {
      const v = [V[k], V[k + 1], V[k + 2]];
      if (!best || v[1] > best[1] + 1e-6 || (Math.abs(v[1] - best[1]) < 1e-6 && v[0] > best[0])) best = v;
    }
    return best;
  };
  const a = corner("Cover bolt 3"), b = corner("Cover bolt 7");
  return { a, b, sa: globalThis.__tk.toScreen(a), sb: globalThis.__tk.toScreen(b) };
});
const expected = Math.hypot(corners.a[0] - corners.b[0], corners.a[1] - corners.b[1], corners.a[2] - corners.b[2]) * 1000;
await tool("measure-distance");
// click a few pixels inside each corner: the snap must find the exact vertex
await page.mouse.click(corners.sa.x - 3, corners.sa.y + 2);
await page.mouse.click(corners.sb.x - 2, corners.sb.y + 3);
await page.waitForTimeout(250);
const measured = await page.evaluate(() => [...document.querySelectorAll(".tk-mlist .tk-fcf b")].map((b) => b.textContent));
const value = parseFloat(measured[0]);
check("a distance between two snapped vertices matches the model", Math.abs(value - expected) < 0.01, `${measured[0]} measured, ${expected.toFixed(2)} mm in the file`);
// an edge and an angle on the input key, a 6 × 6 × 36 mm box: its top edges and corners, read from the file
await page.click('.tk-cube-btns button[aria-label="Isometric view"]');
await page.click('.tk-cube-btns button[aria-label="Fit everything shown"]');
await page.waitForTimeout(600);
const key = await page.evaluate(() => {
  const M = globalThis.__tk.M, p = M.parts.find((q) => q.name === "Input key"), V = p.weld.verts;
  const pts = [];
  for (let k = 0; k < V.length; k += 3) pts.push([V[k], V[k + 1], V[k + 2]]);
  const top = Math.max(...pts.map((v) => v[1]));
  const t = pts.filter((v) => Math.abs(v[1] - top) < 1e-6);
  const maxX = Math.max(...t.map((v) => v[0])), minX = Math.min(...t.map((v) => v[0])), maxZ = Math.max(...t.map((v) => v[2])), minZ = Math.min(...t.map((v) => v[2]));
  const A = [maxX, top, maxZ], O = [maxX, top, minZ], B = [minX, top, minZ];
  const s = globalThis.__tk.toScreen;
  return { len: (maxZ - minZ) * 1000, mid: s([maxX, top, (maxZ + minZ) / 2]), A: s(A), O: s(O), B: s(B), Ac: s([maxX - 0.0008, top, maxZ - 0.002]), Oc: s([maxX - 0.0008, top, minZ + 0.002]), Bc: s([minX + 0.0008, top, minZ + 0.002]) };
});
await tool("measure-edge");
await page.mouse.click(key.mid.x - 1, key.mid.y + 1);
await page.waitForTimeout(200);
await tool("measure-angle");
for (const p of [key.Ac, key.Oc, key.Bc]) await page.mouse.click(p.x, p.y);
await page.waitForTimeout(250);
const labels = await page.evaluate(() => [...document.querySelectorAll(".tk-mlist .tk-fcf b")].map((b) => b.textContent));
check("an edge measures its whole length", labels[1] === key.len.toFixed(2) + " mm", `${labels[1]} measured, ${key.len.toFixed(2)} mm in the file`);
check("an angle through three snapped corners", labels[2] === "90.0°", labels[2]);
await tool("select");

// 6. the explode slider moves parts
const gearBefore = await page.evaluate(() => globalThis.__tk.partScreen(globalThis.__tk.M.parts.findIndex((p) => p.name === "Gear z38")));
await page.locator(".tk-explode input").fill("70");
await page.waitForTimeout(300);
const exploded = await page.evaluate(() => { const M = globalThis.__tk.M; const p = M.parts.find((q) => q.name === "Gear z38"); return Math.hypot(...p.offset) * 1000; });
const gearAfter = await page.evaluate(() => globalThis.__tk.partScreen(globalThis.__tk.M.parts.findIndex((p) => p.name === "Gear z38")));
check("the explode slider moves parts", exploded > 20 && Math.hypot(gearAfter.x - gearBefore.x, gearAfter.y - gearBefore.y) > 15, `gear moved ${exploded.toFixed(1)} mm, ${Math.hypot(gearAfter.x - gearBefore.x, gearAfter.y - gearBefore.y).toFixed(0)} px`);
await page.locator(".tk-explode input").fill("0");
await page.waitForTimeout(200);

// 7. a note survives a reload; so does a saved view
await page.click('.tk-cube-btns button[aria-label="Isometric view"]');
await page.waitForTimeout(600);
await tool("note");
const coverForNote = await visiblePoint("Housing cover");
await page.mouse.click(coverForNote.x, coverForNote.y);
await page.waitForTimeout(200);
const text = "Add a lifting eye on the cover: 19.7 kg is over the one-hand limit.";
await page.fill(".tk-noteform textarea", text);
await page.click('.tk-noteform button:has-text("Pin note")');
await page.waitForTimeout(200);
await tool("select");
await page.click('.tk-tabs button[data-tab="views"]');
await page.fill(".tk-inline input", "Cover from the iso corner");
await page.click('.tk-inline button:has-text("Save")');
await page.waitForTimeout(200);
await page.reload();
await ready();
await page.click('.tk-tabs button[data-tab="notes"]');
const notes = await page.evaluate(() => [...document.querySelectorAll(".tk-note p")].map((p) => p.textContent));
const pins = await page.evaluate(() => globalThis.__tk.M.notes.length);
check("a note survives a reload", notes.includes(text) && pins === 3, `${notes.length} notes listed, ${pins} pins`);
await page.click('.tk-tabs button[data-tab="views"]');
check("a saved view survives a reload", (await page.locator('.tk-viewrow[data-view="Cover from the iso corner"]').count()) === 1);

// 8. the bill of materials as CSV
await page.click('a[href="bom"]');
await page.waitForSelector(".tk-table tbody tr");
const [csvDl] = await Promise.all([page.waitForEvent("download"), page.click('button:has-text("Download CSV")')]);
const csv = readFileSync(await csvDl.path(), "utf8").trim().split(/\r?\n/);
const parseCsv = (line) => {
  const out = [];
  let cur = "", q = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (q) {
      if (ch === '"' && line[i + 1] === '"') { cur += '"'; i++; }
      else if (ch === '"') q = false;
      else cur += ch;
    } else if (ch === '"') q = true;
    else if (ch === ",") { out.push(cur); cur = ""; }
    else cur += ch;
  }
  out.push(cur);
  return out;
};
const rows = csv.slice(1).map(parseCsv);
const qty = rows.reduce((s, r) => s + Number(r[5]), 0);
const bolts = rows.find((r) => r[1] === "ISO 4017-M10x22");
const brg = rows.find((r) => r[1] === "6204-2RS");
check("the BOM CSV has a row per part number", csv[0].startsWith("Item,Part number") && rows.length === 23, `${rows.length} rows`);
check("and the quantities add up to the 36 parts", qty === 36 && bolts?.[5] === "8" && brg?.[5] === "4", `qty ${qty}, bolts ${bolts?.[5]}, 6204 bearings ${brg?.[5]}`);

// 9. an STL import: a 20 mm cube, binary
await page.click('a[href="./"], a.tk-btn[href=""]');
await ready();
const tris = [
  [0, 0, 0, 1, 1, 0, 1, 0, 0], [0, 0, 0, 0, 1, 0, 1, 1, 0], [0, 0, 1, 1, 0, 1, 1, 1, 1], [0, 0, 1, 1, 1, 1, 0, 1, 1],
  [0, 0, 0, 1, 0, 0, 1, 0, 1], [0, 0, 0, 1, 0, 1, 0, 0, 1], [0, 1, 0, 1, 1, 1, 1, 1, 0], [0, 1, 0, 0, 1, 1, 1, 1, 1],
  [0, 0, 0, 0, 0, 1, 0, 1, 1], [0, 0, 0, 0, 1, 1, 0, 1, 0], [1, 0, 0, 1, 1, 0, 1, 1, 1], [1, 0, 0, 1, 1, 1, 1, 0, 1],
];
const stl = Buffer.alloc(84 + tris.length * 50);
stl.write("tilik test cube", 0);
stl.writeUInt32LE(tris.length, 80);
tris.forEach((t, i) => t.forEach((v, k) => stl.writeFloatLE(v * 20, 84 + i * 50 + 12 + k * 4)));
const [chooser] = await Promise.all([page.waitForEvent("filechooser"), page.click('.tk-tree-foot button:has-text("Open")')]);
await chooser.setFiles({ name: "bracket-test.stl", mimeType: "model/stl", buffer: stl });
await page.waitForFunction(() => globalThis.__tk.M.file === "bracket-test.stl" && document.querySelector(".tk-count")?.textContent.includes("1 part"), null, { timeout: 10000 });
const stlVol = await page.evaluate(() => globalThis.__tk.M.parts[0].volume * 1e6);
check("an STL import replaces the model", (await page.evaluate(() => globalThis.__tk.M.parts.length)) === 1, `volume ${stlVol.toFixed(3)} cm³`);
check("and its volume is the cube's 8 cm³", Math.abs(stlVol - 8) < 1e-6);

// 10. a screenshot of the view is a real PNG
const [shot] = await Promise.all([page.waitForEvent("download"), page.click('.tk-dock button[aria-label="Save a picture of the view"]')]);
const png = readFileSync(await shot.path());
const w = png.readUInt32BE(16), h = png.readUInt32BE(20);
check("the screenshot is a PNG of the view", png.subarray(1, 4).toString() === "PNG" && w >= 600 && h >= 400, `${w} × ${h}, ${png.length} bytes`);

// 11. frame time while orbiting the full model
await page.reload();
await ready();
await page.waitForTimeout(500);
const box = await page.locator(".tk-vp").boundingBox();
await page.evaluate(() => { globalThis.__tk.stats.frames.length = 0; globalThis.__orbit = []; let last = performance.now(); const tick = (t) => { globalThis.__orbit.push(t - last); last = t; if (globalThis.__orbit.length < 400) requestAnimationFrame(tick); }; requestAnimationFrame(tick); });
await page.mouse.move(box.x + box.width * 0.5, box.y + box.height * 0.5);
await page.mouse.down();
for (let i = 0; i < 90; i++) {
  await page.mouse.move(box.x + box.width * 0.5 + Math.sin(i / 9) * 220, box.y + box.height * 0.5 + Math.cos(i / 13) * 60);
  await page.waitForTimeout(16);
}
await page.mouse.up();
const ft = await page.evaluate(() => {
  const f = [...globalThis.__tk.stats.frames].sort((a, b) => a - b);
  const iv = [...globalThis.__orbit].slice(5).sort((a, b) => a - b);
  const q = (arr, p) => arr[Math.min(arr.length - 1, Math.floor(arr.length * p))];
  return { n: f.length, median: q(f, 0.5), p95: q(f, 0.95), interval: q(iv, 0.5), interval95: q(iv, 0.95) };
});
console.log(`      frame interval while orbiting: ${ft.interval.toFixed(1)} ms median, ${ft.interval95.toFixed(1)} ms p95 (${(1000 / ft.interval).toFixed(0)} fps)`);
check("orbiting stays inside a 60 fps frame", ft.median < 16.7, `${ft.n} frames, draw ${ft.median.toFixed(1)} ms median, ${ft.p95.toFixed(1)} ms p95`);

check("no console errors", errors.length === 0, errors.slice(0, 3).join(" | "));
await browser.close();
console.log(failed ? `${failed} check(s) failed` : "all checks passed");
process.exit(failed ? 1 : 0);
