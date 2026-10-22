// Drives samples/Meet: two tabs of one browser call each other over WebRTC,
// with a third tab that has no camera. Fails on any console error.
// usage: node meet.mjs <base-url> [screenshot-dir]
import { launch } from "./chrome.mjs";
import { mkdirSync } from "node:fs";
import { join as path } from "node:path";

process.env.RZ_FAKE_MEDIA = "1";
const [base, shots] = process.argv.slice(2);
const url = (p) => new URL(p, base.endsWith("/") ? base : base + "/").href;
if (shots) mkdirSync(shots, { recursive: true });

const browser = await launch();
const problems = [];
const results = [];
const check = (name, ok, detail = "") => {
  results.push(`${ok ? "pass" : "FAIL"}  ${name}${detail ? "  (" + detail + ")" : ""}`);
  if (!ok) problems.push(name);
};

async function open(context, label) {
  const page = await context.newPage();
  page.on("console", (m) => {
    if (m.type() === "error" && !m.text().includes("Failed to load resource")) problems.push(`[${label}] ${m.text()}`);
  });
  page.on("pageerror", (e) => problems.push(`[${label}] ${e.message}`));
  return page;
}

async function join(page, name, bureau) {
  await page.goto(url("r/rapat-pagi"));
  await page.waitForSelector("#join", { timeout: 20000 });
  await page.waitForTimeout(800);
  await page.fill("#join-name", name);
  if (bureau) await page.click(`.bureau:has-text("${bureau}")`);
  await page.click("#join");
  await page.waitForSelector(".call", { timeout: 10000 });
}

// Waits until fn() in the page is truthy, up to ms; returns the last value.
async function until(page, fn, arg, ms = 15000) {
  const end = Date.now() + ms;
  let v;
  while (Date.now() < end) {
    v = await page.evaluate(fn, arg);
    if (v) return v;
    await page.waitForTimeout(250);
  }
  return v;
}

const remoteVideo = () => {
  const vs = [...document.querySelectorAll('video[data-remote="true"]')];
  return vs.length > 0 && vs.every((v) => v.videoWidth > 0) ? vs.map((v) => `${v.videoWidth}x${v.videoHeight}`).join(",") : "";
};
const tileOf = (name) => [...document.querySelectorAll(".tile")].find((t) => t.querySelector(".strap-name")?.textContent.toUpperCase().includes(name.toUpperCase()));

const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
await ctx.grantPermissions(["camera", "microphone"]);
const a = await open(ctx, "A");
const b = await open(ctx, "B");

await join(a, "Nur Halimah");
if (shots) await a.screenshot({ path: path(shots, "alone.png") });
await join(b, "Yohanis Latumahina", "Ambon");

check("A sees B's video", !!(await until(a, remoteVideo)), await a.evaluate(remoteVideo));
check("B sees A's video", !!(await until(b, remoteVideo)), await b.evaluate(remoteVideo));
check("both tiles show on A", (await a.locator(".grid > .tile").count()) === 2);

await a.fill("#chat-input", "Air di Pasar Girian naik 40 cm sejak subuh.");
await a.press("#chat-input", "Enter");
check("chat crosses the data channel", !!(await until(b, () => document.querySelector(".chat-log")?.textContent.includes("40 cm sejak subuh"))));

await a.click("#ctl-mic");
check("mute shows on the other side", !!(await until(b, (n) => {
  const t = [...document.querySelectorAll(".tile")].find((t) => t.querySelector(".strap-name")?.textContent.toUpperCase().includes(n));
  return t?.querySelector(".strap-muted");
}, "NUR HALIMAH")));
await a.click("#ctl-mic");

await b.click("#ctl-hand");
check("raised hand shows on the other side", !!(await until(a, () => document.querySelector(".hand-badge"))));

await a.click("#ctl-react");
await a.click('.react-pop button[aria-label="React 👏"]');
check("reaction floats on the other side", !!(await until(b, () => document.querySelector(".float")?.textContent, null, 3000)));

await a.click('button[aria-label="Next story"]');
check("the rundown moves for everyone", (await until(b, () => document.querySelector(".rundown li.now .slug")?.textContent === "HARGA-CAKALANG")) === true);

// Talking: the fake microphone beeps, so a tally lamp lights at some point.
check("an active speaker gets the tally", !!(await until(a, () => document.querySelector(".tile.on-air"), null, 8000)));

await a.click("#ctl-rec");
check("recording is announced to the others", !!(await until(b, () => document.querySelector(".notice-rec"))));
await a.waitForTimeout(2500);
if (shots) await a.screenshot({ path: path(shots, "call.png") });
await a.click("#ctl-rec");
check("stopping the recording keeps the call", !!(await until(a, () => !document.querySelector(".rec-lamp"))));

await a.click("#ctl-share");
const shared = await until(b, () => document.querySelector(".spotlight .tile.screen video")?.videoWidth > 0, null, 10000);
check("screen share reaches the other side", !!shared);
if (shots && shared) await b.screenshot({ path: path(shots, "share.png") });
if (shared) {
  await a.click("#ctl-share");
  check("stopping the share goes back to the grid", !!(await until(b, () => !document.querySelector(".spotlight") && document.querySelector(".grid"))));
}

// A tab without a camera joins with its microphone only.
const c = await open(ctx, "C");
await c.addInitScript(() => {
  const real = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
  navigator.mediaDevices.getUserMedia = (q) => (q && q.video ? Promise.reject(new DOMException("Requested device not found", "NotFoundError")) : real(q));
});
await join(c, "Grace Rumengan", "Bitung");
check("a tab without a camera sees the others", !!(await until(c, remoteVideo)));
check("its tile shows a slate on the other side", !!(await until(a, (n) => {
  const t = [...document.querySelectorAll(".tile")].find((t) => t.querySelector(".strap-name")?.textContent.toUpperCase().includes(n));
  return t?.querySelector(".slate-label")?.textContent === "No camera";
}, "GRACE")));
check("three tiles on A", (await until(a, () => document.querySelectorAll(".grid > .tile").length === 3)) === true);
if (shots) await a.screenshot({ path: path(shots, "three.png") });

await b.click("#ctl-leave");
await b.click("#leave-confirm");
check("leaving removes the tile", (await until(a, () => document.querySelectorAll(".grid > .tile").length === 2)) === true);
check("the one who left sees the left screen", !!(await until(b, () => document.querySelector(".left-card"))));

// Phone width: the call fits without sideways scrolling.
await c.setViewportSize({ width: 400, height: 820 });
await c.waitForTimeout(600);
const overflow = await c.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
check("the call fits a 400 px phone", overflow <= 0, `${overflow}px over`);
if (shots) await c.screenshot({ path: path(shots, "phone.png") });

await a.click("#ctl-leave");
await a.click("#end-all");
await a.waitForSelector(".left-card", { timeout: 10000 });
check("ending for everyone ends the call for the others", (await until(c, () => document.querySelector(".left-card h1")?.textContent ?? "")).includes("Nur Halimah ended the call"));
await a.click('.left-card a[href="recordings"]');
const tapes = await until(a, () => document.querySelectorAll(".tape").length, null, 5000);
check("a recording is saved", tapes === 1, `${tapes} recordings`);
const size = await a.evaluate(() => [...document.querySelectorAll(".tape dd")].pop()?.textContent ?? "");
check("the recording has data", /\d/.test(size) && !/^0/.test(size), size);
// Play the recording and sample a frame: the fake cameras are green.
const frame = await a.evaluate(async () => {
  const v = document.createElement("video");
  v.muted = true;
  v.src = document.querySelector(".tape video").src;
  await v.play();
  await new Promise((r) => setTimeout(r, 1200));
  const c = document.createElement("canvas");
  c.width = 160;
  c.height = 90;
  const g = c.getContext("2d");
  g.drawImage(v, 0, 0, 160, 90);
  const d = g.getImageData(0, 0, 160, 90).data;
  let green = 0;
  for (let i = 0; i < d.length; i += 4) if (d[i + 1] > 100 && d[i + 1] > d[i] + 40) green++;
  return { size: `${v.videoWidth}x${v.videoHeight}`, green: Math.round((green * 100) / (160 * 90)) };
});
check("the recording shows the call", frame.green > 20, `${frame.size}, ${frame.green}% camera picture`);
if (shots) await a.screenshot({ path: path(shots, "recordings.png") });

await browser.close();
for (const r of results) console.log(r);
const errors = problems.filter((p) => p.startsWith("["));
for (const p of errors) console.log(p);
console.log(problems.length ? `meet: ${problems.length} problem(s)` : "meet: all checks passed");
process.exit(problems.length ? 1 : 0);
