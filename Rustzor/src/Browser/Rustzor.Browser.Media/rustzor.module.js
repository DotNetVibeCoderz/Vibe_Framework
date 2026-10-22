// Rustzor.Browser.Media — getUserMedia, screen capture, devices, recording, mixing.
export function install({ ops, dispatch, elementOf, addHandle, getHandle, releaseHandle }) {
  ops["media.user"] = async ({ video, audio, w, h, device, facing, mic }) => {
    const v = video ? { width: { ideal: w }, height: { ideal: h }, ...(device ? { deviceId: { exact: device } } : { facingMode: facing }) } : false;
    const a = audio ? (mic ? { deviceId: { exact: mic }, echoCancellation: true, noiseSuppression: true } : true) : false;
    return addHandle(await navigator.mediaDevices.getUserMedia({ video: v, audio: a }));
  };
  ops["media.display"] = async ({ audio }) => addHandle(await navigator.mediaDevices.getDisplayMedia({ video: true, audio }));
  ops["media.devices"] = async () =>
    (await navigator.mediaDevices.enumerateDevices()).map((d) => ({ id: d.deviceId, kind: d.kind, label: d.label }));
  ops["media.kinds"] = ({ h }) => {
    const s = getHandle(h);
    return { audio: !!s?.getAudioTracks().length, video: !!s?.getVideoTracks().length };
  };
  ops["media.attach"] = ({ h, el, muted }) => {
    const v = elementOf(el);
    if (!v) return;
    const s = getHandle(h);
    if (v.srcObject !== s) v.srcObject = s;
    v.muted = muted;
    v.playsInline = true;
    v.play().catch(() => {});
  };
  ops["media.tracks"] = ({ h, kind, on }) => {
    for (const t of getHandle(h)?.getTracks() ?? []) if (t.kind === kind) t.enabled = on;
  };
  ops["media.onEnded"] = ({ h, id }) => {
    const s = getHandle(h);
    let told = false;
    for (const t of s?.getTracks() ?? [])
      t.addEventListener("ended", () => {
        if (!told) dispatch(id, true);
        told = true;
      });
  };
  const meters = new Map();
  ops["media.level"] = ({ h }) => {
    const s = getHandle(h);
    if (!s || s.getAudioTracks().length === 0) return 0;
    let m = meters.get(h);
    if (!m) {
      const ctx = new AudioContext();
      const an = ctx.createAnalyser();
      an.fftSize = 256;
      ctx.createMediaStreamSource(s).connect(an);
      m = { ctx, an, buf: new Uint8Array(an.fftSize) };
      meters.set(h, m);
    }
    if (m.ctx.state === "suspended") m.ctx.resume().catch(() => {});
    m.an.getByteTimeDomainData(m.buf);
    let sum = 0;
    for (const v of m.buf) sum += ((v - 128) / 128) ** 2;
    return Math.min(1, Math.sqrt(sum / m.buf.length) * 4);
  };
  ops["media.stop"] = ({ h }) => {
    getHandle(h)?.getTracks().forEach((t) => t.stop());
    meters.get(h)?.ctx.close().catch(() => {});
    meters.delete(h);
    releaseHandle(h);
  };
  ops["media.recorder"] = ({ h, type }) => {
    const rec = new MediaRecorder(getHandle(h), MediaRecorder.isTypeSupported(type) ? { mimeType: type } : {});
    rec.__chunks = [];
    rec.ondataavailable = (e) => e.data.size && rec.__chunks.push(e.data);
    return addHandle(rec);
  };
  ops["media.recStart"] = ({ r }) => {
    const rec = getHandle(r);
    rec.__chunks = [];
    rec.start(250);
  };
  ops["media.recStop"] = ({ r }) =>
    new Promise((resolve) => {
      const rec = getHandle(r);
      rec.onstop = () => {
        const blob = new Blob(rec.__chunks, { type: rec.mimeType });
        resolve({ url: URL.createObjectURL(blob), size: blob.size });
      };
      rec.stop();
    });

  // A mixer draws several streams into one canvas (a grid with name labels)
  // and mixes their audio, giving one stream to record or send.
  ops["media.mixCreate"] = ({ w, h, fps }) => {
    const canvas = document.createElement("canvas");
    canvas.width = w;
    canvas.height = h;
    const g = canvas.getContext("2d");
    const ac = new AudioContext();
    const dest = ac.createMediaStreamDestination();
    const box = { canvas, g, ac, dest, items: [], videos: new Map(), sources: new Map(), bg: "#111", fg: "#fff" };
    const out = canvas.captureStream(fps);
    dest.stream.getAudioTracks().forEach((t) => out.addTrack(t));
    box.stream = addHandle(out);
    box.timer = setInterval(() => drawMix(box), Math.round(1000 / fps));
    return addHandle(box);
  };
  ops["media.mixStream"] = ({ m }) => getHandle(m).stream;
  ops["media.mixSet"] = ({ m, items, bg, fg }) => {
    const box = getHandle(m);
    box.bg = bg || box.bg;
    box.fg = fg || box.fg;
    const keep = new Set(items.map((i) => i.h));
    for (const [h, v] of box.videos) if (!keep.has(h)) { v.srcObject = null; box.videos.delete(h); }
    for (const [h, src] of box.sources) if (!keep.has(h)) { src.disconnect(); box.sources.delete(h); }
    for (const it of items) {
      const s = getHandle(it.h);
      if (!s) continue;
      if (!box.videos.has(it.h)) {
        const v = document.createElement("video");
        v.muted = true;
        v.playsInline = true;
        v.srcObject = s;
        v.play().catch(() => {});
        box.videos.set(it.h, v);
      }
      if (!box.sources.has(it.h) && s.getAudioTracks().length) {
        const src = box.ac.createMediaStreamSource(s);
        src.connect(box.dest);
        box.sources.set(it.h, src);
      }
    }
    box.items = items;
    if (box.ac.state === "suspended") box.ac.resume().catch(() => {});
  };
  function drawMix(box) {
    const { g, canvas, items } = box;
    const W = canvas.width, H = canvas.height;
    g.fillStyle = box.bg;
    g.fillRect(0, 0, W, H);
    const n = Math.max(1, items.length);
    const cols = Math.ceil(Math.sqrt(n)), rows = Math.ceil(n / cols);
    const pad = 8, cw = (W - pad * (cols + 1)) / cols, ch = (H - pad * (rows + 1)) / rows;
    items.forEach((it, i) => {
      const x = pad + (i % cols) * (cw + pad), y = pad + Math.floor(i / cols) * (ch + pad);
      const v = box.videos.get(it.h);
      g.save();
      g.beginPath();
      g.rect(x, y, cw, ch);
      g.clip();
      g.fillStyle = "#222";
      g.fillRect(x, y, cw, ch);
      if (v && it.video && v.videoWidth) {
        const s = Math.max(cw / v.videoWidth, ch / v.videoHeight);
        const dw = v.videoWidth * s, dh = v.videoHeight * s;
        g.drawImage(v, x + (cw - dw) / 2, y + (ch - dh) / 2, dw, dh);
      } else {
        g.fillStyle = box.fg;
        g.font = `600 ${Math.round(ch / 4)}px system-ui, sans-serif`;
        g.textAlign = "center";
        g.textBaseline = "middle";
        g.fillText(it.initials || "?", x + cw / 2, y + ch / 2);
      }
      g.restore();
      if (it.label) {
        g.font = "600 15px system-ui, sans-serif";
        const tw = g.measureText(it.label).width;
        g.fillStyle = "rgba(0,0,0,.72)";
        g.fillRect(x, y + ch - 28, tw + 20, 28);
        g.fillStyle = "#fff";
        g.textAlign = "left";
        g.textBaseline = "middle";
        g.fillText(it.label, x + 10, y + ch - 14);
      }
    });
  }
  ops["media.mixClose"] = ({ m }) => {
    const box = getHandle(m);
    if (!box) return;
    clearInterval(box.timer);
    getHandle(box.stream)?.getTracks().forEach((t) => t.stop());
    for (const v of box.videos.values()) v.srcObject = null;
    box.ac.close().catch(() => {});
    releaseHandle(box.stream);
    releaseHandle(m);
  };
}
