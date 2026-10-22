// Screenshots of the Galasin sample for the docs: the sign-up sheet before a match, a match
// in play with bots (seen from an attacker's tab), the moment of a tag, the rules page, and
// a phone. Two tabs share the room so the sheet lists two people.
// usage: node galasin-shots.mjs <base-url> <out-dir>
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5392").replace(/\/$/, "");
const out = process.argv[3] ?? ".";
const room = "rt05-" + Date.now().toString(36);
const browser = await launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function open(name, team, path = "r/" + room) {
  const page = await ctx.newPage();
  await page.goto(`${base}/${path}`);
  await page.waitForFunction(() => globalThis.__gs?.M?.ready && !document.querySelector(".gl-loading"), null, { timeout: 30000 });
  if (name) {
    await page.fill("#gl-name", name);
    await page.press("#gl-name", "Tab");
    await page.click(`.gl-arm[data-team="${team}"]`);
  }
  return page;
}

// 1. The sign-up sheet, with a friend in a second tab.
const a = await open("Ujang", "p");
const b = await open("Siti", "m");
await a.bringToFront();
await wait(2500);
await a.screenshot({ path: `${out}/sample-galasin-lobby.png` });

// 2. A match with bots, from the attacker's tab, part way up the court.
await a.click('[data-minutes="5"]');
await a.check("#gl-bots");
await a.click("#gl-start");
await a.waitForFunction(() => document.querySelector(".gl-yard").dataset.phase === "play", null, { timeout: 10000 });
await a.evaluate(() => globalThis.__gs.teleport(2.6, -1.2));
await a.keyboard.down("ArrowUp");
await wait(260);
await a.keyboard.up("ArrowUp");
await wait(1600);
await a.screenshot({ path: `${out}/sample-galasin.png` });

// 3. A tag: the guard's lunge and the swap stamp, from the guard's tab.
await b.bringToFront();
const guard = await b.evaluate(() => globalThis.__gs.me());
await a.evaluate(([x]) => globalThis.__gs.teleport(x + 0.9, 0.55), [guard.x]);
await b.keyboard.press("Space");
await b.waitForSelector('[data-banner="swap"]', { timeout: 4000 }).catch(() => {});
await wait(250);
await b.screenshot({ path: `${out}/sample-galasin-tag.png` });

// 4. The rules page.
const r = await ctx.newPage();
await r.goto(`${base}/aturan`);
await r.waitForSelector(".gl-court");
await wait(600);
await r.screenshot({ path: `${out}/sample-galasin-rules.png` });

// 5. A phone, in English, mid-match as a watcher of the match above.
const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, hasTouch: true, isMobile: true });
const p = await phone.newPage();
await p.goto(`${base}/r/${room}-hp`);
await p.waitForFunction(() => globalThis.__gs?.M?.ready, null, { timeout: 30000 });
await p.click(".gl-lang button:has-text('EN')");
await p.fill("#gl-name", "Wulan");
await p.press("#gl-name", "Tab");
await p.click("#gl-start");
await p.waitForFunction(() => document.querySelector(".gl-yard").dataset.phase === "play", null, { timeout: 10000 });
await wait(2200);
await p.screenshot({ path: `${out}/sample-galasin-phone.png` });

await browser.close();
console.log("saved to", out);
