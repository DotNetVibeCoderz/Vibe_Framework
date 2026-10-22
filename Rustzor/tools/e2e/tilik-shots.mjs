// The documentation screenshots of the Tilik sample: a review with measurements, a
// section through the second stage, the exploded assembly, and the dark theme.
// usage: node tilik-shots.mjs <base-url> <out-dir>
import { join } from "path";
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5340").replace(/\/$/, "");
const out = process.argv[3] ?? ".";
const browser = await launch();

async function open(theme, viewport = { width: 1440, height: 900 }) {
  const context = await browser.newContext({ viewport, colorScheme: theme, deviceScaleFactor: 1 });
  const page = await context.newPage();
  await page.goto(base + "/");
  await page.evaluate((t) => localStorage.setItem("rz-theme", t), theme);
  await page.reload();
  await page.waitForFunction(() => globalThis.__tk?.M?.parts?.length > 0 && document.querySelector(".tk-count"));
  await page.waitForTimeout(400);
  return page;
}
const tool = (page, id) => page.click(`.tk-tool[data-tool-id="${id}"]`);
const row = (page, name) => page.locator(`.tk-row[data-node="${name}"]`);
async function expand(page, name) {
  const caret = row(page, name).locator(".tk-caret");
  if (!(await caret.getAttribute("class")).includes("open")) await caret.click();
}
const cam = (page, c) => page.evaluate((c) => { const M = globalThis.__tk.M; Object.assign(M.cam, c); M.anim = null; globalThis.__tk.redraw(); }, c);
async function clickVertex(page, part, pick) {
  const s = await page.evaluate(([part, pick]) => {
    const M = globalThis.__tk.M, p = M.parts.find((q) => q.name === part), V = p.weld.verts;
    let best = null, score = -Infinity;
    for (let k = 0; k < V.length; k += 3) {
      const v = [V[k] + p.offset[0], V[k + 1] + p.offset[1], V[k + 2] + p.offset[2]];
      const sc = pick[0] * v[0] + pick[1] * v[1] + pick[2] * v[2];
      if (sc > score) { score = sc; best = v; }
    }
    return globalThis.__tk.toScreen(best);
  }, [part, pick]);
  await page.mouse.click(s.x - 2, s.y + 2);
}

// 1. the review: cover off, the output gear selected, two dimensions, the notes
{
  const page = await open("light");
  await expand(page, "RW-63 reducer");
  await expand(page, "Housing");
  await row(page, "Housing cover").locator(".tk-row-act").first().click();
  await row(page, "Fasteners").locator(".tk-row-act").first().click();
  await cam(page, { yaw: 0.55, pitch: 0.62, dist: 0.5, target: [0.03, -0.01, 0] });
  await tool(page, "measure-distance");
  await clickVertex(page, "Input shaft", [0, 0, 1]);
  await clickVertex(page, "Output shaft", [0, 0, -1]);
  await tool(page, "measure-edge");
  await tool(page, "select");
  const g = await page.evaluate(() => globalThis.__tk.toScreen([0.0795, 0.05, -0.014]));
  await page.mouse.click(g.x, g.y);
  await page.waitForTimeout(400);
  await page.screenshot({ path: join(out, "sample-tilik.png") });
  await page.context().close();
}

// 2. a section through the second stage, hatched
{
  const page = await open("light");
  await tool(page, "section");
  await page.click('.tk-sectionbar .tk-seg button:has-text("Z")');
  await page.evaluate(() => { const M = globalThis.__tk.M; });
  const at = await page.evaluate(() => { const M = globalThis.__tk.M; return (-0.012 - M.bbox.lo[2]) / (M.bbox.hi[2] - M.bbox.lo[2]); });
  await page.locator(".tk-sectionbar input").fill(String(Math.round(at * 1000)));
  await cam(page, { yaw: 0.42, pitch: 0.3, dist: 0.48, target: [0.035, 0, -0.012] });
  await page.click(".tk-vp", { position: { x: 5, y: 5 } }).catch(() => {});
  await page.waitForTimeout(500);
  await page.screenshot({ path: join(out, "sample-tilik-section.png") });
  await page.context().close();
}

// 3. exploded, in x-ray, with the tree open
{
  const page = await open("light");
  await expand(page, "RW-63 reducer");
  await expand(page, "Output shaft assembly");
  await page.click('.tk-tool[data-mode="edges"]');
  await page.locator(".tk-explode input").fill("62");
  await page.waitForTimeout(200);
  await page.click('.tk-cube-btns button[aria-label="Fit everything shown"]');
  await page.waitForTimeout(600);
  await row(page, "Gear z38").locator(".tk-row-main").click();
  await page.waitForTimeout(300);
  await page.screenshot({ path: join(out, "sample-tilik-exploded.png") });
  await page.context().close();
}

// 4. dark: x-ray with the notes
{
  const page = await open("dark");
  await page.click('.tk-tool[data-mode="xray"]');
  await page.click('.tk-tabs button[data-tab="notes"]');
  await page.click('.tk-note[data-note="1"] .tk-note-pin');
  await cam(page, { yaw: 0.7, pitch: 0.35, dist: 0.62, target: [0.03, -0.005, 0] });
  await page.waitForTimeout(500);
  await page.screenshot({ path: join(out, "sample-tilik-dark.png") });
  await page.context().close();
}

// phone, for checking the layout (not a docs image)
{
  const page = await open("light", { width: 400, height: 820 });
  await page.screenshot({ path: join(out, "tilik-phone.png") });
  await page.click('.tk-phonebar button:has-text("Details")');
  await page.waitForTimeout(300);
  await page.screenshot({ path: join(out, "tilik-phone-details.png") });
  await page.context().close();
}

await browser.close();
console.log("screenshots written to", out);
