// Rustzor.Browser.WebGL — WebGL 2 resources and batched frames.
export function install({ ops, addHandle, getHandle, elementOf }) {
  const MODES = [0x0000, 0x0001, 0x0002, 0x0003, 0x0004, 0x0005, 0x0006];
  const fit = (box) => {
    const c = box.gl.canvas;
    const dpr = devicePixelRatio || 1;
    const w = Math.round((c.clientWidth || c.width) * dpr);
    const h = Math.round((c.clientHeight || c.height) * dpr);
    const changed = c.width !== w || c.height !== h;
    if (changed) {
      c.width = w;
      c.height = h;
    }
    return { w, hh: h, changed };
  };
  ops["gl.ctx"] = ({ el, aa, xr }) => {
    const gl = elementOf(el).getContext("webgl2", { antialias: aa, xrCompatible: xr });
    if (!gl) throw new Error("WebGL 2 is not available");
    const box = { gl, locs: new Map(), objects: new Map(), next: 1 };
    const size = fit(box);
    return { h: addHandle(box), w: size.w, hh: size.hh };
  };
  ops["gl.fit"] = ({ h }) => fit(getHandle(h));
  const keep = (box, o) => {
    const id = box.next++;
    box.objects.set(id, o);
    return id;
  };
  ops["gl.program"] = ({ h, vs, fs }) => {
    const box = getHandle(h);
    const gl = box.gl;
    const compile = (type, src) => {
      const s = gl.createShader(type);
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
      return s;
    };
    const p = gl.createProgram();
    gl.attachShader(p, compile(gl.VERTEX_SHADER, vs));
    gl.attachShader(p, compile(gl.FRAGMENT_SHADER, fs));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
    return keep(box, p);
  };
  ops["gl.buffer"] = ({ h, data, dynamic }) => {
    const box = getHandle(h);
    const gl = box.gl;
    const b = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, b);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(data), dynamic ? gl.DYNAMIC_DRAW : gl.STATIC_DRAW);
    return keep(box, b);
  };
  ops["gl.indices"] = ({ h, data }) => {
    const box = getHandle(h);
    const gl = box.gl;
    const b = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, b);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint32Array(data), gl.STATIC_DRAW);
    return keep(box, b);
  };
  ops["gl.texture"] = ({ h, url }) =>
    new Promise((resolve, reject) => {
      const box = getHandle(h);
      const gl = box.gl;
      const img = new Image();
      img.crossOrigin = "anonymous";
      img.onload = () => {
        const t = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, t);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
        gl.generateMipmap(gl.TEXTURE_2D);
        resolve(keep(box, t));
      };
      img.onerror = () => reject(new Error(`could not load ${url}`));
      img.src = url;
    });
  const uniform = (box, prog, name) => {
    const key = prog + ":" + name;
    if (!box.locs.has(key)) box.locs.set(key, box.gl.getUniformLocation(box.objects.get(prog), name));
    return box.locs.get(key);
  };
  ops["gl.batch"] = ({ h, c }) => {
    const box = getHandle(h);
    const gl = box.gl;
    const o = (id) => box.objects.get(id);
    for (const x of c) {
      switch (x[0]) {
        case 1: gl.bindBuffer(gl.ARRAY_BUFFER, o(x[1])); gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(x[2]), gl.DYNAMIC_DRAW); break;
        case 2: gl.viewport(x[1], x[2], x[3], x[4]); break;
        case 3: gl.clearColor(x[1], x[2], x[3], x[4]); break;
        case 4: gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT); break;
        case 5: gl.useProgram(o(x[1])); break;
        case 6: {
          const loc = gl.getAttribLocation(o(x[1]), x[2]);
          if (loc < 0) break;
          gl.bindBuffer(gl.ARRAY_BUFFER, o(x[3]));
          gl.enableVertexAttribArray(loc);
          gl.vertexAttribPointer(loc, x[4], gl.FLOAT, false, x[5], x[6]);
          break;
        }
        case 7: {
          const v = x[3];
          const loc = uniform(box, x[1], x[2]);
          [null, gl.uniform1f, gl.uniform2f, gl.uniform3f, gl.uniform4f][v.length].call(gl, loc, ...v);
          break;
        }
        case 8: gl.uniformMatrix4fv(uniform(box, x[1], x[2]), false, new Float32Array(x[3])); break;
        case 9: gl.activeTexture(gl.TEXTURE0 + x[4]); gl.bindTexture(gl.TEXTURE_2D, o(x[3])); gl.uniform1i(uniform(box, x[1], x[2]), x[4]); break;
        case 10: gl.drawArrays(MODES[x[1]], x[2], x[3]); break;
        case 11: gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, o(x[2])); gl.drawElements(MODES[x[1]], x[3], gl.UNSIGNED_INT, 0); break;
        case 12: x[1] ? gl.enable(gl.DEPTH_TEST) : gl.disable(gl.DEPTH_TEST); break;
        case 13:
          if (x[1]) {
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
          } else gl.disable(gl.BLEND);
          break;
      }
    }
  };
}
