// Screenshots of the Loket sample for the docs, with the offline demo model:
// a conversation with a tool card and an open citation, the document archive on the cited
// passage, the dark theme, and a phone.
// usage: node loket-shots.mjs <base-url> <out-dir>
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5360").replace(/\/$/, "");
const out = process.argv[3] ?? ".";
const browser = await launch();

async function conversation(page) {
  await page.goto(base + "/");
  await page.evaluate(() => localStorage.clear());
  await page.goto(base + "/");
  await page.waitForSelector(".lk-board");
  for (const q of ["Apa syarat membuat KTP-el baru, dan kapan kartunya jadi?", "Berapa 15% dari 2.400.000?"]) {
    await page.fill("#lk-input", q);
    await page.click('[data-testid="send"]');
    await page.waitForSelector(".lk-msg.streaming", { timeout: 5000 });
    await page.waitForSelector('[data-testid="send"]', { timeout: 30000 });
    await page.waitForTimeout(150);
  }
}

// 1. Light, desktop: an answer with citations, the calculator card, a source open.
{
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: "light" });
  const page = await context.newPage();
  await conversation(page);
  await page.locator('[data-testid="answer"] .lk-cite').first().click();
  await page.waitForSelector(".lk-sheet");
  await page.screenshot({ path: `${out}/sample-loket.png` });

  // 2. The archive, on the passage that was cited.
  await page.click(".lk-sheet .lk-btn");
  await page.waitForSelector(".lk-passage.hit");
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${out}/sample-loket-docs.png` });
  await context.close();
}

// 3. Dark.
{
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: "dark" });
  const page = await context.newPage();
  await conversation(page);
  await page.fill("#lk-input", "Catat: telepon Pak RT soal surat domisili");
  await page.click('[data-testid="send"]');
  await page.waitForSelector(".lk-msg.streaming");
  await page.waitForSelector('[data-testid="send"]', { timeout: 30000 });
  await page.waitForTimeout(150);
  await page.screenshot({ path: `${out}/sample-loket-dark.png` });
  await context.close();
}

// 4. A phone.
{
  const context = await browser.newContext({ viewport: { width: 400, height: 860 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, colorScheme: "light" });
  const page = await context.newPage();
  await conversation(page);
  await page.screenshot({ path: `${out}/sample-loket-phone.png` });
  await context.close();
}

await browser.close();
console.log("loket screenshots written to " + out);
