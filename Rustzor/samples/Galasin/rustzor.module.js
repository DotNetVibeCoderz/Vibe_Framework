// Galasin's yard engine. C# owns the match: the lobby, the roster and its bots, whose turn
// it is to attack, tagging, points, the clock, and the network session between tabs. This
// module owns what has to happen every frame: it loads the glTF models (the rigged kid and
// the yard props), samples and cross-fades their animations, skins them on the GPU, draws
// the yard at golden hour with a shadow map, chalk lines and fog, moves the local player
// from the keyboard, a gamepad or a touch stick inside the limits of the player's role,
// smooths the other players between network snapshots, labels them, and plays the whistle
// and the kentongan. Embedded in the app's assembly; Rustzor's bundler ships it.
// Built by Gravicode Studios, led by Kang Fadhil.
export function install({ ops, dispatch, elementOf }) {
  // ── vectors and matrices (column-major, like GL) ─────────────────────
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
  const lerp = (a, b, t) => a + (b - a) * t;
  const angleLerp = (a, b, t) => {
    let d = (b - a) % (Math.PI * 2);
    if (d > Math.PI) d -= Math.PI * 2;
    if (d < -Math.PI) d += Math.PI * 2;
    return a + d * t;
  };
  const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
  const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  const norm = (a) => { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };

  function mul(a, b, o = new Float32Array(16)) {
    for (let c = 0; c < 4; c++) {
      const b0 = b[c * 4], b1 = b[c * 4 + 1], b2 = b[c * 4 + 2], b3 = b[c * 4 + 3];
      o[c * 4] = a[0] * b0 + a[4] * b1 + a[8] * b2 + a[12] * b3;
      o[c * 4 + 1] = a[1] * b0 + a[5] * b1 + a[9] * b2 + a[13] * b3;
      o[c * 4 + 2] = a[2] * b0 + a[6] * b1 + a[10] * b2 + a[14] * b3;
      o[c * 4 + 3] = a[3] * b0 + a[7] * b1 + a[11] * b2 + a[15] * b3;
    }
    return o;
  }
  function compose(t, q, s, o = new Float32Array(16)) {
    const [qx, qy, qz, qw] = q;
    const xx = qx * qx, yy = qy * qy, zz = qz * qz, xy = qx * qy, xz = qx * qz, yz = qy * qz, wx = qw * qx, wy = qw * qy, wz = qw * qz;
    o[0] = (1 - 2 * (yy + zz)) * s[0]; o[1] = 2 * (xy + wz) * s[0]; o[2] = 2 * (xz - wy) * s[0]; o[3] = 0;
    o[4] = 2 * (xy - wz) * s[1]; o[5] = (1 - 2 * (xx + zz)) * s[1]; o[6] = 2 * (yz + wx) * s[1]; o[7] = 0;
    o[8] = 2 * (xz + wy) * s[2]; o[9] = 2 * (yz - wx) * s[2]; o[10] = (1 - 2 * (xx + yy)) * s[2]; o[11] = 0;
    o[12] = t[0]; o[13] = t[1]; o[14] = t[2]; o[15] = 1;
    return o;
  }
  function invert(a) {
    const o = new Float32Array(16);
    const b00 = a[0] * a[5] - a[1] * a[4], b01 = a[0] * a[6] - a[2] * a[4], b02 = a[0] * a[7] - a[3] * a[4];
    const b03 = a[1] * a[6] - a[2] * a[5], b04 = a[1] * a[7] - a[3] * a[5], b05 = a[2] * a[7] - a[3] * a[6];
    const b06 = a[8] * a[13] - a[9] * a[12], b07 = a[8] * a[14] - a[10] * a[12], b08 = a[8] * a[15] - a[11] * a[12];
    const b09 = a[9] * a[14] - a[10] * a[13], b10 = a[9] * a[15] - a[11] * a[13], b11 = a[10] * a[15] - a[11] * a[14];
    let det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
    det = det ? 1 / det : 0;
    o[0] = (a[5] * b11 - a[6] * b10 + a[7] * b09) * det;
    o[1] = (a[2] * b10 - a[1] * b11 - a[3] * b09) * det;
    o[2] = (a[13] * b05 - a[14] * b04 + a[15] * b03) * det;
    o[3] = (a[10] * b04 - a[9] * b05 - a[11] * b03) * det;
    o[4] = (a[6] * b08 - a[4] * b11 - a[7] * b07) * det;
    o[5] = (a[0] * b11 - a[2] * b08 + a[3] * b07) * det;
    o[6] = (a[14] * b02 - a[12] * b05 - a[15] * b01) * det;
    o[7] = (a[8] * b05 - a[10] * b02 + a[11] * b01) * det;
    o[8] = (a[4] * b10 - a[5] * b08 + a[7] * b06) * det;
    o[9] = (a[1] * b08 - a[0] * b10 - a[3] * b06) * det;
    o[10] = (a[12] * b04 - a[13] * b02 + a[15] * b00) * det;
    o[11] = (a[9] * b02 - a[8] * b04 - a[11] * b00) * det;
    o[12] = (a[5] * b07 - a[4] * b09 - a[6] * b06) * det;
    o[13] = (a[0] * b09 - a[1] * b07 + a[2] * b06) * det;
    o[14] = (a[13] * b01 - a[12] * b03 - a[14] * b00) * det;
    o[15] = (a[8] * b03 - a[9] * b01 + a[10] * b00) * det;
    return o;
  }
  function lookAt(eye, at, up) {
    const z = norm(sub(eye, at)), x = norm(cross(up, z)), y = cross(z, x);
    return new Float32Array([x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1]);
  }
  function perspective(fovy, aspect, n, f) {
    const t = 1 / Math.tan(fovy / 2);
    return new Float32Array([t / aspect, 0, 0, 0, 0, t, 0, 0, 0, 0, (f + n) / (n - f), -1, 0, 0, (2 * f * n) / (n - f), 0]);
  }
  function ortho(l, r, b, t, n, f) {
    return new Float32Array([2 / (r - l), 0, 0, 0, 0, 2 / (t - b), 0, 0, 0, 0, -2 / (f - n), 0, -(r + l) / (r - l), -(t + b) / (t - b), -(f + n) / (f - n), 1]);
  }
  const project = (m, p) => {
    const x = m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12];
    const y = m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13];
    const w = m[3] * p[0] + m[7] * p[1] + m[11] * p[2] + m[15];
    return w > 0.01 ? [x / w, y / w, w] : null;
  };
  function slerpInto(o, a, b, t) {
    // nlerp along the short way; close enough between animation keys 1/30 s apart
    let d = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
    const s = d < 0 ? -1 : 1;
    let x = a[0] + (b[0] * s - a[0]) * t, y = a[1] + (b[1] * s - a[1]) * t, z = a[2] + (b[2] * s - a[2]) * t, w = a[3] + (b[3] * s - a[3]) * t;
    d = Math.hypot(x, y, z, w) || 1;
    o[0] = x / d; o[1] = y / d; o[2] = z / d; o[3] = w / d;
    return o;
  }

  // ── the field (the same numbers C# uses for the rules) ───────────────
  const FIELD = { half: 4.5, lines: [0, 6, 12], length: 12, home: -0.5, goal: 12.5 };
  const KID_SCALE = 0.8;
  const TEAM = { m: [0.80, 0.09, 0.10], p: [0.96, 0.95, 0.91] };
  const TEAM_CSS = { m: "#c8102e", p: "#f6f1e4" };

  // ── state ────────────────────────────────────────────────────────────
  const M = {
    host: null, canvas: null, gl: null, overlay: null, ctx2d: null, cb: 0, w: 0, h: 0, dpr: 1,
    models: null, ready: false, raf: 0, last: 0, time: 0,
    view: "lobby", phase: "lobby", attack: "p", winner: "", gen: -1,
    roster: new Map(),      // id -> { id, name, team, bot, me }
    ents: new Map(),        // id -> render state
    meId: "", me: null, role: { mode: "none", line: 0, speed: 4.4, frozen: true },
    keys: new Set(), stick: null, action: false, actionLatch: false, hold: null,
    cam: { eye: [0, 9, -12], at: [0, 0, 6], fx: 0, fz: 0 },
    vp: null, lightVP: null, sound: true, audio: null, lang: "id",
  };
  const stats = { frames: [], gaps: [], loadMs: 0, snaps: 0, lastSnapAt: 0 };
  const send = (m) => M.cb && dispatch(M.cb, m);

  // ── GL programs ──────────────────────────────────────────────────────
  const SKIN_VS = `#version 300 es
  layout(location=0) in vec3 aPos; layout(location=1) in vec3 aNor; layout(location=2) in vec2 aUv;
  layout(location=3) in vec4 aCol; layout(location=4) in uvec4 aJoint; layout(location=5) in vec4 aWeight;
  uniform mat4 uModel, uVP, uLightVP; uniform mat4 uJoints[24]; uniform int uSkinned;
  out vec3 vNor; out vec3 vWorld; out vec2 vUv; out float vMask; out vec4 vLight;
  void main(){
    mat4 skin = mat4(1.0);
    if (uSkinned == 1) skin = aWeight.x * uJoints[aJoint.x] + aWeight.y * uJoints[aJoint.y] + aWeight.z * uJoints[aJoint.z] + aWeight.w * uJoints[aJoint.w];
    mat4 m = uModel * skin;
    vec4 w = m * vec4(aPos, 1.0);
    vWorld = w.xyz; vNor = mat3(m) * aNor; vUv = aUv; vMask = aCol.r; vLight = uLightVP * w;
    gl_Position = uVP * w;
  }`;
  const LIGHTING = `
  uniform vec3 uSun, uSunCol, uSky, uBounce, uFog, uEye; uniform float uFogNear, uFogFar;
  uniform highp sampler2DShadow uShadow;
  vec3 srgb(vec3 c){ return mix(12.92*c, 1.055*pow(c, vec3(1.0/2.4)) - 0.055, step(0.0031308, c)); }
  float shadowAt(vec4 lp, float bias){
    vec3 p = lp.xyz / lp.w * 0.5 + 0.5;
    if (p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0 || p.z > 1.0) return 1.0;
    float s = 0.0; vec2 texel = vec2(1.0 / 2048.0);
    for (int i = -1; i <= 1; i++) for (int j = -1; j <= 1; j++)
      s += texture(uShadow, vec3(p.xy + vec2(i, j) * texel * 1.3, p.z - bias));
    return s / 9.0;
  }
  vec3 shade(vec3 base, vec3 n, vec3 world, vec4 lp, float ao){
    float nl = dot(n, uSun);
    float sh = shadowAt(lp, 0.0012 + 0.002 * (1.0 - abs(nl)));
    vec3 hemi = mix(uBounce, uSky, n.y * 0.5 + 0.5);
    vec3 v = normalize(uEye - world);
    float rim = pow(1.0 - max(dot(n, v), 0.0), 3.0) * 0.35;
    vec3 c = base * (hemi * ao + uSunCol * max(nl, 0.0) * sh) + uSunCol * rim * sh * base;
    float f = smoothstep(uFogNear, uFogFar, length(world - uEye));
    return mix(c, uFog, f);
  }`;
  const SKIN_FS = `#version 300 es
  precision highp float;
  in vec3 vNor; in vec3 vWorld; in vec2 vUv; in float vMask; in vec4 vLight; out vec4 o;
  uniform sampler2D uTex; uniform int uHasTex; uniform vec4 uColor; uniform vec3 uTeam; uniform float uTeamOn;
  uniform float uHighlight;
  ${LIGHTING}
  void main(){
    vec4 base = uColor;
    if (uHasTex == 1) base *= texture(uTex, vUv);
    float lum = dot(base.rgb, vec3(0.3, 0.55, 0.15));
    base.rgb = mix(base.rgb, uTeam * (0.35 + 0.75 * lum), clamp(vMask, 0.0, 1.0) * uTeamOn);
    vec3 n = normalize(vNor); if (!gl_FrontFacing) n = -n;
    vec3 c = shade(base.rgb, n, vWorld, vLight, 0.62);
    c += vec3(1.0, 0.86, 0.45) * uHighlight;
    o = vec4(srgb(c), 1.0);
  }`;
  const DEPTH_FS = `#version 300 es
  precision mediump float; out vec4 o; void main(){ o = vec4(1.0); }`;
  const GROUND_VS = `#version 300 es
  layout(location=0) in vec3 aPos; uniform mat4 uVP, uLightVP;
  out vec3 vWorld; out vec4 vLight;
  void main(){ vWorld = aPos; vLight = uLightVP * vec4(aPos, 1.0); gl_Position = uVP * vec4(aPos, 1.0); }`;
  const GROUND_FS = `#version 300 es
  precision highp float;
  in vec3 vWorld; in vec4 vLight; out vec4 o;
  uniform vec3 uMe; uniform vec3 uMeCol; uniform vec4 uMyLine; uniform float uLineOn; uniform float uTime;
  ${LIGHTING}
  float hash(vec2 p){ p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
  float noise(vec2 p){ vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y); }
  float fbm(vec2 p){ float s = 0.0, a = 0.5; for (int i = 0; i < 4; i++){ s += a * noise(p); p *= 2.03; a *= 0.5; } return s; }
  float seg(vec2 p, vec2 a, vec2 b){ vec2 pa = p - a, ba = b - a; float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0); return length(pa - ba * h); }
  void main(){
    vec2 p = vWorld.xz;
    float n = fbm(p * 0.45), fine = noise(p * 7.0);
    vec3 dirt = mix(vec3(0.46, 0.27, 0.13), vec3(0.62, 0.40, 0.21), n) * (0.9 + 0.12 * fine);
    // packed earth where feet run: the court and the lanes to it are worn lighter
    float worn = smoothstep(1.6, 0.0, max(abs(p.x) - 4.6, max(-p.y - 3.4, p.y - 15.4)));
    dirt = mix(dirt, vec3(0.70, 0.49, 0.28) * (0.92 + 0.1 * fine), worn * 0.55);
    // the yard is dirt; past its edge, grass
    vec2 q = abs(p - vec2(0.0, 7.5)) - vec2(12.0, 15.0);
    float yard = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) + (n - 0.5) * 3.0;
    vec3 grass = mix(vec3(0.18, 0.29, 0.10), vec3(0.32, 0.42, 0.14), fbm(p * 0.8)) * (0.85 + 0.2 * fine);
    vec3 base = mix(dirt, grass, smoothstep(-0.5, 1.5, yard));
    // chalk: the outline, the three guard lines and the centre line
    float d = 1e3;
    d = min(d, seg(p, vec2(-4.5, 0.0), vec2(4.5, 0.0)));
    d = min(d, seg(p, vec2(-4.5, 6.0), vec2(4.5, 6.0)));
    d = min(d, seg(p, vec2(-4.5, 12.0), vec2(4.5, 12.0)));
    d = min(d, seg(p, vec2(-4.5, 0.0), vec2(-4.5, 12.0)));
    d = min(d, seg(p, vec2(4.5, 0.0), vec2(4.5, 12.0)));
    d = min(d, seg(p, vec2(0.0, 0.0), vec2(0.0, 12.0)));
    // the home and far boxes, dashed
    float dash = step(0.5, fract((p.x + p.y) * 1.4));
    float box = min(min(seg(p, vec2(-4.5, -3.0), vec2(4.5, -3.0)), seg(p, vec2(-4.5, 15.0), vec2(4.5, 15.0))),
                    min(min(seg(p, vec2(-4.5, -3.0), vec2(-4.5, 0.0)), seg(p, vec2(4.5, -3.0), vec2(4.5, 0.0))),
                        min(seg(p, vec2(-4.5, 12.0), vec2(-4.5, 15.0)), seg(p, vec2(4.5, 12.0), vec2(4.5, 15.0)))));
    float chalk = smoothstep(0.075, 0.035, d) * (0.65 + 0.35 * noise(p * 22.0));
    chalk = max(chalk, smoothstep(0.05, 0.02, box) * dash * 0.55 * (0.6 + 0.4 * noise(p * 22.0)));
    base = mix(base, vec3(0.97, 0.95, 0.88), chalk);
    // the line the local guard keeps to glows in the team colour
    if (uLineOn > 0.5) {
      float dl = seg(p, uMyLine.xy, uMyLine.zw);
      base = mix(base, uMeCol, smoothstep(0.16, 0.05, dl) * (0.55 + 0.25 * sin(uTime * 4.0)));
    }
    // a ring under the local player
    if (uMe.z > 0.5) {
      float r = length(p - uMe.xy);
      base = mix(base, uMeCol, smoothstep(0.07, 0.0, abs(r - 0.55)) * 0.9);
    }
    vec3 c = shade(base, vec3(0.0, 1.0, 0.0), vWorld, vLight, 0.75);
    o = vec4(srgb(c), 1.0);
  }`;
  const SKY_VS = `#version 300 es
  out vec2 vNdc; void main(){ vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0; vNdc = p; gl_Position = vec4(p, 0.9999, 1.0); }`;
  const SKY_FS = `#version 300 es
  precision highp float; in vec2 vNdc; out vec4 o;
  uniform mat4 uInvVP; uniform vec3 uSun, uEye;
  vec3 srgb(vec3 c){ return mix(12.92*c, 1.055*pow(c, vec3(1.0/2.4)) - 0.055, step(0.0031308, c)); }
  void main(){
    vec4 w = uInvVP * vec4(vNdc, 1.0, 1.0);
    vec3 d = normalize(w.xyz / w.w - uEye);
    float h = clamp(d.y, -0.2, 1.0);
    vec3 zenith = vec3(0.20, 0.36, 0.62), horizon = vec3(1.0, 0.66, 0.38), haze = vec3(0.98, 0.80, 0.58);
    vec3 c = mix(horizon, zenith, pow(max(h, 0.0), 0.55));
    c = mix(haze, c, smoothstep(-0.05, 0.12, h));
    float s = max(dot(d, uSun), 0.0);
    c += vec3(1.0, 0.72, 0.42) * pow(s, 18.0) * 0.6 + vec3(1.0, 0.92, 0.75) * smoothstep(0.9985, 0.9993, s) * 2.0;
    o = vec4(srgb(c), 1.0);
  }`;

  function compile(gl, vs, fs) {
    const p = gl.createProgram();
    for (const [type, src] of [[gl.VERTEX_SHADER, vs], [gl.FRAGMENT_SHADER, fs]]) {
      const s = gl.createShader(type);
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error("shader: " + gl.getShaderInfoLog(s));
      gl.attachShader(p, s);
    }
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error("program: " + gl.getProgramInfoLog(p));
    const u = {};
    const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < n; i++) {
      const info = gl.getActiveUniform(p, i);
      const name = info.name.replace(/\[0\]$/, "");
      u[name] = gl.getUniformLocation(p, info.name);
    }
    return { p, u };
  }

  // ── glTF binary ──────────────────────────────────────────────────────
  const COMP = { 5120: Int8Array, 5121: Uint8Array, 5122: Int16Array, 5123: Uint16Array, 5125: Uint32Array, 5126: Float32Array };
  const SIZE = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT4: 16 };

  async function loadGlb(url) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`${url}: ${res.status}`);
    const buf = await res.arrayBuffer();
    const dv = new DataView(buf);
    if (dv.getUint32(0, true) !== 0x46546c67) throw new Error(url + " is not a GLB file");
    const jsonLen = dv.getUint32(12, true);
    const json = JSON.parse(new TextDecoder().decode(new Uint8Array(buf, 20, jsonLen)));
    const binOff = 20 + jsonLen + 8;
    const view = (i) => {
      const bv = json.bufferViews[i];
      return buf.slice(binOff + (bv.byteOffset ?? 0), binOff + (bv.byteOffset ?? 0) + bv.byteLength);
    };
    const accessor = (i) => {
      const a = json.accessors[i];
      const bv = json.bufferViews[a.bufferView];
      const T = COMP[a.componentType], n = SIZE[a.type];
      const start = binOff + (bv.byteOffset ?? 0) + (a.byteOffset ?? 0);
      const arr = new T(buf.slice(start, start + a.count * n * T.BYTES_PER_ELEMENT));
      return { arr, n, count: a.count, type: a.componentType, normalized: !!a.normalized };
    };
    const images = await Promise.all((json.images ?? []).map((im) => createImageBitmap(new Blob([view(im.bufferView)], { type: im.mimeType }))));
    return { json, accessor, images };
  }

  function uploadTexture(gl, bitmap) {
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.SRGB8_ALPHA8, gl.RGBA, gl.UNSIGNED_BYTE, bitmap);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    const aniso = gl.getExtension("EXT_texture_filter_anisotropic");
    if (aniso) gl.texParameterf(gl.TEXTURE_2D, aniso.TEXTURE_MAX_ANISOTROPY_EXT, 4);
    return t;
  }

  /** GPU buffers per primitive, the node tree, skins and animations of one GLB. */
  function buildModel(gl, glb) {
    const { json, accessor } = glb;
    const textures = glb.images.map((b) => uploadTexture(gl, b));
    const materials = (json.materials ?? []).map((m) => {
      const pbr = m.pbrMetallicRoughness ?? {};
      const ti = pbr.baseColorTexture?.index;
      return {
        color: (pbr.baseColorFactor ?? [1, 1, 1, 1]).slice(0, 4),
        tex: ti !== undefined ? textures[json.textures[ti].source] : null,
      };
    });
    const meshes = json.meshes.map((mesh) => mesh.primitives.map((prim) => {
      const vao = gl.createVertexArray();
      gl.bindVertexArray(vao);
      const attr = (name, loc, integer) => {
        if (prim.attributes[name] === undefined) {
          gl.disableVertexAttribArray(loc);
          return null;
        }
        const a = accessor(prim.attributes[name]);
        const b = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, b);
        gl.bufferData(gl.ARRAY_BUFFER, a.arr, gl.STATIC_DRAW);
        gl.enableVertexAttribArray(loc);
        if (integer) gl.vertexAttribIPointer(loc, a.n, a.type, 0, 0);
        else gl.vertexAttribPointer(loc, a.n, a.type, a.normalized || a.type !== 5126, 0, 0);
        return a;
      };
      const pos = attr("POSITION", 0);
      attr("NORMAL", 1);
      attr("TEXCOORD_0", 2);
      attr("COLOR_0", 3);
      attr("JOINTS_0", 4, true);
      attr("WEIGHTS_0", 5);
      if (prim.attributes.COLOR_0 === undefined) gl.vertexAttrib4f(3, 0, 0, 0, 1);
      const idx = accessor(prim.indices);
      const ib = gl.createBuffer();
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ib);
      gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, idx.arr, gl.STATIC_DRAW);
      gl.bindVertexArray(null);
      let lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
      for (let i = 0; i < pos.count; i++)
        for (let k = 0; k < 3; k++) {
          const v = pos.arr[i * 3 + k];
          if (v < lo[k]) lo[k] = v;
          if (v > hi[k]) hi[k] = v;
        }
      return {
        vao, count: idx.count, indexType: idx.type, material: materials[prim.material ?? 0] ?? { color: [1, 1, 1, 1], tex: null },
        mask: prim.attributes.COLOR_0 !== undefined, lo, hi, tris: idx.count / 3,
      };
    }));
    const nodes = json.nodes.map((n, i) => ({
      i, name: n.name ?? "", children: n.children ?? [], mesh: n.mesh, skin: n.skin, parent: -1,
      t: n.translation ?? [0, 0, 0], r: n.rotation ?? [0, 0, 0, 1], s: n.scale ?? [1, 1, 1],
    }));
    nodes.forEach((n) => n.children.forEach((c) => (nodes[c].parent = n.i)));
    const order = [];
    const visit = (i) => { order.push(i); nodes[i].children.forEach(visit); };
    nodes.filter((n) => n.parent < 0).forEach((n) => visit(n.i));
    const skins = (json.skins ?? []).map((s) => ({ joints: s.joints, ibm: accessor(s.inverseBindMatrices).arr }));
    const clips = {};
    for (const a of json.animations ?? []) {
      let t0 = Infinity, t1 = 0;
      const channels = a.channels.map((c) => {
        const smp = a.samplers[c.sampler];
        const times = accessor(smp.input).arr, values = accessor(smp.output).arr;
        t0 = Math.min(t0, times[0]);
        t1 = Math.max(t1, times[times.length - 1]);
        return { node: c.target.node, path: c.target.path, times, values, n: c.target.path === "rotation" ? 4 : 3, hint: 0 };
      });
      clips[a.name] = { name: a.name, channels, t0, duration: Math.max(1e-3, t1 - t0) };
    }
    return { nodes, order, meshes, skins, clips, byName: Object.fromEntries(nodes.map((n) => [n.name, n])) };
  }

  /** One animated copy of a model: its own pose, clips and cross-fade. */
  function makeInstance(model) {
    const nodes = model.nodes.map((n) => ({ t: n.t.slice(), r: n.r.slice(), s: n.s.slice() }));
    return {
      model, nodes, world: model.nodes.map(() => new Float32Array(16)), local: new Float32Array(16),
      joints: model.skins.map((s) => new Float32Array(s.joints.length * 16)),
      clip: null, time: 0, speed: 1, loop: true, prev: null, prevTime: 0, fade: 1, fadeLen: 0.18, onEnd: null,
    };
  }
  function play(inst, name, { loop = true, speed = 1, fade = 0.18, restart = false } = {}) {
    const clip = inst.model.clips[name];
    if (!clip) return;
    inst.speed = speed;
    if (inst.clip === clip && !restart) return;
    inst.prev = inst.clip;
    inst.prevTime = inst.time;
    inst.prevLoop = inst.loop;
    inst.clip = clip;
    inst.time = 0;
    inst.loop = loop;
    inst.fade = inst.prev ? 0 : 1;
    inst.fadeLen = fade;
  }
  const tmpA = [0, 0, 0, 0], tmpB = [0, 0, 0, 0];
  function sampleChannel(ch, t, out) {
    const ts = ch.times, n = ch.n, last = ts.length - 1;
    if (t <= ts[0]) { for (let k = 0; k < n; k++) out[k] = ch.values[k]; return; }
    if (t >= ts[last]) { for (let k = 0; k < n; k++) out[k] = ch.values[last * n + k]; return; }
    let i = ch.hint;
    if (i >= last || ts[i] > t) i = 0;
    while (i < last - 1 && ts[i + 1] < t) i++;
    ch.hint = i;
    const f = (t - ts[i]) / (ts[i + 1] - ts[i]);
    if (n === 4) {
      for (let k = 0; k < 4; k++) { tmpA[k] = ch.values[i * 4 + k]; tmpB[k] = ch.values[(i + 1) * 4 + k]; }
      slerpInto(out, tmpA, tmpB, f);
    } else for (let k = 0; k < n; k++) out[k] = lerp(ch.values[i * n + k], ch.values[(i + 1) * n + k], f);
  }
  const tmpPose = [0, 0, 0, 0];
  function applyClip(inst, clip, time, weight) {
    const t = clip.t0 + time;
    for (const ch of clip.channels) {
      const node = inst.nodes[ch.node];
      const target = ch.path === "translation" ? node.t : ch.path === "rotation" ? node.r : node.s;
      if (weight >= 1) { sampleChannel(ch, t, target); continue; }
      sampleChannel(ch, t, tmpPose);
      if (ch.n === 4) slerpInto(target, target.slice(), tmpPose, weight);
      else for (let k = 0; k < 3; k++) target[k] = lerp(target[k], tmpPose[k], weight);
    }
  }
  function advance(inst, dt) {
    const c = inst.clip;
    if (!c) return;
    inst.time += dt * inst.speed;
    if (inst.time >= c.duration) {
      if (inst.loop) inst.time %= c.duration;
      else {
        inst.time = c.duration;
        const cb = inst.onEnd;
        inst.onEnd = null;
        cb?.();
      }
    }
    if (inst.prev) {
      inst.prevTime += dt * inst.speed;
      if (inst.prevLoop) inst.prevTime %= inst.prev.duration;
      else inst.prevTime = Math.min(inst.prevTime, inst.prev.duration);
      inst.fade = Math.min(1, inst.fade + dt / inst.fadeLen);
      if (inst.fade >= 1) inst.prev = null;
    }
  }
  function pose(inst) {
    const m = inst.model;
    if (inst.prev) applyClip(inst, inst.prev, inst.prevTime, 1);
    if (inst.clip) applyClip(inst, inst.clip, inst.time, inst.prev ? inst.fade : 1);
    for (const i of m.order) {
      const n = inst.nodes[i], p = m.nodes[i].parent;
      compose(n.t, n.r, n.s, inst.local);
      if (p < 0) inst.world[i].set(inst.local);
      else mul(inst.world[p], inst.local, inst.world[i]);
    }
    m.skins.forEach((s, k) => {
      const out = inst.joints[k];
      const tmp = new Float32Array(16);
      s.joints.forEach((j, idx) => {
        mul(inst.world[j], s.ibm.subarray(idx * 16, idx * 16 + 16), tmp);
        out.set(tmp, idx * 16);
      });
    });
  }

  // ── setup ────────────────────────────────────────────────────────────
  function initGl() {
    const gl = M.canvas.getContext("webgl2", { antialias: true, alpha: false, powerPreference: "high-performance", preserveDrawingBuffer: false });
    if (!gl) throw new Error("WebGL2 is not available in this browser");
    M.gl = gl;
    M.skinProg = compile(gl, SKIN_VS, SKIN_FS);
    M.depthProg = compile(gl, SKIN_VS, DEPTH_FS);
    M.groundProg = compile(gl, GROUND_VS, GROUND_FS);
    M.skyProg = compile(gl, SKY_VS, SKY_FS);
    M.skyVao = gl.createVertexArray();
    // meshes without a skin leave the joint attribute off; its constant must still be an unsigned int
    gl.vertexAttribI4ui(4, 0, 0, 0, 0);
    gl.vertexAttrib4f(5, 0, 0, 0, 0);
    // the ground: one big quad, finely enough divided for the fog to read right
    const g = [], gi = [];
    const N = 24, S = 90;
    for (let j = 0; j <= N; j++) for (let i = 0; i <= N; i++) g.push(-S / 2 + (S * i) / N, 0, -S / 2 + 7.5 + (S * j) / N);
    for (let j = 0; j < N; j++) for (let i = 0; i < N; i++) {
      const a = j * (N + 1) + i;
      gi.push(a, a + N + 1, a + 1, a + 1, a + N + 1, a + N + 2);
    }
    M.groundVao = gl.createVertexArray();
    gl.bindVertexArray(M.groundVao);
    const gb = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, gb);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(g), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
    const gib = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, gib);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array(gi), gl.STATIC_DRAW);
    M.groundCount = gi.length;
    gl.bindVertexArray(null);
    // the sun's shadow map
    M.shadowSize = 2048;
    M.shadowTex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, M.shadowTex);
    gl.texStorage2D(gl.TEXTURE_2D, 1, gl.DEPTH_COMPONENT24, M.shadowSize, M.shadowSize);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_COMPARE_MODE, gl.COMPARE_REF_TO_TEXTURE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_COMPARE_FUNC, gl.LEQUAL);
    M.shadowFbo = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, M.shadowFbo);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, M.shadowTex, 0);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    // the sun sits low in the west, as it does when the kampung comes out to play
    M.sun = norm([-0.62, 0.5, 0.42]);
    const lightView = lookAt([M.sun[0] * 40, M.sun[1] * 40, 7.5 + M.sun[2] * 40], [0, 0, 7.5], [0, 1, 0]);
    M.lightVP = mul(ortho(-24, 24, -24, 24, 1, 90), lightView);
  }

  async function loadModels(base) {
    const t0 = performance.now();
    const [kid, yard] = await Promise.all([loadGlb(base + "models/anak.glb"), loadGlb(base + "models/kampung.glb")]);
    const gl = M.gl;
    M.kid = buildModel(gl, kid);
    M.yard = buildModel(gl, yard);
    M.flag = makeInstance(M.yard);
    play(M.flag, "Wave", { speed: 0.9 });
    // where each piece of the kampung stands, in metres; the court runs from z 0 to 12
    const place = (name, x, z, yaw, s = 1) => ({ node: M.yard.byName[name], x, z, yaw, s });
    M.props = [
      place("gapura", 0, 18.6, Math.PI),
      place("posronda", -9.6, 3.5, Math.PI / 2),
      place("tree", 9.0, 9.5, 0.4),
      place("tree", -9.4, 14.5, 2.1, 0.85),
      place("onthel", 7.4, 6.2, -2.3),
      place("tiang", 6.2, 17.6, 0),
      place("bendera", 6.2, 17.6, 0),
    ].filter((p) => p.node);
    // the tree line round the yard, the same tree again and again, fading into the haze
    let seed = 7;
    const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
    for (let i = 0; i < 26; i++) {
      const a = (i / 26) * Math.PI * 2 + rnd() * 0.15;
      const r = 27 + rnd() * 9;
      const x = Math.cos(a) * r * 0.9, z = 7.5 + Math.sin(a) * r;
      if (z < -9 && Math.abs(x) < 18) continue; // keep the camera's side open
      M.props.push(place("tree", x, z, rnd() * 6.28, 0.8 + rnd() * 0.7));
    }
    for (const p of M.props) p.matrix = compose([p.x, 0, p.z], [0, Math.sin(p.yaw / 2), 0, Math.cos(p.yaw / 2)], [p.s, p.s, p.s]);
    stats.loadMs = performance.now() - t0;
    M.ready = true;
    send({ type: "ready", loadMs: stats.loadMs, tris: M.kid.meshes.flat().reduce((s, m) => s + m.tris, 0) + M.yard.meshes.flat().reduce((s, m) => s + m.tris, 0) });
  }

  // ── entities ─────────────────────────────────────────────────────────
  function entity(id) {
    let e = M.ents.get(id);
    if (!e) {
      e = { id, x: 0, z: 0, yaw: 0, snaps: [], inst: M.kid ? makeInstance(M.kid) : null, speed: 0, lunge: 0, flags: 0, seen: 0, lastLunge: 0, px: 0, pz: 0 };
      if (e.inst) play(e.inst, "Idle");
      M.ents.set(id, e);
    }
    if (!e.inst && M.kid) { e.inst = makeInstance(M.kid); play(e.inst, "Idle"); }
    return e;
  }

  function applySnapshot(s) {
    stats.snaps++;
    const now = performance.now();
    stats.lastSnapAt = now;
    M.phase = s.ph;
    M.attack = s.atk;
    M.winner = s.win ?? "";
    M.timeLeft = s.tl / 100;
    const seen = new Set();
    // "id,x,z,yaw,lunge,reached;…" in centimetres and centiradians
    for (const item of s.e ? s.e.split(";") : []) {
      const parts = item.split(",");
      const id = parts[0], x = +parts[1] / 100, z = +parts[2] / 100, yaw = +parts[3] / 100, l = +parts[4], f = +parts[5];
      seen.add(id);
      const e = entity(id);
      e.flags = f;
      if (id === M.meId) continue;
      if (s.gen !== M.gen || e.snaps.length === 0) {
        e.snaps = [];
        e.x = x; e.z = z; e.yaw = yaw;
      }
      e.snaps.push({ t: now, x, z, yaw, l });
      if (e.snaps.length > 4) e.snaps.shift();
    }
    M.gen = s.gen;
    for (const id of [...M.ents.keys()]) if (!seen.has(id) && id !== M.meId) M.ents.delete(id);
  }

  const DELAY = 110; // ms behind the newest snapshot, so there are two to blend between
  function interpolate(e, now) {
    const s = e.snaps;
    if (!s.length) return;
    const t = now - DELAY;
    let a = s[0], b = s[s.length - 1];
    for (let i = 0; i < s.length - 1; i++) if (s[i].t <= t && s[i + 1].t >= t) { a = s[i]; b = s[i + 1]; break; }
    let x, z, yaw;
    if (t >= b.t) {
      // past the newest: hold, extrapolating a little so a late packet does not stutter
      const p = s.length > 1 ? s[s.length - 2] : b;
      const k = Math.min((t - b.t) / Math.max(1, b.t - p.t), 0.5);
      x = b.x + (b.x - p.x) * k; z = b.z + (b.z - p.z) * k; yaw = b.yaw;
    } else if (t <= a.t) { x = a.x; z = a.z; yaw = a.yaw; } else {
      const f = (t - a.t) / Math.max(1, b.t - a.t);
      x = lerp(a.x, b.x, f); z = lerp(a.z, b.z, f); yaw = angleLerp(a.yaw, b.yaw, f);
    }
    e.x = x; e.z = z;
    e.yaw = angleLerp(e.yaw, yaw, 0.35);
    e.lunge = b.l;
  }

  // ── the local player ─────────────────────────────────────────────────
  function inputVector() {
    let ix = 0, iz = 0;
    const k = M.keys;
    if (k.has("ArrowLeft") || k.has("KeyA")) ix -= 1;
    if (k.has("ArrowRight") || k.has("KeyD")) ix += 1;
    if (k.has("ArrowUp") || k.has("KeyW")) iz += 1;
    if (k.has("ArrowDown") || k.has("KeyS")) iz -= 1;
    const pads = navigator.getGamepads?.() ?? [];
    for (const p of pads) {
      if (!p) continue;
      const ax = p.axes[0] ?? 0, ay = p.axes[1] ?? 0;
      if (Math.hypot(ax, ay) > 0.2) { ix += ax; iz -= ay; }
      if (p.buttons[0]?.pressed) M.padAction = true; else M.padAction = false;
    }
    if (M.stick) { ix += M.stick.x; iz += M.stick.y; }
    if (M.hold) { ix += M.hold.x; iz += M.hold.z; }
    // screen right is world -x: the camera looks down the court from behind the home box
    let dx = -ix, dz = iz;
    const l = Math.hypot(dx, dz);
    if (l > 1) { dx /= l; dz /= l; }
    return [dx, dz];
  }

  function moveMe(dt) {
    const me = M.me;
    if (!me) return;
    const r = M.role;
    me.lungeCd = Math.max(0, me.lungeCd - dt);
    me.dashCd = Math.max(0, me.dashCd - dt);
    me.lunge = Math.max(0, me.lunge - dt);
    me.dash = Math.max(0, me.dash - dt);
    const wantAction = M.keys.has("Space") || M.keys.has("KeyJ") || M.padAction || M.action;
    if (wantAction && !M.actionLatch && !r.frozen && r.mode !== "none") {
      if (r.mode === "free") {
        if (me.dashCd <= 0) { me.dash = 0.35; me.dashCd = 2.2; }
      } else if (me.lungeCd <= 0) {
        me.lunge = 0.38; me.lungeCd = 1.3; me.lungeSeq++;
        if (me.ent?.inst) play(me.ent.inst, "Tag", { loop: false, speed: 1.15, fade: 0.08, restart: true });
      }
    }
    M.actionLatch = wantAction;
    let [dx, dz] = r.frozen || r.mode === "none" ? [0, 0] : inputVector();
    if (r.mode === "x") dz = 0;
    if (r.mode === "z") dx = 0;
    const speed = r.speed * (me.dash > 0 ? 1.6 : 1) * (me.lunge > 0 ? 0.55 : 1);
    // a little acceleration, so a tap is a step and not a teleport
    const k = 1 - Math.exp(-dt * 14);
    me.vx = lerp(me.vx, dx * speed, k);
    me.vz = lerp(me.vz, dz * speed, k);
    me.x += me.vx * dt;
    me.z += me.vz * dt;
    if (r.mode === "free") { me.x = clamp(me.x, -FIELD.half + 0.15, FIELD.half - 0.15); me.z = clamp(me.z, -3.2, 15.2); }
    else if (r.mode === "x") { me.z = r.line; me.x = clamp(me.x, -FIELD.half, FIELD.half); }
    else if (r.mode === "z") { me.x = 0; me.z = clamp(me.z, 0, FIELD.length); }
    else if (r.mode === "yard") { me.x = clamp(me.x, -11, 11); me.z = clamp(me.z, -6, 21); }
    const sp = Math.hypot(me.vx, me.vz);
    if (sp > 0.4) me.yaw = angleLerp(me.yaw, Math.atan2(me.vx, me.vz), 1 - Math.exp(-dt * 12));
    else if (r.mode === "x") me.yaw = angleLerp(me.yaw, Math.PI, 1 - Math.exp(-dt * 5)); // guards face the attackers
    me.speed = sp;
  }

  // ── drawing ──────────────────────────────────────────────────────────
  function camera(dt) {
    const c = M.cam;
    let eye, at;
    if (M.view === "lobby") {
      // a slow sway behind the home line while players gather, the court kept clear of the sign-up sheet
      const a = Math.sin(M.time * 0.12) * 0.55 - 0.25;
      eye = [Math.sin(a) * 16 + 2.5, 7.5, 3.5 - Math.cos(a) * 16];
      at = [2.8, 0.6, 4.5];
    } else {
      const fx = M.me && M.role.mode !== "none" ? M.me.x : 0;
      const fz = M.me && M.role.mode !== "none" ? M.me.z : 6;
      c.fx = lerp(c.fx, fx, 1 - Math.exp(-dt * 3));
      c.fz = lerp(c.fz, fz, 1 - Math.exp(-dt * 3));
      const aspect = M.w / Math.max(1, M.h);
      const back = aspect < 1 ? 17 + (1 - aspect) * 10 : 13.5;
      // a narrow (portrait) view cannot hold the whole width: follow the player across more closely
      const follow = aspect < 1 ? 0.8 : 0.35;
      eye = [c.fx * follow, aspect < 1 ? 15 : 11.5, c.fz * 0.35 - back + 2];
      at = [c.fx * follow * 0.85, 0, 5.2 + (c.fz - 6) * 0.3];
    }
    c.eye = c.eye ? c.eye.map((v, i) => lerp(v, eye[i], 1 - Math.exp(-dt * 4))) : eye;
    c.at = c.at ? c.at.map((v, i) => lerp(v, at[i], 1 - Math.exp(-dt * 4))) : at;
    const view = lookAt(c.eye, c.at, [0, 1, 0]);
    const proj = perspective((40 * Math.PI) / 180, M.w / Math.max(1, M.h), 0.3, 160);
    M.vp = mul(proj, view);
  }

  function setLighting(prog) {
    const gl = M.gl, u = prog.u;
    gl.uniform3fv(u.uSun, M.sun);
    gl.uniform3f(u.uSunCol, 1.55, 1.12, 0.78);
    gl.uniform3f(u.uSky, 0.42, 0.52, 0.72);
    gl.uniform3f(u.uBounce, 0.46, 0.33, 0.22);
    gl.uniform3f(u.uFog, 0.96, 0.74, 0.52);
    gl.uniform3fv(u.uEye, M.cam.eye);
    gl.uniform1f(u.uFogNear, 24);
    gl.uniform1f(u.uFogFar, 70);
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, M.shadowTex);
    gl.uniform1i(u.uShadow, 1);
  }

  const modelTmp = new Float32Array(16);
  function drawInstance(prog, model, inst, matrix, opts = {}) {
    const gl = M.gl, u = prog.u;
    model.nodes.forEach((n, i) => {
      if (n.mesh === undefined) return;
      if (opts.only && !opts.only.has(i)) return;
      const skinned = n.skin !== undefined && inst;
      if (skinned) {
        // skinned meshes take the joints' world matrices; the mesh node's own transform is in them
        gl.uniformMatrix4fv(u.uModel, false, matrix);
        gl.uniformMatrix4fv(u.uJoints, false, inst.joints[n.skin]);
        gl.uniform1i(u.uSkinned, 1);
      } else {
        mul(matrix, inst ? inst.world[i] : compose(n.t, n.r, n.s), modelTmp);
        gl.uniformMatrix4fv(u.uModel, false, modelTmp);
        gl.uniform1i(u.uSkinned, 0);
      }
      for (const prim of model.meshes[n.mesh]) {
        if (u.uColor) {
          gl.uniform4fv(u.uColor, prim.material.color);
          gl.uniform1i(u.uHasTex, prim.material.tex ? 1 : 0);
          if (prim.material.tex) {
            gl.activeTexture(gl.TEXTURE0);
            gl.bindTexture(gl.TEXTURE_2D, prim.material.tex);
            gl.uniform1i(u.uTex, 0);
          }
          gl.uniform1f(u.uTeamOn, prim.mask && opts.team ? 1 : 0);
          if (opts.team) gl.uniform3fv(u.uTeam, opts.team);
          gl.uniform1f(u.uHighlight, opts.highlight ?? 0);
        }
        gl.bindVertexArray(prim.vao);
        gl.drawElements(gl.TRIANGLES, prim.count, prim.indexType, 0);
      }
    });
  }

  function propNodeSet(p) { return (p._only ??= new Set([p.node.i])); }

  function drawWorld(prog) {
    for (const p of M.props) {
      const isFlag = p.node.name === "bendera";
      drawInstance(prog, M.yard, isFlag ? M.flag : null, p.matrix, { only: propNodeSet(p) });
    }
    for (const e of M.ents.values()) {
      if (!e.inst || e.hidden) continue;
      const info = M.roster.get(e.id);
      drawInstance(prog, M.kid, e.inst, e.matrix, { team: TEAM[info?.team ?? "p"], highlight: e.flash ?? 0 });
    }
  }

  function updateCharacters(dt, now) {
    for (const e of M.ents.values()) {
      if (e.id === M.meId && M.me) {
        e.x = M.me.x; e.z = M.me.z; e.yaw = M.me.yaw; e.speed = M.me.speed; e.lunge = M.me.lunge > 0 ? 1 : 0;
        M.me.ent = e;
      } else {
        interpolate(e, now);
        const sp = Math.hypot(e.x - e.px, e.z - e.pz) / Math.max(dt, 1e-3);
        e.speed = lerp(e.speed, sp, 1 - Math.exp(-dt * 10));
        if (e.lunge && !e.lastLunge && e.inst) play(e.inst, "Tag", { loop: false, speed: 1.15, fade: 0.08, restart: true });
        e.lastLunge = e.lunge;
      }
      e.px = e.x; e.pz = e.z;
      const inst = e.inst;
      if (!inst) continue;
      const info = M.roster.get(e.id);
      const busy = inst.clip?.name === "Tag" && inst.time < inst.clip.duration - 0.05;
      if (M.phase === "end" && M.winner) {
        play(inst, M.winner === "draw" ? "Idle" : info?.team === M.winner ? "Cheer" : "Sad", { fade: 0.3 });
      } else if (!busy) {
        if (e.speed > 0.35) play(inst, "Run", { speed: clamp(e.speed / 4.2, 0.6, 1.7) * 1.05, fade: 0.15 });
        else play(inst, "Idle", { fade: 0.25 });
      }
      advance(inst, dt);
      pose(inst);
      const s = KID_SCALE;
      e.matrix = compose([e.x, 0, e.z], [0, Math.sin(e.yaw / 2), 0, Math.cos(e.yaw / 2)], [s, s, s], e.matrix);
      e.flash = Math.max(0, (e.flash ?? 0) - dt * 1.5);
    }
    advance(M.flag, dt);
    pose(M.flag);
  }

  function resize() {
    const r = M.host.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const w = Math.max(1, Math.round(r.width * dpr)), h = Math.max(1, Math.round(r.height * dpr));
    if (w !== M.canvas.width || h !== M.canvas.height) {
      M.canvas.width = w; M.canvas.height = h;
      M.overlay.width = w; M.overlay.height = h;
    }
    M.w = w; M.h = h; M.dpr = dpr;
  }

  function frame(t) {
    if (!M.gl) return;
    M.raf = requestAnimationFrame(frame);
    const dt = M.last ? Math.min(0.1, (t - M.last) / 1000) : 1 / 60;
    if (M.last) { stats.gaps.push(t - M.last); if (stats.gaps.length > 600) stats.gaps.shift(); }
    M.last = t;
    if (!M.ready) return;
    const t0 = performance.now();
    M.time += dt;
    resize();
    moveMe(dt);
    updateCharacters(dt, t0);
    camera(dt);
    const gl = M.gl;
    gl.enable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);
    // 1. the sun's depth
    gl.bindFramebuffer(gl.FRAMEBUFFER, M.shadowFbo);
    gl.viewport(0, 0, M.shadowSize, M.shadowSize);
    gl.clear(gl.DEPTH_BUFFER_BIT);
    gl.useProgram(M.depthProg.p);
    gl.uniformMatrix4fv(M.depthProg.u.uVP, false, M.lightVP);
    gl.uniformMatrix4fv(M.depthProg.u.uLightVP, false, M.lightVP);
    gl.enable(gl.POLYGON_OFFSET_FILL);
    gl.polygonOffset(1.5, 3);
    drawWorld(M.depthProg);
    gl.disable(gl.POLYGON_OFFSET_FILL);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    // 2. the view
    gl.viewport(0, 0, M.w, M.h);
    gl.clearColor(0.96, 0.74, 0.52, 1);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    gl.useProgram(M.skyProg.p);
    gl.uniformMatrix4fv(M.skyProg.u.uInvVP, false, invert(M.vp));
    gl.uniform3fv(M.skyProg.u.uSun, M.sun);
    gl.uniform3fv(M.skyProg.u.uEye, M.cam.eye);
    gl.bindVertexArray(M.skyVao);
    gl.depthMask(false);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.depthMask(true);
    // ground
    const gp = M.groundProg;
    gl.useProgram(gp.p);
    gl.uniformMatrix4fv(gp.u.uVP, false, M.vp);
    gl.uniformMatrix4fv(gp.u.uLightVP, false, M.lightVP);
    setLighting(gp);
    gl.uniform1f(gp.u.uTime, M.time);
    const myTeam = M.roster.get(M.meId)?.team;
    const meCol = TEAM[myTeam ?? "p"].map((v) => (myTeam === "p" ? v * 0.85 : v));
    gl.uniform3fv(gp.u.uMeCol, myTeam === "p" ? [1.0, 0.86, 0.3] : meCol);
    gl.uniform3f(gp.u.uMe, M.me?.x ?? 0, M.me?.z ?? 0, M.me && M.role.mode !== "none" ? 1 : 0);
    const guarding = M.me && (M.role.mode === "x" || M.role.mode === "z");
    const line = M.role.mode === "x" ? [-FIELD.half, M.role.line, FIELD.half, M.role.line] : [0, 0, 0, FIELD.length];
    gl.uniform4fv(gp.u.uMyLine, line);
    gl.uniform1f(gp.u.uLineOn, guarding ? 1 : 0);
    gl.bindVertexArray(M.groundVao);
    gl.drawElements(gl.TRIANGLES, M.groundCount, gl.UNSIGNED_SHORT, 0);
    // everything standing on it
    const sp = M.skinProg;
    gl.useProgram(sp.p);
    gl.uniformMatrix4fv(sp.u.uVP, false, M.vp);
    gl.uniformMatrix4fv(sp.u.uLightVP, false, M.lightVP);
    setLighting(sp);
    drawWorld(sp);
    gl.bindVertexArray(null);
    drawOverlay();
    stats.frames.push(performance.now() - t0);
    if (stats.frames.length > 600) stats.frames.shift();
  }

  // ── names over heads ─────────────────────────────────────────────────
  function drawOverlay() {
    const g = M.ctx2d, dpr = M.dpr;
    g.clearRect(0, 0, M.w, M.h);
    if (M.view === "lobby" && M.phase === "lobby") return;
    const list = [];
    for (const e of M.ents.values()) {
      const info = M.roster.get(e.id);
      if (!info) continue;
      const p = project(M.vp, [e.x, 1.48, e.z]);
      if (!p) continue;
      list.push({ e, info, x: (p[0] * 0.5 + 0.5) * M.w, y: (1 - (p[1] * 0.5 + 0.5)) * M.h, depth: p[2] });
    }
    list.sort((a, b) => b.depth - a.depth);
    g.textAlign = "center";
    g.textBaseline = "middle";
    for (const { e, info, x, y, depth } of list) {
      const s = clamp(16 / depth, 0.7, 1.15) * dpr;
      const me = e.id === M.meId;
      g.font = `${me ? 800 : 600} ${Math.round(12.5 * s)}px "Familjen Grotesk", system-ui, sans-serif`;
      const label = info.name;
      const w = g.measureText(label).width + 14 * s;
      const h = 19 * s;
      g.globalAlpha = 0.92;
      g.fillStyle = info.team === "m" ? "#a50f25" : "#f6f1e4";
      roundRect(g, x - w / 2, y - h / 2, w, h, h / 2);
      g.fill();
      if (me) { g.lineWidth = 2 * dpr; g.strokeStyle = "#f3b43c"; g.stroke(); }
      g.globalAlpha = 1;
      g.fillStyle = info.team === "m" ? "#fff6ea" : "#2a1a0e";
      g.fillText(label, x, y + 0.5 * s);
      if (e.flags & 1) {
        // reached the far end: on the way home now, a little flag says so
        const fx = x + w / 2 + 6 * s, fy = y - 4 * s;
        g.fillStyle = "#3a2a1a";
        g.fillRect(fx, fy - 6 * s, 1.6 * s, 16 * s);
        g.fillStyle = "#c8102e";
        g.fillRect(fx + 1.6 * s, fy - 6 * s, 10 * s, 4 * s);
        g.fillStyle = "#f6f1e4";
        g.fillRect(fx + 1.6 * s, fy - 2 * s, 10 * s, 4 * s);
      }
    }
  }
  function roundRect(g, x, y, w, h, r) {
    g.beginPath();
    g.moveTo(x + r, y);
    g.arcTo(x + w, y, x + w, y + h, r);
    g.arcTo(x + w, y + h, x, y + h, r);
    g.arcTo(x, y + h, x, y, r);
    g.arcTo(x, y, x + w, y, r);
    g.closePath();
  }

  // ── sound: a referee's whistle and the kentongan, synthesised ────────
  function audio() {
    if (!M.sound) return null;
    if (!M.audio) {
      const AC = window.AudioContext || window.webkitAudioContext;
      if (!AC) return null;
      M.audio = new AC();
    }
    if (M.audio.state === "suspended") M.audio.resume();
    return M.audio;
  }
  function whistle(long) {
    const a = audio();
    if (!a) return;
    const t = a.currentTime, len = long ? 0.9 : 0.45;
    const o = a.createOscillator(), lfo = a.createOscillator(), lg = a.createGain(), g = a.createGain();
    o.type = "sine"; o.frequency.value = 2650;
    lfo.frequency.value = 28; lg.gain.value = 140;
    lfo.connect(lg).connect(o.frequency);
    g.gain.setValueAtTime(0, t);
    g.gain.linearRampToValueAtTime(0.16, t + 0.02);
    g.gain.setValueAtTime(0.16, t + len - 0.08);
    g.gain.linearRampToValueAtTime(0, t + len);
    o.connect(g).connect(a.destination);
    o.start(t); lfo.start(t); o.stop(t + len); lfo.stop(t + len);
  }
  function tok(times = 2) {
    const a = audio();
    if (!a) return;
    for (let i = 0; i < times; i++) {
      const t = a.currentTime + i * 0.16;
      const o = a.createOscillator(), g = a.createGain(), f = a.createBiquadFilter();
      o.type = "triangle"; o.frequency.setValueAtTime(420, t); o.frequency.exponentialRampToValueAtTime(260, t + 0.12);
      f.type = "bandpass"; f.frequency.value = 520; f.Q.value = 4;
      g.gain.setValueAtTime(0.5, t); g.gain.exponentialRampToValueAtTime(0.001, t + 0.18);
      o.connect(f).connect(g).connect(a.destination);
      o.start(t); o.stop(t + 0.2);
    }
  }

  // ── touch: a stick on the left, the action button on the right ───────
  function touchControls() {
    const wrap = document.createElement("div");
    wrap.className = "gs-touch";
    wrap.innerHTML = `<div class="gs-stick" aria-hidden="true"><span></span></div><button class="gs-act" type="button" aria-label="Action">⚑</button>`;
    M.host.appendChild(wrap);
    const pad = wrap.querySelector(".gs-stick"), knob = pad.querySelector("span"), act = wrap.querySelector(".gs-act");
    let pid = null, cx = 0, cy = 0;
    pad.addEventListener("pointerdown", (e) => {
      pid = e.pointerId; pad.setPointerCapture(pid);
      const r = pad.getBoundingClientRect();
      cx = r.left + r.width / 2; cy = r.top + r.height / 2;
      moveStick(e);
    });
    const moveStick = (e) => {
      if (e.pointerId !== pid) return;
      let x = (e.clientX - cx) / 46, y = (e.clientY - cy) / 46;
      const l = Math.hypot(x, y);
      if (l > 1) { x /= l; y /= l; }
      M.stick = { x, y: -y };
      knob.style.transform = `translate(${x * 34}px, ${y * 34}px)`;
    };
    pad.addEventListener("pointermove", moveStick);
    const end = (e) => { if (e.pointerId !== pid) return; pid = null; M.stick = null; knob.style.transform = ""; };
    pad.addEventListener("pointerup", end);
    pad.addEventListener("pointercancel", end);
    act.addEventListener("pointerdown", (e) => { e.preventDefault(); M.action = true; audio(); });
    act.addEventListener("pointerup", () => (M.action = false));
    act.addEventListener("pointercancel", () => (M.action = false));
    M.touch = wrap;
  }

  const onKeyDown = (e) => {
    if (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)) return;
    if (["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Space"].includes(e.code) && M.view === "play") e.preventDefault();
    M.keys.add(e.code);
    if (M.audio === null && M.sound) audio();
  };
  const onKeyUp = (e) => M.keys.delete(e.code);
  const onBlur = () => M.keys.clear();

  // ── ops ──────────────────────────────────────────────────────────────
  ops["gs.attach"] = async ({ host, id, base }) => {
    M.host = elementOf(host);
    M.cb = id;
    M.canvas = document.createElement("canvas");
    M.canvas.className = "gs-gl";
    M.overlay = document.createElement("canvas");
    M.overlay.className = "gs-overlay";
    M.host.prepend(M.overlay);
    M.host.prepend(M.canvas);
    M.ctx2d = M.overlay.getContext("2d");
    try {
      initGl();
    } catch (e) {
      send({ type: "error", message: e.message });
      return false;
    }
    touchControls();
    addEventListener("keydown", onKeyDown);
    addEventListener("keyup", onKeyUp);
    addEventListener("blur", onBlur);
    // say goodbye as the tab closes, so the others hand the match on at once instead of waiting for a timeout
    M.onHide = () => send({ type: "leaving" });
    addEventListener("pagehide", M.onHide);
    M.onVis = () => send({ type: "visible", v: document.visibilityState === "visible" });
    document.addEventListener("visibilitychange", M.onVis);
    M.last = 0;
    M.raf = requestAnimationFrame(frame);
    try {
      await loadModels(base ?? "");
    } catch (e) {
      send({ type: "error", message: e.message });
      return false;
    }
    return true;
  };
  ops["gs.detach"] = () => {
    cancelAnimationFrame(M.raf);
    removeEventListener("keydown", onKeyDown);
    removeEventListener("keyup", onKeyUp);
    removeEventListener("blur", onBlur);
    if (M.onHide) removeEventListener("pagehide", M.onHide);
    if (M.onVis) document.removeEventListener("visibilitychange", M.onVis);
    M.touch?.remove();
    M.canvas?.remove();
    M.overlay?.remove();
    M.gl?.getExtension("WEBGL_lose_context")?.loseContext();
    M.gl = null;
    M.ready = false;
    M.cb = 0;
    M.ents.clear();
  };
  /** Who is in the match (or the lobby): names and teams, and which one is this tab. */
  ops["gs.roster"] = ({ me, list, view }) => {
    M.meId = me;
    M.view = view ?? M.view;
    M.roster = new Map(list.map((p) => [p.id, p]));
    for (const id of [...M.ents.keys()]) if (!M.roster.has(id)) M.ents.delete(id);
    for (const p of list) {
      const e = entity(p.id);
      if (p.x !== undefined && (p.id !== M.meId || !M.me)) {
        e.x = p.x; e.z = p.z; e.yaw = p.yaw ?? 0; e.snaps = [];
      }
    }
    if (!M.roster.has(M.meId)) M.me = null;
  };
  /** What the local player may do: run free (attacker), keep to a line (guard), or watch. */
  ops["gs.role"] = ({ mode, line, speed, frozen, x, z, yaw, teleport, gen }) => {
    M.role = { mode, line: line ?? 0, speed: speed ?? 4.4, frozen: !!frozen, gen: gen ?? -1 };
    if (mode === "none") { M.me = null; return; }
    if (!M.me || teleport) {
      M.me = { x, z, yaw: yaw ?? 0, vx: 0, vz: 0, speed: 0, lunge: 0, lungeCd: 0, lungeSeq: 0, dash: 0, dashCd: 0, ent: null };
    }
  };
  /** The host's view of the match, twenty times a second. */
  ops["gs.snap"] = (s) => applySnapshot(s);
  /** The local player, read by C# twenty times a second and sent to the host. */
  ops["gs.me"] = () => {
    const me = M.me;
    if (!me) return null;
    // "x,z,yaw,lunge,gen" in centimetres and centiradians: cheap for C# to read and to pass on
    return `${Math.round(me.x * 100)},${Math.round(me.z * 100)},${Math.round(me.yaw * 100)},${me.lunge > 0 ? 1 : 0},${M.role.gen}`;
  };
  ops["gs.event"] = ({ kind, who }) => {
    if (kind === "swap") { whistle(false); const e = M.ents.get(who); if (e) e.flash = 1; }
    else if (kind === "score") { tok(2); const e = M.ents.get(who); if (e) e.flash = 1; }
    else if (kind === "start") whistle(false);
    else if (kind === "end") { whistle(true); setTimeout(() => tok(3), 900); }
  };
  ops["gs.sound"] = ({ on }) => { M.sound = !!on; if (!on && M.audio) M.audio.suspend(); };
  ops["gs.view"] = ({ view }) => { M.view = view; };
  ops["gs.copy"] = async ({ text }) => {
    try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
  };

  // For tools/e2e/galasin.mjs: read the frame, and steer the local player.
  globalThis.__gs = {
    M, stats,
    me: () => (M.me ? { x: M.me.x, z: M.me.z, yaw: M.me.yaw, mode: M.role.mode, line: M.role.line, frozen: M.role.frozen } : null),
    ent: (id) => { const e = M.ents.get(id); return e ? { x: e.x, z: e.z, anim: e.inst?.clip?.name, flags: e.flags } : null; },
    latest: (id) => { const e = M.ents.get(id); const s = e?.snaps.at(-1); return s ? { x: s.x, z: s.z } : e ? { x: e.x, z: e.z } : null; },
    ents: () => [...M.ents.values()].map((e) => ({ id: e.id, x: e.x, z: e.z, anim: e.inst?.clip?.name ?? null, team: M.roster.get(e.id)?.team, bot: !!M.roster.get(e.id)?.bot })),
    hold: (x, z) => { M.hold = x || z ? { x, z } : null; },
    teleport: (x, z) => { if (M.me) { M.me.x = x; M.me.z = z; M.me.vx = M.me.vz = 0; } },
    act: () => { M.action = true; setTimeout(() => (M.action = false), 60); },
    frameStats: () => {
      const f = [...stats.frames].sort((a, b) => a - b), g = [...stats.gaps].sort((a, b) => a - b);
      const q = (a, p) => (a.length ? a[Math.min(a.length - 1, Math.floor(a.length * p))] : 0);
      return { n: f.length, cpuMedian: q(f, 0.5), cpuP95: q(f, 0.95), gapMedian: q(g, 0.5), gapP95: q(g, 0.95), snaps: stats.snaps, loadMs: stats.loadMs };
    },
    resetStats: () => { stats.frames.length = 0; stats.gaps.length = 0; },
  };
}
