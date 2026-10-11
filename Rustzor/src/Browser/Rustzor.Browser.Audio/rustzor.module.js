// Rustzor.Browser.Audio — Web Audio synthesis, clips, music, speech.
export function install({ ops, addHandle, getHandle }) {
  let ctx = null;
  let master = null;
  let analyser = null;
  const voices = new Map();
  let nextVoice = 1;

  const audio = () => {
    if (!ctx) {
      ctx = new (globalThis.AudioContext || globalThis.webkitAudioContext)();
      master = ctx.createGain();
      master.gain.value = 0.8;
      analyser = ctx.createAnalyser();
      analyser.fftSize = 512;
      master.connect(analyser);
      analyser.connect(ctx.destination);
    }
    return ctx;
  };

  ops["audio.resume"] = () => audio().resume().then(() => true);
  ops["audio.master"] = ({ volume }) => {
    audio();
    master.gain.value = volume;
  };
  ops["audio.tone"] = ({ f, d, wave, v, a, r, slide, at }) => {
    const c = audio();
    const t0 = c.currentTime + (at || 0);
    const osc = c.createOscillator();
    const gain = c.createGain();
    osc.type = wave;
    osc.frequency.setValueAtTime(f, t0);
    if (slide) osc.frequency.exponentialRampToValueAtTime(Math.max(1, slide), t0 + d);
    gain.gain.setValueAtTime(0, t0);
    gain.gain.linearRampToValueAtTime(v, t0 + a);
    gain.gain.setValueAtTime(v, t0 + Math.max(a, d - r));
    gain.gain.linearRampToValueAtTime(0, t0 + d);
    osc.connect(gain).connect(master);
    osc.start(t0);
    osc.stop(t0 + d + 0.02);
  };
  ops["audio.noise"] = ({ d, v, filter }) => {
    const c = audio();
    const length = Math.max(1, Math.floor(c.sampleRate * d));
    const buffer = c.createBuffer(1, length, c.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < length; i++) data[i] = (Math.random() * 2 - 1) * (1 - i / length);
    const src = c.createBufferSource();
    src.buffer = buffer;
    const lp = c.createBiquadFilter();
    lp.type = "lowpass";
    lp.frequency.value = filter;
    const gain = c.createGain();
    gain.gain.value = v;
    src.connect(lp).connect(gain).connect(master);
    src.start();
  };
  ops["audio.load"] = async ({ url }) => {
    const c = audio();
    const bytes = await (await fetch(url)).arrayBuffer();
    return addHandle(await c.decodeAudioData(bytes));
  };
  ops["audio.play"] = ({ h, v, rate, loop, pan }) => {
    const c = audio();
    const src = c.createBufferSource();
    src.buffer = getHandle(h);
    src.playbackRate.value = rate;
    src.loop = loop;
    const gain = c.createGain();
    gain.gain.value = v;
    const panner = c.createStereoPanner ? c.createStereoPanner() : null;
    if (panner) {
      panner.pan.value = pan;
      src.connect(gain).connect(panner).connect(master);
    } else src.connect(gain).connect(master);
    src.start();
    const id = nextVoice++;
    voices.set(id, src);
    src.onended = () => voices.delete(id);
    return id;
  };
  ops["audio.stop"] = ({ voice }) => {
    voices.get(voice)?.stop();
    voices.delete(voice);
  };
  ops["audio.spectrum"] = ({ bands }) => {
    audio();
    const data = new Uint8Array(analyser.frequencyBinCount);
    analyser.getByteFrequencyData(data);
    const step = Math.max(1, Math.floor(data.length / bands));
    const out = [];
    for (let i = 0; i < bands; i++) out.push(data[i * step] ?? 0);
    return out;
  };
  ops["audio.music"] = ({ url, loop }) => {
    const el = new Audio(url);
    el.loop = loop;
    return addHandle(el);
  };
  ops["audio.musicPlay"] = ({ h }) => getHandle(h).play().then(() => true);
  ops["audio.musicPause"] = ({ h }) => getHandle(h).pause();
  ops["audio.musicVolume"] = ({ h, v }) => {
    getHandle(h).volume = v;
  };
  ops["audio.speak"] = ({ text, lang, rate, pitch }) => {
    const u = new SpeechSynthesisUtterance(text);
    if (lang) u.lang = lang;
    u.rate = rate;
    u.pitch = pitch;
    speechSynthesis.speak(u);
  };
}
