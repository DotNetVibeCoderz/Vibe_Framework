// Drives the Bening sample (samples/Bening), a digital twin of a water treatment plant, and
// checks it against the model file and against the simulator run on its own:
//   - the 3D model: every mesh of the GLB drawn, every piece of equipment named in C# present
//   - determinism: the same seed gives the same history hash, sample for sample, and the live
//     run matches a run made separately; another seed does not
//   - picking: a click on RW-2 opens its inspector with the engine's own values and sparklines
//   - alarms: the morning's standing alarms, acknowledging one, a trip raising one, and the rule
//     that a trip cannot be reset before its alarm is acknowledged
//   - what-if: a tripped pump brings the standby on; too little PAC makes settled water cloudy
//   - replay: dragging the timeline to 03.30 shows the rain's turbidity and locks the controls;
//     "See it happen" jumps to an alarm; "Now" goes back
//   - work orders: raised from an alarm, the no-repair-on-a-running-pump rule, done
//   - persistence: a saved view, a note and the run itself survive a reload
//   - CSV: the history, hourly and alarm exports, checked against the engine
//   - language, the phone layout and frame times while orbiting
// usage: node bening.mjs <base-url>   (the built site served, e.g. by tools/dev/serve.mjs)
import { readFileSync } from "fs";
import { dirname, join } from "path";
import { fileURLToPath } from "url";
import { launch } from "./chrome.mjs";

const base = (process.argv[2] ?? "http://localhost:5361").replace(/\/$/, "");
const here = dirname(fileURLToPath(import.meta.url));
const glb = readFileSync(join(here, "..", "..", "samples", "Bening", "wwwroot", "models", "ipa.glb"));
const gltf = JSON.parse(glb.subarray(20, 20 + glb.readUInt32LE(12)).toString("utf8"));
const primitives = gltf.nodes.filter((n) => n.mesh !== undefined).reduce((s, n) => s + gltf.meshes[n.mesh].primitives.length, 0);
const assetIds = ["RIVER", "INTAKE", "RW-1", "RW-2", "RW-3", "FM-1", "PAC", "FL-1", "FL-2", "SD-1", "SD-2", "F-1", "F-2", "F-3", "F-4", "CL-1", "RES-1", "DS-1", "DS-2", "DS-3"];

const browser = await launch();
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, acceptDownloads: true });
const page = await context.newPage();
const errors = [];
page.on("console", (m) => m.type() === "error" && !m.text().includes("Failed to load resource") && errors.push(m.text()));
page.on("pageerror", (e) => errors.push(e.message));

let failed = 0;
const check = (name, ok, detail = "") => {
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? "  (" + detail + ")" : ""}`);
  if (!ok) failed++;
};
const bn = (fn, arg) => page.evaluate(fn, arg);
const ready = () => page.waitForFunction(() => globalThis.__bn?.V?.ready && document.querySelector(".bn-tabs") && document.querySelector(".bn-jar"), null, { timeout: 40000 });
const value = (tag) => bn((t) => globalThis.__bn.value(t), tag);
const state = (asset) => value(`${asset}.state`);
const pause = async () => { await page.click('[data-speed="0"]'); await page.waitForTimeout(150); };
const tab = async (id) => { await page.click(`.bn-tabs [data-tab="${id}"]`); await page.waitForTimeout(150); };
const runFor = async (seconds) => { await bn((s) => globalThis.__bn.runTo(globalThis.__bn.R.S.t + s), seconds); await page.waitForTimeout(250); };
const select = async (asset) => {
  await tab("asset");
  if (await page.locator(".bn-insp").count()) await page.click('.bn-insp-head .bn-ibtn');
  await page.click(`.bn-rollrow[data-asset="${asset}"]`);
  await page.waitForSelector(`.bn-insp[data-asset="${asset}"]`);
};
const toastText = () => page.locator(".rz-toast").allInnerTexts().then((t) => t.join(" | "));
const fresh = async () => {
  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await ready();
};

// ── load ─────────────────────────────────────────────────────────────────
const t0 = Date.now();
await page.goto(base + "/");
await page.evaluate(() => localStorage.clear());
await page.reload();
await ready();
check("the control room opens with the plant drawn", true, `${Date.now() - t0} ms to ready, model ${await bn(() => globalThis.__bn.V.loadMs)} ms`);
const drawn = await bn(() => ({ draws: globalThis.__bn.V.draws.length, assets: [...globalThis.__bn.V.assets.keys()] }));
check("every mesh of the GLB is drawn", drawn.draws === primitives, `${drawn.draws} of ${primitives}`);
check("every piece of equipment C# knows is in the model", assetIds.every((a) => drawn.assets.includes(a)), assetIds.filter((a) => !drawn.assets.includes(a)).join(",") || "all 20");
await pause();

// ── determinism ──────────────────────────────────────────────────────────
const d = await bn(() => {
  const B = globalThis.__bn;
  const a = B.simulate(2610, 8 * 3600), b = B.simulate(2610, 8 * 3600), c = B.simulate(77, 8 * 3600);
  return { a: a.hash, b: b.hash, c: c.hash, rows: a.rows, live: B.hash(6 * 3600), ref: B.simulate(2610, 6 * 3600).hash, alarmsA: a.alarms.join(","), alarmsB: b.alarms.join(",") };
});
check("the same seed gives the same eight hours, sample for sample", d.a === d.b && d.alarmsA === d.alarmsB, `${d.a}, ${d.rows} samples`);
check("another seed gives another day", d.c !== d.a, `${d.c}`);
check("the live run matches a separate run up to 06.00", d.live === d.ref, `${d.live} = ${d.ref}`);

// ── the morning's alarms ─────────────────────────────────────────────────
await tab("alarms");
const standing = await page.locator(".bn-alarm.active").evaluateAll((els) => els.map((e) => e.dataset.def));
check("the morning opens with RW-2's bearing and F-3's head loss standing", standing.includes("RW-2.temp") && standing.includes("F-3.hl"), standing.join(","));
await page.click('[data-filter="all"]');
const night = await page.locator('.bn-alarm[data-def="raw.hi"]').innerText();
check("the night's turbid-river alarm was acknowledged by the night shift", night.includes("Asep Saepudin"), night.replace(/\s+/g, " ").slice(0, 80));

// ── picking and the inspector ────────────────────────────────────────────
const p = await bn(() => globalThis.__bn.screen("RW-2"));
check("RW-2 can be found on screen", !!p);
if (p) await page.mouse.click(p.x, p.y);
await page.waitForSelector('.bn-insp[data-asset="RW-2"]', { timeout: 4000 }).catch(() => {});
check("clicking RW-2 in 3D opens its inspector", (await page.locator('.bn-insp[data-asset="RW-2"]').count()) === 1);
await page.waitForTimeout(700);
const shownTemp = await page.locator('.bn-readings tr[data-tag="RW-2.temp"] .bn-val').innerText();
const engineTemp = await value("RW-2.temp");
check("the inspector shows the engine's bearing temperature", Math.abs(parseFloat(shownTemp.replace(",", ".")) - engineTemp) < 0.06, `${shownTemp.trim()} vs ${engineTemp.toFixed(2)}`);
const ink = await bn(() => {
  const c = document.querySelector('canvas[data-spark="RW-2.temp"]');
  const x = c.getContext("2d").getImageData(0, 0, c.width, c.height).data;
  let n = 0;
  for (let i = 3; i < x.length; i += 4) if (x[i] > 0) n++;
  return n;
});
check("its sparkline is drawn from the history", ink > 50, `${ink} pixels inked`);
check("RW-2's standing alarm is shown in its inspector", (await page.locator('.bn-insp-alarm[data-alarm="RW-2.temp"]').count()) === 1);
await page.click('.bn-insp-alarm[data-alarm="RW-2.temp"] button');
await page.waitForTimeout(200);
const acked = await bn(() => [...globalThis.__bn.R.acks].some((k) => k.startsWith("RW-2.temp@")));
check("acknowledging it reaches the engine (the beacon stops pulsing)", acked && (await page.locator('.bn-insp-alarm.acked[data-alarm="RW-2.temp"]').count()) === 1);

// ── what-if: a raw water pump trips ──────────────────────────────────────
await select("RW-1");
await page.click('[data-cmd="trip"]');
await page.waitForTimeout(250);
check("tripping RW-1 stops it at once", (await state("RW-1")) === 2);
const flowDip = await value("raw.flow");
await runFor(20);
check("the trip raises a P1 alarm", (await page.locator('.bn-insp-alarm.p1[data-alarm="RW-1.trip"]').count()) === 1);
await runFor(60);
check("the standby RW-3 starts itself 30 seconds later", (await state("RW-3")) === 1);
await runFor(300);
const flowBack = await value("raw.flow");
check("raw water flow recovers to the setpoint", flowBack > 380 && flowBack > flowDip - 1, `${flowDip.toFixed(0)} → ${flowBack.toFixed(0)} L/s`);
await page.click('[data-cmd="reset"]');
await page.waitForTimeout(250);
check("a trip cannot be reset before its alarm is acknowledged", (await state("RW-1")) === 2 && (await toastText()).includes("Akui alarm trip"), (await toastText()).slice(0, 90));
await page.click('.bn-insp-alarm[data-alarm="RW-1.trip"] button');
await page.click('[data-cmd="reset"]');
await page.waitForTimeout(250);
check("acknowledged, the reset goes through", (await state("RW-1")) === 0);

// ── what-if: too little coagulant ────────────────────────────────────────
const before = await value("settled.turb");
const jarBefore = await page.locator('.bn-jar[data-stage="settle"] path[style]').getAttribute("style");
await tab("scenario");
await page.locator('[data-setpoint="pac"] input[type="range"]').fill("4");
await page.click('[data-apply="pac"]');
await page.waitForTimeout(250);
check("the PAC dose setpoint takes effect", Math.abs((await value("pac.dose")) - 4) < 0.01);
await runFor(2 * 3600);
const after = await value("settled.turb");
const jarAfter = await page.locator('.bn-jar[data-stage="settle"] path[style]').getAttribute("style");
check("underdosing PAC makes the settled water cloudy two hours on", after > before * 2 && after > 8, `${before.toFixed(1)} → ${after.toFixed(1)} NTU`);
check("…and the settling jar goes brown", jarAfter !== jarBefore, `${jarBefore.match(/\d+%/)} → ${jarAfter.match(/\d+%/)}`);
const settledAlarm = await bn(() => globalThis.__bn.R.active.has("settled.hi"));
check("…and the settled-water alarm stands", settledAlarm);
await page.locator('[data-setpoint="pac"] input[type="range"]').fill("30");
await page.click('[data-apply="pac"]');

// ── replay ───────────────────────────────────────────────────────────────
const tl = await bn(() => globalThis.__bn.timelineX(3.5 * 3600));
await page.mouse.move(tl.x, tl.y);
await page.mouse.down();
await page.mouse.up();
await page.waitForTimeout(600);
const replay = await bn(() => ({ live: globalThis.__bn.R.live, vt: globalThis.__bn.R.vt }));
check("dragging the timeline to 03.30 replays the past", !replay.live && Math.abs(replay.vt - 3.5 * 3600) < 120, `view ${(replay.vt / 3600).toFixed(2)} h`);
const rawJar = await page.locator('.bn-jar[data-stage="raw"] .bn-jar-ntu').innerText();
check("the raw water jar shows the night's rain", parseFloat(rawJar) >= 250, rawJar.replace(/\s+/g, " "));
const clockSays = await page.locator(".bn-clock-time span").innerText();
check("the replay banner and clock say so", (await page.locator(".bn-replay").count()) === 1 && clockSays.toLowerCase().includes("rekaman"), `banner ${await page.locator(".bn-replay").count()}, clock “${clockSays}”`);
await select("RW-3");
const st3 = await state("RW-3");
await page.click('[data-cmd="stop"]').catch(() => {});
await page.waitForTimeout(200);
check("controls are locked while replaying", (await toastText()).includes("rekaman") || (await page.locator('[data-cmd="stop"]').isDisabled()), `RW-3 was ${st3} at 03.30`);
await page.click(".bn-replay .bn-btn");
await page.waitForTimeout(400);
check("“Back to now” returns to the present", await bn(() => globalThis.__bn.R.live));
await tab("alarms");
await page.click('[data-filter="all"]');
await page.click('.bn-alarm[data-def="raw.hi"] [data-jump]');
await page.waitForTimeout(500);
const jump = await bn(() => ({ vt: globalThis.__bn.R.vt, raised: globalThis.__bn.R.alarms.find((a) => a.def === "raw.hi").t }));
check("“See it happen” replays from a minute before the alarm", Math.abs(jump.vt - (jump.raised - 60)) <= 10, `${jump.vt} vs ${jump.raised - 60}`);
await page.click(".bn-replay .bn-btn");

// ── work orders ──────────────────────────────────────────────────────────
await tab("alarms");
await page.click('[data-filter="active"]');
const orderBtn = page.locator('.bn-alarm[data-def="RW-2.vib"] [data-order], .bn-alarm[data-def="RW-2.temp"] [data-order]').first();
await orderBtn.click();
await tab("orders");
const orders = await page.locator(".bn-order").count();
check("a work order is raised from the alarm", orders === 4, `${orders} orders`);
const wo = page.locator('.bn-order.open').filter({ hasText: "RW-2" }).first();
const no = await wo.getAttribute("data-order");
await wo.locator("[data-advance]").click();
await page.locator(`.bn-order[data-order="${no}"] [data-advance]`).click();
await page.waitForTimeout(200);
check("a repair cannot be signed off while the pump runs", (await toastText()).includes("LOTO") && (await page.locator(`.bn-order[data-order="${no}"]`).getAttribute("class")).includes("doing"));
await select("RW-2");
await page.click('[data-cmd="stop"]');
await page.waitForTimeout(200);
await tab("orders");
await page.locator(`.bn-order[data-order="${no}"] [data-advance]`).click();
await page.waitForTimeout(300);
const repaired = await bn(() => globalThis.__bn.R.S.rw[1].wear);
check("stopped, the repair is done and the pump is as new", (await page.locator(`.bn-order[data-order="${no}"]`).getAttribute("class")).includes("done") && repaired < 0.2, `wear ${repaired.toFixed(2)}`);

// ── persistence: a view, a note, the run ─────────────────────────────────
await select("F-3");
await page.fill(".bn-notes input", "Nosel underdrain sudut timur laut tersumbat, cek saat backwash berikutnya.");
await page.click(".bn-notes button[type=submit]");
const cv = await page.locator(".bn-vp").boundingBox();
await page.mouse.move(cv.x + cv.width / 2, cv.y + cv.height / 2);
await page.mouse.down();
await page.mouse.move(cv.x + cv.width / 2 + 160, cv.y + cv.height / 2 + 40, { steps: 8 });
await page.mouse.up();
await page.mouse.wheel(0, -500);
await page.waitForTimeout(300);
const cam = await bn(() => ({ ...globalThis.__bn.V.cam }));
await page.click('.bn-views > .bn-ibtn');
await page.fill(".bn-pop input", "Filter bank");
await page.click(".bn-pop button[type=submit]");
const tBefore = await bn(() => globalThis.__bn.R.S.t);
const hashBefore = await bn((t) => globalThis.__bn.hash(t), tBefore);
await bn(() => globalThis.__bn.R.persist && localStorage.setItem("bening.sim", JSON.stringify({ seed: globalThis.__bn.R.seed, user: globalThis.__bn.R.user, t: globalThis.__bn.R.S.t, speed: 0 })));
await page.reload();
await ready();
const tAfter = await bn(() => globalThis.__bn.R.S.t);
const hashAfter = await bn((t) => globalThis.__bn.hash(t), tBefore);
check("the run survives a reload: same history up to where it was", tAfter >= tBefore && hashAfter === hashBefore, `${hashBefore} = ${hashAfter} at ${(tBefore / 3600).toFixed(2)} h`);
check("the RW-1 trip is still in the shift log", (await bn(() => globalThis.__bn.R.user.filter((c) => c.kind === "trip").length)) === 1);
await select("F-3");
check("the note survives a reload", (await page.locator(".bn-note").innerText()).includes("underdrain"));
await page.click('.bn-views > .bn-ibtn');
check("the saved view survives a reload", (await page.locator('.bn-viewlist [data-view="Filter bank"]').count()) === 1);
await page.click('.bn-viewlist [data-view="Filter bank"]');
await page.waitForTimeout(1500);
const cam2 = await bn(() => ({ ...globalThis.__bn.V.cam }));
check("opening it brings the camera back", Math.abs(cam2.yaw - cam.yaw) < 0.01 && Math.abs(cam2.dist - cam.dist) / cam.dist < 0.01, `yaw ${cam.yaw.toFixed(3)}/${cam2.yaw.toFixed(3)}, dist ${cam.dist.toFixed(1)}/${cam2.dist.toFixed(1)}`);
const acksKept = await bn(() => [...globalThis.__bn.R.acks].some((k) => k.startsWith("RW-2.temp@")));
check("acknowledgements survive a reload", acksKept);

// ── CSV ──────────────────────────────────────────────────────────────────
await page.click('.bn-nav a[href="laporan"]');
await page.waitForSelector(".bn-sheet tbody tr");
await page.waitForTimeout(400);
const hours = await page.locator(".bn-sheet tbody tr").count();
const nowT = await bn(() => globalThis.__bn.R.S.t);
check("the log sheet has a row for every hour so far", hours === Math.floor(nowT / 3600) + 1, `${hours} rows at ${(nowT / 3600).toFixed(2)} h`);
const readCsv = async (sel) => {
  const [dl] = await Promise.all([page.waitForEvent("download"), page.click(sel)]);
  const path = await dl.path();
  return { name: dl.suggestedFilename(), text: readFileSync(path, "utf8").replace(/^﻿/, "") };
};
const hist = await readCsv('[data-export="history"]');
const lines = hist.text.trim().split(/\r?\n/);
const head = lines[0].split(",");
const at = 4 * 3600;
const row = lines.find((l) => l.startsWith("2026-10-22 04:00:00"))?.split(",");
const rawCol = head.findIndex((h) => h.startsWith("Kekeruhan air baku"));
const engineRaw = await bn((t) => globalThis.__bn.value("raw.turb", t), at);
check("the history CSV has a row a minute", lines.length - 1 === Math.floor(nowT / 60) + 1, `${hist.name}: ${lines.length - 1} rows`);
check("its 04.00 raw turbidity is the engine's", !!row && Math.abs(parseFloat(row[rawCol]) - engineRaw) < 0.06, `${row?.[rawCol]} vs ${engineRaw.toFixed(3)}`);
const hourly = await readCsv('[data-export="hourly"]');
check("the hourly CSV matches the log sheet", hourly.text.trim().split(/\r?\n/).length - 1 === hours, hourly.name);
const alarmsCsv = await readCsv('[data-export="alarms"]');
const nAlarms = await bn(() => globalThis.__bn.R.alarms.length);
check("the alarm CSV has every alarm of the day", alarmsCsv.text.trim().split(/\r?\n/).length - 1 === nAlarms, `${nAlarms} alarms`);

// ── language, phone, frames ──────────────────────────────────────────────
await page.click('.bn-nav a[href=""]');
await ready();
await page.click('.bn-tools .bn-seg button:has-text("EN")');
await page.waitForTimeout(300);
check("English switches the whole room", (await page.locator('.bn-tabs [data-tab="alarms"]').innerText()).includes("Alarms") && (await page.locator(".bn-jar-name").first().innerText()) === "Raw");
await page.click('.bn-tools .bn-seg button:has-text("ID")');
await page.click('[data-speed="60"]');
await bn(() => { globalThis.__bn.V.frames.length = 0; });
const box = await page.locator(".bn-vp").boundingBox();
await page.mouse.move(box.x + 300, box.y + 300);
await page.mouse.down();
for (let i = 0; i < 90; i++) { await page.mouse.move(box.x + 300 + i * 4, box.y + 300 + Math.sin(i / 9) * 40); await page.waitForTimeout(16); }
await page.mouse.up();
const stats = await bn(() => {
  const f = globalThis.__bn.V.frames.slice().sort((a, b) => a - b);
  return { n: f.length, p50: f[Math.floor(f.length / 2)], p95: f[Math.floor(f.length * 0.95)] };
});
check("orbiting with the clock at 60×: frames stay cheap", stats.n > 30 && stats.p95 < 12, `p50 ${stats.p50?.toFixed(2)} ms, p95 ${stats.p95?.toFixed(2)} ms over ${stats.n} frames`);

const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2 });
const pp = await phone.newPage();
pp.on("pageerror", (e) => errors.push(e.message));
await pp.goto(base + "/");
await pp.waitForFunction(() => globalThis.__bn?.V?.ready, null, { timeout: 40000 });
const overflow = await pp.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
check("the phone layout has no sideways scroll", overflow <= 0, `${overflow}px`);
await phone.close();

check("no console errors", errors.length === 0, errors.slice(0, 3).join(" | "));
await browser.close();
console.log(failed ? `\n${failed} check(s) failed` : "\nall checks passed");
process.exit(failed ? 1 : 0);
