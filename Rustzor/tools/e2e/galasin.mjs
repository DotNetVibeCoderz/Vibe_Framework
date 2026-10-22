// Drives the Galasin sample (samples/Galasin), gobak sodor across browser tabs: two tabs
// sign up and start a match without bots, then the checks play the rules out with the
// players' own keys and positions — moving in sync, guards held to their lines, the safe
// home box, a tag that swaps the teams, a round trip that scores (and half trips that do
// not), and a win. A second match fills the teams with bots and checks that the tabs agree
// on where every bot is, that a third tab can watch, that the match carries on when the
// referee's tab closes, the language toggle, the rules page, and how long frames and the
// referee's ticks take. Fails on any console error.
// usage: node galasin.mjs <base-url>   (the built site served, e.g. by tools/dev/serve.mjs)
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5392").replace(/\/$/, "");
const room = "uji" + Date.now().toString(36);
const browser = await launch();
const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
const errors = [];
let failed = 0;
const check = (name, ok, detail = "") => {
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  (" + detail + ")" : ""}`);
  if (!ok) failed++;
};
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function open(label, path = "r/" + room) {
  const page = await ctx.newPage();
  page.on("console", (m) => m.type() === "error" && !m.text().includes("Failed to load resource") && errors.push(`[${label}] ${m.text()}`));
  page.on("pageerror", (e) => errors.push(`[${label}] ${e.message}`));
  await page.goto(`${base}/${path}`);
  await page.waitForFunction(() => globalThis.__gs?.M?.ready && !document.querySelector(".gl-loading"), null, { timeout: 30000 });
  return page;
}
/** Polls fn in the page until it returns something truthy, up to ms; returns the last value. */
async function until(page, fn, arg, ms = 8000) {
  const end = Date.now() + ms;
  let v;
  while (Date.now() < end) {
    v = await page.evaluate(fn, arg);
    if (v) return v;
    await wait(100);
  }
  return v;
}
const attr = (page, name) => page.getAttribute(".gl-yard", name);
const meId = (page) => attr(page, "data-me");
const me = (page) => page.evaluate(() => globalThis.__gs.me());
const ent = (page, id) => page.evaluate((id) => globalThis.__gs.ent(id), id);
const teleport = (page, x, z) => page.evaluate(([x, z]) => globalThis.__gs.teleport(x, z), [x, z]);
const score = async (page, team) => +(await page.textContent(`[data-score="${team}"]`));
const phaseIs = (page, phase, ms = 8000) => until(page, (p) => document.querySelector(".gl-yard")?.dataset.phase === p, phase, ms);

// ── 1. two tabs sign up ──────────────────────────────────────────────
const a = await open("A");
const b = await open("B");
await a.fill("#gl-name", "Ujang");
await a.press("#gl-name", "Tab");
await a.click('.gl-arm[data-team="m"]');
await b.fill("#gl-name", "Siti");
await b.press("#gl-name", "Tab");
await b.click('.gl-arm[data-team="p"]');
const rollOk = (names) => {
  const t = document.querySelector(".gl-roll")?.textContent ?? "";
  return names.every((n) => t.includes(n)) && document.querySelector(".gl-count")?.dataset.people === "2";
};
check("each tab's sign-up sheet lists both players", (await until(a, rollOk, ["Ujang", "Siti"])) && (await until(b, rollOk, ["Ujang", "Siti"])));
const hosts = [await attr(a, "data-host"), await attr(b, "data-host")];
const refereeName = async (p) => p.evaluate(() => document.querySelector(".gl-roll-host")?.closest("li")?.querySelector(".gl-roll-name")?.textContent);
check("exactly one tab referees, and both name the same one", hosts.filter((h) => h === "1").length === 1 && (await refereeName(a)) === (await refereeName(b)), `${await refereeName(a)}`);

// ── 2. a match without bots ──────────────────────────────────────────
await a.click('[data-minutes="2"]');
await a.click('[data-target="3"]');
await a.uncheck("#gl-bots");
await a.click("#gl-start");
const playing = (await phaseIs(a, "play", 9000)) && (await phaseIs(b, "play", 3000));
check("starting from either tab begins the match in both, after the count-down", playing);
const aId = await meId(a), bId = await meId(b);
const roles = [await attr(a, "data-role"), await attr(b, "data-role")];
check("White attacks first, Red guards line 1", roles[0] === "x" && roles[1] === "free" && (await me(a)).line === 0, roles.join(" / "));
check("no bots were added", (await a.locator(".gl-teams li").count()) === 2 && (await b.locator(".gl-teams li").count()) === 2);

// ── 3. moving, and the other tab seeing it ───────────────────────────
await b.bringToFront();
const b0 = await me(b);
await b.keyboard.down("ArrowLeft");          // screen left is +x: the camera looks down the court
await wait(450);
await b.keyboard.up("ArrowLeft");
await wait(450);
const b1 = await me(b);
const seen = await ent(a, bId);
check("the arrow keys move the attacker", b1.x - b0.x > 1.0 && Math.abs(b1.z - b0.z) < 0.05, `x ${b0.x.toFixed(2)} → ${b1.x.toFixed(2)}`);
check("the other tab draws the move where it ended", Math.abs(seen.x - b1.x) < 0.12 && Math.abs(seen.z - b1.z) < 0.12, `seen ${seen.x.toFixed(2)}, ${seen.z.toFixed(2)}`);

await a.bringToFront();
const a0 = await me(a);
await a.keyboard.down("ArrowUp");
await a.keyboard.down("ArrowRight");
await wait(400);
await a.keyboard.up("ArrowUp");
await a.keyboard.up("ArrowRight");
await wait(450);
const a1 = await me(a);
const aSeen = await ent(b, aId);
check("a guard slides along the line and cannot leave it", a1.z === 0 && a0.x - a1.x > 0.8, `x ${a0.x.toFixed(2)} → ${a1.x.toFixed(2)}, z ${a1.z}`);
check("the attacker's tab sees the guard on the line", Math.abs(aSeen.z) < 0.02 && Math.abs(aSeen.x - a1.x) < 0.12);

// ── 4. the rules ─────────────────────────────────────────────────────
const gen0 = +(await attr(a, "data-gen"));
await teleport(b, a1.x + 0.3, -0.62);         // within reach, but behind the home line
await wait(900);
check("home is safe: no tag behind the start line", (await attr(a, "data-attack")) === "p" && +(await attr(a, "data-gen")) === gen0);

await teleport(b, a1.x + 0.35, 0.45);         // on the court, 0.57 m from the guard
const swapped = await until(a, () => document.querySelector(".gl-yard").dataset.attack === "m" && document.querySelector('[data-banner="swap"]'), null, 3000);
const swappedB = await until(b, () => document.querySelector(".gl-yard").dataset.attack === "m" && document.querySelector('[data-banner="swap"]'), null, 2000);
check("a guard's touch on the court swaps the teams, in both tabs", !!swapped && !!swappedB);
check("the banner says who caught whom", (await a.textContent('[data-banner="swap"]')).includes("Ujang menangkap Siti"));
await phaseIs(a, "play", 4000);
const roles2 = [await attr(a, "data-role"), await attr(b, "data-role")];
const aHome = await me(a);
check("after the swap Red attacks from home and White guards line 1", roles2[0] === "free" && roles2[1] === "x" && Math.abs(aHome.z + 1.8) < 0.05, `${roles2.join(" / ")}, z ${aHome.z.toFixed(2)}`);

const bGuard = await me(b);
const lane = bGuard.x > 0 ? -3.6 : 3.6;     // the far side from the only guard
await teleport(a, lane, 5);
await wait(250);
await teleport(a, lane, -2);
await wait(500);
check("half a trip scores nothing", (await score(a, "m")) === 0 && (await score(b, "m")) === 0);
await teleport(a, lane, 13.6);
const reached = await until(a, () => document.querySelector('[data-banner="reach"]'), null, 2000);
check("touching the far side is marked", !!reached);
await teleport(a, lane, -2);
await until(b, () => document.querySelector('[data-score="m"]')?.textContent === "1", null, 2000);
check("the round trip scores one, in both tabs", (await score(a, "m")) === 1 && (await score(b, "m")) === 1);
const tallies = await b.evaluate(() => document.querySelectorAll(".gl-board-team.m .gl-tally path").length);
check("the board keeps it in turus", tallies === 1);
await teleport(a, lane, -2.5);
await wait(400);
check("coming home again without a new trip scores nothing", (await score(a, "m")) === 1);

for (let i = 0; i < 2; i++) {
  await teleport(a, lane, 13.6);
  await wait(250);
  await teleport(a, lane, -2);
  await wait(350);
}
const ended = (await phaseIs(a, "end", 3000)) && (await phaseIs(b, "end", 2000));
check("the first to the target wins: the match ends in both tabs", ended && (await score(b, "m")) === 3);
check("the result is announced", ((await b.textContent('[data-banner="end"]')) ?? "").includes("Merah menang"));
await wait(500);
const anims = [(await ent(b, aId))?.anim, (await ent(a, bId))?.anim];
check("the winner cheers and the loser hangs their head", anims[0] === "Cheer" && anims[1] === "Sad", anims.join(" / "));
check("after the result everyone is back on the sign-up sheet", (await phaseIs(a, "lobby", 10000)) && (await phaseIs(b, "lobby", 2000)));

// ── 5. a full match with bots, a watcher, and the referee leaving ────
await a.check("#gl-bots");
await a.click("#gl-start");
check("a match with bots starts with eight kids", (await phaseIs(a, "play", 9000)) && (await a.locator(".gl-teams li").count()) === 8);
await a.bringToFront();
await wait(2500);
// both tabs hold every bot at the same place: compare the newest snapshot each tab has, read
// at the same moment (they can differ by at most the one tick a broadcast is in flight)
const bots = await a.evaluate(() => globalThis.__gs.ents().filter((e) => e.bot).map((e) => e.id));
const pos = async (p) => p.evaluate((ids) => ids.map((id) => globalThis.__gs.latest(id)), bots);
let worst = Infinity;
for (let i = 0; i < 5 && worst > 0.25; i++) {
  const [pa, pb] = await Promise.all([pos(a), pos(b)]);
  worst = Math.max(...pa.map((p, k) => Math.hypot(p.x - pb[k].x, p.z - pb[k].z)));
}
check("both tabs have every bot in the same place", bots.length === 6 && worst < 0.25, `worst ${worst.toFixed(2)} m`);
const moving = (await a.evaluate((ids) => ids.map((id) => globalThis.__gs.ent(id)), bots)).filter((p) => p.anim === "Run").length;
check("bots run, and their legs run with them", moving >= 1, `${moving} running`);

const snaps0 = await b.evaluate(() => globalThis.__gs.stats.snaps);
await a.evaluate(() => globalThis.__gs.resetStats());
await wait(3000);
const fs = await a.evaluate(() => globalThis.__gs.frameStats());
const snaps1 = await b.evaluate(() => globalThis.__gs.stats.snaps);
const hostPage = (await attr(a, "data-host")) === "1" ? a : b;
const tick = +(await hostPage.getAttribute(".gl-perf", "data-tick"));
check("snapshots arrive about twenty times a second", (snaps1 - snaps0) / 3 > 15, `${((snaps1 - snaps0) / 3).toFixed(1)}/s`);
check("a frame of the yard takes little time", fs.cpuP95 < 8 && fs.gapMedian < 20, `cpu median ${fs.cpuMedian.toFixed(1)} ms, p95 ${fs.cpuP95.toFixed(1)} ms, frame interval ${fs.gapMedian.toFixed(1)} ms`);
check("the referee's C# tick stays small", tick > 0 && tick < 8, `${tick.toFixed(2)} ms per tick, 20 ticks a second`);

const c = await open("C");
const watching = await until(c, () => document.querySelector(".gl-watch") && globalThis.__gs.ents().length === 8, null, 6000);
check("a third tab that opens mid-match watches it", !!watching);

const hostIsA = (await attr(a, "data-host")) === "1";
const leaving = hostIsA ? a : b, staying = hostIsA ? b : a;
const leaverId = await meId(leaving);
const clock0 = await staying.textContent("[data-clock]");
await leaving.close();
const tookOver = await until(staying, () => document.querySelector(".gl-yard").dataset.host === "1" || null, null, 4000)
  || await until(c, () => document.querySelector(".gl-yard").dataset.host === "1" || null, null, 2000);
await wait(2500);
const clock1 = await staying.textContent("[data-clock]");
const left = await staying.evaluate((id) => document.querySelector(`[data-player="${id}"] .gl-teams-bot`) !== null, leaverId);
check("when the referee's tab closes, another tab takes over", !!tookOver);
check("the match goes on: the clock keeps running", clock1 !== clock0 && (await attr(staying, "data-phase")) !== "lobby", `${clock0} → ${clock1}`);
check("the player who left is played by a bot", left);

// ── 6. language, the rules page, and the phone layout ────────────────
await staying.bringToFront();
await staying.click(".gl-lang button:has-text('EN')");
check("the language toggle switches the bars to English", ((await staying.textContent("[data-strip]")) ?? "").match(/attack|guard|sodor|Watching/i) !== null);
await staying.goto(`${base}/aturan`);
await staying.waitForSelector(".gl-court");
check("the rules page draws the court and lists the rules", (await staying.locator(".gl-steps li").count()) === 5 && (await staying.textContent("h1")) === "Galasin");
await staying.click(".gl-lang button:has-text('ID')");
check("the rules read in Indonesian too", ((await staying.textContent(".gl-steps")) ?? "").includes("penjaga"));
const phone = await ctx.newPage();
phone.on("pageerror", (e) => errors.push(`[phone] ${e.message}`));
await phone.setViewportSize({ width: 390, height: 844 });
await phone.goto(`${base}/r/${room}x`);
await phone.waitForSelector("#gl-start:not([disabled])", { timeout: 30000 });
const overflow = await phone.evaluate(() => document.documentElement.scrollWidth - innerWidth);
check("on a phone the sheet fits the screen", overflow <= 0);

check("no console errors", errors.length === 0, errors.slice(0, 3).join(" | "));
await browser.close();
console.log(failed ? `${failed} check(s) failed` : "all checks passed");
process.exit(failed ? 1 : 0);
