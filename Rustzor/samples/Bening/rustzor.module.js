// Bening's twin engine. C# owns the plant model: which equipment exists and what it is
// called, the alarm rules and their limits, what an operator may do (and refuses what the
// rules forbid), the alarm log and its acknowledgements, work orders, notes and saved views.
// This module owns what runs per sample or per frame:
//   - the simulator: a seeded, deterministic model of IPA Leuwi Bening (river, raw water
//     pumps, coagulation, flocculation, settling, four filters, chlorination, the reservoir
//     and the distribution pumps) stepped every 10 simulated seconds, with every sample
//     kept so the day can be replayed
//   - alarm evaluation against C#'s rules on every sample (delay and deadband included)
//   - the 3D view: glTF binary, WebGL2 with a shadow map, turning parts, water tinted by its
//     turbidity, the reservoir gauge, equipment tinted by state, GPU picking, alarm beacons
//   - the timeline strip and the sparklines, drawn from the history
// Same seed and same operator commands give the same plant, sample for sample.
// Embedded in the app's assembly; Rustzor's bundler ships it.
// Built by Gravicode Studios, led by Kang Fadhil.
export function install({ ops, dispatch, addHandle, elementOf }) {
  // ── tags: one value per sample each, in this order everywhere ────────
  const TAGS = ["river.level", "raw.turb"];
  const each = (prefix, n, suffix) => { for (let i = 1; i <= n; i++) TAGS.push(`${prefix}-${i}.${suffix}`); };
  for (const s of ["state", "flow", "kw", "amp", "vib", "temp"]) each("RW", 3, s);
  TAGS.push("raw.flow", "flow.sp", "pac.dose", "pac.demand", "pac.tank", "settled.turb");
  for (const s of ["hl", "state", "turb"]) each("F", 4, s);
  TAGS.push("treated.turb", "prod.flow", "cl.dose", "cl.res", "res.level", "demand");
  for (const s of ["state", "flow", "kw"]) each("DS", 3, s);
  TAGS.push("dist.press", "plant.kw");
  const NT = TAGS.length;
  const I = Object.fromEntries(TAGS.map((t, i) => [t, i]));

  const DT = 10; // simulated seconds per sample
  const DAY = 86400;
  const START = 6 * 3600; // the app opens at 06:00 on day 0, with the night already in the history

  // the night before the app opens: what the night shift did (part of the scenario, not the viewer's)
  const NIGHT = [
    { t: 2 * 3600 + 600, kind: "rain", target: "river", value: 340, who: "BMKG" },
    { t: 2 * 3600 + 2700, kind: "pac", target: "PAC", value: 42, who: "Asep Saepudin" },
    { t: 3 * 3600 + 1500, kind: "pac", target: "PAC", value: 55, who: "Asep Saepudin" },
    { t: 4 * 3600 + 3000, kind: "pac", target: "PAC", value: 38, who: "Asep Saepudin" },
    { t: 5 * 3600 + 1800, kind: "pac", target: "PAC", value: 30, who: "Asep Saepudin" },
    { t: 5 * 3600 + 1800, kind: "flow", target: "INTAKE", value: 390, who: "Asep Saepudin" },
  ];

  // ── deterministic randomness ─────────────────────────────────────────
  function rng(seed) {
    let a = seed >>> 0;
    return () => {
      a = (a + 0x6d2b79f5) >>> 0;
      let t = a;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }
  const lag = (v, target, tau, dt) => v + (target - v) * (1 - Math.exp(-dt / tau));
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

  // ── the plant model ──────────────────────────────────────────────────
  function newState(seed) {
    return {
      seed, t: 0, r: rng(seed),
      river: 1.2, raw: 34,
      rw: [
        { state: 1, auto: true, flow: 180, temp: 58, vib: 1.5, wear: 0.18, base: 1.5 },
        { state: 1, auto: true, flow: 180, temp: 64, vib: 2.8, wear: 0.55, base: 2.2 },
        { state: 0, auto: true, flow: 0, temp: 30, vib: 0, wear: 0.1, base: 1.4 },
      ],
      autoAt: -1,
      rawFlow: 360, flowSp: 360,
      pacDose: 28, pacTank: 64, settled: 1.6,
      filters: [
        { hl: 0.8, state: 0, until: 0, turb: 0.12, ripen: 0, auto: true },
        { hl: 1.1, state: 0, until: 0, turb: 0.13, ripen: 0, auto: true },
        { hl: 1.33, state: 0, until: 0, turb: 0.14, ripen: 0, auto: true },
        { hl: 0.5, state: 0, until: 0, turb: 0.11, ripen: 0, auto: true },
      ],
      prod: 355, treated: 0.13, clDose: 1.1, clRes: 0.72,
      level: 2.6, demand: 300,
      ds: [
        { state: 1, auto: true, flow: 150 },
        { state: 1, auto: true, flow: 150 },
        { state: 0, auto: true, flow: 0 },
      ],
      dsAutoAt: -1,
      press: 3.6, kw: 0,
      rain: [],
      log: [], // events the model raised by itself (trips, auto starts), for C#'s operator log
    };
  }

  function noise(S, a) {
    return (S.r() + S.r() + S.r() - 1.5) * a;
  }

  function demandAt(t) {
    const h = (t / 3600) % 24;
    const g = (c, w) => Math.exp(-(((h - c) / w) ** 2)) + Math.exp(-(((h - c + 24) / w) ** 2)) + Math.exp(-(((h - c - 24) / w) ** 2));
    return 372 + 92 * g(6.8, 1.4) + 64 * g(18.2, 1.7) - 92 * g(2.6, 2.3);
  }

  function apply(S, c) {
    switch (c.kind) {
      case "rain": S.rain.push({ t0: c.t, amp: c.value }); break;
      case "pac": S.pacDose = c.value; break;
      case "cl": S.clDose = c.value; break;
      case "flow": S.flowSp = c.value; break;
      case "refill": S.pacTank = Math.max(S.pacTank, c.value || 92); break;
      default: {
        const p = pumpOf(S, c.target);
        if (p) {
          if (c.kind === "start" && p.state === 0) { p.state = 1; }
          else if (c.kind === "stop" && p.state === 1) { p.state = 0; }
          else if (c.kind === "trip" && p.state === 1) { p.state = 2; trip(S, c.target, "operator"); }
          else if (c.kind === "reset" && p.state === 2) { p.state = 0; }
          else if (c.kind === "auto") { p.auto = !!c.value; }
          else if (c.kind === "repair") { p.wear = 0.12; p.base = 1.4; if (p.state === 2) p.state = 0; }
          return;
        }
        const f = filterOf(S, c.target);
        if (f && c.kind === "backwash" && f.state === 0) { f.state = 1; f.until = c.t + 720; }
        else if (f && c.kind === "offline" && f.state === 0) { f.state = 2; }
        else if (f && c.kind === "online" && f.state === 2) { f.state = 0; }
        else if (f && c.kind === "auto") { f.auto = !!c.value; }
      }
    }
  }
  const pumpOf = (S, id) => (id?.startsWith("RW-") ? S.rw[+id[3] - 1] : id?.startsWith("DS-") ? S.ds[+id[3] - 1] : null);
  const filterOf = (S, id) => (id?.startsWith("F-") ? S.filters[+id[2] - 1] : null);
  function trip(S, id, why) {
    S.log.push({ t: S.t, kind: "tripped", target: id, value: why });
    if (id.startsWith("RW-")) S.autoAt = S.t + 30;
    else S.dsAutoAt = S.t + 30;
  }

  function step(S, out) {
    const dt = DT;
    S.t += dt;
    const t = S.t, h = (t / 3600) % 24;
    // the river: a daily swing, plus whatever the rain upstream brings down
    let rain = 0;
    for (const e of S.rain) {
      const x = t - e.t0;
      if (x > 0) rain += e.amp * (x < 2700 ? x / 2700 : Math.exp(-(x - 2700) / 6000));
    }
    S.raw = lag(S.raw, Math.max(6, 33 + 5 * Math.sin((2 * Math.PI * (h - 14)) / 24) + rain), 600, dt) + noise(S, 0.6);
    S.river = lag(S.river, 1.2 + rain * 0.0042, 900, dt) + noise(S, 0.003);

    // raw water pumps: duty and standby, with protection trips and the standby auto-start
    for (let i = 0; i < 3; i++) {
      const p = S.rw[i];
      if (p.state === 1) p.wear += (dt / 3600) * (i === 1 ? 0.045 : 0.002);
    }
    if (S.autoAt >= 0 && t >= S.autoAt) {
      S.autoAt = -1;
      const want = Math.min(3, Math.ceil(S.flowSp / 225));
      const running = S.rw.filter((p) => p.state === 1).length;
      if (running < want) {
        const sb = S.rw.findIndex((p) => p.state === 0 && p.auto);
        if (sb >= 0) { S.rw[sb].state = 1; S.log.push({ t, kind: "autostart", target: `RW-${sb + 1}`, value: "" }); }
      }
    }
    const nRun = S.rw.filter((p) => p.state === 1).length;
    const cap = 225 * (S.river < 0.7 ? 0.8 : 1);
    S.rawFlow = lag(S.rawFlow, Math.min(S.flowSp, cap * nRun), 60, dt);
    for (let i = 0; i < 3; i++) {
      const p = S.rw[i];
      const on = p.state === 1;
      p.flow = on && nRun ? S.rawFlow / nRun + noise(S, 0.8) : 0;
      const extra = 70 * Math.pow(Math.max(0, p.wear - 0.45), 1.2);
      p.temp = lag(p.temp, on ? 52 + 0.04 * p.flow + extra : 30, 900, dt) + noise(S, 0.08);
      p.vib = on ? Math.max(0.3, p.base + 6 * Math.max(0, p.wear - 0.45) + noise(S, 0.12)) : 0;
      if (on && (p.temp > 90 || p.vib > 7.1)) { p.state = 2; trip(S, `RW-${i + 1}`, p.temp > 90 ? "temp" : "vib"); }
    }

    // coagulation and settling: too little PAC for the turbidity and the floc does not form
    const demand = 12 + 0.11 * S.raw;
    const ratio = S.pacDose / demand;
    const fr = ratio >= 1 ? 0.035 + 0.008 * (ratio - 1) : 0.035 + 0.5 * Math.pow(1 - ratio, 1.4);
    S.settled = lag(S.settled, S.rawFlow > 5 ? S.raw * fr + 0.4 : S.settled, 2400, dt) + noise(S, 0.02);
    S.pacTank = Math.max(0, S.pacTank - (S.pacDose * S.rawFlow * dt) / 4.2e7);
    const pacOn = S.pacTank > 0.5;
    if (!pacOn) S.settled = lag(S.settled, S.raw * 0.6, 1200, dt);

    // filters: head loss builds with the solids they catch; a backwash takes 12 minutes
    let bw = false;
    const online = S.filters.filter((f) => f.state === 0);
    for (const f of S.filters) {
      if (f.state === 1 && t >= f.until) { f.state = 0; f.hl = 0.35; f.ripen = 0.5; }
      if (f.state === 1) bw = true;
    }
    // a filter in auto backwashes itself at terminal head loss, one at a time
    if (!bw) {
      const due = S.filters.findIndex((f) => f.state === 0 && f.auto && f.hl >= 2.6);
      if (due >= 0) {
        const f = S.filters[due];
        f.state = 1; f.until = t + 720; bw = true;
        S.log.push({ t, kind: "autobackwash", target: `F-${due + 1}`, value: "" });
      }
    }
    S.prod = lag(S.prod, Math.max(0, S.rawFlow * 0.985 - (bw ? 55 : 0)), 120, dt);
    const per = online.length ? S.prod / online.length : 0;
    let tw = 0, ts = 0;
    for (const f of S.filters) {
      if (f.state === 0) {
        f.hl += dt * S.settled * per * 0.7e-7 + noise(S, 0.0004);
        f.ripen = lag(f.ripen, 0, 900, dt);
        f.turb = Math.max(0.03, 0.05 + S.settled * 0.055 + Math.max(0, f.hl - 2.3) * 1.6 + f.ripen + noise(S, 0.006));
        tw += f.turb * per; ts += per;
      } else {
        f.turb = f.state === 1 ? 25 + noise(S, 1) : 0;
      }
    }
    S.treated = ts > 0 ? tw / ts : S.treated;

    // chlorine: what the dose leaves after the water's demand, measured at the reservoir
    const clDemand = 0.34 + 0.12 * S.treated;
    S.clRes = Math.max(0, lag(S.clRes, S.prod > 5 ? Math.max(0, S.clDose - clDemand) : S.clRes * 0.98, 1500, dt) + noise(S, 0.004));

    // the reservoir and the city: two distribution pumps on duty, one on standby
    S.demand = demandAt(t) * (1 + noise(S, 0.01));
    if (S.dsAutoAt >= 0 && t >= S.dsAutoAt) {
      S.dsAutoAt = -1;
      const sb = S.ds.findIndex((p) => p.state === 0 && p.auto);
      if (sb >= 0) { S.ds[sb].state = 1; S.log.push({ t, kind: "autostart", target: `DS-${sb + 1}`, value: "" }); }
    }
    let nDs = S.ds.filter((p) => p.state === 1).length;
    if (S.level < 0.6 && nDs) {
      for (let i = 0; i < 3; i++) if (S.ds[i].state === 1) { S.ds[i].state = 2; S.log.push({ t, kind: "tripped", target: `DS-${i + 1}`, value: "level" }); }
      nDs = 0;
    }
    const delivered = Math.min(S.demand, 260 * nDs);
    for (const p of S.ds) p.flow = p.state === 1 && nDs ? delivered / nDs + noise(S, 0.6) : 0;
    S.level = clamp(S.level + ((S.prod - delivered) * dt) / 1000 / 840, 0, 4.8);
    const r = nDs ? delivered / (nDs * 260) : 0;
    const short = S.demand > 0 ? Math.max(0, S.demand - delivered) / S.demand : 0;
    S.press = nDs ? Math.max(0.2, 4.05 - 0.9 * r * r - 4.5 * short + noise(S, 0.02)) : 0.2 + noise(S, 0.01);

    // write the sample
    out[I["river.level"]] = S.river;
    out[I["raw.turb"]] = S.raw;
    let kw = 38;
    for (let i = 0; i < 3; i++) {
      const p = S.rw[i], n = i + 1;
      const pkw = p.state === 1 ? 9 + 0.3 * p.flow : 0;
      kw += pkw;
      out[I[`RW-${n}.state`]] = p.state;
      out[I[`RW-${n}.flow`]] = p.flow;
      out[I[`RW-${n}.kw`]] = pkw;
      out[I[`RW-${n}.amp`]] = pkw * 1.64;
      out[I[`RW-${n}.vib`]] = p.vib;
      out[I[`RW-${n}.temp`]] = p.temp;
    }
    out[I["raw.flow"]] = S.rawFlow;
    out[I["flow.sp"]] = S.flowSp;
    out[I["pac.dose"]] = pacOn ? S.pacDose : 0;
    out[I["pac.demand"]] = demand;
    out[I["pac.tank"]] = S.pacTank;
    out[I["settled.turb"]] = S.settled;
    for (let i = 0; i < 4; i++) {
      const f = S.filters[i], n = i + 1;
      out[I[`F-${n}.hl`]] = f.hl;
      out[I[`F-${n}.state`]] = f.state;
      out[I[`F-${n}.turb`]] = f.turb;
    }
    out[I["treated.turb"]] = S.treated;
    out[I["prod.flow"]] = S.prod;
    out[I["cl.dose"]] = S.clDose;
    out[I["cl.res"]] = S.clRes;
    out[I["res.level"]] = S.level;
    out[I["demand"]] = S.demand;
    for (let i = 0; i < 3; i++) {
      const p = S.ds[i], n = i + 1;
      const pkw = p.state === 1 ? 12 + 0.28 * p.flow : 0;
      kw += pkw;
      out[I[`DS-${n}.state`]] = p.state;
      out[I[`DS-${n}.flow`]] = p.flow;
      out[I[`DS-${n}.kw`]] = pkw;
    }
    out[I["dist.press"]] = S.press;
    out[I["plant.kw"]] = kw;
  }

  // ── the run: state, history, commands, alarms ────────────────────────
  // History is one Float32Array, a row of NT values per sample; row k is the sample at k·DT.
  const R = {
    S: null, hist: new Float32Array(0), rows: 0, cmds: [], user: [], defs: [], active: new Map(), counters: null,
    alarms: [], acks: new Set(), speed: 60, acc: 0, vt: -1, live: true, seed: 2610, persist: true,
    cb: 0, raf: 0, last: 0, tickAt: 0, savedAt: 0,
  };

  function ensure(rows) {
    if (rows * NT <= R.hist.length) return;
    const next = new Float32Array(Math.max(rows * NT, R.hist.length * 2, 8640 * NT));
    next.set(R.hist);
    R.hist = next;
  }

  const row = new Float32Array(NT);
  function advance(fire) {
    // apply the commands due at this time, then step, then judge the alarms
    const S = R.S;
    while (R.cmdAt < R.cmds.length && R.cmds[R.cmdAt].t <= S.t) apply(S, R.cmds[R.cmdAt++]);
    step(S, row);
    ensure(R.rows + 1);
    R.hist.set(row, R.rows * NT);
    R.rows++;
    judge(row, S.t, fire);
  }

  function judge(v, t, fire) {
    for (let d = 0; d < R.defs.length; d++) {
      const a = R.defs[d];
      const x = v[a.ti];
      const on = R.active.get(a.id);
      if (!on) {
        const hit = a.op === ">" ? x > a.v : a.op === "<" ? x < a.v : Math.abs(x - a.v) < 1e-6;
        R.counters[d] = hit ? R.counters[d] + DT : 0;
        if (hit && R.counters[d] >= a.delay) {
          const ev = { key: `${a.id}@${t}`, def: a.id, on: true, t, val: x };
          R.active.set(a.id, ev);
          R.alarms.push(ev);
          fire?.push(ev);
        }
      } else {
        const clear = a.op === ">" ? x < a.v - a.db : a.op === "<" ? x > a.v + a.db : Math.abs(x - a.v) >= 1e-6;
        if (clear) {
          on.off = t;
          R.active.delete(a.id);
          R.counters[d] = 0;
          const ev = { key: on.key, def: a.id, on: false, t, val: x };
          fire?.push(ev);
        }
      }
    }
  }

  function begin(seed, user) {
    R.seed = seed;
    R.S = newState(seed);
    R.rows = 0;
    R.user = user.slice();
    R.cmds = [...NIGHT, ...R.user].sort((a, b) => a.t - b.t);
    R.cmdAt = 0;
    R.active = new Map();
    R.counters = new Float32Array(R.defs.length);
    R.alarms = [];
    R.acc = 0;
    // row 0 is midnight itself
    step(R.S, row);
    R.S.t = 0;
    ensure(1);
    R.hist.set(row, 0);
    R.rows = 1;
  }

  function runTo(t, fire) {
    while (R.S.t + DT <= t) advance(fire);
  }

  const now = () => R.S.t;
  const viewT = () => (R.live ? R.S.t : clamp(R.vt, 0, R.S.t));
  const rowAt = (t) => clamp(Math.round(t / DT), 0, R.rows - 1);
  const valuesAt = (t) => (t >= R.S.t ? present() : R.hist.subarray(rowAt(t) * NT, rowAt(t) * NT + NT));

  // The present: the last sample, with what an operator has just changed since it was taken
  // (a pump stopped, a setpoint moved) so the screen answers a command at once even while the
  // clock is paused. The history itself is never rewritten.
  const now_ = new Float32Array(NT);
  function present() {
    const S = R.S, k = R.rows - 1;
    now_.set(R.hist.subarray(k * NT, k * NT + NT));
    for (let i = 0; i < 3; i++) {
      now_[I[`RW-${i + 1}.state`]] = S.rw[i].state;
      now_[I[`DS-${i + 1}.state`]] = S.ds[i].state;
    }
    for (let i = 0; i < 4; i++) now_[I[`F-${i + 1}.state`]] = S.filters[i].state;
    now_[I["flow.sp"]] = S.flowSp;
    now_[I["pac.dose"]] = S.pacTank > 0.5 ? S.pacDose : 0;
    now_[I["cl.dose"]] = S.clDose;
    now_[I["pac.tank"]] = S.pacTank;
    return now_;
  }

  function kpis(t) {
    // today, from midnight of the viewed day up to the viewed time
    const end = rowAt(t), start = rowAt(Math.floor(t / DAY) * DAY);
    let prod = 0, kwh = 0, pac = 0, avail = 0, good = 0, n = 0, flow = 0;
    const H = R.hist;
    for (let k = start; k <= end; k++) {
      const o = k * NT;
      prod += (H[o + I["prod.flow"]] * DT) / 1000;
      kwh += (H[o + I["plant.kw"]] * DT) / 3600;
      pac += (H[o + I["pac.dose"]] * H[o + I["raw.flow"]] * DT) / 1e6;
      if (H[o + I["raw.flow"]] > 50) avail++;
      const cl = H[o + I["cl.res"]];
      if (H[o + I["treated.turb"]] < 1 && cl >= 0.3 && cl <= 1.5) good++;
      flow += H[o + I["prod.flow"]];
      n++;
    }
    const A = n ? avail / n : 0, P = n ? Math.min(1, flow / n / 400) : 0, Q = n ? good / n : 0;
    return [prod, kwh, prod > 0 ? kwh / prod : 0, A * P * Q, A, P, Q, pac];
  }

  function hash(rows) {
    // FNV-1a over the history's bytes: the same seed and commands must give the same number
    const b = new Uint8Array(R.hist.buffer, 0, rows * NT * 4);
    let x = 0x811c9dc5;
    for (let i = 0; i < b.length; i++) x = Math.imul(x ^ b[i], 0x01000193) >>> 0;
    return x.toString(16).padStart(8, "0");
  }

  // ── persistence: the viewer's own run survives a reload ──────────────
  const KEY = "bening.sim";
  function save() {
    if (!R.persist) return;
    try { localStorage.setItem(KEY, JSON.stringify({ seed: R.seed, user: R.user, t: R.S.t, speed: R.speed })); } catch {}
  }
  function restore() {
    try { return JSON.parse(localStorage.getItem(KEY) || "null"); } catch { return null; }
  }

  // ── math for the view ────────────────────────────────────────────────
  const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
  const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
  const scale = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
  const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  const norm = (a) => { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };
  function mat(a, b) {
    const o = new Float32Array(16);
    for (let c = 0; c < 4; c++)
      for (let r = 0; r < 4; r++)
        o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
    return o;
  }
  function lookAt(eye, at, up) {
    const z = norm(sub(eye, at)), x = norm(cross(up, z)), y = cross(z, x);
    return new Float32Array([x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1]);
  }
  function persp(fovy, aspect, n, f) {
    const t = 1 / Math.tan(fovy / 2);
    return new Float32Array([t / aspect, 0, 0, 0, 0, t, 0, 0, 0, 0, (f + n) / (n - f), -1, 0, 0, (2 * f * n) / (n - f), 0]);
  }
  function ortho(l, r, b, t, n, f) {
    return new Float32Array([2 / (r - l), 0, 0, 0, 0, 2 / (t - b), 0, 0, 0, 0, -2 / (f - n), 0, -(r + l) / (r - l), -(t + b) / (t - b), -(f + n) / (f - n), 1]);
  }
  function trs(n) {
    if (n.matrix) return new Float32Array(n.matrix);
    const [tx, ty, tz] = n.translation ?? [0, 0, 0];
    const [qx, qy, qz, qw] = n.rotation ?? [0, 0, 0, 1];
    const [sx, sy, sz] = n.scale ?? [1, 1, 1];
    const xx = qx * qx, yy = qy * qy, zz = qz * qz, xy = qx * qy, xz = qx * qz, yz = qy * qz, wx = qw * qx, wy = qw * qy, wz = qw * qz;
    return new Float32Array([
      (1 - 2 * (yy + zz)) * sx, 2 * (xy + wz) * sx, 2 * (xz - wy) * sx, 0,
      2 * (xy - wz) * sy, (1 - 2 * (xx + zz)) * sy, 2 * (yz + wx) * sy, 0,
      2 * (xz + wy) * sz, 2 * (yz - wx) * sz, (1 - 2 * (xx + yy)) * sz, 0,
      tx, ty, tz, 1,
    ]);
  }
  function rot(axis, a) {
    const c = Math.cos(a), s = Math.sin(a);
    if (axis === "x") return new Float32Array([1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1]);
    if (axis === "y") return new Float32Array([c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1]);
    return new Float32Array([c, s, 0, 0, -s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
  }
  const xf = (m, p) => [m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12], m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13], m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14]];
  const srgbToLinear = (c) => (c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
  const hex = (s) => {
    const m = /^#?([0-9a-f]{6})$/i.exec((s || "").trim());
    if (!m) return [0.5, 0.5, 0.5];
    const n = parseInt(m[1], 16);
    return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255].map(srgbToLinear);
  };

  // ── the view's state ─────────────────────────────────────────────────
  const V = {
    host: null, canvas: null, gl: null, overlay: null, c2: null, w: 0, h: 0, dpr: 1, cb: 0, raf: 0,
    cam: { target: [6, 0, 14], dist: 150, yaw: -0.38, pitch: 0.6, fov: (34 * Math.PI) / 180 },
    goal: null, draws: [], assets: new Map(), names: {}, selected: null, hover: null, pins: [],
    angle: 0, lastFrame: 0, lastTick: 0, frames: [], drag: null, colors: {}, ready: false, loadMs: 0,
    timeline: null, tl: null, sparkAt: 0,
  };

  // ── GL ───────────────────────────────────────────────────────────────
  const VS = `#version 300 es
  layout(location=0) in vec3 aPos; layout(location=1) in vec3 aNrm;
  uniform mat4 uVP, uM, uLight;
  out vec3 vW; out vec3 vN; out vec4 vL;
  void main(){ vec4 w = uM * vec4(aPos, 1.0); vW = w.xyz; vN = mat3(uM) * aNrm; vL = uLight * w; gl_Position = uVP * w; }`;
  const FS = `#version 300 es
  precision highp float; precision highp sampler2DShadow;
  in vec3 vW; in vec3 vN; in vec4 vL; out vec4 o;
  uniform vec3 uColor, uEye, uSun, uSky, uGround, uFog; uniform vec4 uTint; uniform float uMetal, uRough, uWater, uTime, uFogNear, uFogFar, uExposure;
  uniform sampler2DShadow uShadow;
  vec3 srgb(vec3 c){ return mix(12.92*c, 1.055*pow(c, vec3(1.0/2.4)) - 0.055, step(0.0031308, c)); }
  float shadow(){
    vec3 p = vL.xyz / vL.w * 0.5 + 0.5;
    if (p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0) return 1.0;
    float s = 0.0; vec2 px = vec2(1.0 / 2048.0);
    for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) s += texture(uShadow, vec3(p.xy + vec2(x, y) * px, p.z - 0.0012));
    return s / 9.0;
  }
  void main(){
    vec3 n = normalize(vN); if (!gl_FrontFacing) n = -n;
    vec3 base = uColor;
    if (uWater > 0.5) {
      // a slow ripple, so water reads as water
      float r = sin(vW.x * 1.7 + uTime * 1.3) * 0.5 + sin(vW.z * 2.3 - uTime * 0.9) * 0.5;
      n = normalize(n + vec3(0.04 * r, 0.0, 0.03 * r));
    }
    vec3 v = normalize(uEye - vW);
    float sh = shadow();
    float nl = max(dot(n, uSun), 0.0);
    vec3 h = normalize(uSun + v);
    float shin = clamp(2.0 / max(uRough * uRough * uRough * uRough, 0.002) - 2.0, 4.0, 600.0);
    vec3 spec0 = mix(vec3(0.04), base, uMetal);
    vec3 diff = base * (1.0 - uMetal * 0.8);
    vec3 amb = mix(uGround, uSky, n.y * 0.5 + 0.5);
    vec3 col = diff * amb * 0.62 + sh * (diff * nl * 1.05 + spec0 * pow(max(dot(n, h), 0.0), shin) * (shin + 8.0) / 40.0 * nl);
    if (uWater > 0.5) {
      float fres = pow(1.0 - max(dot(n, v), 0.0), 4.0);
      col = mix(col, uSky * 1.1, fres * 0.6);
    }
    col *= uExposure;
    col = mix(col, uTint.rgb, uTint.a);
    float d = length(uEye - vW);
    col = mix(col, uFog, smoothstep(uFogNear, uFogFar, d));
    o = vec4(srgb(col), 1.0);
  }`;
  const SHADOW_VS = `#version 300 es
  layout(location=0) in vec3 aPos; uniform mat4 uLight, uM;
  void main(){ gl_Position = uLight * uM * vec4(aPos, 1.0); }`;
  const SHADOW_FS = `#version 300 es
  precision mediump float; out vec4 o; void main(){ o = vec4(1.0); }`;
  const PICK_FS = `#version 300 es
  precision mediump float; uniform vec4 uId; out vec4 o; void main(){ o = uId; }`;

  function program(gl, vs, fs) {
    const p = gl.createProgram();
    for (const [type, src] of [[gl.VERTEX_SHADER, vs], [gl.FRAGMENT_SHADER, fs]]) {
      const s = gl.createShader(type);
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
      gl.attachShader(p, s);
    }
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
    const u = {};
    const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < n; i++) {
      const name = gl.getActiveUniform(p, i).name;
      u[name] = gl.getUniformLocation(p, name);
    }
    return { p, u };
  }

  function initGl() {
    const gl = V.canvas.getContext("webgl2", { antialias: true, preserveDrawingBuffer: true });
    if (!gl) throw new Error("This browser has no WebGL2.");
    V.gl = gl;
    V.prog = program(gl, VS, FS);
    V.shadowProg = program(gl, SHADOW_VS, SHADOW_FS);
    V.pickProg = program(gl, VS, PICK_FS);
    V.shadowTex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, V.shadowTex);
    gl.texStorage2D(gl.TEXTURE_2D, 1, gl.DEPTH_COMPONENT24, 2048, 2048);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_COMPARE_MODE, gl.COMPARE_REF_TO_TEXTURE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    V.shadowFbo = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, V.shadowFbo);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, V.shadowTex, 0);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    V.pickFbo = gl.createFramebuffer();
    V.pickTex = gl.createRenderbuffer();
    V.pickDepth = gl.createRenderbuffer();
    V.pickW = V.pickH = 0;
    V.shadowDirty = true;
  }

  function parseGlb(buf) {
    const dv = new DataView(buf);
    if (dv.getUint32(0, true) !== 0x46546c67) throw new Error("Not a glTF binary file.");
    let off = 12, json = null, bin = null;
    while (off < buf.byteLength) {
      const len = dv.getUint32(off, true), type = dv.getUint32(off + 4, true);
      if (type === 0x4e4f534a) json = JSON.parse(new TextDecoder().decode(new Uint8Array(buf, off + 8, len)));
      else if (type === 0x004e4942) bin = new Uint8Array(buf, off + 8, len);
      off += 8 + len;
    }
    const acc = (i) => {
      const a = json.accessors[i], v = json.bufferViews[a.bufferView];
      const start = (v.byteOffset ?? 0) + (a.byteOffset ?? 0);
      const comps = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4 }[a.type];
      const T = { 5126: Float32Array, 5123: Uint16Array, 5125: Uint32Array, 5121: Uint8Array }[a.componentType];
      return new T(bin.buffer.slice(bin.byteOffset + start, bin.byteOffset + start + a.count * comps * T.BYTES_PER_ELEMENT));
    };
    return { json, acc };
  }

  function upload(positions, normals, indices) {
    const gl = V.gl;
    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    const mk = (data, loc) => {
      const b = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, b);
      gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
      gl.enableVertexAttribArray(loc);
      gl.vertexAttribPointer(loc, 3, gl.FLOAT, false, 0, 0);
      return b;
    };
    const bufs = [mk(positions, 0), mk(normals, 1)];
    const ib = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ib);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW);
    gl.bindVertexArray(null);
    return { vao, bufs: [...bufs, ib], count: indices.length, type: indices instanceof Uint32Array ? gl.UNSIGNED_INT : gl.UNSIGNED_SHORT };
  }

  async function load(url) {
    const t0 = performance.now();
    const res = await fetch(url);
    if (!res.ok) throw new Error(`The plant model did not load (${res.status}).`);
    const { json, acc } = parseGlb(await res.arrayBuffer());
    const mats = (json.materials ?? []).map((m) => {
      const pbr = m.pbrMetallicRoughness ?? {};
      return { color: (pbr.baseColorFactor ?? [0.8, 0.8, 0.8, 1]).slice(0, 3), metal: pbr.metallicFactor ?? 0, rough: pbr.roughnessFactor ?? 0.7 };
    });
    const parents = new Map();
    for (const [i, n] of json.nodes.entries()) for (const c of n.children ?? []) parents.set(c, i);
    const world = new Map();
    const worldOf = (i) => {
      if (world.has(i)) return world.get(i);
      const local = trs(json.nodes[i]);
      const p = parents.get(i);
      const w = p === undefined ? local : mat(worldOf(p), local);
      world.set(i, w);
      return w;
    };
    const assetOf = (i) => {
      for (let k = i; k !== undefined; k = parents.get(k)) if (json.nodes[k].extras?.asset) return json.nodes[k].extras.asset;
      return "SITE";
    };
    V.draws = [];
    V.assets = new Map();
    let ai = 0;
    for (const [i, n] of json.nodes.entries()) {
      if (n.mesh === undefined) continue;
      const asset = assetOf(i);
      if (!V.assets.has(asset)) V.assets.set(asset, { id: asset, index: ++ai, lo: [1e9, 1e9, 1e9], hi: [-1e9, -1e9, -1e9] });
      const A = V.assets.get(asset);
      const p = parents.get(i);
      const parentW = p === undefined ? new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]) : worldOf(p);
      const local = trs(n);
      const w = worldOf(i);
      for (const prim of json.meshes[n.mesh].primitives) {
        const pos = acc(prim.attributes.POSITION), nrm = acc(prim.attributes.NORMAL);
        const idx = prim.indices !== undefined ? acc(prim.indices) : new Uint32Array(pos.length / 3).map((_, k) => k);
        for (let k = 0; k < pos.length; k += 3) {
          const q = xf(w, [pos[k], pos[k + 1], pos[k + 2]]);
          for (let c = 0; c < 3; c++) { A.lo[c] = Math.min(A.lo[c], q[c]); A.hi[c] = Math.max(A.hi[c], q[c]); }
        }
        const m = mats[prim.material ?? 0] ?? { color: [0.8, 0.8, 0.8], metal: 0, rough: 0.7 };
        V.draws.push({
          name: n.name, asset: A, mesh: upload(pos, nrm, idx), color: m.color, metal: m.metal, rough: m.rough,
          spin: n.extras?.spin ?? null, water: n.extras?.water ?? null, level: n.extras?.level ?? null,
          parentW, local, model: w,
        });
      }
    }
    for (const A of V.assets.values()) A.center = [(A.lo[0] + A.hi[0]) / 2, A.hi[1], (A.lo[2] + A.hi[2]) / 2];
    V.byIndex = new Map([...V.assets.values()].map((a) => [a.index, a]));
    // the scene's extent, for the shadow camera and the fog
    const site = V.assets.get("SITE");
    V.bounds = site ? { lo: site.lo, hi: site.hi } : { lo: [-100, 0, -60], hi: [100, 10, 60] };
    V.shadowDirty = true;
    V.loadMs = Math.round(performance.now() - t0);
  }

  // ── the frame ────────────────────────────────────────────────────────
  function readColors() {
    const cs = getComputedStyle(V.host);
    const get = (n, d) => hex(cs.getPropertyValue(n) || d);
    V.colors = {
      sky: get("--vp-sky", "#dce8ea"), ground: get("--vp-ground", "#6b6a58"), fog: get("--vp-fog", "#e4ecea"),
      clear: get("--vp-water-clear", "#2a7f96"), mud: get("--vp-water-mud", "#8a6a3c"), select: get("--vp-select", "#1d7fa6"),
      alarm: get("--vp-alarm", "#c8372d"), warn: get("--vp-warn", "#d98e04"), stop: get("--vp-stop", "#9aa3a1"),
      bg: (cs.getPropertyValue("--vp-fog") || "#e4ecea").trim(),
      ink: (cs.getPropertyValue("--ink") || "#102a2b").trim(), paper: (cs.getPropertyValue("--panel") || "#f8faf8").trim(),
      alarmCss: (cs.getPropertyValue("--alarm") || "#c8372d").trim(), warnCss: (cs.getPropertyValue("--warn") || "#d98e04").trim(),
      lowCss: (cs.getPropertyValue("--low") || "#b78a1c").trim(), accentCss: (cs.getPropertyValue("--accent") || "#1d7fa6").trim(),
      mudCss: (cs.getPropertyValue("--mud") || "#8a6a3c").trim(), clearCss: (cs.getPropertyValue("--clear") || "#1d7fa6").trim(),
      lineCss: (cs.getPropertyValue("--line") || "#c9d3cf").trim(), ink2Css: (cs.getPropertyValue("--ink-3") || "#6b7d7b").trim(),
      night: matchMedia("(prefers-color-scheme: dark)").matches && document.documentElement.dataset.theme !== "light" || document.documentElement.dataset.theme === "dark",
    };
  }

  function camera() {
    const c = V.cam;
    const eye = add(c.target, [c.dist * Math.cos(c.pitch) * Math.sin(c.yaw), c.dist * Math.sin(c.pitch), c.dist * Math.cos(c.pitch) * Math.cos(c.yaw)]);
    V.eye = eye;
    V.view = lookAt(eye, c.target, [0, 1, 0]);
    V.proj = persp(c.fov, V.w / Math.max(1, V.h), Math.max(0.5, c.dist * 0.02), c.dist * 6 + 400);
    V.vp = mat(V.proj, V.view);
  }

  function project(p) {
    const m = V.vp;
    const x = m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12];
    const y = m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13];
    const w = m[3] * p[0] + m[7] * p[1] + m[11] * p[2] + m[15];
    return { x: ((x / w) * 0.5 + 0.5) * V.w, y: (1 - ((y / w) * 0.5 + 0.5)) * V.h, behind: w <= 0 };
  }

  function waterTurb(stage, v) {
    if (stage === "raw" || stage === "mix") return v[I["raw.turb"]];
    if (stage === "floc") return v[I["raw.turb"]] * 0.75;
    if (stage === "settle") return v[I["settled.turb"]];
    if (stage.startsWith("F-")) {
      const st = v[I[`${stage}.state`]];
      return st === 1 ? 260 : v[I["settled.turb"]] * 0.9;
    }
    return 1;
  }

  function turbColor(ntu) {
    const f = clamp(Math.log10(ntu + 1) / Math.log10(300), 0, 1);
    const a = V.colors.clear, b = V.colors.mud;
    return [a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f];
  }

  // which assets are in alarm right now (or at the viewed time), by highest priority
  function alarmMap(t) {
    const out = new Map();
    for (const ev of R.alarms) {
      if (ev.t > t || (ev.off !== undefined && ev.off <= t)) continue;
      const d = R.defsById.get(ev.def);
      if (!d) continue;
      const cur = out.get(d.asset);
      const acked = R.acks.has(ev.key);
      if (!cur || d.prio < cur.prio || (d.prio === cur.prio && !acked && cur.acked)) out.set(d.asset, { prio: d.prio, acked, def: d, ev });
    }
    return out;
  }

  function stateOf(asset, v) {
    const k = I[`${asset}.state`];
    return k === undefined ? 1 : v[k];
  }

  // the clock: runs whenever the app does, with or without the 3D view on screen
  function clock(ts) {
    R.raf = requestAnimationFrame(clock);
    const realDt = R.last ? Math.min(0.25, (ts - R.last) / 1000) : 0;
    R.last = ts;
    if (R.speed > 0 && R.S) {
      R.acc += realDt * R.speed;
      const fired = [];
      let steps = 0;
      while (R.acc >= DT && steps < 400) { advance(fired); R.acc -= DT; steps++; }
      if (steps && (fired.length || R.S.log.length)) flushEvents(fired);
    }
    if (ts - R.tickAt > 450) { R.tickAt = ts; tick(); }
    if (ts - R.savedAt > 5000) { R.savedAt = ts; save(); }
  }

  function frame(ts) {
    V.raf = requestAnimationFrame(frame);
    const gl = V.gl;
    if (!gl || !V.ready) return;
    const realDt = V.lastFrame ? Math.min(0.1, (ts - V.lastFrame) / 1000) : 0;
    V.lastFrame = ts;
    const work0 = performance.now();

    // camera easing
    if (V.goal) {
      const g = V.goal, c = V.cam, k = 1 - Math.exp(-realDt * 6);
      c.target = add(c.target, scale(sub(g.target, c.target), k));
      c.dist += (g.dist - c.dist) * k;
      c.yaw += (g.yaw - c.yaw) * k;
      c.pitch += (g.pitch - c.pitch) * k;
      if (Math.abs(g.dist - c.dist) < 0.05 && Math.hypot(...sub(g.target, c.target)) < 0.02 && Math.abs(g.yaw - c.yaw) < 0.001) V.goal = null;
    }

    const rect = V.host.getBoundingClientRect();
    const dpr = Math.min(2, devicePixelRatio || 1);
    if (rect.width !== V.w || rect.height !== V.h || dpr !== V.dpr) {
      V.w = rect.width; V.h = rect.height; V.dpr = dpr;
      V.canvas.width = Math.max(1, Math.round(V.w * dpr));
      V.canvas.height = Math.max(1, Math.round(V.h * dpr));
      V.overlay.width = V.canvas.width;
      V.overlay.height = V.canvas.height;
    }
    camera();
    const t = viewT();
    const v = valuesAt(t);
    const alarms = alarmMap(t);
    // parts turn while their equipment runs at the viewed time
    V.angle += realDt;
    const pulse = 0.5 + 0.5 * Math.sin(ts / 220);

    // per-draw model matrices (spinning parts and the gauge change every frame)
    for (const d of V.draws) {
      if (d.spin) {
        const st = stateOf(d.asset.id, v);
        const running = d.asset.id.startsWith("FL-") || d.asset.id === "FM-1" ? v[I["raw.flow"]] > 20 : d.asset.id === "PAC" ? v[I["pac.dose"]] > 0 : d.asset.id === "CL-1" ? v[I["prod.flow"]] > 20 : st === 1;
        const rate = d.asset.id.startsWith("FL-") ? 0.35 : d.asset.id === "FM-1" ? 2.2 : 9;
        d.phase = (d.phase ?? 0) + (running ? realDt * rate * Math.min(1, R.speed ? 1 : 1) : 0);
        d.model = mat(mat(d.parentW, d.local), rot(d.spin, d.phase));
      } else if (d.level) {
        const f = clamp(v[I["res.level"]] / 4.8, 0.01, 1);
        d.model = mat(mat(d.parentW, d.local), new Float32Array([1, 0, 0, 0, 0, f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]));
      }
    }

    // shadows: the sun does not move, only the turning parts, so redraw the map rarely
    const sun = norm([-0.45, 0.82, 0.38]);
    if (V.shadowDirty || (V.shadowAt ?? 0) < ts - 250) {
      V.shadowAt = ts;
      V.shadowDirty = false;
      const b = V.bounds, c = scale(add(b.lo, b.hi), 0.5);
      const r = Math.hypot(b.hi[0] - b.lo[0], b.hi[2] - b.lo[2]) / 2;
      const lv = lookAt(add(c, scale(sun, r * 1.5)), c, [0, 1, 0]);
      V.light = mat(ortho(-r, r, -r, r, 1, r * 3.2), lv);
      gl.bindFramebuffer(gl.FRAMEBUFFER, V.shadowFbo);
      gl.viewport(0, 0, 2048, 2048);
      gl.clear(gl.DEPTH_BUFFER_BIT);
      gl.enable(gl.DEPTH_TEST);
      gl.useProgram(V.shadowProg.p);
      gl.uniformMatrix4fv(V.shadowProg.u.uLight, false, V.light);
      for (const d of V.draws) {
        if (d.water) continue;
        gl.uniformMatrix4fv(V.shadowProg.u.uM, false, d.model);
        gl.bindVertexArray(d.mesh.vao);
        gl.drawElements(gl.TRIANGLES, d.mesh.count, d.mesh.type, 0);
      }
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }

    gl.viewport(0, 0, V.canvas.width, V.canvas.height);
    const fog = V.colors.fog;
    const fogS = fog.map((x) => (x <= 0.0031308 ? 12.92 * x : 1.055 * Math.pow(x, 1 / 2.4) - 0.055));
    gl.clearColor(fogS[0], fogS[1], fogS[2], 1);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    gl.enable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);
    const P = V.prog;
    gl.useProgram(P.p);
    gl.uniformMatrix4fv(P.u.uVP, false, V.vp);
    gl.uniformMatrix4fv(P.u.uLight, false, V.light);
    gl.uniform3fv(P.u.uEye, V.eye);
    gl.uniform3fv(P.u.uSun, sun);
    const night = V.colors.night;
    gl.uniform3fv(P.u.uSky, night ? [0.32, 0.38, 0.45] : V.colors.sky);
    gl.uniform3fv(P.u.uGround, V.colors.ground);
    gl.uniform3fv(P.u.uFog, fog);
    gl.uniform1f(P.u.uFogNear, V.cam.dist * 1.2);
    gl.uniform1f(P.u.uFogFar, V.cam.dist * 3.2 + 150);
    gl.uniform1f(P.u.uTime, ts / 1000);
    gl.uniform1f(P.u.uExposure, night ? 0.5 : 1);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, V.shadowTex);
    gl.uniform1i(P.u.uShadow, 0);
    const sel = V.selected, hov = V.hover;
    for (const d of V.draws) {
      const id = d.asset.id;
      let color = d.color, metal = d.metal, rough = d.rough, tint = [0, 0, 0, 0];
      if (d.water) {
        color = turbColor(waterTurb(d.water, v));
        metal = 0;
        rough = 0.12;
      } else if (id !== "SITE" && id !== "RIVER") {
        const st = stateOf(id, v);
        const al = alarms.get(id);
        if (st === 2) tint = [...V.colors.alarm, 0.42 + (al && !al.acked ? 0.18 * pulse : 0)];
        else if (al) tint = [...(al.prio <= 2 ? V.colors.alarm : V.colors.warn), (al.acked ? 0.14 : 0.1 + 0.26 * pulse) * (al.prio === 3 ? 0.7 : 1)];
        else if (st === 0) tint = [...V.colors.stop, 0.5];
        if (id === sel) tint = tint[3] > 0 ? tint : [...V.colors.select, 0.22];
        else if (id === hov) tint = tint[3] > 0 ? tint : [...V.colors.select, 0.1];
      }
      gl.uniformMatrix4fv(P.u.uM, false, d.model);
      gl.uniform3fv(P.u.uColor, color);
      gl.uniform1f(P.u.uMetal, metal);
      gl.uniform1f(P.u.uRough, rough);
      gl.uniform1f(P.u.uWater, d.water ? 1 : 0);
      gl.uniform4fv(P.u.uTint, tint);
      gl.bindVertexArray(d.mesh.vao);
      gl.drawElements(gl.TRIANGLES, d.mesh.count, d.mesh.type, 0);
    }
    gl.bindVertexArray(null);
    drawOverlay(alarms, pulse);
    if (V.tl) drawTimeline();
    if (ts - V.sparkAt > 400) { V.sparkAt = ts; drawSparks(); }
    const ms = performance.now() - work0;
    V.frames.push(ms);
    if (V.frames.length > 240) V.frames.shift();
  }

  function drawOverlay(alarms, pulse) {
    const c = V.c2, dpr = V.dpr;
    c.setTransform(dpr, 0, 0, dpr, 0, 0);
    c.clearRect(0, 0, V.w, V.h);
    const col = V.colors;
    c.font = `600 11px "Atkinson Hyperlegible Mono", ui-monospace, monospace`;
    c.textBaseline = "middle";
    // alarm beacons: a ring over the equipment, pulsing until someone acknowledges it
    for (const [asset, a] of alarms) {
      const A = V.assets.get(asset);
      if (!A) continue;
      const p = project(add(A.center, [0, 2.5, 0]));
      if (p.behind) continue;
      const css = a.prio <= 1 ? col.alarmCss : a.prio === 2 ? col.warnCss : col.lowCss;
      const r = 9 + (a.acked ? 0 : 7 * pulse);
      c.beginPath();
      c.arc(p.x, p.y, r, 0, Math.PI * 2);
      c.strokeStyle = css;
      c.lineWidth = 2;
      c.globalAlpha = a.acked ? 0.8 : 1 - 0.5 * pulse;
      c.stroke();
      c.globalAlpha = 1;
      c.beginPath();
      c.arc(p.x, p.y, 5, 0, Math.PI * 2);
      c.fillStyle = css;
      c.fill();
      const label = `${asset} · ${V.names[`alarm:${a.def.id}`] ?? a.def.id}`;
      const w = c.measureText(label).width + 14;
      c.fillStyle = css;
      roundRect(c, p.x + 14, p.y - 10, w, 20, 4);
      c.fill();
      c.fillStyle = "#fff";
      c.fillText(label, p.x + 21, p.y + 0.5);
    }
    // the selected and hovered equipment get a name tag
    for (const id of [V.hover, V.selected]) {
      if (!id || id === "SITE" || alarms.has(id) && id !== V.selected) continue;
      const A = V.assets.get(id);
      if (!A) continue;
      const p = project(add(A.center, [0, 1.2, 0]));
      if (p.behind) continue;
      const label = `${id}  ${V.names[id] ?? ""}`.trim();
      const w = c.measureText(label).width + 14;
      // above the alarm beacon when there is one, so the two tags never overlap
      const y = alarms.has(id) ? project(add(A.center, [0, 2.5, 0])).y - 30 : p.y - 22;
      c.fillStyle = col.paper;
      c.strokeStyle = id === V.selected ? col.accentCss : col.lineCss;
      c.lineWidth = id === V.selected ? 2 : 1;
      roundRect(c, p.x - w / 2, y - 10, w, 20, 4);
      c.fill();
      c.stroke();
      c.fillStyle = col.ink;
      c.fillText(label, p.x - w / 2 + 7, y + 0.5);
      c.beginPath();
      c.moveTo(p.x, y + 10);
      c.lineTo(p.x, p.y);
      c.strokeStyle = id === V.selected ? col.accentCss : col.lineCss;
      c.stroke();
    }
    // the viewer's notes: a small numbered pin over the equipment
    for (const pin of V.pins) {
      const A = V.assets.get(pin.asset);
      if (!A) continue;
      const p = project(add(A.center, [2.2, 3.4, 0]));
      if (p.behind) continue;
      c.fillStyle = col.ink;
      c.beginPath();
      c.moveTo(p.x, p.y + 12);
      c.lineTo(p.x - 5, p.y + 3);
      c.arc(p.x, p.y, 7, Math.PI * 0.8, Math.PI * 0.2);
      c.closePath();
      c.fill();
      c.fillStyle = col.paper;
      c.textAlign = "center";
      c.fillText(String(pin.n), p.x, p.y + 0.5);
      c.textAlign = "left";
    }
  }

  function roundRect(c, x, y, w, h, r) {
    c.beginPath();
    c.moveTo(x + r, y);
    c.arcTo(x + w, y, x + w, y + h, r);
    c.arcTo(x + w, y + h, x, y + h, r);
    c.arcTo(x, y + h, x, y, r);
    c.arcTo(x, y, x + w, y, r);
    c.closePath();
  }

  // ── picking ──────────────────────────────────────────────────────────
  function pick(x, y) {
    const gl = V.gl;
    const W = V.canvas.width, H = V.canvas.height;
    if (V.pickW !== W || V.pickH !== H) {
      gl.bindRenderbuffer(gl.RENDERBUFFER, V.pickTex);
      gl.renderbufferStorage(gl.RENDERBUFFER, gl.RGBA8, W, H);
      gl.bindRenderbuffer(gl.RENDERBUFFER, V.pickDepth);
      gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, W, H);
      gl.bindFramebuffer(gl.FRAMEBUFFER, V.pickFbo);
      gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.RENDERBUFFER, V.pickTex);
      gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, V.pickDepth);
      V.pickW = W; V.pickH = H;
    }
    gl.bindFramebuffer(gl.FRAMEBUFFER, V.pickFbo);
    gl.viewport(0, 0, W, H);
    const px = Math.round(x * V.dpr), py = Math.round(H - y * V.dpr);
    gl.enable(gl.SCISSOR_TEST);
    gl.scissor(px, py, 1, 1);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    gl.useProgram(V.pickProg.p);
    gl.uniformMatrix4fv(V.pickProg.u.uVP, false, V.vp);
    for (const d of V.draws) {
      const k = d.asset.id === "SITE" || d.asset.id === "RIVER" ? 0 : d.asset.index;
      gl.uniform4fv(V.pickProg.u.uId, [(k & 255) / 255, ((k >> 8) & 255) / 255, 0, 1]);
      gl.uniformMatrix4fv(V.pickProg.u.uM, false, d.model);
      gl.bindVertexArray(d.mesh.vao);
      gl.drawElements(gl.TRIANGLES, d.mesh.count, d.mesh.type, 0);
    }
    const out = new Uint8Array(4);
    gl.readPixels(px, py, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, out);
    gl.disable(gl.SCISSOR_TEST);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.bindVertexArray(null);
    const k = out[0] + out[1] * 256;
    return k ? V.byIndex.get(k)?.id ?? null : null;
  }

  // ── pointer: orbit, pan, zoom, click to select ───────────────────────
  function onDown(e) {
    V.host.focus({ preventScroll: true });
    V.drag = { x: e.clientX, y: e.clientY, sx: e.clientX, sy: e.clientY, pan: e.button === 2 || e.shiftKey, moved: false, id: e.pointerId };
    V.canvas.setPointerCapture?.(e.pointerId);
    V.goal = null;
  }
  function onMove(e) {
    const r = V.canvas.getBoundingClientRect();
    if (!V.drag) {
      // hover picks at most every 60 ms: a pick reads a pixel back from the GPU
      if (e.target !== V.canvas || performance.now() - (V.hoverAt ?? 0) < 60) return;
      V.hoverAt = performance.now();
      const id = pick(e.clientX - r.left, e.clientY - r.top);
      if (id !== V.hover) {
        V.hover = id;
        V.canvas.style.cursor = id ? "pointer" : "";
      }
      return;
    }
    const d = V.drag;
    const dx = e.clientX - d.x, dy = e.clientY - d.y;
    d.x = e.clientX; d.y = e.clientY;
    if (Math.hypot(e.clientX - d.sx, e.clientY - d.sy) > 4) d.moved = true;
    const c = V.cam;
    if (d.pan) {
      const s = (c.dist * Math.tan(c.fov / 2) * 2) / Math.max(1, V.h);
      const right = [Math.cos(c.yaw), 0, -Math.sin(c.yaw)];
      const fwd = [Math.sin(c.yaw), 0, Math.cos(c.yaw)];
      c.target = add(c.target, add(scale(right, -dx * s), scale(fwd, -dy * s)));
    } else {
      c.yaw -= dx * 0.006;
      c.pitch = clamp(c.pitch + dy * 0.005, 0.12, 1.45);
    }
  }
  function onUp(e) {
    const d = V.drag;
    V.drag = null;
    if (!d || d.moved) return;
    const r = V.canvas.getBoundingClientRect();
    const id = pick(e.clientX - r.left, e.clientY - r.top);
    V.selected = id;
    send({ type: "pick", asset: id ?? "" });
  }
  function onWheel(e) {
    e.preventDefault();
    V.goal = null;
    V.cam.dist = clamp(V.cam.dist * Math.exp(e.deltaY * 0.0012), 12, 320);
  }
  function onKey(e) {
    if (e.key === "Escape") { V.selected = null; send({ type: "pick", asset: "" }); }
  }

  function focus(asset) {
    const A = V.assets.get(asset);
    if (!A) return;
    const size = Math.max(A.hi[0] - A.lo[0], A.hi[2] - A.lo[2], A.hi[1] - A.lo[1]);
    V.goal = { target: [(A.lo[0] + A.hi[0]) / 2, (A.lo[1] + A.hi[1]) / 2, (A.lo[2] + A.hi[2]) / 2], dist: clamp(size * 2.6 + 10, 18, 200), yaw: V.cam.yaw, pitch: Math.max(0.45, V.cam.pitch) };
  }

  // ── the timeline strip ───────────────────────────────────────────────
  // The viewed day from 00:00 to 24:00: raw turbidity (mud, log scale) and the reservoir
  // level (blue) as two traces, a tick for every alarm, the present, and the replay head.
  function drawTimeline() {
    const cv = V.tl, r = cv.getBoundingClientRect();
    const dpr = V.dpr;
    if (cv.width !== Math.round(r.width * dpr) || cv.height !== Math.round(r.height * dpr)) {
      cv.width = Math.round(r.width * dpr);
      cv.height = Math.round(r.height * dpr);
    }
    const c = cv.getContext("2d"), W = r.width, H = r.height;
    c.setTransform(dpr, 0, 0, dpr, 0, 0);
    c.clearRect(0, 0, W, H);
    const col = V.colors;
    const day0 = Math.floor(viewT() / DAY) * DAY;
    const X = (t) => ((t - day0) / DAY) * W;
    const top = 6, bot = H - 16, ph = bot - top;
    // future: hatched
    const nx = X(R.S.t);
    c.fillStyle = col.lineCss;
    c.globalAlpha = 0.35;
    for (let x = Math.floor(nx / 6) * 6; x < W; x += 6) c.fillRect(x, top, 1, ph);
    c.globalAlpha = 1;
    // hours
    c.font = `500 10px "Atkinson Hyperlegible Mono", ui-monospace, monospace`;
    c.textBaseline = "alphabetic";
    c.fillStyle = col.ink2Css;
    for (let hh = 0; hh <= 24; hh += 1) {
      const x = (hh / 24) * W;
      c.fillRect(x, bot, 1, hh % 6 === 0 ? 6 : 3);
      if (hh % 3 === 0 && hh < 24) c.fillText(String(hh).padStart(2, "0"), x + 3, H - 3);
    }
    const end = Math.min(R.S.t, day0 + DAY);
    const k0 = rowAt(day0), k1 = rowAt(end);
    const stride = Math.max(1, Math.floor((k1 - k0) / (W * 1.5)));
    const trace = (tag, f, css, width) => {
      c.beginPath();
      for (let k = k0; k <= k1; k += stride) {
        const y = bot - f(R.hist[k * NT + I[tag]]) * ph;
        const x = X(k * DT);
        k === k0 ? c.moveTo(x, y) : c.lineTo(x, y);
      }
      c.strokeStyle = css;
      c.lineWidth = width;
      c.stroke();
    };
    trace("raw.turb", (x) => clamp(Math.log10(x + 1) / Math.log10(500), 0, 1), col.mudCss, 1.5);
    trace("res.level", (x) => clamp(x / 4.8, 0, 1), col.clearCss, 1.5);
    // alarms
    for (const ev of R.alarms) {
      if (ev.t < day0 || ev.t > day0 + DAY) continue;
      const d = R.defsById.get(ev.def);
      const x = X(ev.t);
      c.fillStyle = d.prio <= 1 ? col.alarmCss : d.prio === 2 ? col.warnCss : col.lowCss;
      c.beginPath();
      c.moveTo(x, top);
      c.lineTo(x + 4, top + 6);
      c.lineTo(x - 4, top + 6);
      c.closePath();
      c.fill();
      if (ev.off !== undefined) { c.globalAlpha = 0.25; c.fillRect(x, top + 6, Math.max(1, X(ev.off) - x), 3); c.globalAlpha = 1; }
    }
    // the present and the replay head
    c.fillStyle = col.ink;
    c.fillRect(nx - 1, top - 2, 2, ph + 4);
    if (!R.live) {
      const vx = X(viewT());
      c.fillStyle = col.accentCss;
      c.fillRect(vx - 1.5, top - 4, 3, ph + 8);
      c.beginPath();
      c.arc(vx, top - 2, 5, 0, Math.PI * 2);
      c.fill();
    }
  }

  function timelineAt(e) {
    const r = V.tl.getBoundingClientRect();
    const day0 = Math.floor(viewT() / DAY) * DAY;
    return clamp(day0 + ((e.clientX - r.left) / r.width) * DAY, 0, R.S.t);
  }
  function tlDown(e) {
    V.tlDrag = true;
    V.tl.setPointerCapture?.(e.pointerId);
    setView(timelineAt(e));
  }
  function tlMove(e) { if (V.tlDrag) setView(timelineAt(e)); }
  function tlUp() { V.tlDrag = false; }

  function setView(t) {
    if (t >= R.S.t - DT) { R.live = true; }
    else { R.live = false; R.vt = Math.round(t / DT) * DT; }
    tick();
  }

  // ── sparklines: any canvas[data-spark] in the page, two hours up to the viewed time ──
  function drawSparks() {
    const list = document.querySelectorAll("canvas[data-spark]");
    if (!list.length || !R.S) return;
    const t1 = viewT(), t0 = Math.max(0, t1 - 7200);
    const k0 = rowAt(t0), k1 = rowAt(t1);
    for (const cv of list) {
      const ti = I[cv.dataset.spark];
      if (ti === undefined) continue;
      const r = cv.getBoundingClientRect();
      const dpr = V.dpr || 1;
      if (cv.width !== Math.round(r.width * dpr)) { cv.width = Math.round(r.width * dpr); cv.height = Math.round(r.height * dpr); }
      const c = cv.getContext("2d"), W = r.width, H = r.height;
      c.setTransform(dpr, 0, 0, dpr, 0, 0);
      c.clearRect(0, 0, W, H);
      let lo = Infinity, hi = -Infinity;
      for (let k = k0; k <= k1; k++) { const x = R.hist[k * NT + ti]; lo = Math.min(lo, x); hi = Math.max(hi, x); }
      const lim = cv.dataset.limit !== undefined ? +cv.dataset.limit : null;
      if (lim !== null) { lo = Math.min(lo, lim); hi = Math.max(hi, lim); }
      if (hi - lo < 1e-6) { hi += 1; lo -= 1; }
      const pad = (hi - lo) * 0.12;
      lo -= pad; hi += pad;
      const Y = (x) => H - 2 - ((x - lo) / (hi - lo)) * (H - 4);
      if (lim !== null) {
        c.strokeStyle = V.colors.alarmCss;
        c.setLineDash([3, 3]);
        c.beginPath();
        c.moveTo(0, Y(lim)); c.lineTo(W, Y(lim));
        c.lineWidth = 1;
        c.stroke();
        c.setLineDash([]);
      }
      c.beginPath();
      const n = Math.max(1, k1 - k0);
      for (let k = k0; k <= k1; k++) {
        const x = ((k - k0) / n) * W, y = Y(R.hist[k * NT + ti]);
        k === k0 ? c.moveTo(x, y) : c.lineTo(x, y);
      }
      c.strokeStyle = cv.dataset.color || V.colors.accentCss;
      c.lineWidth = 1.5;
      c.stroke();
    }
  }

  // ── talking to C# ────────────────────────────────────────────────────
  const send = (m) => R.cb && dispatch(R.cb, m);
  const fmt = (x) => (Math.abs(x) >= 100 ? x.toFixed(1) : x.toFixed(3));

  function tick() {
    if (!R.S) return;
    const t = viewT();
    const v = valuesAt(t);
    send({ type: "tick", t: R.S.t, vt: t, live: R.live, speed: R.speed, v: Array.from(v, fmt).join(","), k: kpis(t).map((x) => x.toFixed(4)).join(",") });
  }

  function flushEvents(fired) {
    const log = R.S.log.splice(0);
    send({ type: "events", alarms: fired, log });
  }

  function readyMessage() {
    return {
      type: "ready", tags: TAGS, t: R.S.t, seed: R.seed, speed: R.speed, loadMs: V.loadMs,
      alarms: R.alarms.map((e) => ({ key: e.key, def: e.def, t: e.t, off: e.off ?? -1, val: e.val })),
      cmds: R.cmds.map((c) => ({ t: c.t, kind: c.kind, target: c.target, value: c.value, who: c.who ?? "", night: NIGHT.includes(c) })),
      log: R.S.log.splice(0),
      hash: hash(R.rows),
    };
  }

  function boot({ seed, defs, fresh, id }) {
    if (id) R.cb = id;
    if (!R.raf && typeof requestAnimationFrame === "function") R.raf = requestAnimationFrame(clock);
    R.defs = defs.map((d) => ({ ...d, ti: I[d.tag] }));
    R.defsById = new Map(R.defs.map((d) => [d.id, d]));
    const saved = fresh ? null : restore();
    const s = saved && saved.seed === seed ? saved : null;
    begin(seed, s?.user ?? []);
    if (s?.speed !== undefined) R.speed = s.speed;
    runTo(Math.max(START, s?.t ?? START));
    R.live = true;
    return readyMessage();
  }

  // ── ops ──────────────────────────────────────────────────────────────
  ops["bn.attach"] = ({ host, timeline }) => {
    V.host = elementOf(host);
    V.canvas = document.createElement("canvas");
    V.canvas.className = "bn-gl";
    V.overlay = document.createElement("canvas");
    V.overlay.className = "bn-overlay";
    V.host.prepend(V.overlay);
    V.host.prepend(V.canvas);
    V.c2 = V.overlay.getContext("2d");
    V.w = V.h = 0;
    initGl();
    readColors();
    V.theme = new MutationObserver(readColors);
    V.theme.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "class"] });
    V.media = matchMedia("(prefers-color-scheme: dark)");
    V.media.addEventListener("change", readColors);
    V.canvas.addEventListener("pointerdown", onDown);
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    V.canvas.addEventListener("wheel", onWheel, { passive: false });
    V.canvas.addEventListener("contextmenu", (e) => e.preventDefault());
    V.host.addEventListener("keydown", onKey);
    if (timeline) {
      // the strip lives in another component: C# names it by its DOM id
      V.tl = typeof timeline === "string" && /\D/.test(timeline) ? document.getElementById(timeline) : elementOf(timeline);
      V.tl.addEventListener("pointerdown", tlDown);
      V.tl.addEventListener("pointermove", tlMove);
      V.tl.addEventListener("pointerup", tlUp);
      V.tl.addEventListener("pointercancel", tlUp);
    }
    V.raf = requestAnimationFrame(frame);
    return addHandle(V);
  };
  ops["bn.detach"] = () => {
    cancelAnimationFrame(V.raf);
    save();
    V.theme?.disconnect();
    V.media?.removeEventListener("change", readColors);
    window.removeEventListener("pointermove", onMove);
    window.removeEventListener("pointerup", onUp);
    const gl = V.gl;
    if (gl) for (const d of V.draws) { gl.deleteVertexArray(d.mesh.vao); for (const b of d.mesh.bufs) gl.deleteBuffer(b); }
    gl?.getExtension("WEBGL_lose_context")?.loseContext();
    V.canvas?.remove();
    V.overlay?.remove();
    V.gl = null;
    V.ready = false;
    V.tl = null;
  };
  // the simulation can start before the view (the report page has no 3D view)
  ops["bn.boot"] = (cfg) => {
    if (R.S && !cfg.fresh && R.seed === cfg.seed && R.defs.length) {
      if (cfg.id) R.cb = cfg.id;
      return readyMessage();
    }
    return boot(cfg);
  };
  ops["bn.load"] = async ({ url }) => {
    await load(url);
    V.ready = true;
    V.lastFrame = 0;
    return { loadMs: V.loadMs, assets: [...V.assets.keys()] };
  };
  ops["bn.names"] = ({ names }) => { V.names = names; };
  ops["bn.pins"] = ({ pins }) => { V.pins = pins; };
  ops["bn.acks"] = ({ keys }) => { R.acks = new Set(keys); };
  ops["bn.speed"] = ({ speed }) => { R.speed = speed; R.acc = 0; save(); tick(); };
  ops["bn.cmd"] = (c) => {
    // C# has already decided the command is allowed; it takes effect at the present time
    const cmd = { t: R.S.t, kind: c.kind, target: c.target, value: c.value, who: c.who };
    R.user.push(cmd);
    R.cmds.push(cmd);
    apply(R.S, cmd);
    R.cmdAt = R.cmds.length;
    save();
    tick();
    if (R.S.log.length) flushEvents([]);
    return { t: cmd.t };
  };
  ops["bn.view"] = ({ t, live }) => {
    if (live) { R.live = true; tick(); return; }
    setView(t);
  };
  ops["bn.select"] = ({ asset, focus: f }) => {
    V.selected = asset || null;
    if (asset && f) focus(asset);
  };
  ops["bn.getCamera"] = () => ({ target: V.cam.target, dist: V.cam.dist, yaw: V.cam.yaw, pitch: V.cam.pitch });
  ops["bn.setCamera"] = ({ cam }) => { V.goal = { target: cam.target, dist: cam.dist, yaw: cam.yaw, pitch: cam.pitch }; };
  ops["bn.home"] = () => { V.goal = { target: [6, 0, 14], dist: 150, yaw: -0.38, pitch: 0.6 }; };
  ops["bn.reset"] = ({ seed }) => {
    try { localStorage.removeItem(KEY); } catch {}
    R.speed = 60;
    return boot({ seed, defs: R.defs, fresh: true });
  };
  ops["bn.hourly"] = ({ tags }) => {
    // one row per hour of the viewed day so far: the mean of each tag over that hour
    const t1 = R.S.t, day0 = Math.floor(t1 / DAY) * DAY;
    const out = [];
    for (let h = 0; h < 24; h++) {
      const a = day0 + h * 3600;
      if (a > t1) break;
      const k0 = rowAt(a), k1 = rowAt(Math.min(t1, a + 3600 - DT));
      const sums = tags.map(() => 0);
      for (let k = k0; k <= k1; k++) tags.forEach((tg, j) => (sums[j] += R.hist[k * NT + I[tg]]));
      out.push({ h, n: k1 - k0 + 1, v: sums.map((s) => s / (k1 - k0 + 1)) });
    }
    return out;
  };
  ops["bn.csv"] = ({ name, cols, step = 60, date = "2026-10-22" }) => {
    // every `step` seconds of the day so far, the columns C# chose, with C#'s headers
    const lines = [["time", ...cols.map((c) => c.label)].map(q).join(",")];
    const base = Date.parse(date + "T00:00:00Z");
    for (let t = 0; t <= R.S.t; t += step) {
      const k = rowAt(t);
      const d = new Date(base + t * 1000).toISOString().replace("T", " ").slice(0, 19);
      lines.push([d, ...cols.map((c) => fmt(R.hist[k * NT + I[c.tag]]))].join(","));
    }
    const text = lines.join("\r\n") + "\r\n";
    download(name, text, "text/csv");
    return { rows: lines.length - 1, bytes: text.length };
  };
  ops["bn.download"] = ({ name, text, mime }) => download(name, text, mime);
  ops["bn.stats"] = () => {
    const f = V.frames.slice().sort((a, b) => a - b);
    return { frames: f.length, p50: f[Math.floor(f.length / 2)] ?? 0, p95: f[Math.floor(f.length * 0.95)] ?? 0, rows: R.rows, t: R.S?.t ?? 0 };
  };
  const q = (s) => (/[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
  function download(name, text, mime) {
    const url = URL.createObjectURL(new Blob(["﻿" + text], { type: (mime || "text/plain") + ";charset=utf-8" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 4000);
  }

  // hooks for the e2e script and for the curious
  globalThis.__bn = {
    R, V, TAGS, I,
    value: (tag, t) => valuesAt(t ?? viewT())[I[tag]],
    hash: (t) => hash(Math.min(R.rows, rowAt(t ?? R.S.t) + 1)),
    // fast-forward the live run, as if the clock had run that far
    runTo: (t) => {
      const fired = [];
      runTo(t, fired);
      if (fired.length || R.S.log.length) flushEvents(fired);
      tick();
      return R.S.t;
    },
    // a run of its own, away from the live one: same seed and commands, same history
    simulate: (seed, until, cmds = []) => {
      const keep = { ...R, S: R.S, hist: R.hist, rows: R.rows };
      R.persist = false;
      R.hist = new Float32Array(0);
      begin(seed, cmds);
      runTo(until);
      const out = { hash: hash(R.rows), rows: R.rows, alarms: R.alarms.map((e) => e.key), last: Array.from(valuesAt(until)) };
      Object.assign(R, keep, { persist: true });
      return out;
    },
    screen: (asset) => {
      const A = V.assets.get(asset);
      if (!A) return null;
      // a point on the asset that is not hidden behind something else
      const r = V.canvas.getBoundingClientRect();
      const tries = [[0.5, 0.5], [0.5, 1], [0.35, 0.8], [0.65, 0.8], [0.5, 0.7], [0.3, 0.5], [0.7, 0.5]];
      for (const [fx, fy] of tries) {
        const p = project([A.lo[0] + (A.hi[0] - A.lo[0]) * fx, A.lo[1] + (A.hi[1] - A.lo[1]) * fy, A.lo[2] + (A.hi[2] - A.lo[2]) * 0.5]);
        if (!p.behind && p.x > 0 && p.y > 0 && p.x < V.w && p.y < V.h && pick(p.x, p.y) === asset) return { x: r.left + p.x, y: r.top + p.y };
      }
      return null;
    },
    pickAt: (x, y) => { const r = V.canvas.getBoundingClientRect(); return pick(x - r.left, y - r.top); },
    pixel: (x, y) => {
      const gl = V.gl, r = V.canvas.getBoundingClientRect();
      const px = new Uint8Array(4);
      gl.readPixels(Math.round((x - r.left) * V.dpr), Math.round(V.canvas.height - (y - r.top) * V.dpr), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px);
      return [...px];
    },
    timelineX: (t) => {
      const r = V.tl.getBoundingClientRect();
      const day0 = Math.floor(viewT() / DAY) * DAY;
      return { x: r.left + ((t - day0) / DAY) * r.width, y: r.top + r.height / 2 };
    },
  };
}
