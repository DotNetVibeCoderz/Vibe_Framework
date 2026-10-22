// The screenshots of the Kamar Gelap sample in docs/images: the editor on the
// sample (dark), the start page, the light theme with the history, and a phone.
// usage: node kamargelap-shots.mjs <base-url> <out-dir>
import { launch } from "./chrome.mjs";
import { join } from "node:path";

const base = (process.argv[2] ?? "http://localhost:5320").replace(/\/$/, "");
const out = process.argv[3] ?? ".";
const browser = await launch();

async function open(scheme, viewport) {
  const context = await browser.newContext({ viewport, colorScheme: scheme, deviceScaleFactor: 1 });
  const page = await context.newPage();
  await page.goto(base + "/");
  await page.evaluate(() => new Promise((r) => { const q = indexedDB.deleteDatabase("kamar-gelap"); q.onsuccess = q.onerror = q.onblocked = () => r(); }));
  return { context, page };
}
const ready = (page) => page.waitForFunction(() => globalThis.__kg?.meta?.layers?.length >= 5, null, { timeout: 20000 }).then(() => page.waitForTimeout(1500));
const P = (page, x, y) =>
  page.evaluate(([x, y]) => {
    const E = globalThis.__kg;
    const r = E.stage.getBoundingClientRect();
    return { x: r.left + E.view.x + x * E.view.zoom, y: r.top + E.view.y + y * E.view.zoom };
  }, [x, y]);
async function drag(page, a, b, n = 24) {
  const pa = await P(page, a.x, a.y);
  const pb = await P(page, b.x, b.y);
  await page.mouse.move(pa.x, pa.y);
  await page.mouse.down();
  for (let i = 1; i <= n; i++) await page.mouse.move(pa.x + ((pb.x - pa.x) * i) / n, pa.y + ((pb.y - pa.y) * i) / n);
  await page.mouse.up();
  await page.waitForTimeout(250);
}

// 1. The editor, dark: the levels layer's mask, an elliptical selection round the sun.
{
  const { context, page } = await open("dark", { width: 1440, height: 900 });
  await page.goto(base + "/");
  await page.click("text=Open the sample");
  await ready(page);
  await page.click(".kg-layer:has-text('Lift the terraces')");
  await page.keyboard.press("Shift+M");
  await page.keyboard.press("m");
  await drag(page, { x: 1180, y: 520 }, { x: 1460, y: 800 });
  await page.mouse.move(700, 500);
  await page.waitForTimeout(600);
  await page.screenshot({ path: join(out, "sample-kamargelap.png") });

  // 2. The start page, with the picture left on the bench.
  await page.waitForFunction(() => document.querySelector(".kg-save")?.textContent === "Saved in this browser", null, { timeout: 20000 });
  await page.goto(base + "/");
  await page.waitForSelector(".kg-continue-card img", { timeout: 15000 });
  await page.waitForTimeout(500);
  await page.screenshot({ path: join(out, "sample-kamargelap-start.png") });
  await context.close();
}

// 3. Light theme: a black & white print with a sepia tint, and the history.
{
  const { context, page } = await open("light", { width: 1440, height: 900 });
  await page.goto(base + "/");
  await page.click("text=Open the sample");
  await ready(page);
  await page.click(".kg-layer:has-text('Caption')");
  await page.click("button[aria-label='New adjustment layer']");
  await page.click(".kg-pop button:has-text('Black & White')");
  await page.waitForTimeout(300);
  const sliders = page.locator(".kg-props input[type=range]");
  await sliders.nth(4).fill("62");
  await sliders.nth(3).fill("34");
  await page.click(".kg-tabs button:has-text('History')");
  await page.mouse.move(700, 500);
  await page.waitForTimeout(700);
  await page.screenshot({ path: join(out, "sample-kamargelap-light.png") });
  await context.close();
}

// 4. A phone, dark: the picture and the tool strip.
{
  const { context, page } = await open("dark", { width: 400, height: 860 });
  await page.goto(base + "/");
  await page.click("text=Open the sample");
  await ready(page);
  await page.screenshot({ path: join(out, "sample-kamargelap-phone.png") });
  await context.close();
}

await browser.close();
console.log("screenshots written to " + out);
