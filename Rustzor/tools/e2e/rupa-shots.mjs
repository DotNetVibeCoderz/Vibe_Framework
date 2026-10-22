// The Rupa screenshots for the docs: the editor with a selection, snapping and
// spacing while dragging, an instance in the dark theme, and the file list.
// usage: node rupa-shots.mjs <base-url> <out-dir>
import { launch } from "./chrome.mjs";
import { join } from "node:path";

const base = (process.argv[2] ?? "http://localhost:5310").replace(/\/$/, "");
const out = process.argv[3] ?? "docs/images";
const browser = await launch();

async function open(theme) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
  const page = await context.newPage();
  await page.goto(base + "/");
  await page.evaluate((t) => {
    localStorage.clear();
    if (t) localStorage.setItem("rz-theme", t);
  }, theme);
  return page;
}
const ready = async (page) => {
  await page.waitForFunction(() => globalThis.__rupaInspect && document.querySelector(".rp-world g"), null, { timeout: 15000 });
  await page.waitForTimeout(2500);
};
const find = (s, name) => {
  const walk = (ls, parent) => {
    for (const l of ls) {
      if (l.name === name) return { ...l, parent };
      const r = l.children && walk(l.children, l);
      if (r) return r;
    }
  };
  return walk(s.doc.layers, null);
};
const at = async (page, wx, wy) => {
  const s = await page.evaluate(() => globalThis.__rupaInspect());
  const b = await page.evaluate(() => document.querySelector(".rp-canvas").getBoundingClientRect().toJSON());
  return { x: b.x + wx * s.zoom + s.panX, y: b.y + wy * s.zoom + s.panY };
};
const zoomAround = async (page, wx, wy, zoom) => {
  const s = await page.evaluate(() => globalThis.__rupaInspect());
  const p = await at(page, wx, wy);
  await page.mouse.move(p.x, p.y);
  await page.keyboard.down("Control");
  await page.mouse.wheel(0, -100 * Math.log(zoom / s.zoom));
  await page.keyboard.up("Control");
  await page.waitForTimeout(150);
  const c = await page.evaluate(() => {
    const r = document.querySelector(".rp-canvas").getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
  });
  const q = await at(page, wx, wy);
  await page.mouse.move(c.x, c.y);
  await page.mouse.wheel(q.x - c.x, q.y - c.y);
  await page.waitForTimeout(250);
};

// 1. The editor: the three screens, the next-train card selected.
let page = await open("light");
await page.goto(base + "/file/lintas");
await ready(page);
await zoomAround(page, 690, 400, 0.6);
let s = await page.evaluate(() => globalThis.__rupaInspect());
let home = s.doc.layers.find((l) => l.kind === "Frame" && l.name === "Home");
let card = home.children.find((c) => c.name === "Next train");
let p = await at(page, home.x + card.x + card.w / 2, home.y + card.y + 150);
await page.mouse.click(p.x, p.y);
await page.mouse.move(p.x + 260, p.y + 40);
await page.waitForTimeout(300);
await page.screenshot({ path: join(out, "sample-rupa.png") });

// 2. Snapping: the service notice dragged towards the shortcuts, at 160%.
await page.keyboard.press("Escape");
await zoomAround(page, 195, 640, 1.6);
s = await page.evaluate(() => globalThis.__rupaInspect());
const notice = find(s, "Service notice");
const from = await at(page, notice.x + 150, notice.y + 32);
const to = await at(page, notice.x + 150 - 13, notice.y + 32 - 9);
await page.mouse.move(from.x, from.y);
await page.mouse.down();
for (let i = 1; i <= 14; i++) await page.mouse.move(from.x + ((to.x - from.x) * i) / 14, from.y + ((to.y - from.y) * i) / 14);
await page.waitForTimeout(250);
await page.screenshot({ path: join(out, "sample-rupa-guides.png") });
await page.keyboard.press("Escape");
await page.mouse.up();
await page.context().close();

// 3. Dark: a timetable row, an instance of a component, with its text overrides.
page = await open("dark");
await page.goto(base + "/file/lintas");
await ready(page);
await zoomAround(page, 840, 520, 0.95);
s = await page.evaluate(() => globalThis.__rupaInspect());
// Top-level frames by name (the tab bars have text layers called "Timetable" too).
const sched = s.doc.layers.find((l) => l.kind === "Frame" && l.name === "Timetable");
const list = sched.children.find((c) => c.name === "Departures");
const row = list.children[2];
p = await at(page, sched.x + list.x + row.x + 60, sched.y + list.y + row.y + 30);
// A click selects the list (a child of the screen); a double-click goes into it.
await page.mouse.dblclick(p.x, p.y);
await page.mouse.move(p.x + 500, p.y + 300);
await page.waitForTimeout(300);
await page.screenshot({ path: join(out, "sample-rupa-dark.png") });
await page.context().close();

// 4. The file list, with the sample and one new file.
page = await open("light");
await page.goto(base + "/");
await page.waitForTimeout(2500);
await page.locator(".rp-btn.primary", { hasText: "New design file" }).click();
await ready(page);
await page.goto(base + "/");
await page.waitForTimeout(2000);
await page.screenshot({ path: join(out, "sample-rupa-files.png") });
await page.context().close();

await browser.close();
console.log("rupa screenshots written to " + out);
