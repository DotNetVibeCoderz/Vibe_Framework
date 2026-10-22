// Rustzor.Browser.WebRTC — peer connections with pluggable signalling.
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle, elementOf }) {
  ops["rtc.create"] = ({ id, stun }) => {
    const pc = new RTCPeerConnection({ iceServers: stun.map((urls) => ({ urls })) });
    const box = { pc, id, channel: null, pending: [], meter: null };
    const signal = (o) => dispatch(id, { type: "signal", data: JSON.stringify(o) });
    pc.onicecandidate = (e) => e.candidate && signal({ candidate: e.candidate });
    pc.ontrack = (e) => {
      const stream = e.streams[0] ?? (box.remote ?? new MediaStream());
      if (!e.streams[0]) stream.addTrack(e.track);
      if (box.remote === stream) return;
      box.remote = stream;
      dispatch(id, { type: "stream", h: addHandle(stream) });
    };
    pc.onconnectionstatechange = () => dispatch(id, { type: "state", state: pc.connectionState });
    pc.ondatachannel = (e) => wireChannel(box, e.channel);
    box.signal = signal;
    return addHandle(box);
  };
  function wireChannel(box, ch) {
    box.channel = ch;
    ch.onopen = () => dispatch(box.id, { type: "open" });
    ch.onclose = () => dispatch(box.id, { type: "closed" });
    ch.onmessage = (e) => dispatch(box.id, { type: "data", data: String(e.data) });
    if (ch.readyState === "open") dispatch(box.id, { type: "open" });
  }
  // Every connection carries one audio and one video slot, sending or not, so
  // a peer without a camera still receives video, and a screen can be shared
  // later by swapping the track without renegotiating.
  function ensureSlots(pc) {
    for (const kind of ["audio", "video"]) {
      const has = pc.getTransceivers().some((t) => (t.sender.track?.kind ?? t.receiver.track?.kind) === kind);
      if (!has) pc.addTransceiver(kind, { direction: "sendrecv" });
    }
  }
  ops["rtc.addStream"] = ({ h, stream }) => {
    const box = getHandle(h);
    const s = getHandle(stream);
    s.getTracks().forEach((t) => box.pc.addTrack(t, s));
    box.local = s;
  };
  ops["rtc.call"] = async ({ h }) => {
    const box = getHandle(h);
    wireChannel(box, box.pc.createDataChannel("rustzor"));
    ensureSlots(box.pc);
    const offer = await box.pc.createOffer();
    await box.pc.setLocalDescription(offer);
    box.signal({ description: box.pc.localDescription });
    return true;
  };
  ops["rtc.signal"] = async ({ h, data }) => {
    const box = getHandle(h);
    if (!box) return false;
    const msg = JSON.parse(data);
    if (msg.description) {
      await box.pc.setRemoteDescription(msg.description);
      for (const c of box.pending.splice(0)) await box.pc.addIceCandidate(c);
      if (msg.description.type === "offer") {
        // Slots the offer created with nothing to send stay open both ways, so
        // a track (a camera turned on later, a shared screen) can fill them.
        for (const t of box.pc.getTransceivers()) if (t.direction === "recvonly") t.direction = "sendrecv";
        await box.pc.setLocalDescription(await box.pc.createAnswer());
        box.signal({ description: box.pc.localDescription });
      }
    } else if (msg.candidate) {
      if (box.pc.remoteDescription) await box.pc.addIceCandidate(msg.candidate);
      else box.pending.push(msg.candidate);
    }
    return true;
  };
  ops["rtc.replace"] = async ({ h, kind, stream }) => {
    const box = getHandle(h);
    if (!box) return false;
    const track = stream ? getHandle(stream)?.getTracks().find((t) => t.kind === kind) ?? null : null;
    const t = box.pc.getTransceivers().find((x) => (x.sender.track?.kind ?? x.receiver.track?.kind) === kind);
    if (!t) return false;
    await t.sender.replaceTrack(track);
    return true;
  };
  ops["rtc.send"] = ({ h, data }) => {
    const ch = getHandle(h)?.channel;
    if (ch && ch.readyState === "open") ch.send(data);
  };
  ops["rtc.attach"] = ({ h, el, muted }) => {
    const v = elementOf(el);
    if (!v) return;
    const s = getHandle(h);
    if (v.srcObject !== s) v.srcObject = s;
    v.muted = !!muted;
    v.playsInline = true;
    v.play().catch(() => {});
  };
  // Loudness 0..1 of a received stream, for "who is speaking".
  ops["rtc.level"] = ({ h }) => {
    const s = getHandle(h);
    if (!s || s.getAudioTracks().length === 0) return 0;
    let m = levelMeters.get(h);
    if (!m) {
      const ctx = new AudioContext();
      const an = ctx.createAnalyser();
      an.fftSize = 256;
      ctx.createMediaStreamSource(s).connect(an);
      m = { ctx, an, buf: new Uint8Array(an.fftSize) };
      levelMeters.set(h, m);
    }
    if (m.ctx.state === "suspended") m.ctx.resume().catch(() => {});
    m.an.getByteTimeDomainData(m.buf);
    let sum = 0;
    for (const v of m.buf) sum += ((v - 128) / 128) ** 2;
    return Math.min(1, Math.sqrt(sum / m.buf.length) * 4);
  };
  ops["rtc.videoSize"] = ({ el }) => {
    const v = elementOf(el);
    return v ? { w: v.videoWidth, h: v.videoHeight } : { w: 0, h: 0 };
  };
  ops["rtc.release"] = ({ h }) => {
    levelMeters.get(h)?.ctx.close().catch(() => {});
    levelMeters.delete(h);
    releaseHandle(h);
  };
  ops["rtc.close"] = ({ h }) => {
    const box = getHandle(h);
    if (!box) return;
    box.channel?.close();
    box.pc.close();
    releaseHandle(h);
  };
  const levelMeters = new Map();
}
