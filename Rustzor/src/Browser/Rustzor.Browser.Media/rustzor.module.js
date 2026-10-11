// Rustzor.Browser.Media — getUserMedia, screen capture, devices, recording.
export function install({ ops, elementOf, addHandle, getHandle, releaseHandle }) {
  ops["media.user"] = async ({ video, audio, w, h, device, facing }) => {
    const v = video ? { width: { ideal: w }, height: { ideal: h }, ...(device ? { deviceId: { exact: device } } : { facingMode: facing }) } : false;
    return addHandle(await navigator.mediaDevices.getUserMedia({ video: v, audio }));
  };
  ops["media.display"] = async ({ audio }) => addHandle(await navigator.mediaDevices.getDisplayMedia({ video: true, audio }));
  ops["media.devices"] = async () =>
    (await navigator.mediaDevices.enumerateDevices()).map((d) => ({ id: d.deviceId, kind: d.kind, label: d.label }));
  ops["media.attach"] = ({ h, el, muted }) => {
    const v = elementOf(el);
    v.srcObject = getHandle(h);
    v.muted = muted;
    v.playsInline = true;
    v.play().catch(() => {});
  };
  ops["media.tracks"] = ({ h, kind, on }) => {
    for (const t of getHandle(h).getTracks()) if (t.kind === kind) t.enabled = on;
  };
  const meters = new Map();
  ops["media.level"] = ({ h }) => {
    let m = meters.get(h);
    if (!m) {
      const ctx = new AudioContext();
      const src = ctx.createMediaStreamSource(getHandle(h));
      const an = ctx.createAnalyser();
      an.fftSize = 256;
      src.connect(an);
      m = { an, buf: new Uint8Array(an.fftSize) };
      meters.set(h, m);
    }
    m.an.getByteTimeDomainData(m.buf);
    let sum = 0;
    for (const v of m.buf) sum += ((v - 128) / 128) ** 2;
    return Math.min(1, Math.sqrt(sum / m.buf.length) * 4);
  };
  ops["media.stop"] = ({ h }) => {
    getHandle(h)?.getTracks().forEach((t) => t.stop());
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
      rec.onstop = () => resolve(URL.createObjectURL(new Blob(rec.__chunks, { type: rec.mimeType })));
      rec.stop();
    });
}
