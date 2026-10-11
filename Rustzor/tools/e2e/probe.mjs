// Opens a page in headless Chrome, runs optional steps, prints console + DOM.
// usage: node probe.mjs <url> [screenshot.png] [steps.json]
//   FULL=0 captures the viewport only (right for screens with a modal open).
import { chromium } from "playwright-core";
const [url, shot, stepsJson] = process.argv.slice(2);
const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? "C:/Program Files/Google/Chrome/Application/chrome.exe", headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on("console", (m) => console.log(`[${m.type()}] ${m.text()}`));
page.on("pageerror", (e) => console.log(`[pageerror] ${e.stack ?? e.message}`));
if (process.env.RZ_INIT) await page.addInitScript({ path: process.env.RZ_INIT });
await page.goto(url);
await page.waitForTimeout(1500);
const fullPage = process.env.FULL !== "0";
const steps = stepsJson ? JSON.parse(stepsJson) : [];
for (const s of steps) {
  if (s.click) await page.click(s.click, s.position ? { position: s.position } : undefined);
  if (s.fill) await page.fill(s.fill, s.value);
  if (s.drag) await page.dragAndDrop(s.drag, s.to);
  if (s.type) await page.type(s.type, s.value);
  if (s.press) await page.press(s.press, s.key);
  if (s.wait) await page.waitForTimeout(s.wait);
  if (s.shot) await page.screenshot({ path: s.shot, fullPage });
  if (s.eval) console.log("EVAL:", JSON.stringify(await page.evaluate(s.eval)));
  if (s.goto) await page.goto(s.goto);
  if (s.dump) console.log("DOM:", (await page.locator(s.dump).innerHTML()).slice(0, 3000));
}
await page.waitForTimeout(300);
console.log("DOM:", (await page.locator("body").innerHTML()).slice(0, 4000));
if (shot) await page.screenshot({ path: shot, fullPage });
await browser.close();
