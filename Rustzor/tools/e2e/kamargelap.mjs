// Drives the Kamar Gelap sample (samples/KamarGelap) with the mouse and keyboard and
// checks the pixels it reads back after each step: a brush stroke, a multiply layer
// filled inside a selection, an adjustment layer switched on and off, a Gaussian
// blur, undo and redo, a crop, a PNG export, and the autosave across a reload. It
// also times brush frames and the filters on the 2000 × 1500 sample.
// usage: node kamargelap.mjs <base-url>   (the built site served, e.g. by tools/dev/serve.mjs)
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5320").replace(/\/$/, "");
const browser = await launch();
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, acceptDownloads: true });
const page = await context.newPage();
const errors = [];
page.on("console", (m) => m.type() === "error" && !m.text().includes("Failed to load resource") && errors.push(m.text()));
page.on("pageerror", (e) => errors.push(e.message));

let failed = 0;
const check = (name, ok, detail = "") => {
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  (" + detail + ")" : ""}`);
  if (!ok) {
    failed++;
    if (errors.length) console.log("      console: " + errors.splice(0).slice(0, 3).join(" | "));
  }
};
const meta = () => page.evaluate(() => globalThis.__kg.meta);
const sample = (x, y) => page.evaluate(([x, y]) => globalThis.__kg.sampleAt(x, y), [x, y]);
const layerPx = (id, x, y) => page.evaluate(([id, x, y]) => globalThis.__kg.layerPixel(id, x, y), [id, x, y]);
const steps = () => page.evaluate(() => globalThis.__kg.history.length);
const idOf = async (name) => (await meta()).layers.find((l) => l.name === name)?.id;
const close = (a, b, tol = 4) => a.slice(0, 3).every((v, i) => Math.abs(v - b[i]) <= tol);
// Document pixels to page pixels.
const P = async (x, y) =>
  page.evaluate(([x, y]) => {
    const E = globalThis.__kg;
    const r = E.stage.getBoundingClientRect();
    return { x: r.left + E.view.x + x * E.view.zoom, y: r.top + E.view.y + y * E.view.zoom };
  }, [x, y]);
const drag = async (a, b, n = 16) => {
  const pa = await P(a.x, a.y);
  const pb = await P(b.x, b.y);
  await page.mouse.move(pa.x, pa.y);
  await page.mouse.down();
  for (let i = 1; i <= n; i++) {
    await page.mouse.move(pa.x + ((pb.x - pa.x) * i) / n, pa.y + ((pb.y - pa.y) * i) / n);
    await page.waitForTimeout(16);
  }
  await page.mouse.up();
  await page.waitForTimeout(200);
};
const key = async (k) => {
  await page.keyboard.press(k);
  await page.waitForTimeout(150);
};
const stats = () => page.evaluate(() => globalThis.__kgStats);
const pct = (a, p) => {
  const s = [...a].sort((x, y) => x - y);
  return s.length ? s[Math.min(s.length - 1, Math.floor((s.length * p) / 100))] : 0;
};
const variance = (id, x, y, w, h) =>
  page.evaluate(([id, x, y, w, h]) => {
    const d = globalThis.__kg.layerRegion(id, x, y, w, h);
    let s = 0;
    let s2 = 0;
    const n = d.length / 4;
    for (let i = 0; i < d.length; i += 4) {
      const v = d[i] * 0.3 + d[i + 1] * 0.59 + d[i + 2] * 0.11;
      s += v;
      s2 += v * v;
    }
    return s2 / n - (s / n) ** 2;
  }, [id, x, y, w, h]);
const setFg = async (hex) => {
  await page.click(".kg-tabs button:has-text('Colour')");
  await page.fill(".kg-hex", hex);
  await page.press(".kg-hex", "Enter");
  await page.evaluate(() => document.activeElement?.blur());
  await page.waitForTimeout(120);
};

// A clean browser: no picture left from an earlier run.
await page.goto(base + "/");
await page.evaluate(() => new Promise((r) => { const q = indexedDB.deleteDatabase("kamar-gelap"); q.onsuccess = q.onerror = q.onblocked = () => r(); }));
await page.goto(base + "/");
await page.waitForSelector("text=Open the sample");
await page.click("text=Open the sample");
await page.waitForFunction(() => globalThis.__kg?.meta?.layers?.length === 5 && globalThis.__kg.history.length >= 1, null, { timeout: 20000 });
await page.waitForTimeout(1200);
const renderer = await page.evaluate(() => globalThis.__kg.renderer);
check("the sample opens with five layers", (await meta()).layers.length === 5, renderer);
const photo = await idOf("Merapi, 05.40");

// 1. A brush stroke on the photo.
await setFg("#ff0000");
await key("b");
const before1 = await sample(600, 300);
const outside = await layerPx(photo, 600, 420);
const s0 = await steps();
await drag({ x: 300, y: 300 }, { x: 900, y: 300 }, 30);
const lp = await layerPx(photo, 600, 300);
check("a stroke paints the layer where it went", lp[0] >= 250 && lp[1] === 0 && lp[2] === 0 && lp[3] >= 250, lp.join(","));
check("and leaves it alone beside the stroke", close(await layerPx(photo, 600, 420), outside, 0));
const after1 = await sample(600, 300);
check("the composite turns red there", after1[0] > after1[1] + 120 && Math.abs(after1[0] - before1[0]) + Math.abs(after1[1] - before1[1]) > 60, before1.join(",") + " -> " + after1.join(","));
check("the stroke is one history step", (await steps()) === s0 + 1);
// A second stroke, timed with the GPU waited on: each frame stamps, uploads the touched region and composites it.
await page.evaluate(() => { globalThis.__kg.sync = true; globalThis.__kgStats.strokeFrames.length = 0; });
await drag({ x: 300, y: 380 }, { x: 1700, y: 380 }, 60);
await page.evaluate(() => { globalThis.__kg.sync = false; });
const st = await stats();
const brushMedian = pct(st.strokeFrames, 50);
const brushP95 = pct(st.strokeFrames, 95);
const brushFrames = st.strokeFrames.length;
await key("Control+z");

// 2. A new layer at the top, filled inside a rectangular selection, set to multiply.
await page.click(".kg-layer:has-text('Caption')");
await key("Control+Shift+N");
const top = (await meta()).active;
check("a new layer goes on top", (await meta()).layers.at(-1).id === top);
await key("m");
await drag({ x: 1200, y: 200 }, { x: 1500, y: 450 }, 10);
const sel = (await meta()).sel;
check("the marquee makes a selection", sel && sel.kind === "rect" && Math.abs(sel.w - 300) <= 2 && Math.abs(sel.h - 250) <= 2, sel ? sel.w + "x" + sel.h : "none");
const baseIn = await sample(1300, 300);
const baseOut = await sample(1000, 300);
await setFg("#80c0ff");
await key("Alt+Backspace");
await page.selectOption(".kg-layer-mix select", "multiply");
await page.waitForTimeout(250);
const mIn = await sample(1300, 300);
const expect = [baseIn[0] * 128 / 255, baseIn[1] * 192 / 255, baseIn[2]];
check("multiply darkens by the fill colour inside the selection", close(mIn, expect, 3), mIn.slice(0, 3).join(",") + " vs " + expect.map(Math.round).join(","));
check("and nothing changes outside it", close(await sample(1000, 300), baseOut, 0));
check("the layer pixel outside the selection is empty", (await layerPx(top, 1000, 300))[3] === 0);

// 3. A black & white adjustment, then hidden: the picture comes back exactly.
await key("Control+d");
const pre = await sample(800, 900);
await page.click("button[aria-label='New adjustment layer']");
await page.click(".kg-pop button:has-text('Black & White')");
await page.waitForTimeout(300);
const bw = await sample(800, 900);
check("black & white makes the picture grey", Math.abs(bw[0] - bw[1]) <= 2 && Math.abs(bw[1] - bw[2]) <= 2 && !close(bw, pre, 2), bw.slice(0, 3).join(","));
await page.click(".kg-layer.on .kg-eye");
await page.waitForTimeout(250);
check("hiding it restores every value", close(await sample(800, 900), pre, 0), (await sample(800, 900)).join(","));

// 4. Gaussian blur on the photo, timed.
await page.click(".kg-layer:has-text('Merapi, 05.40') .kg-layer-name");
const v0 = await variance(photo, 1400, 1150, 200, 200);
const filterTimes = {};
await page.evaluate(() => { globalThis.__kg.sync = true; });
const h0 = await steps();
await page.click(".kg-menu-btn:has-text('Filter')");
await page.click(".kg-pop button:has-text('Gaussian blur')");
await page.waitForTimeout(400);
await page.fill(".rz-modal input[type=range]", "20");
await page.waitForTimeout(900);
await page.click(".rz-modal button:has-text('Apply')");
await page.waitForTimeout(300);
filterTimes.blur20 = (await stats()).filters.at(-1);
const v1 = await variance(photo, 1400, 1150, 200, 200);
check("blur lowers the variance of the terraces", v1 < v0 * 0.6, Math.round(v0) + " -> " + Math.round(v1));
check("the blur is one history step", (await steps()) === h0 + 1);

// 5. Undo and redo.
await key("Control+z");
const vu = await variance(photo, 1400, 1150, 200, 200);
check("undo brings the sharp picture back", Math.abs(vu - v0) < 0.5, Math.round(vu) + "");
await key("Control+Shift+z");
const vr = await variance(photo, 1400, 1150, 200, 200);
check("redo blurs it again", Math.abs(vr - v1) < 0.5, Math.round(vr) + "");

// The other filters, for timing (each is undone afterwards).
for (const [name, label] of [["sharpen", "Sharpen"], ["noise", "Add noise"]]) {
  await page.click(".kg-menu-btn:has-text('Filter')");
  await page.click(`.kg-pop button:has-text('${label}')`);
  await page.waitForTimeout(700);
  await page.click(".rz-modal button:has-text('Apply')");
  await page.waitForTimeout(300);
  filterTimes[name] = (await stats()).filters.at(-1);
  await key("Control+z");
}

// 6. Crop.
await key("c");
await drag({ x: 100, y: 100 }, { x: 1100, y: 800 }, 10);
await key("Enter");
await page.waitForTimeout(400);
const m6 = await meta();
check("the crop makes the picture 1000 × 700", m6.w === 1000 && m6.h === 700, m6.w + "x" + m6.h);
const afterCrop = await sample(500, 200);

// 7. Export a PNG and decode it.
await page.click(".kg-export-btn");
await page.click(".rz-modal button:has-text('Export PNG')");
await page.waitForTimeout(1200);
const png = await page.evaluate(async () => {
  const b = globalThis.__kgLastExport;
  if (!b) return null;
  const img = await createImageBitmap(b);
  return { w: img.width, h: img.height, bytes: b.size, type: b.type };
});
check("the PNG decodes at 1000 × 700", png && png.w === 1000 && png.h === 700 && png.type === "image/png", png ? png.bytes + " bytes" : "no export");

// 8. The autosave survives a reload.
await page.waitForFunction(() => document.querySelector(".kg-save")?.textContent === "Saved in this browser", null, { timeout: 20000 });
const layersBefore = (await meta()).layers.length;
await page.reload();
await page.waitForFunction(() => globalThis.__kg?.meta?.w === 1000 && globalThis.__kg.history.length >= 1, null, { timeout: 20000 });
await page.waitForTimeout(800);
const m8 = await meta();
check("after a reload the cropped picture is back", m8.w === 1000 && m8.h === 700 && m8.layers.length === layersBefore, m8.layers.length + " layers");
check("with the same pixels", close(await sample(500, 200), afterCrop, 1), (await sample(500, 200)).join(",") + " vs " + afterCrop.join(","));

check("no console errors", errors.length === 0, errors.slice(0, 3).join(" | "));
console.log(`brush frame (stamp, upload, composite, GPU finished) on 2000 × 1500 with 5 layers: median ${brushMedian.toFixed(1)} ms, p95 ${brushP95.toFixed(1)} ms over ${brushFrames} frames`);
for (const [k, v] of Object.entries(filterTimes)) console.log(`filter ${k}: ${v ? Math.round(v.ms) + " ms (GPU passes " + Math.round(v.gpu) + " ms)" : "n/a"}`);
console.log(`renderer: ${renderer}`);
await browser.close();
console.log(failed ? `${failed} check(s) failed` : "all checks passed");
process.exit(failed ? 1 : 0);
