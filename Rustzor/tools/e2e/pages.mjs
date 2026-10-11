// Opens each route of a running Rustzor app in headless Chrome, waits for it
// to render, saves a full-page screenshot, and reports console errors per page.
// usage: node pages.mjs <base-url> <out-dir> <route> [route ...]
//   routes are relative ("", "deals", "companies/1"); a screenshot is saved as
//   <out-dir>/<route with / as ->.png ("home.png" for "").
import { chromium } from "playwright-core";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const [base, outDir, ...routes] = process.argv.slice(2);
mkdirSync(outDir, { recursive: true });
const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? "C:/Program Files/Google/Chrome/Application/chrome.exe", headless: true });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
let current = "";
const problems = [];
page.on("console", (m) => {
  const text = m.text();
  if (m.type() === "error" && !text.includes("Failed to load resource")) problems.push(`[${current}] ${text}`);
});
page.on("pageerror", (e) => problems.push(`[${current}] ${e.message}`));

for (const route of routes.length ? routes : [""]) {
  current = route || "/";
  await page.goto(new URL(route, base.endsWith("/") ? base : base + "/").href);
  await page.waitForTimeout(Number(process.env.WAIT ?? 3500));
  const state = await page.evaluate(() => (document.querySelector(".app-error") ? "error" : document.querySelector(".app-loading") ? "loading" : "ok"));
  if (state !== "ok") problems.push(`[${current}] page did not render (${state})`);
  const name = (route.replace(/\//g, "-") || "home") + ".png";
  await page.screenshot({ path: join(outDir, name), fullPage: true });
  console.log(`${state.padEnd(7)} ${current}`);
}
await browser.close();
for (const p of problems) console.log(p);
process.exit(problems.length ? 1 : 0);
