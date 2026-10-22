// Kamar Gelap's pixel engine. C# owns the document (layers, adjustments,
// selection, history order) and sends it here as JSON; this module owns the
// pixels: a 2D canvas per layer and mask, a WebGL2 compositor that blends the
// stack with the layer's mode and mask and runs adjustment layers as shaders,
// filters as shader passes, brushes and strokes in the pointer handler, and
// copy-on-write snapshots for the history. Per-pixel work never crosses into
// C#. Embedded in the app's assembly; Rustzor's bundler ships it.
export function install({ ops, dispatch, addHandle, getHandle, elementOf }) {
  const stats = (globalThis.__kgStats = { strokeFrames: [], composites: [], filters: [], last: null });
  const keep = (list, v) => {
    list.push(v);
    if (list.length > 400) list.shift();
  };

  // ── state ─────────────────────────────────────────────────────────────
  const E = {
    w: 0,
    h: 0,
    meta: { layers: [], active: null, sel: null },
    layers: new Map(), // id -> { canvas, mask, shared, maskShared, ver, maskVer }
    history: [], // index -> Map id -> { canvas, mask }
    tool: { tool: "brush", size: 40, hardness: 0.7, opacity: 1, flow: 1, color: "#000000", color2: "#ffffff", tolerance: 32, target: "pixels", mode: "linear" },
    view: { zoom: 1, x: 0, y: 0 },
    stage: null,
    sheet: null,
    glHost: null,
    cb: 0,
    cloneSrc: null,
  };
  globalThis.__kg = E;

  const mk = (w, h) => {
    const c = document.createElement("canvas");
    c.width = w;
    c.height = h;
    return c;
  };
  const ctx2d = (c) => c.getContext("2d", { willReadFrequently: false });
  const clone = (c) => {
    const n = mk(c.width, c.height);
    ctx2d(n).drawImage(c, 0, 0);
    return n;
  };
  const layerMeta = (id) => E.meta.layers.find((l) => l.id === id);
  const send = (m) => E.cb && dispatch(E.cb, m);

  /** The layer's pixels, made private to the live document before they change. */
  function own(id, which = "canvas") {
    const L = E.layers.get(id);
    if (!L) throw new Error("no layer " + id);
    if (which === "mask") {
      if (L.maskShared) {
        L.mask = clone(L.mask);
        L.maskShared = false;
      }
      L.maskVer++;
      return L.mask;
    }
    if (L.shared) {
      L.canvas = clone(L.canvas);
      L.shared = false;
    }
    L.ver++;
    return L.canvas;
  }

  function newLayer(id, fill) {
    const c = mk(E.w, E.h);
    if (fill) {
      const g = ctx2d(c);
      g.fillStyle = fill;
      g.fillRect(0, 0, E.w, E.h);
    }
    E.layers.set(id, { canvas: c, mask: null, shared: false, maskShared: false, ver: 1, maskVer: 0 });
    return c;
  }

  // ── selection as a path in document space ────────────────────────────
  function selPath(sel, dx = 0, dy = 0) {
    if (!sel) return null;
    const p = new Path2D();
    if (sel.inv) p.rect(-dx - 1, -dy - 1, E.w + 2, E.h + 2);
    if (sel.kind === "rect") p.rect(sel.x - dx, sel.y - dy, sel.w, sel.h);
    else if (sel.kind === "ellipse") p.ellipse(sel.x + sel.w / 2 - dx, sel.y + sel.h / 2 - dy, Math.abs(sel.w / 2), Math.abs(sel.h / 2), 0, 0, Math.PI * 2);
    else if (sel.kind === "lasso" && sel.pts && sel.pts.length >= 6) {
      p.moveTo(sel.pts[0] - dx, sel.pts[1] - dy);
      for (let i = 2; i < sel.pts.length; i += 2) p.lineTo(sel.pts[i] - dx, sel.pts[i + 1] - dy);
      p.closePath();
    }
    return p;
  }
  const clipSel = (g, dx, dy) => {
    const p = selPath(E.meta.sel, dx, dy);
    if (p) g.clip(p, "evenodd");
  };

  // ── WebGL2 compositor ─────────────────────────────────────────────────
  let gl = null;
  let canvas = null;
  const prog = {};
  const fbo = {}; // name -> { fb, tex }
  const tex = new Map(); // layer id -> { t, ver, m, mver }
  let cacheKey = null;
  let pending = null; // region to render, or "all"
  let raf = 0;

  const VS = `#version 300 es
in vec2 p; void main(){ gl_Position = vec4(p, 0.0, 1.0); }`;
  const HEAD = `#version 300 es
precision highp float; uniform vec2 u_size; out vec4 o;`;
  const BLEND = `${HEAD}
uniform sampler2D u_base, u_src, u_mask; uniform vec2 u_off; uniform float u_opacity; uniform int u_mode; uniform int u_hasMask;
vec3 ov(vec3 b, vec3 s){ return mix(2.0*b*s, 1.0-2.0*(1.0-b)*(1.0-s), step(0.5, b)); }
vec3 soft(vec3 b, vec3 s){ vec3 d = mix(((16.0*b-12.0)*b+4.0)*b, sqrt(b), step(0.25, b)); return mix(b-(1.0-2.0*s)*b*(1.0-b), b+(2.0*s-1.0)*(d-b), step(0.5, s)); }
vec3 blend(vec3 b, vec3 s){
  if (u_mode == 1) return b*s;
  if (u_mode == 2) return b+s-b*s;
  if (u_mode == 3) return ov(b, s);
  if (u_mode == 4) return min(b, s);
  if (u_mode == 5) return max(b, s);
  if (u_mode == 6) return soft(b, s);
  if (u_mode == 7) return abs(b-s);
  return s;
}
void main(){
  vec2 px = gl_FragCoord.xy;
  vec4 base = texture(u_base, px/u_size);
  vec2 q = (px - u_off)/u_size;
  vec4 src = (q.x < 0.0 || q.y < 0.0 || q.x > 1.0 || q.y > 1.0) ? vec4(0.0) : texture(u_src, q);
  float m = u_hasMask == 1 ? texture(u_mask, q).r : 1.0;
  float as = src.a*u_opacity*m, ab = base.a;
  vec3 co = as*(1.0-ab)*src.rgb + as*ab*blend(base.rgb, src.rgb) + (1.0-as)*ab*base.rgb;
  float ao = as + ab*(1.0-as);
  o = vec4(ao > 0.0 ? co/ao : vec3(0.0), ao);
}`;
  const ADJUST = `${HEAD}
uniform sampler2D u_base, u_mask; uniform float u_opacity; uniform int u_kind; uniform int u_hasMask; uniform float u_p[6];
vec3 rgb2hsl(vec3 c){ float mx=max(c.r,max(c.g,c.b)), mn=min(c.r,min(c.g,c.b)); float l=(mx+mn)*0.5; if(mx==mn) return vec3(0.0,0.0,l);
  float d=mx-mn; float s=l>0.5? d/(2.0-mx-mn) : d/(mx+mn); float h;
  if(mx==c.r) h=(c.g-c.b)/d+(c.g<c.b?6.0:0.0); else if(mx==c.g) h=(c.b-c.r)/d+2.0; else h=(c.r-c.g)/d+4.0; return vec3(h/6.0,s,l); }
float hue2(float p,float q,float t){ t=fract(t); if(t<1.0/6.0) return p+(q-p)*6.0*t; if(t<0.5) return q; if(t<2.0/3.0) return p+(q-p)*(2.0/3.0-t)*6.0; return p; }
vec3 hsl2rgb(vec3 h){ if(h.y==0.0) return vec3(h.z); float q=h.z<0.5? h.z*(1.0+h.y) : h.z+h.y-h.z*h.y; float p=2.0*h.z-q;
  return vec3(hue2(p,q,h.x+1.0/3.0), hue2(p,q,h.x), hue2(p,q,h.x-1.0/3.0)); }
vec3 adjust(vec3 c){
  if (u_kind == 0) { // brightness, contrast
    c += u_p[0]/200.0;
    float k = pow(2.0, u_p[1]/50.0);
    return clamp((c-0.5)*k+0.5, 0.0, 1.0);
  }
  if (u_kind == 1) { // levels: in black, in white, gamma, out black, out white
    vec3 v = clamp((c*255.0-u_p[0])/max(u_p[1]-u_p[0], 1.0), 0.0, 1.0);
    v = pow(v, vec3(1.0/max(u_p[2], 0.01)));
    return (u_p[3] + v*(u_p[4]-u_p[3]))/255.0;
  }
  if (u_kind == 2) { // hue, saturation, lightness
    vec3 h = rgb2hsl(c);
    h.x = fract(h.x + u_p[0]/360.0);
    float s = u_p[1]/100.0;
    h.y = clamp(s > 0.0 ? h.y + (1.0-h.y)*s*h.y*1.5 : h.y*(1.0+s), 0.0, 1.0);
    vec3 r = hsl2rgb(h);
    float l = u_p[2]/100.0;
    return l > 0.0 ? mix(r, vec3(1.0), l) : r*(1.0+l);
  }
  // black and white: channel weights in percent, then a tint
  float g = clamp((c.r*u_p[0] + c.g*u_p[1] + c.b*u_p[2])/100.0, 0.0, 1.0);
  vec3 tint = hsl2rgb(vec3(u_p[3]/360.0, 0.55, 0.5));
  return mix(vec3(g), clamp(vec3(g) + (tint-0.5)*0.6*(1.0-abs(2.0*g-1.0)), 0.0, 1.0), u_p[4]/100.0);
}
void main(){
  vec2 uv = gl_FragCoord.xy/u_size;
  vec4 base = texture(u_base, uv);
  float m = u_hasMask == 1 ? texture(u_mask, uv).r : 1.0;
  o = vec4(mix(base.rgb, adjust(base.rgb), u_opacity*m), base.a);
}`;
  const COPY = `${HEAD}
uniform sampler2D u_src; uniform int u_flip; uniform vec2 u_srcSize;
void main(){ vec2 uv = gl_FragCoord.xy/u_size; if (u_flip == 1) uv.y = 1.0-uv.y; o = texture(u_src, uv); }`;
  // Separable Gaussian on premultiplied colour: pass 0 premultiplies, pass 1 divides back.
  const BLUR = `${HEAD}
uniform sampler2D u_src; uniform vec2 u_dir; uniform float u_sigma; uniform int u_pre; uniform int u_post;
vec4 at(vec2 uv){ vec4 c = texture(u_src, uv); return u_pre == 1 ? vec4(c.rgb*c.a, c.a) : c; }
void main(){
  vec2 uv = gl_FragCoord.xy/u_size;
  int r = int(ceil(u_sigma*3.0));
  vec4 sum = at(uv); float wsum = 1.0;
  for (int i = 1; i <= 300; i++) {
    if (i > r) break;
    float w = exp(-float(i*i)/(2.0*u_sigma*u_sigma));
    vec2 d = u_dir*float(i)/u_size;
    sum += (at(uv+d) + at(uv-d))*w; wsum += 2.0*w;
  }
  vec4 c = sum/wsum;
  o = u_post == 1 ? vec4(c.a > 0.0 ? c.rgb/c.a : vec3(0.0), c.a) : c;
}`;
  const SHARPEN = `${HEAD}
uniform sampler2D u_src, u_blur; uniform float u_amount;
void main(){ vec2 uv = gl_FragCoord.xy/u_size; vec4 a = texture(u_src, uv), b = texture(u_blur, uv);
  o = vec4(clamp(a.rgb + (a.rgb-b.rgb)*u_amount, 0.0, 1.0), a.a); }`;
  const NOISE = `${HEAD}
uniform sampler2D u_src; uniform float u_amount; uniform float u_seed; uniform int u_mono;
float hash(vec2 p){ vec3 q = fract(vec3(p.xyx)*0.1031 + u_seed); q += dot(q, q.yzx+33.33); return fract((q.x+q.y)*q.z); }
float gauss(vec2 p){ return (hash(p)+hash(p+17.3)+hash(p+41.7)-1.5)*1.15; }
void main(){ vec2 uv = gl_FragCoord.xy/u_size; vec4 a = texture(u_src, uv); vec2 p = gl_FragCoord.xy;
  vec3 n = u_mono == 1 ? vec3(gauss(p)) : vec3(gauss(p), gauss(p+101.0), gauss(p+211.0));
  o = vec4(clamp(a.rgb + n*u_amount/100.0*0.5, 0.0, 1.0), a.a); }`;

  function compile(fs) {
    const mkS = (type, src) => {
      const s = gl.createShader(type);
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
      return s;
    };
    const p = gl.createProgram();
    gl.attachShader(p, mkS(gl.VERTEX_SHADER, VS));
    gl.attachShader(p, mkS(gl.FRAGMENT_SHADER, fs));
    gl.bindAttribLocation(p, 0, "p");
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
    const u = {};
    const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < n; i++) {
      const name = gl.getActiveUniform(p, i).name.replace(/\[0\]$/, "");
      u[name] = gl.getUniformLocation(p, gl.getActiveUniform(p, i).name);
    }
    return { p, u };
  }

  function initGl() {
    canvas = document.createElement("canvas");
    canvas.className = "kg-gl-canvas";
    gl = canvas.getContext("webgl2", { premultipliedAlpha: false, preserveDrawingBuffer: true, antialias: false, alpha: true });
    if (!gl) throw new Error("This browser has no WebGL2, which Kamar Gelap needs to draw the picture.");
    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
    prog.blend = compile(BLEND);
    prog.adjust = compile(ADJUST);
    prog.copy = compile(COPY);
    prog.blur = compile(BLUR);
    prog.sharpen = compile(SHARPEN);
    prog.noise = compile(NOISE);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
    gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.NONE);
    E.renderer = (() => {
      const d = gl.getExtension("WEBGL_debug_renderer_info");
      return d ? gl.getParameter(d.UNMASKED_RENDERER_WEBGL) : "WebGL2";
    })();
  }

  function newTex(w, h, linear = false) {
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RGBA8, w, h);
    const f = linear ? gl.LINEAR : gl.NEAREST;
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, f);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, f);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return t;
  }
  function newFbo(w, h, linear = false) {
    const t = newTex(w, h, linear);
    const fb = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t, 0);
    return { fb, tex: t, w, h };
  }
  function dropFbo(f) {
    if (!f) return;
    gl.deleteFramebuffer(f.fb);
    gl.deleteTexture(f.tex);
  }

  function resize(w, h) {
    E.w = w;
    E.h = h;
    canvas.width = w;
    canvas.height = h;
    for (const k of ["a", "b", "cache", "result", "f1", "f2", "f3"]) dropFbo(fbo[k]);
    fbo.a = newFbo(w, h);
    fbo.b = newFbo(w, h);
    fbo.cache = newFbo(w, h);
    fbo.result = newFbo(w, h, true);
    fbo.f1 = fbo.f2 = fbo.f3 = null;
    for (const t of tex.values()) {
      gl.deleteTexture(t.t);
      if (t.m) gl.deleteTexture(t.m);
    }
    tex.clear();
    cacheKey = null;
    layoutSheet();
  }

  /** Brings a layer's textures up to date; a region uploads only that part. */
  function upload(id, region) {
    const L = E.layers.get(id);
    let T = tex.get(id);
    if (!T) {
      T = { t: newTex(E.w, E.h), ver: -1, m: null, mver: -1 };
      tex.set(id, T);
    }
    gl.bindTexture(gl.TEXTURE_2D, T.t);
    if (T.ver !== L.ver) {
      if (region && T.ver === L.ver - 1 && !region.full) subUpload(L.canvas, region);
      else gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, L.canvas);
      T.ver = L.ver;
    }
    if (L.mask) {
      if (!T.m) T.m = newTex(E.w, E.h);
      gl.bindTexture(gl.TEXTURE_2D, T.m);
      if (T.mver !== L.maskVer) {
        if (region && T.mver === L.maskVer - 1 && !region.full) subUpload(L.mask, region);
        else gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, L.mask);
        T.mver = L.maskVer;
      }
    }
    return T;
  }
  function subUpload(src, r) {
    gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, r.x);
    gl.pixelStorei(gl.UNPACK_SKIP_ROWS, r.y);
    gl.texSubImage2D(gl.TEXTURE_2D, 0, r.x, r.y, r.w, r.h, gl.RGBA, gl.UNSIGNED_BYTE, src);
    gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, 0);
    gl.pixelStorei(gl.UNPACK_SKIP_ROWS, 0);
  }

  function bindTex(unit, t) {
    gl.activeTexture(gl.TEXTURE0 + unit);
    gl.bindTexture(gl.TEXTURE_2D, t);
  }
  function draw(target, P, set) {
    gl.bindFramebuffer(gl.FRAMEBUFFER, target ? target.fb : null);
    gl.viewport(0, 0, target ? target.w : canvas.width, target ? target.h : canvas.height);
    gl.useProgram(P.p);
    gl.uniform2f(P.u.u_size, target ? target.w : canvas.width, target ? target.h : canvas.height);
    set(P.u);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }
  function copyTo(target, srcTex, flip = false) {
    draw(target, prog.copy, (u) => {
      bindTex(0, srcTex);
      gl.uniform1i(u.u_src, 0);
      gl.uniform1i(u.u_flip, flip ? 1 : 0);
    });
  }

  const BLENDS = { normal: 0, multiply: 1, screen: 2, overlay: 3, darken: 4, lighten: 5, "soft-light": 6, difference: 7 };
  const ADJ = { "brightness-contrast": 0, levels: 1, "hue-saturation": 2, "black-white": 3 };

  /** One layer onto the base: a blend for pixels and text, a shader for adjustments. */
  function pass(l, from, to, region) {
    const L = E.layers.get(l.id);
    if (l.kind === "adjust") {
      const T = L && L.mask ? upload(l.id, region) : null;
      draw(to, prog.adjust, (u) => {
        bindTex(0, from.tex);
        gl.uniform1i(u.u_base, 0);
        bindTex(1, T && T.m ? T.m : from.tex);
        gl.uniform1i(u.u_mask, 1);
        gl.uniform1i(u.u_hasMask, T && T.m && l.maskOn ? 1 : 0);
        gl.uniform1f(u.u_opacity, l.opacity / 100);
        gl.uniform1i(u.u_kind, ADJ[l.adjust] ?? 0);
        gl.uniform1fv(u.u_p, new Float32Array((l.p || []).concat([0, 0, 0, 0, 0, 0]).slice(0, 6)));
      });
      return;
    }
    if (!L) return;
    const T = upload(l.id, region);
    draw(to, prog.blend, (u) => {
      bindTex(0, from.tex);
      gl.uniform1i(u.u_base, 0);
      bindTex(1, T.t);
      gl.uniform1i(u.u_src, 1);
      bindTex(2, T.m || T.t);
      gl.uniform1i(u.u_mask, 2);
      gl.uniform1i(u.u_hasMask, T.m && l.maskOn ? 1 : 0);
      gl.uniform2f(u.u_off, l.x || 0, l.y || 0);
      gl.uniform1f(u.u_opacity, l.opacity / 100);
      gl.uniform1i(u.u_mode, BLENDS[l.blend] ?? 0);
    });
  }

  function clear(f) {
    gl.bindFramebuffer(gl.FRAMEBUFFER, f.fb);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
  }

  /** Composites the stack. The layers under the active one are cached, so a brush stroke redraws only from there up, and only where it painted. */
  function render() {
    raf = 0;
    if (!gl || !E.w) return;
    const t0 = performance.now();
    const region = pending === "all" || !pending ? null : pending;
    pending = null;
    const all = E.meta.layers;
    let k = all.findIndex((l) => l.id === E.meta.active);
    if (k < 0) k = all.length;
    const below = all.slice(0, k).filter((l) => l.visible);
    const key = JSON.stringify(below) + "|" + below.map((l) => (E.layers.get(l.id)?.ver ?? 0) + "." + (E.layers.get(l.id)?.maskVer ?? 0)).join(",");
    gl.disable(gl.SCISSOR_TEST);
    if (key !== cacheKey) {
      clear(fbo.a);
      let from = fbo.a;
      let to = fbo.b;
      for (const l of below) {
        pass(l, from, to, null);
        [from, to] = [to, from];
      }
      copyTo(fbo.cache, from.tex);
      cacheKey = key;
    }
    if (region) {
      gl.enable(gl.SCISSOR_TEST);
      gl.scissor(region.x, region.y, region.w, region.h);
    }
    const above = all.slice(k).filter((l) => l.visible);
    let from = fbo.cache;
    let to = fbo.a;
    for (const l of above) {
      pass(l, from, to, region);
      from = to;
      to = to === fbo.a ? fbo.b : fbo.a;
    }
    copyTo(fbo.result, from.tex);
    if (region) gl.scissor(region.x, E.h - region.y - region.h, region.w, region.h);
    copyTo(null, fbo.result.tex, true);
    gl.disable(gl.SCISSOR_TEST);
    // For measuring: wait for the GPU, so the time is the whole composite.
    if (E.sync) gpuWait(fbo.result);
    keep(stats.composites, performance.now() - t0);
    scheduleHistogram();
  }
  function invalidate(region) {
    if (!region || pending === "all") pending = "all";
    else if (!pending) pending = { ...region };
    else {
      const x = Math.min(pending.x, region.x);
      const y = Math.min(pending.y, region.y);
      pending = { x, y, w: Math.max(pending.x + pending.w, region.x + region.w) - x, h: Math.max(pending.y + pending.h, region.y + region.h) - y };
    }
    if (!raf) raf = requestAnimationFrame(render);
  }
  // gl.finish() returns at once in Chrome; reading one pixel back waits for the GPU.
  const one = new Uint8Array(4);
  function gpuWait(f) {
    gl.bindFramebuffer(gl.FRAMEBUFFER, f.fb);
    gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, one);
  }
  function renderNow2(region) {
    if (raf) cancelAnimationFrame(raf);
    raf = 0;
    if (region && pending !== "all") pending = pending || region;
    render();
  }
  function renderNow() {
    if (raf) cancelAnimationFrame(raf);
    pending = "all";
    render();
  }
  function readResult(x, y, w, h) {
    const out = new Uint8Array(w * h * 4);
    gl.bindFramebuffer(gl.FRAMEBUFFER, fbo.result.fb);
    gl.readPixels(x, y, w, h, gl.RGBA, gl.UNSIGNED_BYTE, out);
    return out;
  }
  /** The composite as a 2D canvas (for export, thumbnails and the eyedropper). */
  function resultCanvas() {
    renderNow();
    const px = readResult(0, 0, E.w, E.h);
    const c = mk(E.w, E.h);
    ctx2d(c).putImageData(new ImageData(new Uint8ClampedArray(px.buffer), E.w, E.h), 0, 0);
    return c;
  }

  // ── histogram, at rest ────────────────────────────────────────────────
  let histTimer = 0;
  function scheduleHistogram() {
    clearTimeout(histTimer);
    histTimer = setTimeout(histogram, 220);
  }
  function histogram() {
    if (!gl || !E.w) return;
    const w = 256;
    const h = Math.max(1, Math.round((256 * E.h) / E.w));
    if (!fbo.hist || fbo.hist.h !== h) {
      dropFbo(fbo.hist);
      fbo.hist = newFbo(w, h);
    }
    copyTo(fbo.hist, fbo.result.tex);
    const px = new Uint8Array(w * h * 4);
    gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, px);
    const bins = 128;
    const r = new Array(bins).fill(0);
    const g = new Array(bins).fill(0);
    const b = new Array(bins).fill(0);
    const l = new Array(bins).fill(0);
    for (let i = 0; i < px.length; i += 4) {
      if (px[i + 3] < 8) continue;
      r[px[i] >> 1]++;
      g[px[i + 1] >> 1]++;
      b[px[i + 2] >> 1]++;
      l[Math.min(255, (px[i] * 0.299 + px[i + 1] * 0.587 + px[i + 2] * 0.114) | 0) >> 1]++;
    }
    send({ t: "hist", r, g, b, l });
  }

  // ── view: the sheet is scaled and moved with a CSS transform ──────────
  function layoutSheet() {
    if (!E.sheet) return;
    E.sheet.style.width = E.w + "px";
    E.sheet.style.height = E.h + "px";
    E.sheet.style.transform = `translate(${E.view.x}px, ${E.view.y}px) scale(${E.view.zoom})`;
    E.stage.style.setProperty("--kg-zoom", E.view.zoom);
  }
  function setView(zoom, x, y, quiet) {
    E.view.zoom = Math.min(32, Math.max(0.02, zoom));
    E.view.x = x;
    E.view.y = y;
    layoutSheet();
    if (!quiet) send({ t: "view", zoom: E.view.zoom });
  }
  function fit() {
    const r = E.stage.getBoundingClientRect();
    const pad = r.width < 600 ? 28 : 72;
    const z = Math.min((r.width - pad * 2) / E.w, (r.height - pad * 2) / E.h, 1);
    setView(z, (r.width - E.w * z) / 2, (r.height - E.h * z) / 2);
  }
  function zoomAt(z, cx, cy) {
    const v = E.view;
    const nz = Math.min(32, Math.max(0.02, z));
    setView(nz, cx - ((cx - v.x) * nz) / v.zoom, cy - ((cy - v.y) * nz) / v.zoom);
  }
  const toDoc = (e) => {
    const r = E.stage.getBoundingClientRect();
    return { x: (e.clientX - r.left - E.view.x) / E.view.zoom, y: (e.clientY - r.top - E.view.y) / E.view.zoom };
  };

  // ── brushes ───────────────────────────────────────────────────────────
  let tipCache = null;
  function tip(size, hardness, color) {
    const key = size + "|" + hardness + "|" + color;
    if (tipCache && tipCache.key === key) return tipCache.c;
    const s = Math.max(1, Math.ceil(size));
    const c = mk(s, s);
    const g = ctx2d(c);
    const grad = g.createRadialGradient(s / 2, s / 2, 0, s / 2, s / 2, s / 2);
    const h = Math.min(0.99, Math.max(0, hardness));
    grad.addColorStop(0, color);
    grad.addColorStop(h, color);
    grad.addColorStop(1, hexA(color, 0));
    g.fillStyle = grad;
    g.fillRect(0, 0, s, s);
    tipCache = { key, c };
    return c;
  }
  function hexA(hex, a) {
    const n = parseInt(hex.slice(1), 16);
    return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${a})`;
  }
  const gray = (hex) => {
    const n = parseInt(hex.slice(1), 16);
    const v = Math.round(((n >> 16) & 255) * 0.299 + ((n >> 8) & 255) * 0.587 + (n & 255) * 0.114);
    const s = v.toString(16).padStart(2, "0");
    return "#" + s + s + s;
  };

  let stroke = null;
  function beginStroke(p, e) {
    const T = E.tool;
    const id = E.meta.active;
    const m = layerMeta(id);
    if (!m) return false;
    if (!m.visible) {
      send({ t: "warn", msg: "This layer is hidden. Show it to paint on it." });
      return false;
    }
    if (m.locked) {
      send({ t: "warn", msg: "This layer is locked." });
      return false;
    }
    const onMask = (T.target === "mask" || m.kind === "adjust") && E.layers.get(id)?.mask;
    if (m.kind === "adjust" && !onMask) {
      send({ t: "warn", msg: "An adjustment layer has no pixels. Paint on its mask, or pick a pixel layer." });
      return false;
    }
    if (m.kind === "text" && !onMask) {
      send({ t: "warn", msg: "Text layers stay editable. Rasterize the layer to paint on it." });
      return false;
    }
    if (T.tool === "clone" && !E.cloneSrc) {
      send({ t: "warn", msg: "Alt-click to choose where the clone stamp copies from." });
      return false;
    }
    const L = E.layers.get(id);
    const which = onMask ? "mask" : "canvas";
    const before = which === "mask" ? L.mask : L.canvas;
    const wasShared = which === "mask" ? L.maskShared : L.shared;
    const target = own(id, which);
    stroke = {
      id,
      which,
      target,
      before: wasShared ? before : clone(before),
      buf: mk(E.w, E.h),
      ox: m.x || 0,
      oy: m.y || 0,
      last: null,
      dirty: null,
      t0: performance.now(),
      color: onMask ? (T.tool === "eraser" ? "#ffffff" : gray(T.color)) : T.color,
      erase: T.tool === "eraser" && !onMask,
      gradient: T.tool === "gradient" ? { a: p, b: p } : null,
      clone: T.tool === "clone" ? { dx: E.cloneSrc.x - p.x, dy: E.cloneSrc.y - p.y } : null,
    };
    if (stroke.gradient) drawGradient();
    else stamp(p, e.pressure);
    frame();
    return true;
  }
  const pressureOf = (e) => (e.pointerType === "pen" && e.pressure > 0 ? e.pressure : 1);
  function stamp(p, pr = 1) {
    const s = stroke;
    const T = E.tool;
    const g = ctx2d(s.buf);
    const pts = [];
    if (!s.last) pts.push(p);
    else {
      const dx = p.x - s.last.x;
      const dy = p.y - s.last.y;
      const d = Math.hypot(dx, dy);
      const step = Math.max(1, T.size * 0.12);
      if (d < step) return;
      for (let t = step; t <= d; t += step) pts.push({ x: s.last.x + (dx * t) / d, y: s.last.y + (dy * t) / d });
    }
    const size = T.size * (pr || 1);
    g.globalAlpha = T.flow;
    const half = size / 2;
    for (const q of pts) {
      const x = q.x - s.ox - half;
      const y = q.y - s.oy - half;
      if (s.clone) {
        const patch = mk(Math.ceil(size), Math.ceil(size));
        const pg = ctx2d(patch);
        pg.drawImage(s.before, q.x + s.clone.dx - s.ox - half, q.y + s.clone.dy - s.oy - half, size, size, 0, 0, size, size);
        pg.globalCompositeOperation = "destination-in";
        pg.drawImage(tip(size, T.hardness, "#000000"), 0, 0, size, size);
        g.drawImage(patch, x, y);
      } else g.drawImage(tip(T.size, T.hardness, s.color), x, y, size, size);
      grow(x, y, size, size);
    }
    s.last = pts.length ? pts[pts.length - 1] : p;
  }
  function grow(x, y, w, h) {
    const s = stroke;
    const x0 = Math.max(0, Math.floor(x) - 1);
    const y0 = Math.max(0, Math.floor(y) - 1);
    const x1 = Math.min(E.w, Math.ceil(x + w) + 1);
    const y1 = Math.min(E.h, Math.ceil(y + h) + 1);
    if (x1 <= x0 || y1 <= y0) return;
    if (!s.dirty) s.dirty = { x: x0, y: y0, x1, y1 };
    else {
      s.dirty.x = Math.min(s.dirty.x, x0);
      s.dirty.y = Math.min(s.dirty.y, y0);
      s.dirty.x1 = Math.max(s.dirty.x1, x1);
      s.dirty.y1 = Math.max(s.dirty.y1, y1);
    }
  }
  function drawGradient() {
    const s = stroke;
    const g = ctx2d(s.buf);
    g.clearRect(0, 0, E.w, E.h);
    const { a, b } = s.gradient;
    const T = E.tool;
    const grad =
      T.mode === "radial"
        ? g.createRadialGradient(a.x - s.ox, a.y - s.oy, 0, a.x - s.ox, a.y - s.oy, Math.max(1, Math.hypot(b.x - a.x, b.y - a.y)))
        : g.createLinearGradient(a.x - s.ox, a.y - s.oy, b.x - s.ox, b.y - s.oy);
    const c2 = T.mode === "fade" ? hexA(s.color, 0) : s.which === "mask" ? gray(T.color2) : T.color2;
    grad.addColorStop(0, s.color);
    grad.addColorStop(1, c2);
    g.fillStyle = grad;
    g.fillRect(0, 0, E.w, E.h);
    s.dirty = { x: 0, y: 0, x1: E.w, y1: E.h, full: true };
  }
  /** Puts the stroke so far onto the layer, in the region it touched since the last frame. */
  function frame() {
    const s = stroke;
    if (!s || !s.dirty) return;
    const t0 = performance.now();
    const r = { x: s.dirty.x, y: s.dirty.y, w: s.dirty.x1 - s.dirty.x, h: s.dirty.y1 - s.dirty.y, full: !!s.dirty.full };
    s.dirty = null;
    const g = ctx2d(s.target);
    g.save();
    g.beginPath();
    g.rect(r.x, r.y, r.w, r.h);
    g.clip();
    clipSel(g, s.ox, s.oy);
    g.clearRect(r.x, r.y, r.w, r.h);
    g.drawImage(s.before, r.x, r.y, r.w, r.h, r.x, r.y, r.w, r.h);
    g.globalAlpha = E.tool.opacity;
    g.globalCompositeOperation = s.erase ? "destination-out" : "source-over";
    g.drawImage(s.buf, r.x, r.y, r.w, r.h, r.x, r.y, r.w, r.h);
    g.restore();
    // The texture is one version behind, so only this region uploads.
    const L = E.layers.get(s.id);
    const T = tex.get(s.id);
    if (T) {
      if (s.which === "mask") {
        if (T.mver === L.maskVer - 1 || T.mver === L.maskVer) T.mver = L.maskVer - 1;
      } else if (T.ver === L.ver - 1 || T.ver === L.ver) T.ver = L.ver - 1;
    }
    upload(s.id, r);
    const ox = s.ox;
    const oy = s.oy;
    const dr = clipRect({ x: r.x + ox, y: r.y + oy, w: r.w, h: r.h });
    if (dr) invalidate(dr);
    if (E.sync) {
      // For measuring: composite now and wait for the GPU, so this is the whole frame.
      renderNow2(dr);
    }
    stats.last = performance.now() - t0;
    keep(stats.strokeFrames, stats.last);
  }
  const clipRect = (r) => {
    const x = Math.max(0, Math.floor(r.x));
    const y = Math.max(0, Math.floor(r.y));
    const x1 = Math.min(E.w, Math.ceil(r.x + r.w));
    const y1 = Math.min(E.h, Math.ceil(r.y + r.h));
    return x1 > x && y1 > y ? { x, y, w: x1 - x, h: y1 - y } : null;
  };
  function endStroke() {
    const s = stroke;
    if (!s) return;
    frame();
    stroke = null;
    const label = { brush: "Brush", eraser: "Eraser", clone: "Clone stamp", gradient: "Gradient" }[E.tool.tool] || "Paint";
    send({ t: "stroke", label: s.which === "mask" ? label + " on mask" : label, ms: Math.round(performance.now() - s.t0) });
  }

  // ── pointer and keys on the stage ─────────────────────────────────────
  const PAINT = new Set(["brush", "eraser", "clone", "gradient"]);
  let spaceDown = false;
  function attach(stageEl, sheetEl, glEl, id) {
    E.stage = stageEl;
    E.sheet = sheetEl;
    E.glHost = glEl;
    E.cb = id;
    if (!gl) initGl();
    glEl.appendChild(canvas);
    if (E.w) layoutSheet();
    const pointers = new Map();
    let pan = null;
    let pinch = null;
    let raf2 = 0;
    let moveQ = null;
    const flush = () => {
      raf2 = 0;
      if (!moveQ) return;
      const { p, e } = moveQ;
      moveQ = null;
      if (stroke) {
        if (stroke.gradient) {
          stroke.gradient.b = e.shiftKey ? snap45(stroke.gradient.a, p) : p;
          drawGradient();
        } else stamp(p, pressureOf(e));
        frame();
      } else send({ t: "p", ph: "move", x: p.x, y: p.y, s: e.shiftKey, a: e.altKey, c: e.ctrlKey || e.metaKey });
    };
    const down = (e) => {
      if (e.target.closest("[data-ui]")) return;
      e.preventDefault();
      stageEl.focus({ preventScroll: true });
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      try {
        stageEl.setPointerCapture(e.pointerId);
      } catch {}
      if (pointers.size === 2) {
        if (stroke) endStroke();
        send({ t: "p", ph: "cancel" });
        const [a, b] = [...pointers.values()];
        pinch = { d: Math.hypot(a.x - b.x, a.y - b.y), z: E.view.zoom, vx: E.view.x, vy: E.view.y, cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2 };
        return;
      }
      if (pointers.size > 2) return;
      const tool = E.tool.tool;
      if (e.button === 1 || spaceDown || tool === "hand") {
        pan = { x: e.clientX, y: e.clientY, vx: E.view.x, vy: E.view.y };
        stageEl.classList.add("panning");
        return;
      }
      if (e.button !== 0) return;
      const p = toDoc(e);
      if (tool === "clone" && e.altKey) {
        E.cloneSrc = p;
        send({ t: "cloneSrc", x: Math.round(p.x), y: Math.round(p.y) });
        return;
      }
      if (tool === "eyedropper") {
        pick(p, e.altKey);
        return;
      }
      if (PAINT.has(tool)) {
        beginStroke(p, e);
        return;
      }
      send({ t: "p", ph: "down", x: p.x, y: p.y, s: e.shiftKey, a: e.altKey, c: e.ctrlKey || e.metaKey });
    };
    const move = (e) => {
      if (E.posEl) {
        const p = toDoc(e);
        const inside = p.x >= 0 && p.y >= 0 && p.x < E.w && p.y < E.h;
        E.posEl.textContent = inside ? Math.floor(p.x) + ", " + Math.floor(p.y) : "";
      }
      if (!pointers.has(e.pointerId)) {
        if (E.tool.tool !== "hand") updateCursor(e);
        return;
      }
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      updateCursor(e);
      if (pinch && pointers.size === 2) {
        const [a, b] = [...pointers.values()];
        const d = Math.hypot(a.x - b.x, a.y - b.y);
        const r = stageEl.getBoundingClientRect();
        const cx = (a.x + b.x) / 2 - r.left;
        const cy = (a.y + b.y) / 2 - r.top;
        const z = Math.min(32, Math.max(0.02, (pinch.z * d) / pinch.d));
        const ocx = pinch.cx - r.left;
        const ocy = pinch.cy - r.top;
        setView(z, cx - ((ocx - pinch.vx) * z) / pinch.z, cy - ((ocy - pinch.vy) * z) / pinch.z);
        return;
      }
      if (pan) {
        setView(E.view.zoom, pan.vx + e.clientX - pan.x, pan.vy + e.clientY - pan.y, true);
        return;
      }
      if (E.tool.tool === "eyedropper" && e.buttons) {
        pick(toDoc(e), e.altKey);
        return;
      }
      moveQ = { p: toDoc(e), e };
      if (!raf2) raf2 = requestAnimationFrame(flush);
    };
    const up = (e) => {
      if (!pointers.has(e.pointerId)) return;
      pointers.delete(e.pointerId);
      if (pinch) {
        if (pointers.size === 0) {
          pinch = null;
          send({ t: "view", zoom: E.view.zoom });
        }
        return;
      }
      if (pan) {
        pan = null;
        stageEl.classList.remove("panning");
        send({ t: "view", zoom: E.view.zoom });
        return;
      }
      if (raf2) {
        cancelAnimationFrame(raf2);
        flush();
      }
      if (stroke) {
        endStroke();
        return;
      }
      const p = toDoc(e);
      send({ t: "p", ph: e.type === "pointercancel" ? "cancel" : "up", x: p.x, y: p.y, s: e.shiftKey, a: e.altKey, c: e.ctrlKey || e.metaKey });
    };
    const wheel = (e) => {
      if (e.target.closest("[data-ui]")) return;
      e.preventDefault();
      const r = stageEl.getBoundingClientRect();
      if (e.ctrlKey || e.metaKey || e.altKey) zoomAt(E.view.zoom * Math.exp(-e.deltaY * 0.0022), e.clientX - r.left, e.clientY - r.top);
      else setView(E.view.zoom, E.view.x - e.deltaX, E.view.y - e.deltaY, true);
      clearTimeout(wheel.t);
      wheel.t = setTimeout(() => send({ t: "view", zoom: E.view.zoom }), 120);
    };
    const key = (e) => {
      if (e.target.closest && e.target.closest("input, textarea, select, [contenteditable]")) return;
      if (e.code === "Space") {
        if (!spaceDown) stageEl.classList.add("space");
        spaceDown = true;
        e.preventDefault();
        return;
      }
      const ctrl = e.ctrlKey || e.metaKey;
      if (ctrl && (e.key === "0" || e.key === "1" || e.key === "=" || e.key === "+" || e.key === "-")) {
        e.preventDefault();
        const r = stageEl.getBoundingClientRect();
        if (e.key === "0") fit();
        else if (e.key === "1") zoomAt(1, r.width / 2, r.height / 2);
        else zoomAt(E.view.zoom * (e.key === "-" ? 1 / 1.25 : 1.25), r.width / 2, r.height / 2);
        return;
      }
      if (ctrl && ["z", "y", "s", "o", "d", "a", "i", "j", "e"].includes(e.key.toLowerCase())) e.preventDefault();
      if (e.key === "Backspace" || e.key === "Delete" || (e.key.startsWith("F") && e.key.length <= 3) || e.key === "Enter" || e.key === "Escape") e.preventDefault();
      send({ t: "key", k: e.key, c: e.code, ctrl, s: e.shiftKey, a: e.altKey });
    };
    const keyUp = (e) => {
      if (e.code === "Space") {
        spaceDown = false;
        stageEl.classList.remove("space");
      }
    };
    let cursorRaf = 0;
    let lastEv = null;
    const updateCursor = (e) => {
      lastEv = e;
      if (!cursorRaf) cursorRaf = requestAnimationFrame(() => {
        cursorRaf = 0;
        brushCursor(lastEv);
      });
    };
    stageEl.addEventListener("pointerdown", down);
    stageEl.addEventListener("pointermove", move);
    stageEl.addEventListener("pointerup", up);
    stageEl.addEventListener("pointercancel", up);
    stageEl.addEventListener("pointerleave", () => {
      if (E.cursorEl) E.cursorEl.style.display = "none";
      if (E.posEl) E.posEl.textContent = "";
    });
    stageEl.addEventListener("wheel", wheel, { passive: false });
    window.addEventListener("keydown", key);
    window.addEventListener("keyup", keyUp);
    const ro = new ResizeObserver(() => {
      if (!E.fitted && E.w) {
        E.fitted = true;
        fit();
      }
    });
    ro.observe(stageEl);
    return addHandle(() => {
      window.removeEventListener("keydown", key);
      window.removeEventListener("keyup", keyUp);
      ro.disconnect();
      E.cb = 0;
      E.stage = E.sheet = null;
      E.fitted = false;
    });
  }
  const snap45 = (a, p) => {
    const ang = Math.round(Math.atan2(p.y - a.y, p.x - a.x) / (Math.PI / 4)) * (Math.PI / 4);
    const d = Math.hypot(p.x - a.x, p.y - a.y);
    return { x: a.x + Math.cos(ang) * d, y: a.y + Math.sin(ang) * d };
  };
  function brushCursor(e) {
    const c = E.cursorEl;
    if (!c || !e) return;
    const tool = E.tool.tool;
    if (!["brush", "eraser", "clone"].includes(tool) || spaceDown) {
      c.style.display = "none";
      return;
    }
    const r = E.stage.getBoundingClientRect();
    const s = Math.max(4, E.tool.size * E.view.zoom);
    c.style.display = "block";
    c.style.width = c.style.height = s + "px";
    c.style.transform = `translate(${e.clientX - r.left - s / 2}px, ${e.clientY - r.top - s / 2}px)`;
  }
  function pick(p, alt) {
    const x = Math.floor(p.x);
    const y = Math.floor(p.y);
    if (x < 0 || y < 0 || x >= E.w || y >= E.h) return;
    renderNow();
    const px = readResult(x, y, 1, 1);
    const hex = "#" + [px[0], px[1], px[2]].map((v) => v.toString(16).padStart(2, "0")).join("");
    send({ t: "picked", color: hex, alt });
  }

  // ── seed picture: Merapi at dawn, drawn here so nothing is downloaded ─
  function rng(seed) {
    let s = seed >>> 0;
    return () => {
      s = (s * 1664525 + 1013904223) >>> 0;
      return s / 4294967296;
    };
  }
  function paintMerapi(c) {
    const g = ctx2d(c);
    const W = c.width;
    const H = c.height;
    const R = rng(1994);
    // Sky: indigo above, a band of apricot over the ridge.
    const sky = g.createLinearGradient(0, 0, 0, H * 0.62);
    sky.addColorStop(0, "#1c2340");
    sky.addColorStop(0.38, "#5b4a6e");
    sky.addColorStop(0.7, "#e08a63");
    sky.addColorStop(1, "#f6c58a");
    g.fillStyle = sky;
    g.fillRect(0, 0, W, H);
    // The sun just under the shoulder of the volcano.
    const sx = W * 0.66;
    const sy = H * 0.47;
    const sun = g.createRadialGradient(sx, sy, 0, sx, sy, H * 0.42);
    sun.addColorStop(0, "rgba(255,236,190,0.95)");
    sun.addColorStop(0.08, "rgba(255,214,150,0.75)");
    sun.addColorStop(0.35, "rgba(246,160,110,0.25)");
    sun.addColorStop(1, "rgba(246,160,110,0)");
    g.fillStyle = sun;
    g.fillRect(0, 0, W, H);
    // Thin cloud bars.
    for (let i = 0; i < 9; i++) {
      const y = H * (0.12 + R() * 0.3);
      const x = W * R();
      const w = W * (0.18 + R() * 0.35);
      const cl = g.createLinearGradient(x - w / 2, 0, x + w / 2, 0);
      cl.addColorStop(0, "rgba(255,200,170,0)");
      cl.addColorStop(0.5, `rgba(255,${190 + R() * 40},${160 + R() * 30},${0.18 + R() * 0.2})`);
      cl.addColorStop(1, "rgba(255,200,170,0)");
      g.fillStyle = cl;
      g.beginPath();
      g.ellipse(x, y, w / 2, H * (0.006 + R() * 0.01), 0, 0, Math.PI * 2);
      g.fill();
    }
    // Merapi: a cone with a flattened, steaming summit.
    const peakX = W * 0.46;
    const peakY = H * 0.26;
    g.fillStyle = "#3a2f45";
    g.beginPath();
    g.moveTo(-W * 0.05, H * 0.66);
    g.bezierCurveTo(W * 0.18, H * 0.55, W * 0.34, H * 0.36, peakX - W * 0.035, peakY + H * 0.012);
    g.lineTo(peakX + W * 0.03, peakY + H * 0.004);
    g.bezierCurveTo(W * 0.58, H * 0.38, W * 0.78, H * 0.55, W * 1.05, H * 0.63);
    g.lineTo(W * 1.05, H);
    g.lineTo(-W * 0.05, H);
    g.fill();
    // Ash plume drifting west.
    for (let i = 0; i < 60; i++) {
      const t = i / 60;
      const x = peakX - t * W * 0.22 + (R() - 0.5) * W * 0.02;
      const y = peakY - H * 0.02 - t * H * 0.17 + (R() - 0.5) * H * 0.02;
      const r = H * (0.012 + t * 0.05);
      const pl = g.createRadialGradient(x, y, 0, x, y, r);
      pl.addColorStop(0, `rgba(226,196,196,${0.13 * (1 - t * 0.6)})`);
      pl.addColorStop(1, "rgba(226,196,196,0)");
      g.fillStyle = pl;
      g.beginPath();
      g.arc(x, y, r, 0, Math.PI * 2);
      g.fill();
    }
    // Light on the eastern flank.
    const flank = g.createLinearGradient(W * 0.46, 0, W * 0.8, 0);
    flank.addColorStop(0, "rgba(240,150,110,0.0)");
    flank.addColorStop(0.5, "rgba(240,150,110,0.16)");
    flank.addColorStop(1, "rgba(240,150,110,0)");
    g.fillStyle = flank;
    g.globalCompositeOperation = "source-atop";
    g.fillRect(0, peakY, W, H * 0.4);
    g.globalCompositeOperation = "source-over";
    // Mist in the valley.
    const mist = g.createLinearGradient(0, H * 0.52, 0, H * 0.7);
    mist.addColorStop(0, "rgba(246,214,196,0)");
    mist.addColorStop(0.5, "rgba(246,214,196,0.55)");
    mist.addColorStop(1, "rgba(246,214,196,0)");
    g.fillStyle = mist;
    g.fillRect(0, H * 0.5, W, H * 0.22);
    // Ridges, each nearer one darker and greener.
    const ridges = [
      ["#5e5a6c", 0.6, 0.012],
      ["#4a5a52", 0.66, 0.02],
      ["#334a3a", 0.72, 0.03],
    ];
    for (const [col, base, amp] of ridges) {
      g.fillStyle = col;
      g.beginPath();
      g.moveTo(0, H);
      const ph = R() * 10;
      for (let x = 0; x <= W; x += 8) {
        const y = H * (base + Math.sin(x / W * 7 + ph) * amp + Math.sin(x / W * 23 + ph * 2) * amp * 0.35);
        g.lineTo(x, y);
      }
      g.lineTo(W, H);
      g.fill();
    }
    // Rice terraces: curved bands, wet edges catching the sky.
    const top = H * 0.74;
    for (let i = 0; i < 16; i++) {
      const t = i / 16;
      const y0 = top + (H - top) * Math.pow(t, 1.35);
      const y1 = top + (H - top) * Math.pow((i + 1) / 16, 1.35);
      const bend = (R() - 0.5) * H * 0.03;
      const green = `hsl(${92 + R() * 18}, ${42 + R() * 14}%, ${20 + t * 16 + R() * 5}%)`;
      g.fillStyle = green;
      g.beginPath();
      g.moveTo(0, y0);
      g.quadraticCurveTo(W / 2, y0 + bend, W, y0 - bend * 0.5);
      g.lineTo(W, y1);
      g.quadraticCurveTo(W / 2, y1 + bend, 0, y1);
      g.fill();
      g.strokeStyle = `rgba(250,206,160,${0.35 - t * 0.15})`;
      g.lineWidth = Math.max(1, H * 0.002 * (1 + t * 2));
      g.beginPath();
      g.moveTo(0, y0);
      g.quadraticCurveTo(W / 2, y0 + bend, W, y0 - bend * 0.5);
      g.stroke();
    }
    // Coconut palms on the left edge.
    for (let i = 0; i < 4; i++) {
      const bx = W * (0.04 + i * 0.07 + R() * 0.03);
      const by = H * (0.86 + R() * 0.05);
      const th = H * (0.2 + R() * 0.12);
      const lean = (R() - 0.3) * W * 0.03;
      g.strokeStyle = "#1d2620";
      g.lineWidth = H * 0.006;
      g.beginPath();
      g.moveTo(bx, by);
      g.quadraticCurveTo(bx + lean * 0.4, by - th * 0.5, bx + lean, by - th);
      g.stroke();
      g.fillStyle = "#1a231c";
      for (let f = 0; f < 9; f++) {
        const a = (f / 9) * Math.PI * 2 + R() * 0.3;
        const len = H * (0.05 + R() * 0.035);
        g.beginPath();
        g.moveTo(bx + lean, by - th);
        g.quadraticCurveTo(bx + lean + Math.cos(a) * len * 0.6, by - th + Math.sin(a) * len * 0.3 - len * 0.2, bx + lean + Math.cos(a) * len, by - th + Math.abs(Math.sin(a)) * len * 0.5);
        g.lineWidth = H * 0.0045;
        g.strokeStyle = "#1a231c";
        g.stroke();
      }
    }
    // A farmer's hut with a lit window.
    const hx = W * 0.74;
    const hy = H * 0.79;
    g.fillStyle = "#24201c";
    g.fillRect(hx, hy, W * 0.04, H * 0.03);
    g.beginPath();
    g.moveTo(hx - W * 0.006, hy);
    g.lineTo(hx + W * 0.02, hy - H * 0.022);
    g.lineTo(hx + W * 0.046, hy);
    g.fill();
    g.fillStyle = "#ffc46b";
    g.fillRect(hx + W * 0.012, hy + H * 0.009, W * 0.007, H * 0.01);
    // Film grain.
    const id = g.getImageData(0, 0, W, H);
    const d = id.data;
    for (let i = 0; i < d.length; i += 4) {
      const n = (R() + R() - 1) * 9;
      d[i] += n;
      d[i + 1] += n;
      d[i + 2] += n;
    }
    g.putImageData(id, 0, 0);
  }
  function paintVignette(c) {
    const g = ctx2d(c);
    const W = c.width;
    const H = c.height;
    const v = g.createRadialGradient(W / 2, H * 0.48, Math.min(W, H) * 0.3, W / 2, H / 2, Math.hypot(W, H) * 0.58);
    v.addColorStop(0, "rgba(255,255,255,1)");
    v.addColorStop(0.55, "rgba(214,190,176,1)");
    v.addColorStop(1, "rgba(70,46,40,1)");
    g.fillStyle = v;
    g.fillRect(0, 0, W, H);
  }

  async function drawText(id, t) {
    const c = own(id);
    const g = ctx2d(c);
    g.clearRect(0, 0, c.width, c.height);
    const font = `${t.italic ? "italic " : ""}${t.weight || 400} ${t.size}px '${t.font}', serif`;
    try {
      await document.fonts.load(font, t.text || "A");
    } catch {}
    g.font = font;
    g.fillStyle = t.color;
    g.textBaseline = "top";
    g.textAlign = t.align || "left";
    const lines = (t.text || "").split("\n");
    const lh = t.size * 1.15;
    lines.forEach((line, i) => g.fillText(line, t.x, t.y + i * lh));
    const widths = lines.map((l) => g.measureText(l).width);
    const w = Math.max(0, ...widths);
    const x0 = t.align === "center" ? t.x - w / 2 : t.align === "right" ? t.x - w : t.x;
    invalidate();
    return { x: x0, y: t.y, w, h: lh * lines.length };
  }

  // ── filters: shader passes on one layer, inside the selection ────────
  function ensureF() {
    if (!fbo.f1 || fbo.f1.w !== E.w || fbo.f1.h !== E.h) {
      dropFbo(fbo.f1);
      dropFbo(fbo.f2);
      dropFbo(fbo.f3);
      fbo.f1 = newFbo(E.w, E.h, true);
      fbo.f2 = newFbo(E.w, E.h, true);
      fbo.f3 = newFbo(E.w, E.h, true);
    }
  }
  function blurInto(srcTex, sigma) {
    draw(fbo.f2, prog.blur, (u) => {
      bindTex(0, srcTex);
      gl.uniform1i(u.u_src, 0);
      gl.uniform2f(u.u_dir, 1, 0);
      gl.uniform1f(u.u_sigma, sigma);
      gl.uniform1i(u.u_pre, 1);
      gl.uniform1i(u.u_post, 0);
    });
    draw(fbo.f3, prog.blur, (u) => {
      bindTex(0, fbo.f2.tex);
      gl.uniform1i(u.u_src, 0);
      gl.uniform2f(u.u_dir, 0, 1);
      gl.uniform1f(u.u_sigma, sigma);
      gl.uniform1i(u.u_pre, 0);
      gl.uniform1i(u.u_post, 1);
    });
    return fbo.f3;
  }
  function filter(id, kind, p) {
    const m = layerMeta(id);
    if (!m || m.kind !== "pixel") throw new Error("Filters work on pixel layers. Pick one, or rasterize this layer.");
    const t0 = performance.now();
    ensureF();
    const T = upload(id);
    copyTo(fbo.f1, T.t);
    let out;
    if (kind === "blur") out = blurInto(fbo.f1.tex, Math.max(0.3, p.radius / 3));
    else if (kind === "sharpen") {
      const b = blurInto(fbo.f1.tex, Math.max(0.3, p.radius / 3));
      draw(fbo.f2, prog.sharpen, (u) => {
        bindTex(0, fbo.f1.tex);
        gl.uniform1i(u.u_src, 0);
        bindTex(1, b.tex);
        gl.uniform1i(u.u_blur, 1);
        gl.uniform1f(u.u_amount, p.amount / 100);
      });
      out = fbo.f2;
    } else {
      draw(fbo.f2, prog.noise, (u) => {
        bindTex(0, fbo.f1.tex);
        gl.uniform1i(u.u_src, 0);
        gl.uniform1f(u.u_amount, p.amount);
        gl.uniform1f(u.u_seed, Math.random());
        gl.uniform1i(u.u_mono, p.mono ? 1 : 0);
      });
      out = fbo.f2;
    }
    if (E.sync) gpuWait(out);
    const gpu = performance.now() - t0;
    const px = new Uint8Array(E.w * E.h * 4);
    gl.bindFramebuffer(gl.FRAMEBUFFER, out.fb);
    gl.readPixels(0, 0, E.w, E.h, gl.RGBA, gl.UNSIGNED_BYTE, px);
    const tmp = mk(E.w, E.h);
    ctx2d(tmp).putImageData(new ImageData(new Uint8ClampedArray(px.buffer), E.w, E.h), 0, 0);
    const c = own(id);
    const g = ctx2d(c);
    g.save();
    clipSel(g, m.x || 0, m.y || 0);
    g.clearRect(0, 0, E.w, E.h);
    g.drawImage(tmp, 0, 0);
    g.restore();
    invalidate();
    const ms = performance.now() - t0;
    keep(stats.filters, { kind, ms, gpu });
    return { ms: Math.round(ms), gpu: Math.round(gpu) };
  }

  // ── bucket fill ───────────────────────────────────────────────────────
  function bucket(id, x, y) {
    const m = layerMeta(id);
    const ox = m.x || 0;
    const oy = m.y || 0;
    const cx = Math.floor(x - ox);
    const cy = Math.floor(y - oy);
    const L = E.layers.get(id);
    const W = E.w;
    const H = E.h;
    if (cx < 0 || cy < 0 || cx >= W || cy >= H) return false;
    const src = ctx2d(L.canvas).getImageData(0, 0, W, H).data;
    const tol = E.tool.tolerance;
    const i0 = (cy * W + cx) * 4;
    const r0 = src[i0];
    const g0 = src[i0 + 1];
    const b0 = src[i0 + 2];
    const a0 = src[i0 + 3];
    const like = (i) => Math.abs(src[i] - r0) <= tol && Math.abs(src[i + 1] - g0) <= tol && Math.abs(src[i + 2] - b0) <= tol && Math.abs(src[i + 3] - a0) <= tol;
    const seen = new Uint8Array(W * H);
    const out = new ImageData(W, H);
    const n = parseInt(E.tool.color.slice(1), 16);
    const fr = (n >> 16) & 255;
    const fg = (n >> 8) & 255;
    const fb = n & 255;
    const stack = [cx, cy];
    while (stack.length) {
      const sy = stack.pop();
      let sx = stack.pop();
      while (sx > 0 && !seen[sy * W + sx - 1] && like((sy * W + sx - 1) * 4)) sx--;
      let up = false;
      let dn = false;
      for (let px = sx; px < W; px++) {
        const k = sy * W + px;
        if (seen[k] || !like(k * 4)) break;
        seen[k] = 1;
        const o = k * 4;
        out.data[o] = fr;
        out.data[o + 1] = fg;
        out.data[o + 2] = fb;
        out.data[o + 3] = 255;
        if (sy > 0) {
          const u = k - W;
          const ok = !seen[u] && like(u * 4);
          if (ok && !up) stack.push(px, sy - 1);
          up = ok;
        }
        if (sy < H - 1) {
          const d = k + W;
          const ok = !seen[d] && like(d * 4);
          if (ok && !dn) stack.push(px, sy + 1);
          dn = ok;
        }
      }
    }
    const tmp = mk(W, H);
    ctx2d(tmp).putImageData(out, 0, 0);
    const c = own(id);
    const g = ctx2d(c);
    g.save();
    clipSel(g, ox, oy);
    g.globalAlpha = E.tool.opacity;
    g.drawImage(tmp, 0, 0);
    g.restore();
    invalidate();
    return true;
  }

  // ── history snapshots (copy-on-write) ────────────────────────────────
  function snapshot() {
    const m = new Map();
    for (const [id, L] of E.layers) {
      L.shared = true;
      if (L.mask) L.maskShared = true;
      m.set(id, { canvas: L.canvas, mask: L.mask, ver: L.ver, maskVer: L.maskVer });
    }
    return { w: E.w, h: E.h, layers: m };
  }
  function restore(s) {
    if (s.w !== E.w || s.h !== E.h) resize(s.w, s.h);
    const next = new Map();
    for (const [id, v] of s.layers) {
      const old = E.layers.get(id);
      next.set(id, {
        canvas: v.canvas,
        mask: v.mask,
        shared: true,
        maskShared: !!v.mask,
        // A new version number makes the texture upload again.
        ver: (old ? Math.max(old.ver, v.ver) : v.ver) + 1,
        maskVer: (old ? Math.max(old.maskVer, v.maskVer) : v.maskVer) + 1,
      });
    }
    E.layers = next;
    cacheKey = null;
    invalidate();
  }

  // ── files: project, export, autosave ─────────────────────────────────
  const toBlob = (c, type = "image/png", q) => new Promise((res) => c.toBlob(res, type, q));
  const blobToUrl = (b) =>
    new Promise((res) => {
      const r = new FileReader();
      r.onload = () => res(r.result);
      r.readAsDataURL(b);
    });
  const loadImage = async (src) => {
    const blob = src instanceof Blob ? src : await (await fetch(src)).blob();
    return await createImageBitmap(blob);
  };
  function download(blob, name) {
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 4000);
  }
  async function restoreLayers(w, h, layers) {
    resize(w, h);
    E.layers = new Map();
    E.history = [];
    for (const [id, v] of Object.entries(layers)) {
      const c = mk(w, h);
      if (v.png) ctx2d(c).drawImage(await loadImage(v.png), 0, 0);
      let mask = null;
      if (v.mask) {
        mask = mk(w, h);
        ctx2d(mask).drawImage(await loadImage(v.mask), 0, 0);
      }
      E.layers.set(id, { canvas: c, mask, shared: false, maskShared: false, ver: 1, maskVer: mask ? 1 : 0 });
    }
    E.fitted = false;
    if (E.stage) fit();
    invalidate();
  }

  // IndexedDB: one record holding the document JSON and a PNG per layer and mask.
  const idb = () =>
    new Promise((res, rej) => {
      const r = indexedDB.open("kamar-gelap", 1);
      r.onupgradeneeded = () => r.result.createObjectStore("docs");
      r.onsuccess = () => res(r.result);
      r.onerror = () => rej(r.error);
    });
  const blobCache = new WeakMap();
  async function pngOf(c) {
    if (!c) return null;
    let b = blobCache.get(c);
    if (!b) {
      b = await toBlob(c);
      blobCache.set(c, b);
    }
    return b;
  }

  // ── ops ───────────────────────────────────────────────────────────────
  ops["kg.attach"] = ({ stage, sheet, gl: glEl, cursor, pos, id }) => {
    const h = attach(elementOf(stage), elementOf(sheet), elementOf(glEl), id);
    E.cursorEl = elementOf(cursor);
    E.posEl = elementOf(pos);
    return h;
  };
  ops["kg.detach"] = ({ h }) => {
    const f = getHandle(h);
    if (typeof f === "function") f();
  };
  ops["kg.info"] = () => ({ renderer: E.renderer || "", w: E.w, h: E.h });

  ops["kg.newDoc"] = ({ w, h }) => {
    if (!gl) initGl();
    resize(w, h);
    E.layers = new Map();
    E.history = [];
    E.cloneSrc = null;
    E.fitted = false;
    if (E.stage) fit();
  };
  ops["kg.layer"] = ({ id, fill, from }) => {
    if (from) {
      const L = E.layers.get(from);
      E.layers.set(id, { canvas: clone(L.canvas), mask: L.mask ? clone(L.mask) : null, shared: false, maskShared: false, ver: 1, maskVer: L.mask ? 1 : 0 });
    } else newLayer(id, fill);
    invalidate();
  };
  ops["kg.dropLayer"] = ({ id }) => {
    E.layers.delete(id);
    invalidate();
  };
  ops["kg.sample"] = ({ id, kind }) => {
    const c = own(id);
    if (kind === "vignette") paintVignette(c);
    else paintMerapi(c);
    invalidate();
  };
  ops["kg.mask"] = ({ id, mode }) => {
    const L = E.layers.get(id);
    if (!L) return;
    if (mode === "remove") {
      L.mask = null;
      L.maskShared = false;
      L.maskVer++;
    } else if (mode === "apply") {
      const c = own(id);
      const g = ctx2d(c);
      // White keeps a pixel, black removes it: the mask's luminance becomes alpha.
      const mc = clone(L.mask);
      const md = ctx2d(mc).getImageData(0, 0, E.w, E.h);
      for (let i = 0; i < md.data.length; i += 4) {
        md.data[i + 3] = md.data[i];
        md.data[i] = md.data[i + 1] = md.data[i + 2] = 0;
      }
      ctx2d(mc).putImageData(md, 0, 0);
      g.globalCompositeOperation = "destination-in";
      g.drawImage(mc, 0, 0);
      g.globalCompositeOperation = "source-over";
      L.mask = null;
      L.maskVer++;
    } else {
      const m = mk(E.w, E.h);
      const g = ctx2d(m);
      const sel = E.meta.sel;
      const meta = layerMeta(id);
      if (mode === "selection" && sel) {
        g.fillStyle = "#000";
        g.fillRect(0, 0, E.w, E.h);
        g.fillStyle = "#fff";
        g.fill(selPath(sel, meta?.x || 0, meta?.y || 0), "evenodd");
      } else if (mode === "gradient") {
        const gr = g.createLinearGradient(0, E.h * 0.35, 0, E.h * 0.85);
        gr.addColorStop(0, "#000");
        gr.addColorStop(1, "#fff");
        g.fillStyle = gr;
        g.fillRect(0, 0, E.w, E.h);
      } else {
        g.fillStyle = mode === "hide" ? "#000" : "#fff";
        g.fillRect(0, 0, E.w, E.h);
      }
      L.mask = m;
      L.maskShared = false;
      L.maskVer++;
    }
    cacheKey = null;
    invalidate();
  };
  ops["kg.meta"] = ({ doc }) => {
    E.meta = doc;
    invalidate();
  };
  ops["kg.tool"] = (t) => {
    Object.assign(E.tool, t);
    tipCache = null;
    if (E.cursorEl && !["brush", "eraser", "clone"].includes(E.tool.tool)) E.cursorEl.style.display = "none";
  };
  ops["kg.commit"] = ({ i }) => {
    E.history.length = i;
    E.history[i] = snapshot();
  };
  ops["kg.trim"] = ({ keep }) => {
    // C# keeps at most `keep` steps; older snapshots are let go.
    const drop = E.history.length - keep;
    if (drop > 0) E.history.splice(0, drop);
  };
  ops["kg.restore"] = ({ i }) => {
    const s = E.history[i];
    if (!s) throw new Error("that history step is gone");
    restore(s);
  };
  ops["kg.fill"] = ({ id, what }) => {
    const m = layerMeta(id);
    const c = own(id);
    const g = ctx2d(c);
    g.save();
    clipSel(g, m.x || 0, m.y || 0);
    if (what === "clear") g.clearRect(0, 0, E.w, E.h);
    else {
      g.globalAlpha = E.tool.opacity;
      g.fillStyle = what === "bg" ? E.tool.color2 : E.tool.color;
      g.fillRect(-(m.x || 0), -(m.y || 0), E.w, E.h);
    }
    g.restore();
    invalidate();
  };
  ops["kg.bucket"] = ({ id, x, y }) => bucket(id, x, y);
  ops["kg.filter"] = ({ id, kind, p }) => filter(id, kind, p);
  ops["kg.text"] = ({ id, t }) => drawText(id, t);
  ops["kg.bake"] = ({ id, x, y }) => {
    // Moves a layer's pixels by its offset, so the offset can go back to zero.
    const L = E.layers.get(id);
    const src = L.canvas;
    const c = mk(E.w, E.h);
    ctx2d(c).drawImage(src, x, y);
    L.canvas = c;
    L.shared = false;
    L.ver++;
    if (L.mask) {
      const mc = mk(E.w, E.h);
      const g = ctx2d(mc);
      g.fillStyle = "#000";
      g.fillRect(0, 0, E.w, E.h);
      g.drawImage(L.mask, x, y);
      L.mask = mc;
      L.maskShared = false;
      L.maskVer++;
    }
    invalidate();
  };
  ops["kg.crop"] = ({ x, y, w, h, offsets }) => {
    const next = new Map();
    for (const [id, L] of E.layers) {
      const o = offsets[id] || { x: 0, y: 0 };
      const c = mk(w, h);
      ctx2d(c).drawImage(L.canvas, o.x - x, o.y - y);
      let mask = null;
      if (L.mask) {
        mask = mk(w, h);
        const g = ctx2d(mask);
        g.fillStyle = "#000";
        g.fillRect(0, 0, w, h);
        g.drawImage(L.mask, o.x - x, o.y - y);
      }
      next.set(id, { canvas: c, mask, shared: false, maskShared: false, ver: L.ver + 1, maskVer: L.maskVer + 1 });
    }
    resize(w, h);
    E.layers = next;
    E.fitted = false;
    if (E.stage) fit();
    invalidate();
  };
  ops["kg.view"] = ({ what }) => {
    if (!E.stage) return E.view.zoom;
    const r = E.stage.getBoundingClientRect();
    if (what === "fit") fit();
    else if (what === "100") zoomAt(1, r.width / 2, r.height / 2);
    else if (what === "in") zoomAt(E.view.zoom * 1.25, r.width / 2, r.height / 2);
    else if (what === "out") zoomAt(E.view.zoom / 1.25, r.width / 2, r.height / 2);
    return E.view.zoom;
  };
  ops["kg.openImage"] = async ({ h }) => {
    const file = getHandle(h).file;
    const img = await createImageBitmap(file);
    return { w: img.width, h: img.height, name: file.name, bitmap: addHandle(img) };
  };
  ops["kg.placeImage"] = ({ id, bitmap, center }) => {
    const img = getHandle(bitmap);
    const c = own(id);
    const g = ctx2d(c);
    if (center) {
      const s = Math.min(1, (E.w * 0.8) / img.width, (E.h * 0.8) / img.height);
      const w = img.width * s;
      const h = img.height * s;
      g.drawImage(img, (E.w - w) / 2, (E.h - h) / 2, w, h);
    } else g.drawImage(img, 0, 0);
    invalidate();
  };
  ops["kg.export"] = async ({ type, quality, name }) => {
    const c = resultCanvas();
    let out = c;
    if (type !== "image/png") {
      // JPEG has no transparency: flatten onto white, as a print would.
      out = mk(E.w, E.h);
      const g = ctx2d(out);
      g.fillStyle = "#fff";
      g.fillRect(0, 0, E.w, E.h);
      g.drawImage(c, 0, 0);
    }
    const blob = await toBlob(out, type, quality);
    globalThis.__kgLastExport = blob;
    download(blob, name);
    return { bytes: blob.size, w: E.w, h: E.h };
  };
  ops["kg.thumb"] = async () => {
    const c = resultCanvas();
    const s = Math.min(1, 480 / E.w);
    const t = mk(Math.max(1, Math.round(E.w * s)), Math.max(1, Math.round(E.h * s)));
    ctx2d(t).drawImage(c, 0, 0, t.width, t.height);
    return t.toDataURL("image/jpeg", 0.8);
  };
  ops["kg.saveProject"] = async ({ meta, name }) => {
    const layers = {};
    for (const [id, L] of E.layers) layers[id] = { png: await blobToUrl(await pngOf(L.canvas)), mask: L.mask ? await blobToUrl(await pngOf(L.mask)) : null };
    const text = JSON.stringify({ format: "kamar-gelap", version: 1, w: E.w, h: E.h, meta: JSON.parse(meta), layers });
    const blob = new Blob([text], { type: "application/json" });
    download(blob, name);
    return { bytes: blob.size };
  };
  ops["kg.loadProject"] = async ({ h }) => {
    const file = getHandle(h).file;
    const data = JSON.parse(await file.text());
    if (data.format !== "kamar-gelap") throw new Error("That file is not a Kamar Gelap project.");
    await restoreLayers(data.w, data.h, data.layers);
    return JSON.stringify(data.meta);
  };
  ops["kg.autosave"] = async ({ meta, thumb }) => {
    const t0 = performance.now();
    const layers = {};
    for (const [id, L] of E.layers) layers[id] = { png: await pngOf(L.canvas), mask: await pngOf(L.mask) };
    const db = await idb();
    await new Promise((res, rej) => {
      const tx = db.transaction("docs", "readwrite");
      tx.objectStore("docs").put({ w: E.w, h: E.h, meta, layers, thumb, saved: Date.now() }, "current");
      tx.oncomplete = res;
      tx.onerror = () => rej(tx.error);
    });
    db.close();
    return Math.round(performance.now() - t0);
  };
  const readSaved = async () => {
    const db = await idb();
    const rec = await new Promise((res, rej) => {
      const r = db.transaction("docs").objectStore("docs").get("current");
      r.onsuccess = () => res(r.result || null);
      r.onerror = () => rej(r.error);
    });
    db.close();
    return rec;
  };
  ops["kg.savedInfo"] = async () => {
    const rec = await readSaved();
    return rec ? { w: rec.w, h: rec.h, thumb: rec.thumb || "", saved: rec.saved, meta: rec.meta } : null;
  };
  ops["kg.loadSaved"] = async () => {
    const rec = await readSaved();
    if (!rec) return null;
    if (!gl) initGl();
    await restoreLayers(rec.w, rec.h, rec.layers);
    return rec.meta;
  };
  ops["kg.forget"] = async () => {
    const db = await idb();
    await new Promise((res) => {
      const tx = db.transaction("docs", "readwrite");
      tx.objectStore("docs").delete("current");
      tx.oncomplete = res;
    });
    db.close();
  };

  ops["kg.thumbs"] = ({ el }) => {
    const root = elementOf(el);
    if (!root || !E.w) return;
    for (const c of root.querySelectorAll("canvas[data-thumb]")) {
      const L = E.layers.get(c.dataset.thumb);
      const src = L && (c.dataset.mask ? L.mask : L.canvas);
      const g = c.getContext("2d");
      g.clearRect(0, 0, c.width, c.height);
      if (!src) continue;
      const s = Math.min(c.width / E.w, c.height / E.h);
      const w = E.w * s;
      const h = E.h * s;
      g.drawImage(src, (c.width - w) / 2, (c.height - h) / 2, w, h);
    }
  };

  // For the scripted checks: read the composite back.
  E.sampleAt = (x, y) => {
    renderNow();
    return Array.from(readResult(x, y, 1, 1));
  };
  E.region = (x, y, w, h) => {
    renderNow();
    return readResult(x, y, w, h);
  };
  E.layerRegion = (id, x, y, w, h) => Array.from(ctx2d(E.layers.get(id).canvas).getImageData(x, y, w, h).data);
  E.layerPixel = (id, x, y) => Array.from(ctx2d(E.layers.get(id).canvas).getImageData(x, y, 1, 1).data);
}
