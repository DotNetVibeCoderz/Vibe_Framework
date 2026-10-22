// The documentation screenshots of the Siaga Citarum sample (docs/images/sample-siaga*.png).
// usage: node siaga-shots.mjs <base-url> <out-dir>
import { join } from "path";
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5330").replace(/\/$/, "");
const out = process.argv[3] ?? ".";
const browser = await launch();

async function open(theme, route = "/") {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: theme });
  const page = await context.newPage();
  await page.goto(base + route);
  if (route === "/") await page.waitForFunction(() => globalThis.__sg && document.querySelector(".sg-gauge"), null, { timeout: 30000 });
  else await page.waitForSelector(".sg-rtable", { timeout: 30000 });
  await page.waitForTimeout(700);
  return page;
}
async function setView(page, lon, lat, zoom) {
  await page.evaluate(([lon, lat, zoom]) => {
    const v = globalThis.__sg;
    [v.M.cx, v.M.cy] = v.toLocal(lon, lat);
    v.M.s = (256 * 2 ** zoom) / (2 * Math.PI * 6378137);
  }, [lon, lat, zoom]);
  await page.locator(".sg-map").hover({ position: { x: 200, y: 200 } });
  await page.mouse.wheel(0, -0.0001);
  await page.waitForTimeout(500);
}
async function find(page, text, layerName) {
  await page.fill(".sg-search input", text);
  await page.waitForTimeout(250);
  await page.click(`.sg-results button:has(small:text-is("${layerName}"))`);
  await page.waitForTimeout(400);
}

// 1. Kecamatan coloured by affected households, one kecamatan inspected.
{
  const page = await open("light");
  await page.click('[data-layer="kecamatan"] .sg-layer-name');
  await page.selectOption('[data-layer="kecamatan"] select[aria-label="Colour by"]', "graduated");
  await page.selectOption('[data-layer="kecamatan"] select[aria-label="Number field"]', "affected_hh");
  await page.locator('[data-layer="kecamatan"] input[aria-label="Opacity"]').fill("0.7");
  await page.click('[data-layer="kecamatan"] .sg-layer-name');
  await page.click('[data-layer="laporan"] .sg-eye');
  await find(page, "Bojongsoang", "Batas kecamatan");
  await setView(page, 107.655, -6.99, 11.85);
  await page.mouse.move(900, 520);
  await page.screenshot({ path: join(out, "sample-siaga.png") });
  await page.context().close();
}

// 2. A 1 km buffer round the Citarum, and the posko it finds in the table.
{
  const page = await open("light");
  await find(page, "Citarum", "Sungai");
  await page.click(".sg-feature .sg-actions button:has-text('Buffer')");
  await page.click(".sg-chips button:has-text('1,00 km')");
  await page.waitForTimeout(300);
  await page.click(".sg-rail-group.end .sg-tool");
  await page.selectOption(".sg-table-head select", "posko");
  await page.check(".sg-check input");
  await setView(page, 107.63, -6.985, 12.75);
  await page.mouse.move(760, 330);
  await page.screenshot({ path: join(out, "sample-siaga-buffer.png") });
  await page.context().close();
}

// 3. Night shift: the dark sheet, close in on Dayeuhkolot, the gauge open.
{
  const page = await open("dark");
  await find(page, "Pos Duga Air Dayeuhkolot", "Pos duga air");
  await setView(page, 107.628, -6.992, 13.4);
  await page.mouse.move(880, 610);
  await page.screenshot({ path: join(out, "sample-siaga-dark.png") });
  await page.context().close();
}

// 4. The situation report.
{
  const page = await open("light", "/laporan");
  await page.screenshot({ path: join(out, "sample-siaga-report.png") });
  await page.context().close();
}

await browser.close();
console.log("4 screenshots written to " + out);
