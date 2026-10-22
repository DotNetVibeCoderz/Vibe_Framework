// Screenshots of the Bening sample for the docs: the control room with a pump selected, the
// night's rain replayed with the alarm list, the what-if panel at night, the daily log sheet,
// and the phone layout.
// usage: node bening-shots.mjs <base-url> <outdir>
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5361").replace(/\/$/, "");
const out = process.argv[3] ?? ".";
const browser = await launch();

async function open(viewport, theme, scale = 1) {
  const context = await browser.newContext({ viewport, deviceScaleFactor: scale, colorScheme: theme });
  const page = await context.newPage();
  await page.goto(base + "/");
  await page.evaluate((t) => { localStorage.clear(); localStorage.setItem("rz-theme", t); }, theme);
  await page.reload();
  await page.waitForFunction(() => globalThis.__bn?.V?.ready && document.querySelector(".bn-jar"), null, { timeout: 40000 });
  await page.click('[data-speed="0"]');
  await page.evaluate(() => globalThis.__bn.runTo(6 * 3600 + 14 * 60));
  return { context, page };
}
const select = async (page, asset) => {
  await page.click('.bn-tabs [data-tab="asset"]');
  await page.click(`.bn-rollrow[data-asset="${asset}"]`);
  await page.waitForTimeout(1600);
};

// 1. the control room, RW-2 selected
{
  const { context, page } = await open({ width: 1440, height: 900 }, "light");
  await select(page, "RW-2");
  await page.evaluate(() => globalThis.__bn.V.goal = { target: [-48, 1, 34], dist: 58, yaw: -0.5, pitch: 0.52 });
  await page.waitForTimeout(1800);
  await page.screenshot({ path: `${out}/sample-bening.png` });
  await context.close();
}
// 2. the rain at 03.30, replayed, with the alarm list
{
  const { context, page } = await open({ width: 1440, height: 900 }, "light");
  await page.click('.bn-tabs [data-tab="alarms"]');
  await page.click('[data-filter="all"]');
  const p = await page.evaluate(() => globalThis.__bn.timelineX(3.6 * 3600));
  await page.mouse.click(p.x, p.y);
  await page.evaluate(() => globalThis.__bn.V.goal = { target: [-10, 0, 8], dist: 120, yaw: -0.25, pitch: 0.62 });
  await page.waitForTimeout(1800);
  await page.screenshot({ path: `${out}/sample-bening-replay.png` });
  await context.close();
}
// 3. night shift: what-if, with RW-1 tripped
{
  const { context, page } = await open({ width: 1440, height: 900 }, "dark");
  await select(page, "RW-1");
  await page.click('[data-cmd="trip"]');
  await page.evaluate(() => globalThis.__bn.runTo(globalThis.__bn.R.S.t + 20));
  await page.click('.bn-tabs [data-tab="scenario"]');
  await page.evaluate(() => globalThis.__bn.V.goal = { target: [-22, 0, 30], dist: 70, yaw: -0.7, pitch: 0.5 });
  await page.waitForTimeout(1800);
  await page.screenshot({ path: `${out}/sample-bening-dark.png` });
  await context.close();
}
// 4. the daily log sheet
{
  const { context, page } = await open({ width: 1440, height: 900 }, "light");
  await page.evaluate(() => globalThis.__bn.runTo(9 * 3600 + 40 * 60));
  await page.click('.bn-nav a[href="laporan"]');
  await page.waitForSelector(".bn-sheet tbody tr");
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${out}/sample-bening-report.png` });
  await context.close();
}
// 5. the phone
{
  const { context, page } = await open({ width: 390, height: 844 }, "light", 2);
  await page.waitForTimeout(1200);
  await page.screenshot({ path: `${out}/sample-bening-phone.png` });
  await context.close();
}
await browser.close();
console.log("saved 5 screenshots to", out);
