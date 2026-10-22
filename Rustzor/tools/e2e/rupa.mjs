// Drives the Rupa sample (samples/Rupa) like a person would, with the mouse and
// keyboard, and checks the document model after each step: drawing, snapping,
// resizing, grouping, undo and redo, a component change reaching its instances,
// text, the pen, SVG and PNG export, autosave across a reload, and how long a
// drag takes with 300 shapes on the page.
// usage: node rupa.mjs <base-url>   (the built site served, e.g. by tools/dev/serve.mjs)
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5310").replace(/\/$/, "");
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
const inspect = () => page.evaluate(() => globalThis.__rupaInspect());
const walk = (layers, out = [], parent = null) => {
  for (const l of layers) {
    out.push({ ...l, parent });
    if (l.children) walk(l.children, out, l);
  }
  return out;
};
const all = (s) => walk(s.doc.layers);
const byName = (s, name) => all(s).find((n) => n.name === name);
const box = async () => page.evaluate(() => {
  const r = document.querySelector(".rp-canvas").getBoundingClientRect();
  return { x: r.left, y: r.top };
});
// Canvas coordinates to page pixels.
const S = async (wx, wy) => {
  const s = await inspect();
  const b = await box();
  return { x: b.x + wx * s.zoom + s.panX, y: b.y + wy * s.zoom + s.panY };
};
const drag = async (a, b, steps = 12, mid) => {
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  for (let i = 1; i <= steps; i++) {
    await page.mouse.move(a.x + ((b.x - a.x) * i) / steps, a.y + ((b.y - a.y) * i) / steps);
    if (i === steps - 1 && mid) await mid();
  }
  await page.waitForTimeout(60);
  await page.mouse.up();
  await page.waitForTimeout(80);
};
const near = (a, b, eps = 0.6) => Math.abs(a - b) <= eps;

// A fresh browser profile: the sample file is created on first visit.
await page.goto(base + "/");
await page.evaluate(() => localStorage.clear());
await page.goto(base + "/file/lintas");
await page.waitForFunction(() => globalThis.__rupaInspect && document.querySelector(".rp-world g"), null, { timeout: 15000 });
await page.waitForTimeout(2500);

let s = await inspect();
check("the sample opens with its four screens and components", s.doc.layers.filter((l) => l.kind === "Frame").length >= 8 && s.count > 120, `${s.count} layers`);
const rows = await page.evaluate(() => document.querySelectorAll('[data-n] [data-n] [data-n]').length);
check("instances draw their component's layers", rows > 50, `${rows} nested layers drawn`);

// Zoom to 100% at the middle of the canvas, then scroll an empty part of the page into view.
const centre = await page.evaluate(() => {
  const r = document.querySelector(".rp-canvas").getBoundingClientRect();
  return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
});
await page.mouse.move(centre.x, centre.y);
const before = await inspect();
const under = { x: (centre.x - (await box()).x - before.panX) / before.zoom, y: (centre.y - (await box()).y - before.panY) / before.zoom };
await page.keyboard.down("Control");
await page.mouse.wheel(0, -100 * Math.log(1 / s.zoom));
await page.keyboard.up("Control");
await page.waitForTimeout(200);
s = await inspect();
const still = { x: (centre.x - (await box()).x - s.panX) / s.zoom, y: (centre.y - (await box()).y - s.panY) / s.zoom };
check("Ctrl+wheel zooms towards the pointer", near(s.zoom, 1, 0.08) && near(still.x, under.x, 2) && near(still.y, under.y, 2), `zoom ${s.zoom.toFixed(3)}, point under the pointer moved ${Math.hypot(still.x - under.x, still.y - under.y).toFixed(2)}`);
let p = await S(360, 1300);
await page.mouse.wheel(p.x - centre.x, p.y - centre.y);
await page.waitForTimeout(200);
p = await S(360, 1300);
check("the wheel pans the canvas", Math.hypot(p.x - centre.x, p.y - centre.y) < 2);

// Draw an ellipse and a rectangle.
await page.keyboard.press("o");
await drag(await S(60, 1350), await S(140, 1430));
await page.keyboard.press("r");
await drag(await S(300, 1200), await S(400, 1260));
s = await inspect();
const ell = all(s).filter((n) => n.kind === "Ellipse" && n.parent === null).at(-1);
let rect = all(s).filter((n) => n.kind === "Rect" && n.parent === null).at(-1);
check("O and a drag draw an ellipse", ell && near(ell.w, 80, 1.5) && near(ell.h, 80, 1.5), ell ? `${ell.w}×${ell.h}` : s.doc.layers.slice(-3).map((l) => l.kind + " " + l.x + "," + l.y).join("; "));
check("R and a drag draw a rectangle", rect && near(rect.w, 100, 1.5) && near(rect.h, 60, 1.5), rect ? `${rect.w}×${rect.h} at ${rect.x},${rect.y}` : "none");

// Move the rectangle until its left edge is 2 px from the ellipse's: it snaps.
let guides = 0;
const from = await S(rect.x + rect.w / 2, rect.y + rect.h / 2);
const to = await S(ell.x + 2 + rect.w / 2, rect.y + rect.h / 2);
await drag(from, to, 16, async () => {
  guides = await page.evaluate(() => document.querySelectorAll(".rp-guide, .rp-gap").length);
});
s = await inspect();
rect = byName(s, rect.name);
check("dragging snaps the left edge to the ellipse's", rect.x === ell.x, `x ${rect.x}, ellipse ${ell.x}`);
check("guides and spacing show while dragging", guides > 0, `${guides} guide lines`);

// Resize from the bottom-right handle.
const w0 = rect.w, h0 = rect.h;
await drag(await S(rect.x + rect.w, rect.y + rect.h), await S(rect.x + rect.w + 50, rect.y + rect.h + 30), 10);
s = await inspect();
rect = byName(s, rect.name);
check("the corner handle resizes", near(rect.w, w0 + 50, 1) && near(rect.h, h0 + 30, 1), `${rect.w}×${rect.h}`);

// Group the two, undo, redo.
p = await S(ell.x + ell.w / 2, ell.y + ell.h / 2);
await page.keyboard.down("Shift");
await page.mouse.click(p.x, p.y);
await page.keyboard.up("Shift");
s = await inspect();
check("Shift+click adds to the selection", s.selection.length === 2, `${s.selection.length} selected`);
await page.keyboard.press("Control+g");
s = await inspect();
let group = all(s).filter((n) => n.kind === "Group" && n.parent === null).at(-1);
check("Ctrl+G groups them", group && group.children?.length === 2, group ? group.name : "no group");
await page.keyboard.press("Control+z");
s = await inspect();
check("Ctrl+Z ungroups again", !all(s).some((n) => n.name === group.name) && byName(s, rect.name)?.parent === null);
await page.keyboard.press("Control+Shift+z");
s = await inspect();
group = byName(s, group.name);
check("Ctrl+Shift+Z redoes the group", group && group.children?.length === 2);

// Change the main component and look at an instance on the page.
await page.locator(".rp-layer.top .rp-layer-name", { hasText: /^Timetable row$/ }).click();
s = await inspect();
const main = byName(s, "Timetable row");
check("a layer row selects the main component", s.selection[0] === main.id);
// The fill follows a colour style; choosing no style gives a plain colour to edit.
await page.locator(".rp-design .rp-style-pick").first().selectOption("");
await page.waitForTimeout(100);
await page.locator(".rp-design .rp-hex").first().fill("#fff4e5");
await page.locator(".rp-design .rp-hex").first().press("Enter");
await page.waitForTimeout(150);
s = await inspect();
const instances = all(s).filter((n) => n.kind === "Instance" && n.main === main.id);
const fills = await page.evaluate((ids) => ids.map((id) => document.querySelector(`[data-n="${id}"] > rect`)?.getAttribute("fill")), instances.map((i) => i.id));
check("every instance follows the component's new fill", instances.length === 6 && fills.every((f) => f === "#fff4e5"), `${instances.length} instances: ${[...new Set(fills)].join(",")}`);
const texts = await page.evaluate((id) => [...document.querySelectorAll(`[data-n="${id}"] text`)].map((t) => t.textContent.trim()).join(" | "), instances[2].id);
check("instances keep their own text", texts.includes("07.03") && texts.includes("+4 min"), texts);

// Double-click into the timetable list: the third row is an instance; its text overrides are edited in the panel.
const focus = async (wx, wy) => {
  const q = await S(wx, wy);
  await page.mouse.move(centre.x, centre.y);
  await page.mouse.wheel(q.x - centre.x, q.y - centre.y);
  await page.waitForTimeout(200);
};
await focus(660, 520);
s = await inspect();
const sched = s.doc.layers.find((l) => l.kind === "Frame" && l.name === "Timetable");
const deps = sched.children.find((c) => c.name === "Departures");
const third = deps.children[2];
p = await S(sched.x + deps.x + third.x + 60, sched.y + deps.y + third.y + 30);
await page.mouse.dblclick(p.x, p.y);
await page.waitForTimeout(150);
s = await inspect();
const yField = await page.locator(".rp-design .rp-num input").nth(1).inputValue();
check("a double click goes into the list and selects the row, an instance", s.selection[0] === third.id && yField === String(third.y), `selected ${s.selection[0]}, Y field ${yField}`);
const status = page.locator(".rp-design .rp-override input").last();
await status.fill("Delayed");
await page.waitForTimeout(150);
const shown = await page.evaluate((id) => [...document.querySelectorAll(`[data-n="${id}"] text`)].map((t) => t.textContent.trim()).join(" | "), third.id);
check("editing an override changes only that instance", shown.includes("Delayed") && !(await page.evaluate((id) => [...document.querySelectorAll(`[data-n="${id}"] text`)].some((t) => t.textContent.includes("Delayed")), deps.children[1].id)), shown);
await focus(360, 1300);

// Text and pen. A click on empty canvas takes the focus back from the panel's field.
p = await S(420, 1500);
await page.mouse.click(p.x, p.y);
await page.keyboard.press("t");
p = await S(420, 1320);
await page.mouse.click(p.x, p.y);
await page.waitForTimeout(100);
await page.keyboard.type("Halo Rupa");
await page.keyboard.press("Escape");
await page.waitForTimeout(100);
s = await inspect();
const hello = all(s).find((n) => n.kind === "Text" && n.text === "Halo Rupa");
check("T, a click and typing make a text layer", hello && hello.w > 40, hello ? `${hello.w}×${hello.h}` : "none");
await page.keyboard.press("p");
for (const [x, y] of [[620, 1250], [700, 1250], [660, 1320], [620, 1250]]) {
  p = await S(x, y);
  await page.mouse.click(p.x, p.y);
  await page.waitForTimeout(60);
}
s = await inspect();
const vec = all(s).filter((n) => n.kind === "Path" && n.parent === null).at(-1);
check("the pen draws a closed path", vec && vec.closed && vec.points.length === 6, vec ? `${vec.points.length / 2} points` : "none");

// Export the Home frame.
await page.locator(".rp-layer.top .rp-layer-name", { hasText: /^Home$/ }).click();
let download = page.waitForEvent("download");
await page.locator(".rp-design .rp-export button", { hasText: "SVG" }).click();
let file = await download;
const svg = await page.evaluate(() => {
  const t = globalThis.__rupaStats.last;
  const d = new DOMParser().parseFromString(t.text, "image/svg+xml");
  const root = d.documentElement;
  return { ok: !d.querySelector("parsererror") && root.tagName === "svg", w: root.getAttribute("width"), h: root.getAttribute("height"), hasText: t.text.includes("Selamat pagi"), size: t.size };
});
check("SVG export parses, at the frame's size", svg.ok && svg.w === "390" && svg.h === "844" && svg.hasText, `${file.suggestedFilename()} ${svg.size} chars`);
download = page.waitForEvent("download");
await page.locator(".rp-design .rp-export button", { hasText: "PNG 2×" }).click();
file = await download;
const png = await page.evaluate(() => globalThis.__rupaStats.last);
check("PNG export draws the frame at 2×", png.kind === "png" && png.width === 780 && png.height === 1688 && png.size > 20000, `${file.suggestedFilename()} ${png.width}×${png.height}, ${Math.round(png.size / 1024)} KB`);
const colourful = await page.evaluate(async (data) => {
  const img = new Image();
  img.src = data;
  await img.decode();
  const c = document.createElement("canvas");
  c.width = img.width;
  c.height = img.height;
  const g = c.getContext("2d");
  g.drawImage(img, 0, 0);
  const px = g.getImageData(80, 340, 1, 1).data; // inside the night-blue balance card
  return [px[0], px[1], px[2]];
}, png.data);
check("the PNG has the drawing in it", colourful[2] > colourful[0] && colourful[0] < 60, `pixel ${colourful.join(",")}`);

// Autosave: reload and everything is still there.
await page.waitForTimeout(1500);
await page.reload();
await page.waitForFunction(() => globalThis.__rupaInspect && document.querySelector(".rp-world g"), null, { timeout: 15000 });
await page.waitForTimeout(1500);
s = await inspect();
const kept = byName(s, group.name)?.children?.length === 2 && all(s).some((n) => n.text === "Halo Rupa") && byName(s, "Timetable row").fill === "#fff4e5" && all(s).some((n) => n.kind === "Path" && n.closed);
check("a reload keeps every change (autosave)", kept, `${s.count} layers`);

// Performance: 300 shapes, then drag one, a marquee of many, and the whole frame.
// The drags run inside the page: one pointer move per animation frame, as a
// mouse delivers them, so the frame intervals show what a person would see
// (C# handling, rendering, and the browser's own layout and paint).
await page.locator('button[aria-label="File menu"]').click();
await page.locator(".rp-menu button", { hasText: "Add 300 shapes" }).click();
await page.waitForTimeout(400);
s = await inspect();
const stress = s.doc.layers.find((l) => l.name.startsWith("Performance test"));
check("the performance frame holds 300 shapes", stress?.children?.length === 300, `${s.count} layers in the file`);
const q = (a, f) => {
  const x = [...a].sort((m, n) => m - n);
  return x.length ? x[Math.min(x.length - 1, Math.floor(x.length * f))] : NaN;
};
const pace = async (label, from, to, frames = 90) => {
  const r = await page.evaluate(async ({ from, to, frames }) => {
    globalThis.__rupaStats.moves.length = 0;
    const host = document.querySelector(".rp-canvas");
    const ev = (type, x, y, buttons) =>
      host.dispatchEvent(new PointerEvent(type, { clientX: x, clientY: y, pointerId: 7, pointerType: "mouse", isPrimary: true, button: 0, buttons, bubbles: true }));
    ev("pointerdown", from.x, from.y, 1);
    const times = [];
    await new Promise((done) => {
      let i = 0;
      const step = (t) => {
        times.push(t);
        if (++i > frames) return done();
        const k = i / frames;
        ev("pointermove", from.x + (to.x - from.x) * k, from.y + (to.y - from.y) * k, 1);
        requestAnimationFrame(step);
      };
      requestAnimationFrame(step);
    });
    ev("pointerup", to.x, to.y, 0);
    const gaps = times.slice(2).map((t, n) => t - times[n + 1]);
    return { moves: [...globalThis.__rupaStats.moves], gaps };
  }, { from, to, frames });
  await page.waitForTimeout(200);
  const out = { median: q(r.moves, 0.5), p95: q(r.moves, 0.95), frame: q(r.gaps, 0.5), frame95: q(r.gaps, 0.95), fps: 1000 / q(r.gaps, 0.5) };
  console.log(`PERF  ${label}: work per move median ${out.median.toFixed(1)} ms, p95 ${out.p95.toFixed(1)} ms; frame interval median ${out.frame.toFixed(1)} ms, p95 ${out.frame95.toFixed(1)} ms (${out.fps.toFixed(0)} fps)`);
  return out;
};
const top = stress.children.at(-1);
const c0 = await S(stress.x + top.x + top.w / 2, stress.y + top.y + top.h / 2);
const one = await pace("drag 1 of 300 shapes", c0, { x: c0.x + 240, y: c0.y + 120 });
s = await inspect();
const moved = s.doc.layers.find((l) => l.id === stress.id).children.find((c) => c.id === top.id);
check("the dragged shape moved", Math.abs(moved.x - top.x - 240 / s.zoom) < 8, `dx ${(moved.x - top.x).toFixed(1)}`);
check("dragging one of 300 shapes keeps up with the display", one.frame <= 20 && one.median < 16.7, `${one.fps.toFixed(0)} fps, work median ${one.median.toFixed(1)} ms`);
const a = await S(stress.x + 10, stress.y + 10);
const b = await S(stress.x + stress.w - 10, stress.y + stress.h / 2);
await page.keyboard.press("Escape");
await page.keyboard.press("Escape");
await drag(a, b, 8);
s = await inspect();
const many = s.selection.length;
check("a marquee selects many shapes", many > 100, `${many} selected`);
const first = s.doc.layers.find((l) => l.id === stress.id).children.find((c) => c.id === s.selection[0]);
const m0 = await S(stress.x + first.x + first.w / 2, stress.y + first.y + first.h / 2);
const lots = await pace(`drag ${many} selected shapes`, m0, { x: m0.x + 160, y: m0.y + 60 }, 60);
s = await inspect();
const firstAfter = s.doc.layers.find((l) => l.id === stress.id).children.find((c) => c.id === first.id);
check("all the selected shapes moved together", Math.abs(firstAfter.x - first.x - 160 / s.zoom) < 8, `dx ${(firstAfter.x - first.x).toFixed(1)}`);
const label = await page.evaluate((name) => {
  const t = [...document.querySelectorAll(".rp-flabel")].find((e) => e.textContent.includes(name.slice(0, 10)));
  const r = t.getBoundingClientRect();
  return { x: r.left + 10, y: r.top + r.height / 2 };
}, stress.name);
const frame = await pace("drag the 300-shape frame", label, { x: label.x + 200, y: label.y + 80 }, 60);
console.log(`PERF  summary (median/p95 work per move, median fps): one shape ${one.median.toFixed(1)}/${one.p95.toFixed(1)} ms ${one.fps.toFixed(0)} fps; ${many} shapes ${lots.median.toFixed(1)}/${lots.p95.toFixed(1)} ms ${lots.fps.toFixed(0)} fps; whole frame ${frame.median.toFixed(1)}/${frame.p95.toFixed(1)} ms ${frame.fps.toFixed(0)} fps`);

// The editor fits a phone.
await page.setViewportSize({ width: 400, height: 820 });
await page.waitForTimeout(400);
const wide = await page.evaluate(() => document.documentElement.scrollWidth);
check("the editor fits a 400 px screen", wide <= 400, `${wide}px`);
await page.goto(base + "/");
await page.waitForTimeout(1500);
const homeWide = await page.evaluate(() => document.documentElement.scrollWidth);
check("the file list fits a 400 px screen", homeWide <= 400, `${homeWide}px`);

check("no console errors", errors.length === 0, errors.slice(0, 3).join(" / "));
await browser.close();
console.log(failed ? `${failed} check(s) failed` : "rupa: all checks passed");
process.exit(failed ? 1 : 0);
