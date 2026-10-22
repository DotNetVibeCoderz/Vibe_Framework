// Tilik's viewer engine. C# owns the review: the part tree, what is hidden,
// isolated and selected, the display mode, the section and explode settings,
// measurements, notes, saved views and the bill of materials. This module owns
// what has to answer within a frame: it parses glTF binary and STL, keeps each
// part in its own WebGL2 buffers, draws the shaded, edged, wireframe and x-ray
// modes, cuts with the section plane and fills each cut with drafting hatch
// (a stencil pass per part), picks by ray casting, snaps to vertices and edges,
// orbits with inertia, and draws dimensions and note pins on a 2D overlay.
// Embedded in the app's assembly; Rustzor's bundler ships it.
// Built by Gravicode Studios, led by Kang Fadhil.
export function install({ ops, dispatch, addHandle, getHandle, elementOf }) {
  // ── small vector and matrix helpers ──────────────────────────────────
  const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
  const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
  const mul = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
  const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  const len = (a) => Math.hypot(a[0], a[1], a[2]);
  const norm = (a) => { const l = len(a) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

  function matMul(a, b) {
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
  function perspective(fovy, aspect, n, f) {
    const t = 1 / Math.tan(fovy / 2);
    return new Float32Array([t / aspect, 0, 0, 0, 0, t, 0, 0, 0, 0, (f + n) / (n - f), -1, 0, 0, (2 * f * n) / (n - f), 0]);
  }
  function ortho(hw, hh, n, f) {
    return new Float32Array([1 / hw, 0, 0, 0, 0, 1 / hh, 0, 0, 0, 0, -2 / (f - n), 0, 0, 0, -(f + n) / (f - n), 1]);
  }
  function invert(m) {
    const inv = new Float32Array(16);
    const a = m;
    inv[0] = a[5] * a[10] * a[15] - a[5] * a[11] * a[14] - a[9] * a[6] * a[15] + a[9] * a[7] * a[14] + a[13] * a[6] * a[11] - a[13] * a[7] * a[10];
    inv[4] = -a[4] * a[10] * a[15] + a[4] * a[11] * a[14] + a[8] * a[6] * a[15] - a[8] * a[7] * a[14] - a[12] * a[6] * a[11] + a[12] * a[7] * a[10];
    inv[8] = a[4] * a[9] * a[15] - a[4] * a[11] * a[13] - a[8] * a[5] * a[15] + a[8] * a[7] * a[13] + a[12] * a[5] * a[11] - a[12] * a[7] * a[9];
    inv[12] = -a[4] * a[9] * a[14] + a[4] * a[10] * a[13] + a[8] * a[5] * a[14] - a[8] * a[6] * a[13] - a[12] * a[5] * a[10] + a[12] * a[6] * a[9];
    inv[1] = -a[1] * a[10] * a[15] + a[1] * a[11] * a[14] + a[9] * a[2] * a[15] - a[9] * a[3] * a[14] - a[13] * a[2] * a[11] + a[13] * a[3] * a[10];
    inv[5] = a[0] * a[10] * a[15] - a[0] * a[11] * a[14] - a[8] * a[2] * a[15] + a[8] * a[3] * a[14] + a[12] * a[2] * a[11] - a[12] * a[3] * a[10];
    inv[9] = -a[0] * a[9] * a[15] + a[0] * a[11] * a[13] + a[8] * a[1] * a[15] - a[8] * a[3] * a[13] - a[12] * a[1] * a[11] + a[12] * a[3] * a[9];
    inv[13] = a[0] * a[9] * a[14] - a[0] * a[10] * a[13] - a[8] * a[1] * a[14] + a[8] * a[2] * a[13] + a[12] * a[1] * a[10] - a[12] * a[2] * a[9];
    inv[2] = a[1] * a[6] * a[15] - a[1] * a[7] * a[14] - a[5] * a[2] * a[15] + a[5] * a[3] * a[14] + a[13] * a[2] * a[7] - a[13] * a[3] * a[6];
    inv[6] = -a[0] * a[6] * a[15] + a[0] * a[7] * a[14] + a[4] * a[2] * a[15] - a[4] * a[3] * a[14] - a[12] * a[2] * a[7] + a[12] * a[3] * a[6];
    inv[10] = a[0] * a[5] * a[15] - a[0] * a[7] * a[13] - a[4] * a[1] * a[15] + a[4] * a[3] * a[13] + a[12] * a[1] * a[7] - a[12] * a[3] * a[5];
    inv[14] = -a[0] * a[5] * a[14] + a[0] * a[6] * a[13] + a[4] * a[1] * a[14] - a[4] * a[2] * a[13] - a[12] * a[1] * a[6] + a[12] * a[2] * a[5];
    inv[3] = -a[1] * a[6] * a[11] + a[1] * a[7] * a[10] + a[5] * a[2] * a[11] - a[5] * a[3] * a[10] - a[9] * a[2] * a[7] + a[9] * a[3] * a[6];
    inv[7] = a[0] * a[6] * a[11] - a[0] * a[7] * a[10] - a[4] * a[2] * a[11] + a[4] * a[3] * a[10] + a[8] * a[2] * a[7] - a[8] * a[3] * a[6];
    inv[11] = -a[0] * a[5] * a[11] + a[0] * a[7] * a[9] + a[4] * a[1] * a[11] - a[4] * a[3] * a[9] - a[8] * a[1] * a[7] + a[8] * a[3] * a[5];
    inv[15] = a[0] * a[5] * a[10] - a[0] * a[6] * a[9] - a[4] * a[1] * a[10] + a[4] * a[2] * a[9] + a[8] * a[1] * a[6] - a[8] * a[2] * a[5];
    let det = a[0] * inv[0] + a[1] * inv[4] + a[2] * inv[8] + a[3] * inv[12];
    det = det ? 1 / det : 0;
    for (let i = 0; i < 16; i++) inv[i] *= det;
    return inv;
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
  const xform = (m, p) => [m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12], m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13], m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14]];
  const xformDir = (m, p) => [m[0] * p[0] + m[4] * p[1] + m[8] * p[2], m[1] * p[0] + m[5] * p[1] + m[9] * p[2], m[2] * p[0] + m[6] * p[1] + m[10] * p[2]];
  const linearToSrgb = (c) => (c <= 0.0031308 ? 12.92 * c : 1.055 * Math.pow(c, 1 / 2.4) - 0.055);

  // ── state ────────────────────────────────────────────────────────────
  const M = {
    host: null, canvas: null, gl: null, overlay: null, ctx2d: null, cube: null, w: 0, h: 0, dpr: 1,
    cam: { target: [0, 0, 0], dist: 1, yaw: 0.6, pitch: 0.45, ortho: false, fov: (36 * Math.PI) / 180 },
    vp: null, view: null, proj: null, eye: [0, 0, 1],
    parts: [], nodes: [], radius: 1, center: [0, 0, 0], bbox: null,
    hidden: new Set(), isolate: null, selected: new Set(), hover: -1,
    mode: "edges", explode: 0, section: { on: false, axis: "x", offset: 0.5, flip: false },
    tool: "select", pending: [], measures: [], notes: [], selectedNote: null, snap: null,
    cb: 0, dirty: false, inertia: null, drag: null, pointers: new Map(),
    colors: {}, drawn: [], loading: false, file: "",
  };
  const stats = { frames: [], last: 0, loadMs: 0 };
  const send = (m) => M.cb && dispatch(M.cb, m);
  const invalidate = () => {
    if (M.dirty || !M.gl) return;
    M.dirty = true;
    requestAnimationFrame(frame);
  };

  // ── GL setup ─────────────────────────────────────────────────────────
  const VS = `#version 300 es
  layout(location=0) in vec3 aPos; layout(location=1) in vec3 aNrm;
  uniform mat4 uVP; uniform vec3 uOffset;
  out vec3 vWorld; out vec3 vNrm;
  void main(){ vWorld = aPos + uOffset; vNrm = aNrm; gl_Position = uVP * vec4(vWorld, 1.0); }`;
  const FS = `#version 300 es
  precision highp float;
  in vec3 vWorld; in vec3 vNrm; out vec4 o;
  uniform vec4 uColor; uniform float uMetal, uRough; uniform vec3 uEye;
  uniform vec4 uClip; uniform int uClipOn; uniform vec4 uTint; uniform vec3 uSky, uGround;
  vec3 srgb(vec3 c){ return mix(12.92*c, 1.055*pow(c, vec3(1.0/2.4)) - 0.055, step(0.0031308, c)); }
  void main(){
    if (uClipOn == 1 && dot(vWorld, uClip.xyz) > uClip.w) discard;
    vec3 n = normalize(vNrm); if (!gl_FrontFacing) n = -n;
    vec3 v = normalize(uEye - vWorld);
    vec3 base = uColor.rgb;
    vec3 L1 = normalize(vec3(-0.45, 0.85, 0.55)), L2 = normalize(vec3(0.7, 0.25, -0.4));
    float shin = clamp(2.0 / max(uRough*uRough*uRough*uRough, 0.002) - 2.0, 4.0, 900.0);
    vec3 spec0 = mix(vec3(0.04), base, uMetal);
    vec3 diff = base * (1.0 - uMetal * 0.9);
    vec3 col = diff * mix(uGround, uSky, n.y * 0.5 + 0.5) * 0.55;
    for (int i = 0; i < 2; i++) {
      vec3 L = i == 0 ? L1 : L2; float k = i == 0 ? 1.0 : 0.4;
      float nl = max(dot(n, L), 0.0);
      vec3 h = normalize(L + v);
      col += k * (diff * nl + spec0 * pow(max(dot(n, h), 0.0), shin) * (shin + 8.0) / 25.0 * nl);
    }
    vec3 r = reflect(-v, n);
    vec3 env = mix(uGround * 0.6, uSky * 1.25, smoothstep(-0.25, 0.6, r.y));
    col += spec0 * env * mix(0.25, 0.9, uMetal) * (1.0 - uRough * 0.6);
    col = mix(col, uTint.rgb, uTint.a);
    o = vec4(srgb(col), uColor.a);
  }`;
  const LINE_FS = `#version 300 es
  precision highp float;
  in vec3 vWorld; in vec3 vNrm; out vec4 o;
  uniform vec4 uColor; uniform vec4 uClip; uniform int uClipOn;
  void main(){ if (uClipOn == 1 && dot(vWorld, uClip.xyz) > uClip.w) discard; o = uColor; }`;
  const CAP_FS = `#version 300 es
  precision highp float;
  in vec3 vWorld; in vec3 vNrm; out vec4 o;
  uniform vec4 uColor; uniform vec4 uHatch; uniform vec3 uU, uV; uniform float uSpacing, uAngle;
  void main(){
    vec2 p = vec2(dot(vWorld, uU), dot(vWorld, uV));
    vec2 d = vec2(cos(uAngle), sin(uAngle));
    float t = dot(p, d) / uSpacing;
    float line = 1.0 - smoothstep(0.06, 0.16, abs(fract(t) - 0.5) * 2.0 - 0.0);
    float edge = step(0.86, abs(fract(t) - 0.5) * 2.0);
    o = vec4(mix(uColor.rgb, uHatch.rgb, edge), 1.0);
  }`;
  const BG_VS = `#version 300 es
  const vec2 P[3] = vec2[3](vec2(-1.0,-1.0), vec2(3.0,-1.0), vec2(-1.0,3.0));
  out float vY; void main(){ vec2 p = P[gl_VertexID]; vY = p.y * 0.5 + 0.5; gl_Position = vec4(p, 0.999, 1.0); }`;
  const BG_FS = `#version 300 es
  precision highp float; in float vY; out vec4 o; uniform vec3 uTop, uBottom;
  void main(){ o = vec4(mix(uBottom, uTop, clamp(vY, 0.0, 1.0)), 1.0); }`;

  function program(gl, vs, fs) {
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
      u[info.name] = gl.getUniformLocation(p, info.name);
    }
    return { p, u };
  }

  let P = null; // programs
  let capQuad = null;

  function initGl() {
    const gl = M.canvas.getContext("webgl2", { antialias: true, stencil: true, preserveDrawingBuffer: true, premultipliedAlpha: false });
    if (!gl) throw new Error("This browser has no WebGL 2, which Tilik needs to draw the model.");
    M.gl = gl;
    P = { solid: program(gl, VS, FS), line: program(gl, VS, LINE_FS), cap: program(gl, VS, CAP_FS), bg: program(gl, BG_VS, BG_FS) };
    capQuad = { vao: gl.createVertexArray(), buf: gl.createBuffer() };
    gl.bindVertexArray(capQuad.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, capQuad.buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(36), gl.DYNAMIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 24, 0);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 24, 12);
    gl.bindVertexArray(null);
    M.bgVao = gl.createVertexArray();
  }

  function readColors() {
    const cs = getComputedStyle(M.host);
    const hex = (name, fallback) => {
      const v = cs.getPropertyValue(name).trim() || fallback;
      const m = /^#([0-9a-f]{6})$/i.exec(v);
      if (!m) return [0.5, 0.5, 0.5];
      const n = parseInt(m[1], 16);
      return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
    };
    M.colors = {
      top: hex("--vp-top", "#e9eaf0"), bottom: hex("--vp-bottom", "#c9ccd8"),
      sky: hex("--vp-sky", "#f2f4fa"), ground: hex("--vp-ground", "#5d5f6e"),
      edge: hex("--vp-edge", "#1b1b2a"), wire: hex("--vp-wire", "#1b1b2a"), select: hex("--vp-select", "#f2b705"), hatch: hex("--vp-hatch", "#3b2f8f"),
      capBase: hex("--vp-cap", "#efeef7"), dim: cs.getPropertyValue("--vp-dim").trim() || "#3b2f8f",
      dimInk: cs.getPropertyValue("--vp-dim-ink").trim() || "#ffffff", pin: cs.getPropertyValue("--vp-pin").trim() || "#f2b705",
      pinInk: cs.getPropertyValue("--vp-pin-ink").trim() || "#1b1b2a", mono: cs.getPropertyValue("--font-mono").trim() || "monospace",
    };
    invalidate();
  }

  // ── loading: glTF binary ─────────────────────────────────────────────
  const COMP = { 5120: Int8Array, 5121: Uint8Array, 5122: Int16Array, 5123: Uint16Array, 5125: Uint32Array, 5126: Float32Array };
  const SIZE = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT4: 16 };

  function parseGlb(buf) {
    const dv = new DataView(buf);
    if (dv.getUint32(0, true) !== 0x46546c67) throw new Error("This is not a glTF binary (.glb) file.");
    let off = 12, json = null, bin = null;
    while (off < buf.byteLength) {
      const clen = dv.getUint32(off, true), type = dv.getUint32(off + 4, true);
      if (type === 0x4e4f534a) json = JSON.parse(new TextDecoder().decode(new Uint8Array(buf, off + 8, clen)));
      else if (type === 0x004e4942) bin = new Uint8Array(buf, off + 8, clen);
      off += 8 + clen;
    }
    if (!json) throw new Error("The .glb file has no JSON chunk.");
    if (json.extensionsRequired?.length) throw new Error("The .glb needs " + json.extensionsRequired.join(", ") + ", which Tilik does not read. Export it without compression.");
    const accessor = (i) => {
      const a = json.accessors[i];
      const bv = json.bufferViews[a.bufferView];
      const T = COMP[a.componentType], n = SIZE[a.type];
      const start = bin.byteOffset + (bv.byteOffset ?? 0) + (a.byteOffset ?? 0);
      const stride = bv.byteStride ?? 0;
      const elem = n * T.BYTES_PER_ELEMENT;
      let out;
      if (!stride || stride === elem) out = new T(bin.buffer.slice(start, start + a.count * elem));
      else {
        out = new T(a.count * n);
        const src = new DataView(bin.buffer);
        for (let k = 0; k < a.count; k++)
          for (let c = 0; c < n; c++) {
            const p = start + k * stride + c * T.BYTES_PER_ELEMENT;
            out[k * n + c] = T === Float32Array ? src.getFloat32(p, true) : T === Uint16Array ? src.getUint16(p, true) : T === Uint32Array ? src.getUint32(p, true) : src.getUint8(p);
          }
      }
      if (a.normalized && T !== Float32Array) {
        const div = T === Int8Array ? 127 : T === Uint8Array ? 255 : T === Int16Array ? 32767 : 65535;
        out = Float32Array.from(out, (v) => Math.max(v / div, -1));
      } else if (T !== Float32Array && (n === 3 || n === 4)) out = Float32Array.from(out);
      return out;
    };
    const nodes = [];
    const scene = json.scenes?.[json.scene ?? 0] ?? { nodes: json.nodes.map((_, i) => i) };
    const visit = (ni, parent, pm) => {
      const n = json.nodes[ni];
      const world = matMul(pm, trs(n));
      const id = nodes.length;
      nodes.push({ id, name: n.name || `Node ${ni}`, parent, extras: n.extras ?? null, mesh: n.mesh ?? null, world });
      for (const c of n.children ?? []) visit(c, id, world);
    };
    const I = new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
    for (const r of scene.nodes) visit(r, -1, I);
    const parts = [];
    for (const nd of nodes) {
      if (nd.mesh == null) continue;
      const mesh = json.meshes[nd.mesh];
      const pos = [], nrm = [];
      let mat = null;
      for (const prim of mesh.primitives) {
        if ((prim.mode ?? 4) !== 4) continue;
        const P0 = accessor(prim.attributes.POSITION);
        const N0 = prim.attributes.NORMAL != null ? accessor(prim.attributes.NORMAL) : null;
        const idx = prim.indices != null ? accessor(prim.indices) : null;
        const count = idx ? idx.length : P0.length / 3;
        const nm = new Float32Array(16);
        nm.set(invert(nd.world));
        for (let k = 0; k < count; k += 3) {
          const tri = [0, 1, 2].map((t) => (idx ? idx[k + t] : k + t));
          const w = tri.map((v) => xform(nd.world, [P0[v * 3], P0[v * 3 + 1], P0[v * 3 + 2]]));
          const fn = norm(cross(sub(w[1], w[0]), sub(w[2], w[0])));
          for (let t = 0; t < 3; t++) {
            pos.push(w[t][0], w[t][1], w[t][2]);
            if (N0) {
              const v = tri[t];
              // normal matrix = transpose(inverse(world)): multiply by the inverse's rows
              const n0 = [N0[v * 3], N0[v * 3 + 1], N0[v * 3 + 2]];
              const nn = norm([nm[0] * n0[0] + nm[1] * n0[1] + nm[2] * n0[2], nm[4] * n0[0] + nm[5] * n0[1] + nm[6] * n0[2], nm[8] * n0[0] + nm[9] * n0[1] + nm[10] * n0[2]]);
              nrm.push(nn[0], nn[1], nn[2]);
            } else nrm.push(fn[0], fn[1], fn[2]);
          }
        }
        if (mat == null && prim.material != null) mat = json.materials[prim.material];
      }
      if (!pos.length) continue;
      const pbr = mat?.pbrMetallicRoughness ?? {};
      const bc = pbr.baseColorFactor ?? [0.7, 0.7, 0.72, 1];
      parts.push({
        node: nd.id, name: nd.name, extras: nd.extras,
        material: (mat?.name ?? "").replace(/^RW63 /, ""),
        color: [bc[0], bc[1], bc[2], mat?.alphaMode === "BLEND" ? bc[3] : 1],
        metal: pbr.metallicFactor ?? 1, rough: pbr.roughnessFactor ?? 1,
        pos: new Float32Array(pos), nrm: new Float32Array(nrm),
      });
    }
    return { nodes, parts, extras: json.nodes[scene.nodes[0]]?.extras ?? {} };
  }

  // ── loading: STL (binary or ASCII) ───────────────────────────────────
  function parseStl(buf, name) {
    const bytes = new Uint8Array(buf);
    const head = new TextDecoder().decode(bytes.slice(0, Math.min(512, bytes.length)));
    const dv = new DataView(buf);
    let pos = [];
    const binary = bytes.length >= 84 && 84 + dv.getUint32(80, true) * 50 === bytes.length;
    if (binary) {
      const n = dv.getUint32(80, true);
      for (let i = 0; i < n; i++) {
        const o = 84 + i * 50 + 12;
        for (let k = 0; k < 9; k++) pos.push(dv.getFloat32(o + k * 4, true));
      }
    } else if (/^\s*solid/.test(head)) {
      const text = new TextDecoder().decode(bytes);
      const re = /vertex\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)/g;
      let m;
      while ((m = re.exec(text))) pos.push(+m[1], +m[2], +m[3]);
    } else throw new Error("This STL file is neither binary nor ASCII STL.");
    if (pos.length < 9) throw new Error("The STL file has no triangles.");
    // STL has no units; treat it as millimetres, z up (the usual CAD export), and turn it y up.
    const P2 = new Float32Array(pos.length), N2 = new Float32Array(pos.length);
    for (let i = 0; i < pos.length; i += 3) {
      P2[i] = pos[i] * 0.001;
      P2[i + 1] = pos[i + 2] * 0.001;
      P2[i + 2] = -pos[i + 1] * 0.001;
    }
    for (let i = 0; i < P2.length; i += 9) {
      const a = [P2[i], P2[i + 1], P2[i + 2]], b = [P2[i + 3], P2[i + 4], P2[i + 5]], c = [P2[i + 6], P2[i + 7], P2[i + 8]];
      const n = norm(cross(sub(b, a), sub(c, a)));
      for (let t = 0; t < 3; t++) N2.set(n, i + t * 3);
    }
    const base = name.replace(/\.[^.]+$/, "");
    const nodes = [{ id: 0, name: base, parent: -1, extras: null, mesh: 0 }];
    return {
      nodes,
      parts: [{ node: 0, name: base, extras: null, material: "Unknown (STL)", color: [0.55, 0.57, 0.62, 1], metal: 0.3, rough: 0.45, pos: P2, nrm: N2 }],
      extras: { product: base, revision: "", maker: "Imported STL" },
    };
  }

  // ── building GPU buffers and per-part geometry facts ─────────────────
  function weld(pos) {
    const key = (i) => `${Math.round(pos[i] * 1e5)},${Math.round(pos[i + 1] * 1e5)},${Math.round(pos[i + 2] * 1e5)}`;
    const map = new Map(), verts = [], index = new Uint32Array(pos.length / 3);
    for (let i = 0; i < pos.length; i += 3) {
      const k = key(i);
      let v = map.get(k);
      if (v === undefined) {
        v = verts.length / 3;
        map.set(k, v);
        verts.push(pos[i], pos[i + 1], pos[i + 2]);
      }
      index[i / 3] = v;
    }
    return { verts: new Float32Array(verts), index };
  }

  function edgesOf(part) {
    const { verts, index } = part.weld;
    const faceN = [];
    const map = new Map();
    for (let t = 0; t < index.length; t += 3) {
      const a = index[t], b = index[t + 1], c = index[t + 2];
      const pa = [verts[a * 3], verts[a * 3 + 1], verts[a * 3 + 2]];
      const n = norm(cross(sub([verts[b * 3], verts[b * 3 + 1], verts[b * 3 + 2]], pa), sub([verts[c * 3], verts[c * 3 + 1], verts[c * 3 + 2]], pa)));
      faceN.push(n);
      for (const [u, v] of [[a, b], [b, c], [c, a]]) {
        if (u === v) continue;
        const k = u < v ? u * 4194304 + v : v * 4194304 + u;
        const e = map.get(k);
        if (e) e.push(t / 3);
        else map.set(k, [t / 3]);
      }
    }
    const feature = [], all = [];
    const cosLimit = Math.cos((35 * Math.PI) / 180);
    for (const [k, faces] of map) {
      const u = Math.floor(k / 4194304), v = k % 4194304;
      all.push(u, v);
      if (faces.length !== 2 || dot(faceN[faces[0]], faceN[faces[1]]) < cosLimit) feature.push(u, v);
    }
    return { feature: new Uint32Array(feature), all: new Uint32Array(all) };
  }

  function volumeOf(pos) {
    let v = 0;
    for (let i = 0; i < pos.length; i += 9) {
      const a = [pos[i], pos[i + 1], pos[i + 2]], b = [pos[i + 3], pos[i + 4], pos[i + 5]], c = [pos[i + 6], pos[i + 7], pos[i + 8]];
      v += dot(a, cross(b, c)) / 6;
    }
    return Math.abs(v);
  }

  function upload(part) {
    const gl = M.gl;
    const inter = new Float32Array(part.pos.length * 2);
    for (let i = 0, n = part.pos.length / 3; i < n; i++) {
      inter.set(part.pos.subarray(i * 3, i * 3 + 3), i * 6);
      inter.set(part.nrm.subarray(i * 3, i * 3 + 3), i * 6 + 3);
    }
    part.vao = gl.createVertexArray();
    gl.bindVertexArray(part.vao);
    part.vbo = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, part.vbo);
    gl.bufferData(gl.ARRAY_BUFFER, inter, gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 24, 0);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 24, 12);
    part.count = part.pos.length / 3;
    // edges index the welded vertices
    part.evao = gl.createVertexArray();
    gl.bindVertexArray(part.evao);
    part.wbo = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, part.wbo);
    const wv = new Float32Array((part.weld.verts.length / 3) * 6);
    for (let i = 0, n = part.weld.verts.length / 3; i < n; i++) wv.set(part.weld.verts.subarray(i * 3, i * 3 + 3), i * 6);
    gl.bufferData(gl.ARRAY_BUFFER, wv, gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 24, 0);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 24, 12);
    part.ebo = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, part.ebo);
    const both = new Uint32Array(part.edges.feature.length + part.edges.all.length);
    both.set(part.edges.feature);
    both.set(part.edges.all, part.edges.feature.length);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, both, gl.STATIC_DRAW);
    gl.bindVertexArray(null);
  }

  function dispose() {
    const gl = M.gl;
    for (const p of M.parts) {
      gl.deleteBuffer(p.vbo);
      gl.deleteBuffer(p.wbo);
      gl.deleteBuffer(p.ebo);
      gl.deleteVertexArray(p.vao);
      gl.deleteVertexArray(p.evao);
    }
    M.parts = [];
  }

  function adopt(model, file, t0) {
    dispose();
    M.nodes = model.nodes;
    M.file = file;
    const lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
    model.parts.forEach((p, i) => {
      p.index = i;
      p.weld = weld(p.pos);
      p.edges = edgesOf(p);
      const plo = [Infinity, Infinity, Infinity], phi = [-Infinity, -Infinity, -Infinity];
      for (let k = 0; k < p.weld.verts.length; k += 3)
        for (let c = 0; c < 3; c++) {
          plo[c] = Math.min(plo[c], p.weld.verts[k + c]);
          phi[c] = Math.max(phi[c], p.weld.verts[k + c]);
        }
      p.lo = plo;
      p.hi = phi;
      p.center = mul(add(plo, phi), 0.5);
      p.volume = volumeOf(p.pos);
      p.offset = [0, 0, 0];
      for (let c = 0; c < 3; c++) {
        lo[c] = Math.min(lo[c], plo[c]);
        hi[c] = Math.max(hi[c], phi[c]);
      }
      upload(p);
    });
    M.parts = model.parts;
    M.bbox = { lo, hi };
    M.center = mul(add(lo, hi), 0.5);
    M.radius = len(sub(hi, lo)) / 2;
    M.hidden = new Set();
    M.isolate = null;
    M.selected = new Set();
    M.pending = [];
    applyExplode();
    fit(false);
    invalidate();
    requestAnimationFrame(() => {
      stats.loadMs = performance.now() - t0;
      send(payload(model, file, stats.loadMs, lo, hi));
    });
  }

  /** What C# hears about a model: the node tree with each part's facts. */
  function payload(model, file, loadMs, lo, hi) {
    const partOf = new Map(model.parts.map((p) => [p.node, p]));
    return {
      type: "loaded", file, loadMs: Math.round(loadMs), info: model.extras ?? {},
      size: [hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]],
      nodes: model.nodes.map((n) => {
        const p = partOf.get(n.id);
        return {
          id: n.id, name: n.name, parent: n.parent, extras: n.extras ?? {},
          part: p ? p.index : -1, volume: p ? p.volume : 0, material: p ? p.material : "",
          tris: p ? p.pos.length / 9 : 0, box: p ? [p.hi[0] - p.lo[0], p.hi[1] - p.lo[1], p.hi[2] - p.lo[2]] : null,
          color: p ? "#" + p.color.slice(0, 3).map((c) => Math.round(linearToSrgb(c) * 255).toString(16).padStart(2, "0")).join("") : "",
        };
      }),
    };
  }

  /** Reads a model's tree and part facts without drawing it (for the bill of materials). */
  async function inspect(url) {
    const t0 = performance.now();
    const r = await fetch(url);
    if (!r.ok) throw new Error(`The model ${url} could not be fetched (${r.status}).`);
    const model = parseGlb(await r.arrayBuffer());
    const lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
    model.parts.forEach((p, i) => {
      p.index = i;
      p.lo = [Infinity, Infinity, Infinity];
      p.hi = [-Infinity, -Infinity, -Infinity];
      for (let k = 0; k < p.pos.length; k += 3)
        for (let c = 0; c < 3; c++) {
          p.lo[c] = Math.min(p.lo[c], p.pos[k + c]);
          p.hi[c] = Math.max(p.hi[c], p.pos[k + c]);
          lo[c] = Math.min(lo[c], p.pos[k + c]);
          hi[c] = Math.max(hi[c], p.pos[k + c]);
        }
      p.volume = volumeOf(p.pos);
    });
    return payload(model, url.split("/").pop(), performance.now() - t0, lo, hi);
  }

  async function loadUrl(url) {
    const t0 = performance.now();
    M.loading = true;
    try {
      const r = await fetch(url);
      if (!r.ok) throw new Error(`The model ${url} could not be fetched (${r.status}).`);
      const buf = await r.arrayBuffer();
      adopt(parseGlb(buf), url.split("/").pop(), t0);
    } finally {
      M.loading = false;
    }
  }

  async function loadFile(file) {
    const t0 = performance.now();
    try {
      const buf = await file.arrayBuffer();
      const lower = file.name.toLowerCase();
      const model = lower.endsWith(".stl") ? parseStl(buf, file.name) : lower.endsWith(".glb") ? parseGlb(buf) : null;
      if (!model) throw new Error(`${file.name} is not a .glb or .stl file.`);
      adopt(model, file.name, t0);
    } catch (e) {
      send({ type: "error", message: e.message });
    }
  }

  // ── camera ───────────────────────────────────────────────────────────
  function camera() {
    const c = M.cam;
    const dir = [Math.cos(c.pitch) * Math.sin(c.yaw), Math.sin(c.pitch), Math.cos(c.pitch) * Math.cos(c.yaw)];
    M.eye = add(c.target, mul(dir, c.dist));
    const up = Math.abs(c.pitch) > 1.55 ? [-Math.sin(c.yaw) * Math.sign(c.pitch), 0, -Math.cos(c.yaw) * Math.sign(c.pitch)] : [0, 1, 0];
    M.view = lookAt(M.eye, c.target, up);
    const aspect = M.w / Math.max(1, M.h);
    const near = Math.max(c.dist - M.radius * 4, c.dist * 0.01), far = c.dist + M.radius * 6;
    if (c.ortho) {
      const hh = c.dist * Math.tan(c.fov / 2);
      M.proj = ortho(hh * aspect, hh, near, far);
    } else M.proj = perspective(c.fov, aspect, near, far);
    M.vp = matMul(M.proj, M.view);
    M.ivp = invert(M.vp);
    if (M.cube) M.cube.style.transform = `translateZ(-40px) rotateX(${(-c.pitch * 180) / Math.PI}deg) rotateY(${(-c.yaw * 180) / Math.PI}deg)`;
  }

  function visibleParts() {
    return M.parts.filter((p) => !M.hidden.has(p.index) && (!M.isolate || M.isolate.has(p.index)));
  }

  function fit(animate = true) {
    const ps = visibleParts().length ? visibleParts() : M.parts;
    if (!ps.length) return;
    const lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
    for (const p of ps)
      for (let c = 0; c < 3; c++) {
        lo[c] = Math.min(lo[c], p.lo[c] + p.offset[c]);
        hi[c] = Math.max(hi[c], p.hi[c] + p.offset[c]);
      }
    const r = len(sub(hi, lo)) / 2;
    // fit the narrower of the two view angles, so a tall phone view still shows it all
    const aspect = M.w && M.h ? M.w / M.h : 1;
    const half = Math.atan(Math.tan(M.cam.fov / 2) * Math.min(1, aspect));
    const to = { target: mul(add(lo, hi), 0.5), dist: (r / Math.sin(half)) * 1.08 };
    move(to, animate);
  }

  const PRESETS = {
    front: [0, 0], back: [Math.PI, 0], right: [Math.PI / 2, 0], left: [-Math.PI / 2, 0],
    top: [0, Math.PI / 2 - 0.0001], bottom: [0, -Math.PI / 2 + 0.0001], iso: [0.62, 0.5],
  };

  function move(to, animate) {
    const from = { ...M.cam, target: [...M.cam.target] };
    if (!animate) {
      Object.assign(M.cam, to);
      invalidate();
      return;
    }
    let dy = (to.yaw ?? from.yaw) - from.yaw;
    while (dy > Math.PI) dy -= 2 * Math.PI;
    while (dy < -Math.PI) dy += 2 * Math.PI;
    const t0 = performance.now();
    M.anim = (now) => {
      const t = clamp((now - t0) / 380, 0, 1), e = 1 - Math.pow(1 - t, 3);
      if (to.target) M.cam.target = add(from.target, mul(sub(to.target, from.target), e));
      if (to.dist) M.cam.dist = from.dist + (to.dist - from.dist) * e;
      if (to.yaw !== undefined) M.cam.yaw = from.yaw + dy * e;
      if (to.pitch !== undefined) M.cam.pitch = from.pitch + (to.pitch - from.pitch) * e;
      if (t >= 1) M.anim = null;
    };
    invalidate();
  }

  // ── explode and section ──────────────────────────────────────────────
  function applyExplode() {
    const f = M.explode;
    for (const p of M.parts) {
      const d = sub(p.center, M.center);
      // spread mostly along the shaft axis (z here) and out from the middle
      const dir = len(d) < 1e-6 ? [0, 1, 0] : norm(d);
      p.offset = mul(dir, f * M.radius * 0.9 * (0.35 + len(d) / M.radius));
    }
  }

  function clipPlane() {
    const s = M.section;
    if (!s.on || !M.bbox) return null;
    const ax = { x: 0, y: 1, z: 2 }[s.axis];
    const at = M.bbox.lo[ax] + (M.bbox.hi[ax] - M.bbox.lo[ax]) * s.offset;
    const n = [0, 0, 0];
    n[ax] = s.flip ? -1 : 1;
    return { n, d: n[ax] * at, ax, at };
  }

  // ── drawing ──────────────────────────────────────────────────────────
  function resize() {
    const r = M.host.getBoundingClientRect();
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = Math.max(1, Math.round(r.width)), h = Math.max(1, Math.round(r.height));
    if (w === M.w && h === M.h && dpr === M.dpr) return;
    M.w = w;
    M.h = h;
    M.dpr = dpr;
    for (const c of [M.canvas, M.overlay]) {
      c.width = Math.round(w * dpr);
      c.height = Math.round(h * dpr);
      c.style.width = w + "px";
      c.style.height = h + "px";
    }
    invalidate();
  }

  function frame(now) {
    M.dirty = false;
    const t0 = performance.now();
    if (M.anim) M.anim(now);
    if (M.inertia && !M.drag) {
      M.cam.yaw -= M.inertia.vx;
      M.cam.pitch = clamp(M.cam.pitch + M.inertia.vy, -1.5707, 1.5707);
      M.inertia.vx *= 0.9;
      M.inertia.vy *= 0.9;
      if (Math.abs(M.inertia.vx) + Math.abs(M.inertia.vy) < 0.0004) M.inertia = null;
    }
    resize();
    camera();
    draw();
    drawOverlay();
    const ms = performance.now() - t0;
    stats.frames.push(ms);
    if (stats.frames.length > 600) stats.frames.shift();
    stats.last = now;
    if (M.anim || M.inertia) invalidate();
  }

  function draw() {
    const gl = M.gl;
    gl.viewport(0, 0, M.canvas.width, M.canvas.height);
    gl.clearColor(0, 0, 0, 1);
    gl.clearStencil(0);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT | gl.STENCIL_BUFFER_BIT);
    // background gradient
    gl.disable(gl.DEPTH_TEST);
    gl.useProgram(P.bg.p);
    gl.bindVertexArray(M.bgVao);
    gl.uniform3fv(P.bg.u.uTop, M.colors.top);
    gl.uniform3fv(P.bg.u.uBottom, M.colors.bottom);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.enable(gl.DEPTH_TEST);
    gl.depthFunc(gl.LEQUAL);
    gl.disable(gl.CULL_FACE);

    const plane = clipPlane();
    const vis = visibleParts();
    M.drawn = vis.map((p) => p.index);
    const xray = M.mode === "xray";
    const wire = M.mode === "wireframe";
    const sel = M.colors.select;

    const setCommon = (prog) => {
      gl.useProgram(prog.p);
      gl.uniformMatrix4fv(prog.u.uVP, false, M.vp);
      if (prog.u.uClip) {
        gl.uniform1i(prog.u.uClipOn, plane ? 1 : 0);
        gl.uniform4f(prog.u.uClip, plane ? plane.n[0] : 0, plane ? plane.n[1] : 0, plane ? plane.n[2] : 0, plane ? plane.d : 0);
      }
    };

    if (!wire) {
      setCommon(P.solid);
      gl.uniform3fv(P.solid.u.uEye, M.eye);
      gl.uniform3fv(P.solid.u.uSky, M.colors.sky.map(linearize));
      gl.uniform3fv(P.solid.u.uGround, M.colors.ground.map(linearize));
      const order = xray ? vis : [...vis].sort((a, b) => a.color[3] - b.color[3]).reverse();
      if (xray) {
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
        gl.depthMask(false);
      }
      for (const p of order) {
        const glass = p.color[3] < 1;
        if (glass && !xray) {
          gl.enable(gl.BLEND);
          gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
          gl.depthMask(false);
        }
        gl.uniform3fv(P.solid.u.uOffset, p.offset);
        gl.uniform4f(P.solid.u.uColor, p.color[0], p.color[1], p.color[2], xray ? (M.selected.has(p.index) ? 0.75 : 0.22) : p.color[3]);
        gl.uniform1f(P.solid.u.uMetal, p.metal);
        gl.uniform1f(P.solid.u.uRough, Math.max(0.08, p.rough));
        const tint = M.selected.has(p.index) ? 0.55 : p.index === M.hover ? 0.18 : 0;
        gl.uniform4f(P.solid.u.uTint, linearize(sel[0]), linearize(sel[1]), linearize(sel[2]), tint);
        gl.bindVertexArray(p.vao);
        gl.drawArrays(gl.TRIANGLES, 0, p.count);
        if (glass && !xray) {
          gl.disable(gl.BLEND);
          gl.depthMask(true);
        }
      }
      if (xray) {
        gl.disable(gl.BLEND);
        gl.depthMask(true);
      }
    }

    // section caps: per part, count the cut's crossings in the stencil, then fill with hatch
    if (plane && !xray && !wire) drawCaps(vis, plane);

    // edges
    if (M.mode === "edges" || wire || xray) {
      setCommon(P.line);
      const e = wire || xray ? M.colors.wire : M.colors.edge;
      for (const p of vis) {
        const selected = M.selected.has(p.index);
        gl.uniform3fv(P.line.u.uOffset, p.offset);
        gl.uniform4f(P.line.u.uColor, selected ? sel[0] : e[0], selected ? sel[1] : e[1], selected ? sel[2] : e[2], wire ? 0.9 : xray ? 0.6 : 0.75);
        gl.bindVertexArray(p.evao);
        if (wire) gl.drawElements(gl.LINES, p.edges.all.length, gl.UNSIGNED_INT, p.edges.feature.length * 4);
        else gl.drawElements(gl.LINES, p.edges.feature.length, gl.UNSIGNED_INT, 0);
      }
    }
    gl.bindVertexArray(null);
  }

  function linearize(c) {
    return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
  }

  function drawCaps(vis, plane) {
    const gl = M.gl;
    const lo = M.bbox.lo, hi = M.bbox.hi, pad = M.radius * 0.6;
    const ax = plane.ax, u = (ax + 1) % 3, v = (ax + 2) % 3;
    const corner = (a, b) => {
      const p = [0, 0, 0];
      p[ax] = plane.at;
      p[u] = a;
      p[v] = b;
      return p;
    };
    const q = [corner(lo[u] - pad, lo[v] - pad), corner(hi[u] + pad, lo[v] - pad), corner(hi[u] + pad, hi[v] + pad), corner(lo[u] - pad, lo[v] - pad), corner(hi[u] + pad, hi[v] + pad), corner(lo[u] - pad, hi[v] + pad)];
    const data = new Float32Array(36);
    q.forEach((p, i) => data.set([...p, ...plane.n], i * 6));
    gl.bindBuffer(gl.ARRAY_BUFFER, capQuad.buf);
    gl.bufferSubData(gl.ARRAY_BUFFER, 0, data);
    gl.enable(gl.STENCIL_TEST);
    const U = [0, 0, 0], V = [0, 0, 0];
    U[u] = 1;
    V[v] = 1;
    vis.forEach((p) => {
      if (p.color[3] < 1) return;
      // 1. stencil: back faces +1, front faces -1 behind the plane -> inside where != 0
      gl.clear(gl.STENCIL_BUFFER_BIT);
      gl.colorMask(false, false, false, false);
      gl.depthMask(false);
      gl.disable(gl.DEPTH_TEST);
      gl.useProgram(P.line.p);
      gl.uniformMatrix4fv(P.line.u.uVP, false, M.vp);
      gl.uniform1i(P.line.u.uClipOn, 1);
      gl.uniform4f(P.line.u.uClip, plane.n[0], plane.n[1], plane.n[2], plane.d);
      gl.uniform3fv(P.line.u.uOffset, p.offset);
      gl.stencilFunc(gl.ALWAYS, 0, 0xff);
      gl.stencilOpSeparate(gl.BACK, gl.KEEP, gl.KEEP, gl.INCR_WRAP);
      gl.stencilOpSeparate(gl.FRONT, gl.KEEP, gl.KEEP, gl.DECR_WRAP);
      gl.bindVertexArray(p.vao);
      gl.drawArrays(gl.TRIANGLES, 0, p.count);
      // 2. the cap where the stencil says "inside"
      gl.colorMask(true, true, true, true);
      gl.depthMask(true);
      gl.enable(gl.DEPTH_TEST);
      gl.stencilFunc(gl.NOTEQUAL, 0, 0xff);
      gl.stencilOp(gl.KEEP, gl.KEEP, gl.KEEP);
      gl.useProgram(P.cap.p);
      gl.uniformMatrix4fv(P.cap.u.uVP, false, M.vp);
      gl.uniform3fv(P.cap.u.uOffset, [0, 0, 0]);
      const selected = M.selected.has(p.index);
      const base = selected ? M.colors.select : p.color.slice(0, 3).map((c, i) => linearToSrgb(c) * 0.35 + M.colors.capBase[i] * 0.65);
      gl.uniform4f(P.cap.u.uColor, base[0], base[1], base[2], 1);
      gl.uniform4f(P.cap.u.uHatch, M.colors.hatch[0], M.colors.hatch[1], M.colors.hatch[2], 1);
      gl.uniform3fv(P.cap.u.uU, U);
      gl.uniform3fv(P.cap.u.uV, V);
      // drafting convention: neighbouring parts hatch the other way, finer for steel
      gl.uniform1f(P.cap.u.uSpacing, p.material.startsWith("Cast") ? 0.0042 : 0.0026);
      gl.uniform1f(P.cap.u.uAngle, p.index % 2 ? Math.PI / 4 : -Math.PI / 4);
      gl.bindVertexArray(capQuad.vao);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
    });
    gl.disable(gl.STENCIL_TEST);
  }

  // ── projection, rays, picking ────────────────────────────────────────
  function project(p) {
    const m = M.vp;
    const x = m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12];
    const y = m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13];
    const w = m[3] * p[0] + m[7] * p[1] + m[11] * p[2] + m[15];
    return { x: ((x / w) * 0.5 + 0.5) * M.w, y: (0.5 - (y / w) * 0.5) * M.h, behind: w <= 0 };
  }
  function ray(px, py) {
    const nx = (px / M.w) * 2 - 1, ny = 1 - (py / M.h) * 2;
    const unproj = (z) => {
      const m = M.ivp;
      const x = m[0] * nx + m[4] * ny + m[8] * z + m[12], y = m[1] * nx + m[5] * ny + m[9] * z + m[13], zz = m[2] * nx + m[6] * ny + m[10] * z + m[14], w = m[3] * nx + m[7] * ny + m[11] * z + m[15];
      return [x / w, y / w, zz / w];
    };
    const a = unproj(-1), b = unproj(1);
    return { o: a, d: norm(sub(b, a)) };
  }
  function hitBox(r, lo, hi) {
    let t0 = -Infinity, t1 = Infinity;
    for (let c = 0; c < 3; c++) {
      if (Math.abs(r.d[c]) < 1e-12) {
        if (r.o[c] < lo[c] || r.o[c] > hi[c]) return false;
        continue;
      }
      let a = (lo[c] - r.o[c]) / r.d[c], b = (hi[c] - r.o[c]) / r.d[c];
      if (a > b) [a, b] = [b, a];
      t0 = Math.max(t0, a);
      t1 = Math.min(t1, b);
    }
    return t1 >= Math.max(t0, 0);
  }
  /** The nearest visible, unclipped surface under a screen point. */
  function hit(px, py) {
    const r = ray(px, py);
    const plane = clipPlane();
    let best = null;
    for (const p of visibleParts()) {
      const o = p.offset;
      if (!hitBox(r, add(p.lo, o), add(p.hi, o))) continue;
      const P0 = p.pos;
      for (let i = 0; i < P0.length; i += 9) {
        const a = [P0[i] + o[0], P0[i + 1] + o[1], P0[i + 2] + o[2]];
        const e1 = [P0[i + 3] + o[0] - a[0], P0[i + 4] + o[1] - a[1], P0[i + 5] + o[2] - a[2]];
        const e2 = [P0[i + 6] + o[0] - a[0], P0[i + 7] + o[1] - a[1], P0[i + 8] + o[2] - a[2]];
        const h = cross(r.d, e2), det = dot(e1, h);
        if (Math.abs(det) < 1e-14) continue;
        const f = 1 / det, s = sub(r.o, a), u = f * dot(s, h);
        if (u < 0 || u > 1) continue;
        const q = cross(s, e1), v = f * dot(r.d, q);
        if (v < 0 || u + v > 1) continue;
        const t = f * dot(e2, q);
        if (t <= 0 || (best && t >= best.t)) continue;
        const point = add(r.o, mul(r.d, t));
        if (plane && dot(point, plane.n) > plane.d) continue;
        best = { t, part: p.index, point, tri: i / 9 };
      }
    }
    // a click on a section cap picks the cut part, at the plane
    if (plane) {
      const denom = dot(r.d, plane.n);
      if (Math.abs(denom) > 1e-9) {
        const t = (plane.d - dot(r.o, plane.n)) / denom;
        if (t > 0 && (!best || t < best.t)) {
          const point = add(r.o, mul(r.d, t));
          const inside = insideOf(point);
          if (inside >= 0) best = { t, part: inside, point, cap: true };
        }
      }
    }
    return best;
  }
  /** Which visible part contains a point (odd crossings along +x), or -1. */
  function insideOf(pt) {
    for (const p of visibleParts()) {
      const o = p.offset;
      const lo = add(p.lo, o), hi = add(p.hi, o);
      if (pt[0] < lo[0] || pt[0] > hi[0] || pt[1] < lo[1] || pt[1] > hi[1] || pt[2] < lo[2] || pt[2] > hi[2]) continue;
      let n = 0;
      const P0 = p.pos;
      for (let i = 0; i < P0.length; i += 9) {
        const ay = P0[i + 1] + o[1], az = P0[i + 2] + o[2], by = P0[i + 4] + o[1], bz = P0[i + 5] + o[2], cy = P0[i + 7] + o[1], cz = P0[i + 8] + o[2];
        // project to the yz plane and test the point's ray along +x
        const d = (by - ay) * (cz - az) - (bz - az) * (cy - ay);
        if (Math.abs(d) < 1e-16) continue;
        const l1 = ((by - pt[1]) * (cz - pt[2]) - (bz - pt[2]) * (cy - pt[1])) / d;
        const l2 = ((cy - pt[1]) * (az - pt[2]) - (cz - pt[2]) * (ay - pt[1])) / d;
        const l3 = 1 - l1 - l2;
        if (l1 < 0 || l2 < 0 || l3 < 0) continue;
        const x = l1 * (P0[i] + o[0]) + l2 * (P0[i + 3] + o[0]) + l3 * (P0[i + 6] + o[0]);
        if (x > pt[0]) n++;
      }
      if (n % 2 === 1) return p.index;
    }
    return -1;
  }

  /** Snap a hit to the nearest vertex (and, for edges, the nearest feature edge) on screen. */
  function snapAt(px, py, wantEdge) {
    const h = hit(px, py);
    if (!h) return null;
    const p = M.parts[h.part];
    const o = p.offset, V = p.weld.verts;
    // of the corners within 14 px on screen, the one nearest the surface that was hit, so a
    // corner hidden behind the visible one (same pixel, deeper) never wins
    let best = null, bw = Infinity;
    for (let k = 0; k < V.length; k += 3) {
      const w = [V[k] + o[0], V[k + 1] + o[1], V[k + 2] + o[2]];
      const s = project(w);
      if ((s.x - px) ** 2 + (s.y - py) ** 2 > 14 * 14) continue;
      const d = len(sub(w, h.point));
      if (d < bw) {
        bw = d;
        best = w;
      }
    }
    const out = { part: h.part, point: best ?? h.point, vertex: !!best };
    if (wantEdge) {
      const E = p.edges.feature;
      let be = null, bed = 10 * 10;
      for (let k = 0; k < E.length; k += 2) {
        const a = [V[E[k] * 3] + o[0], V[E[k] * 3 + 1] + o[1], V[E[k] * 3 + 2] + o[2]], b = [V[E[k + 1] * 3] + o[0], V[E[k + 1] * 3 + 1] + o[1], V[E[k + 1] * 3 + 2] + o[2]];
        const sa = project(a), sb = project(b);
        const dx = sb.x - sa.x, dy = sb.y - sa.y, l2 = dx * dx + dy * dy || 1;
        const t = clamp(((px - sa.x) * dx + (py - sa.y) * dy) / l2, 0, 1);
        const d = (sa.x + dx * t - px) ** 2 + (sa.y + dy * t - py) ** 2;
        if (d < bed) {
          bed = d;
          be = [a, b];
        }
      }
      if (be) out.edge = chainEdge(p, be);
    }
    return out;
  }

  /** Grow a picked edge segment along collinear neighbours, so a straight edge measures whole. */
  function chainEdge(p, seg) {
    const o = p.offset, V = p.weld.verts, E = p.edges.feature;
    let [a, b] = seg;
    const dir = norm(sub(b, a));
    const grow = (end, sign) => {
      for (let guard = 0; guard < 200; guard++) {
        let next = null;
        for (let k = 0; k < E.length; k += 2) {
          for (const [i, j] of [[E[k], E[k + 1]], [E[k + 1], E[k]]]) {
            const s = [V[i * 3] + o[0], V[i * 3 + 1] + o[1], V[i * 3 + 2] + o[2]];
            if (len(sub(s, end)) > 1e-7) continue;
            const t = [V[j * 3] + o[0], V[j * 3 + 1] + o[1], V[j * 3 + 2] + o[2]];
            const d = sub(t, end);
            if (len(d) < 1e-9) continue;
            if (dot(norm(d), mul(dir, sign)) > 0.9999) next = t;
          }
        }
        if (!next) return end;
        end = next;
      }
      return end;
    };
    return [grow(a, -1), grow(b, 1)];
  }

  // ── tools: select, measure, note ─────────────────────────────────────
  function onTap(px, py, ev) {
    if (M.tool === "select") {
      const h = hit(px, py);
      send({ type: "pick", part: h ? h.part : -1, additive: ev.shiftKey || ev.ctrlKey || ev.metaKey });
      return;
    }
    if (M.tool === "note") {
      const h = hit(px, py);
      if (h) send({ type: "notePlace", point: h.point.map((c) => +c.toFixed(6)), part: h.part });
      return;
    }
    if (M.tool === "measure-edge") {
      const s = snapAt(px, py, true);
      if (s?.edge) {
        const [a, b] = s.edge;
        send({ type: "measure", kind: "edge", pts: [a, b], value: len(sub(b, a)) * 1000, part: s.part });
      }
      return;
    }
    const s = snapAt(px, py, false);
    if (!s) return;
    M.pending.push(s.point);
    const need = M.tool === "measure-angle" ? 3 : 2;
    if (M.pending.length >= need) {
      const pts = M.pending.slice(0, need);
      M.pending = [];
      if (need === 2) send({ type: "measure", kind: "distance", pts, value: len(sub(pts[1], pts[0])) * 1000 });
      else {
        const u = norm(sub(pts[0], pts[1])), v = norm(sub(pts[2], pts[1]));
        send({ type: "measure", kind: "angle", pts, value: (Math.acos(clamp(dot(u, v), -1, 1)) * 180) / Math.PI });
      }
    } else send({ type: "measureStep", count: M.pending.length, need });
    invalidate();
  }

  // ── overlay: dimensions, pins, snap marker ───────────────────────────
  function drawOverlay() {
    const c = M.ctx2d;
    c.setTransform(M.dpr, 0, 0, M.dpr, 0, 0);
    c.clearRect(0, 0, M.w, M.h);
    const C = M.colors;
    for (const m of M.measures) drawMeasure(c, m, C);
    for (const p of M.pending) marker(c, project(p), C.dim, 5);
    if (M.snap && M.tool.startsWith("measure")) {
      const s = project(M.snap.point);
      c.strokeStyle = C.dim;
      c.lineWidth = 1.5;
      if (M.snap.vertex) c.strokeRect(s.x - 5, s.y - 5, 10, 10);
      else {
        c.beginPath();
        c.arc(s.x, s.y, 4, 0, Math.PI * 2);
        c.stroke();
      }
      if (M.snap.edge) {
        const a = project(M.snap.edge[0]), b = project(M.snap.edge[1]);
        c.strokeStyle = C.pin;
        c.lineWidth = 3;
        c.beginPath();
        c.moveTo(a.x, a.y);
        c.lineTo(b.x, b.y);
        c.stroke();
      }
    }
    const now = performance.now();
    const recheck = !M.drag && now - (M.occAt ?? 0) > 180;
    if (recheck) M.occAt = now;
    else if (M.notes.length && !M.occTimer) M.occTimer = setTimeout(() => { M.occTimer = null; invalidate(); }, 220);
    let occChanged = false;
    M.notes.forEach((n) => {
      const p = M.parts[n.part];
      const w = p ? add(n.point, p.offset) : n.point;
      const s = project(w);
      if (s.behind) return;
      if (recheck) {
        const h = hit(s.x, s.y);
        const was = n._hidden;
        n._hidden = !!h && len(sub(h.point, M.eye)) < len(sub(w, M.eye)) - M.radius * 0.02;
        if (was !== n._hidden) occChanged = true;
      }
      c.globalAlpha = n._hidden && n.id !== M.selectedNote ? 0.35 : 1;
      const on = n.id === M.selectedNote;
      c.strokeStyle = C.dim;
      c.lineWidth = 1.25;
      c.beginPath();
      c.moveTo(s.x, s.y);
      c.lineTo(s.x + 16, s.y - 22);
      c.stroke();
      c.fillStyle = C.dim;
      c.beginPath();
      c.arc(s.x, s.y, 2.5, 0, Math.PI * 2);
      c.fill();
      const r = on ? 13 : 11;
      c.fillStyle = on ? C.dim : C.pin;
      c.beginPath();
      c.arc(s.x + 16 + r * 0.6, s.y - 22 - r * 0.6, r, 0, Math.PI * 2);
      c.fill();
      c.strokeStyle = C.pinInk;
      c.stroke();
      c.fillStyle = on ? C.dimInk : C.pinInk;
      c.font = `600 ${on ? 12 : 11}px ${C.mono}`;
      c.textAlign = "center";
      c.textBaseline = "middle";
      c.fillText(String(n.n), s.x + 16 + r * 0.6, s.y - 22 - r * 0.6 + 0.5);
      n._screen = { x: s.x + 16 + r * 0.6, y: s.y - 22 - r * 0.6, r };
      c.globalAlpha = 1;
    });
    if (occChanged) invalidate();
  }

  function marker(c, s, color, r) {
    c.strokeStyle = color;
    c.lineWidth = 1.5;
    c.beginPath();
    c.moveTo(s.x - r, s.y);
    c.lineTo(s.x + r, s.y);
    c.moveTo(s.x, s.y - r);
    c.lineTo(s.x, s.y + r);
    c.stroke();
  }

  function arrow(c, from, to, size) {
    const a = Math.atan2(to.y - from.y, to.x - from.x);
    c.beginPath();
    c.moveTo(to.x, to.y);
    c.lineTo(to.x - size * Math.cos(a - 0.28), to.y - size * Math.sin(a - 0.28));
    c.lineTo(to.x - size * Math.cos(a + 0.28), to.y - size * Math.sin(a + 0.28));
    c.closePath();
    c.fill();
  }

  /** A drafting dimension: extension lines, a dimension line with filled arrows, and the value in a frame. */
  function drawMeasure(c, m, C) {
    const pts = m.pts.map(project);
    if (pts.some((p) => p.behind)) return;
    c.strokeStyle = C.dim;
    c.fillStyle = C.dim;
    c.lineWidth = 1.2;
    let label = m.label, at;
    if (m.kind === "angle") {
      const [a, o, b] = pts;
      const r = 34;
      const a0 = Math.atan2(a.y - o.y, a.x - o.x), a1 = Math.atan2(b.y - o.y, b.x - o.x);
      let d = a1 - a0;
      while (d > Math.PI) d -= 2 * Math.PI;
      while (d < -Math.PI) d += 2 * Math.PI;
      c.beginPath();
      c.moveTo(a.x, a.y);
      c.lineTo(o.x, o.y);
      c.lineTo(b.x, b.y);
      c.stroke();
      c.beginPath();
      c.arc(o.x, o.y, r, a0, a0 + d, d < 0);
      c.stroke();
      const mid = a0 + d / 2;
      at = { x: o.x + Math.cos(mid) * (r + 24), y: o.y + Math.sin(mid) * (r + 24) };
    } else {
      const [a, b] = pts;
      let nx = -(b.y - a.y), ny = b.x - a.x;
      const l = Math.hypot(nx, ny) || 1;
      nx /= l;
      ny /= l;
      if (ny > 0) {
        nx = -nx;
        ny = -ny;
      }
      const off = m.kind === "edge" ? 16 : 26;
      const A = { x: a.x + nx * off, y: a.y + ny * off }, B = { x: b.x + nx * off, y: b.y + ny * off };
      c.globalAlpha = 0.7;
      c.beginPath();
      c.moveTo(a.x + nx * 3, a.y + ny * 3);
      c.lineTo(A.x + nx * 5, A.y + ny * 5);
      c.moveTo(b.x + nx * 3, b.y + ny * 3);
      c.lineTo(B.x + nx * 5, B.y + ny * 5);
      c.stroke();
      c.globalAlpha = 1;
      c.beginPath();
      c.moveTo(A.x, A.y);
      c.lineTo(B.x, B.y);
      c.stroke();
      arrow(c, B, A, 8);
      arrow(c, A, B, 8);
      for (const p of [a, b]) {
        c.beginPath();
        c.arc(p.x, p.y, 2.2, 0, Math.PI * 2);
        c.fill();
      }
      at = { x: (A.x + B.x) / 2 + nx * 14, y: (A.y + B.y) / 2 + ny * 14 };
    }
    // the value in a feature-control-style frame: [symbol | value]
    c.font = `500 12px ${C.mono}`;
    const sym = m.kind === "angle" ? "∠" : m.kind === "edge" ? "⟷" : "↔";
    const tw = c.measureText(label).width, sw = 18, h = 20, w = tw + sw + 14;
    const x = at.x - w / 2, y = at.y - h / 2;
    c.fillStyle = C.dimInk;
    c.fillRect(x, y, w, h);
    c.strokeStyle = C.dim;
    c.lineWidth = 1.2;
    c.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1);
    c.beginPath();
    c.moveTo(x + sw + 0.5, y);
    c.lineTo(x + sw + 0.5, y + h);
    c.stroke();
    c.fillStyle = C.dim;
    c.textAlign = "center";
    c.textBaseline = "middle";
    c.fillText(sym, x + sw / 2 + 0.5, y + h / 2 + 0.5);
    c.textAlign = "left";
    c.fillText(label, x + sw + 7, y + h / 2 + 0.5);
  }

  // ── input ────────────────────────────────────────────────────────────
  function local(ev) {
    const r = M.canvas.getBoundingClientRect();
    return [ev.clientX - r.left, ev.clientY - r.top];
  }
  function onDown(ev) {
    if (ev.target.closest?.("[data-ui]")) return;
    M.host.focus({ preventScroll: true });
    M.canvas.setPointerCapture?.(ev.pointerId);
    const [x, y] = local(ev);
    M.pointers.set(ev.pointerId, { x, y });
    M.inertia = null;
    M.anim = null;
    if (M.pointers.size === 2) {
      const [p, q] = [...M.pointers.values()];
      M.drag = { pinch: Math.hypot(p.x - q.x, p.y - q.y), cx: (p.x + q.x) / 2, cy: (p.y + q.y) / 2, moved: true };
      return;
    }
    const pan = ev.button === 1 || ev.button === 2 || ev.shiftKey;
    const section = M.tool === "section" && M.section.on && ev.button === 0;
    M.drag = { x, y, x0: x, y0: y, pan, section, moved: false, t: performance.now(), vx: 0, vy: 0, start: M.section.offset };
  }
  function onMove(ev) {
    const [x, y] = local(ev);
    if (M.pointers.has(ev.pointerId)) M.pointers.set(ev.pointerId, { x, y });
    const d = M.drag;
    if (!d) {
      if (M.tool.startsWith("measure") && M.parts.length) {
        M.snap = snapAt(x, y, M.tool === "measure-edge");
        invalidate();
      } else if (M.tool === "select" && M.parts.length && !M.hoverQueued) {
        M.hoverQueued = true;
        requestAnimationFrame(() => {
          M.hoverQueued = false;
          const h = hit(x, y);
          const hv = h ? h.part : -1;
          if (hv !== M.hover) {
            M.hover = hv;
            M.canvas.style.cursor = hv >= 0 ? "pointer" : "";
            invalidate();
          }
        });
      }
      return;
    }
    if (d.pinch !== undefined && M.pointers.size === 2) {
      const [p, q] = [...M.pointers.values()];
      const dist = Math.hypot(p.x - q.x, p.y - q.y);
      M.cam.dist *= d.pinch / Math.max(dist, 1);
      d.pinch = dist;
      invalidate();
      return;
    }
    const dx = x - d.x, dy = y - d.y;
    if (Math.abs(x - d.x0) + Math.abs(y - d.y0) > 4) d.moved = true;
    d.x = x;
    d.y = y;
    if (!d.moved) return;
    if (d.section) {
      const total = (y - d.y0) / M.h;
      M.section.offset = clamp(d.start - total, 0, 1);
      send({ type: "section", offset: M.section.offset });
      invalidate();
      return;
    }
    if (d.pan) {
      const per = (2 * M.cam.dist * Math.tan(M.cam.fov / 2)) / M.h;
      const right = [M.view[0], M.view[4], M.view[8]], up = [M.view[1], M.view[5], M.view[9]];
      M.cam.target = add(M.cam.target, add(mul(right, -dx * per), mul(up, dy * per)));
    } else {
      d.vx = dx * 0.008;
      d.vy = dy * 0.008;
      M.cam.yaw -= d.vx;
      M.cam.pitch = clamp(M.cam.pitch + d.vy, -1.5707, 1.5707);
    }
    invalidate();
  }
  function onUp(ev) {
    const d = M.drag;
    M.pointers.delete(ev.pointerId);
    if (M.pointers.size > 0) return;
    M.drag = null;
    if (!d) return;
    if (!d.moved && ev.button === 0 && d.pinch === undefined) {
      const [x, y] = local(ev);
      onTap(x, y, ev);
    } else if (d.moved && !d.pan && !d.section && d.pinch === undefined && performance.now() - d.t > 0) {
      if (Math.abs(d.vx) + Math.abs(d.vy) > 0.002) M.inertia = { vx: d.vx, vy: d.vy };
      invalidate();
    }
    if (d.section) send({ type: "sectionEnd", offset: M.section.offset });
  }
  function onWheel(ev) {
    ev.preventDefault();
    const [x, y] = local(ev);
    const k = Math.exp(ev.deltaY * 0.0012);
    // zoom towards the point under the cursor
    const h = hit(x, y);
    const toward = h ? h.point : M.cam.target;
    M.cam.target = add(toward, mul(sub(M.cam.target, toward), k));
    M.cam.dist = clamp(M.cam.dist * k, M.radius * 0.05, M.radius * 40);
    M.inertia = null;
    invalidate();
  }
  function onKey(ev) {
    if (ev.target.closest?.("input, textarea, select")) return;
    if (ev.key === "Escape" && M.pending.length) {
      M.pending = [];
      send({ type: "measureStep", count: 0, need: 0 });
      invalidate();
    }
  }

  // ── file input and drop ──────────────────────────────────────────────
  function picker() {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = ".glb,.stl,model/gltf-binary,model/stl";
    input.style.display = "none";
    input.addEventListener("change", () => {
      const f = input.files?.[0];
      if (f) loadFile(f);
      input.remove();
    });
    document.body.appendChild(input);
    input.click();
  }

  // ── ops ──────────────────────────────────────────────────────────────
  ops["tk.attach"] = ({ host, cube, id }) => {
    M.host = elementOf(host);
    M.cube = cube ? elementOf(cube) : null;
    M.cb = id;
    M.w = M.h = 0; // a new canvas: size it on the first frame
    M.canvas = document.createElement("canvas");
    M.canvas.className = "tk-gl";
    M.overlay = document.createElement("canvas");
    M.overlay.className = "tk-overlay";
    M.host.prepend(M.overlay);
    M.host.prepend(M.canvas);
    M.ctx2d = M.overlay.getContext("2d");
    initGl();
    readColors();
    M.theme = new MutationObserver(readColors);
    M.theme.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "class"] });
    M.media = matchMedia("(prefers-color-scheme: dark)");
    M.media.addEventListener("change", readColors);
    M.ro = new ResizeObserver(() => invalidate());
    M.ro.observe(M.host);
    M.canvas.addEventListener("pointerdown", onDown);
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    window.addEventListener("pointercancel", onUp);
    M.canvas.addEventListener("wheel", onWheel, { passive: false });
    M.canvas.addEventListener("contextmenu", (e) => e.preventDefault());
    M.host.addEventListener("keydown", onKey);
    M.host.addEventListener("dragover", (e) => {
      e.preventDefault();
      M.host.classList.add("tk-dropping");
    });
    M.host.addEventListener("dragleave", () => M.host.classList.remove("tk-dropping"));
    M.host.addEventListener("drop", (e) => {
      e.preventDefault();
      M.host.classList.remove("tk-dropping");
      const f = e.dataTransfer?.files?.[0];
      if (f) loadFile(f);
    });
    invalidate();
    return addHandle(M);
  };
  ops["tk.detach"] = () => {
    M.ro?.disconnect();
    M.theme?.disconnect();
    M.media?.removeEventListener("change", readColors);
    window.removeEventListener("pointermove", onMove);
    window.removeEventListener("pointerup", onUp);
    window.removeEventListener("pointercancel", onUp);
    if (M.gl) dispose();
    M.canvas?.remove();
    M.overlay?.remove();
    M.gl = null;
    M.cb = 0;
  };
  ops["tk.load"] = async ({ url }) => {
    try {
      await loadUrl(url);
      return true;
    } catch (e) {
      send({ type: "error", message: e.message });
      return false;
    }
  };
  ops["tk.open"] = () => picker();
  ops["tk.inspect"] = async ({ url }) => inspect(url);
  ops["tk.state"] = (s) => {
    M.hidden = new Set(s.hidden ?? []);
    M.isolate = s.isolate ? new Set(s.isolate) : null;
    M.selected = new Set(s.selected ?? []);
    M.mode = s.mode ?? "shaded";
    if (s.explode !== undefined && s.explode !== M.explode) {
      M.explode = s.explode;
      applyExplode();
    }
    if (s.section) M.section = { ...M.section, ...s.section };
    if (s.ortho !== undefined) M.cam.ortho = s.ortho;
    invalidate();
  };
  ops["tk.tool"] = ({ tool }) => {
    M.tool = tool;
    M.pending = [];
    M.snap = null;
    M.hover = -1;
    M.canvas.style.cursor = tool === "select" ? "" : tool === "section" ? "ns-resize" : "crosshair";
    M.host.dataset.tool = tool;
    invalidate();
  };
  ops["tk.view"] = ({ preset }) => {
    if (preset === "fit") return fit(true);
    const v = PRESETS[preset];
    if (v) move({ yaw: v[0], pitch: v[1] }, true);
  };
  ops["tk.fit"] = () => fit(true);
  ops["tk.getCamera"] = () => ({ target: M.cam.target, dist: M.cam.dist, yaw: M.cam.yaw, pitch: M.cam.pitch, ortho: M.cam.ortho });
  ops["tk.setCamera"] = ({ cam, animate = true }) => {
    M.cam.ortho = !!cam.ortho;
    move({ target: cam.target, dist: cam.dist, yaw: cam.yaw, pitch: cam.pitch }, animate);
  };
  ops["tk.measures"] = ({ list }) => {
    M.measures = list;
    invalidate();
  };
  ops["tk.notes"] = ({ list, selected }) => {
    M.notes = list;
    M.selectedNote = selected ?? null;
    invalidate();
  };
  ops["tk.focusPart"] = ({ part }) => {
    const p = M.parts[part];
    if (!p) return;
    const r = len(sub(p.hi, p.lo)) / 2;
    move({ target: add(p.center, p.offset), dist: Math.max(r / Math.sin(M.cam.fov / 2), M.radius * 0.5) * 1.3 }, true);
  };
  ops["tk.download"] = ({ name, text, mime }) => {
    const url = URL.createObjectURL(new Blob([text], { type: mime || "text/plain" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 4000);
  };
  ops["tk.screenshot"] = async ({ name }) => {
    // compose the 3D view and the overlay into one PNG
    frame(performance.now());
    const out = document.createElement("canvas");
    out.width = M.canvas.width;
    out.height = M.canvas.height;
    const c = out.getContext("2d");
    c.drawImage(M.canvas, 0, 0);
    c.drawImage(M.overlay, 0, 0);
    const blob = await new Promise((res) => out.toBlob(res, "image/png"));
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 4000);
    return { width: out.width, height: out.height, bytes: blob.size };
  };

  // hooks for the e2e script and for the curious
  globalThis.__tk = {
    M, stats, project: (p) => project(p),
    partScreen: (i) => {
      const p = M.parts[i];
      return p ? project(add(p.center, p.offset)) : null;
    },
    hitAt: (x, y) => {
      const r = M.canvas.getBoundingClientRect();
      const h = hit(x - r.left, y - r.top);
      return h ? { part: h.part, name: M.parts[h.part].name, point: h.point, cap: !!h.cap } : null;
    },
    toScreen: (p) => {
      const r = M.canvas.getBoundingClientRect();
      const s = project(p);
      return { x: r.left + s.x, y: r.top + s.y };
    },
    drawn: () => M.drawn.map((i) => M.parts[i].name),
    pixel: (x, y) => {
      const gl = M.gl, r = M.canvas.getBoundingClientRect();
      const px = new Uint8Array(4);
      gl.readPixels(Math.round((x - r.left) * M.dpr), Math.round(M.canvas.height - (y - r.top) * M.dpr), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px);
      return [...px];
    },
    redraw: () => frame(performance.now()),
  };
}
