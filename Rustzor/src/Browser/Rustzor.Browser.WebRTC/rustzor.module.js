// Rustzor.Browser.WebRTC — peer connections with pluggable signalling.
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle, elementOf }) {
  ops["rtc.create"] = ({ id, stun }) => {
    const pc = new RTCPeerConnection({ iceServers: stun.map((urls) => ({ urls })) });
    const box = { pc, id, channel: null, pending: [] };
    const signal = (o) => dispatch(id, { type: "signal", data: JSON.stringify(o) });
    pc.onicecandidate = (e) => e.candidate && signal({ candidate: e.candidate });
    pc.ontrack = (e) => {
      if (box.remote === e.streams[0]) return;
      box.remote = e.streams[0];
      dispatch(id, { type: "stream", h: addHandle(e.streams[0]) });
    };
    pc.onconnectionstatechange = () => dispatch(id, { type: "state", state: pc.connectionState });
    pc.ondatachannel = (e) => wireChannel(box, e.channel);
    box.signal = signal;
    return addHandle(box);
  };
  function wireChannel(box, ch) {
    box.channel = ch;
    ch.onmessage = (e) => dispatch(box.id, { type: "data", data: String(e.data) });
  }
  ops["rtc.addStream"] = ({ h, stream }) => {
    const box = getHandle(h);
    const s = getHandle(stream);
    s.getTracks().forEach((t) => box.pc.addTrack(t, s));
  };
  ops["rtc.call"] = async ({ h }) => {
    const box = getHandle(h);
    wireChannel(box, box.pc.createDataChannel("rustzor"));
    const offer = await box.pc.createOffer();
    await box.pc.setLocalDescription(offer);
    box.signal({ description: box.pc.localDescription });
    return true;
  };
  ops["rtc.signal"] = async ({ h, data }) => {
    const box = getHandle(h);
    const msg = JSON.parse(data);
    if (msg.description) {
      await box.pc.setRemoteDescription(msg.description);
      for (const c of box.pending.splice(0)) await box.pc.addIceCandidate(c);
      if (msg.description.type === "offer") {
        await box.pc.setLocalDescription(await box.pc.createAnswer());
        box.signal({ description: box.pc.localDescription });
      }
    } else if (msg.candidate) {
      if (box.pc.remoteDescription) await box.pc.addIceCandidate(msg.candidate);
      else box.pending.push(msg.candidate);
    }
    return true;
  };
  ops["rtc.send"] = ({ h, data }) => {
    const ch = getHandle(h)?.channel;
    if (ch && ch.readyState === "open") ch.send(data);
  };
  ops["rtc.attach"] = ({ h, el }) => {
    const v = elementOf(el);
    v.srcObject = getHandle(h);
    v.playsInline = true;
    v.play().catch(() => {});
  };
  ops["rtc.close"] = ({ h }) => {
    getHandle(h)?.pc.close();
    releaseHandle(h);
  };
}
