// Rustzor.Browser.WebGPU — compute shaders and full-screen fragment shaders.
export function install({ ops, addHandle, getHandle, elementOf }) {
  let adapter = null;
  let device = null;

  const ensure = async () => {
    if (device) return device;
    if (!navigator.gpu) throw new Error("WebGPU is not available in this browser");
    adapter = await navigator.gpu.requestAdapter();
    if (!adapter) throw new Error("no WebGPU adapter");
    device = await adapter.requestDevice();
    return device;
  };
  const fromB64 = (s) => {
    const bin = atob(s);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return bytes;
  };
  const toB64 = (bytes) => {
    let s = "";
    for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(s);
  };

  ops["gpu.supported"] = () => !!navigator.gpu;
  ops["gpu.init"] = async () => {
    await ensure();
    const info = adapter.info || {};
    return { vendor: info.vendor || "", architecture: info.architecture || "", description: info.description || "", f16: adapter.features.has("shader-f16") };
  };
  ops["gpu.compute"] = async ({ code, input, outLen, wx, wy, params }) => {
    const dev = await ensure();
    const inBytes = fromB64(input);
    const inBuf = dev.createBuffer({ size: Math.max(16, inBytes.byteLength), usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
    dev.queue.writeBuffer(inBuf, 0, inBytes);
    const outSize = Math.max(16, outLen * 4);
    const outBuf = dev.createBuffer({ size: outSize, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC });
    const uni = dev.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    dev.queue.writeBuffer(uni, 0, new Float32Array([params[0] ?? 0, params[1] ?? 0, params[2] ?? 0, params[3] ?? 0]));
    const read = dev.createBuffer({ size: outSize, usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST });
    const pipeline = dev.createComputePipeline({ layout: "auto", compute: { module: dev.createShaderModule({ code }), entryPoint: "main" } });
    const bind = dev.createBindGroup({
      layout: pipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: inBuf } },
        { binding: 1, resource: { buffer: outBuf } },
        { binding: 2, resource: { buffer: uni } },
      ].filter((e) => code.includes(`@binding(${e.binding})`)),
    });
    const enc = dev.createCommandEncoder();
    const pass = enc.beginComputePass();
    pass.setPipeline(pipeline);
    pass.setBindGroup(0, bind);
    pass.dispatchWorkgroups(wx, wy);
    pass.end();
    enc.copyBufferToBuffer(outBuf, 0, read, 0, outSize);
    dev.queue.submit([enc.finish()]);
    await read.mapAsync(GPUMapMode.READ);
    const out = new Uint8Array(read.getMappedRange().slice(0, outLen * 4));
    read.unmap();
    [inBuf, outBuf, uni, read].forEach((b) => b.destroy());
    return toB64(out);
  };
  ops["gpu.shaderCanvas"] = async ({ el, code }) => {
    const dev = await ensure();
    const canvas = elementOf(el);
    const dpr = devicePixelRatio || 1;
    canvas.width = Math.round(canvas.clientWidth * dpr);
    canvas.height = Math.round(canvas.clientHeight * dpr);
    const ctx = canvas.getContext("webgpu");
    const format = navigator.gpu.getPreferredCanvasFormat();
    ctx.configure({ device: dev, format, alphaMode: "premultiplied" });
    const header = `struct Uniforms { time: f32, width: f32, height: f32, mouseX: f32, mouseY: f32, pad0: f32, pad1: f32, pad2: f32, p: array<vec4<f32>, 3> };
@group(0) @binding(0) var<uniform> u: Uniforms;
@vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4f {
  var p = array<vec2f, 3>(vec2f(-1.0, -3.0), vec2f(-1.0, 1.0), vec2f(3.0, 1.0));
  return vec4f(p[i], 0.0, 1.0);
}
`;
    const module = dev.createShaderModule({ code: header + code });
    const pipeline = dev.createRenderPipeline({
      layout: "auto",
      vertex: { module, entryPoint: "vs" },
      fragment: { module, entryPoint: "fs", targets: [{ format }] },
      primitive: { topology: "triangle-list" },
    });
    const uniforms = dev.createBuffer({ size: 80, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    const bind = dev.createBindGroup({ layout: pipeline.getBindGroupLayout(0), entries: [{ binding: 0, resource: { buffer: uniforms } }] });
    return addHandle({ dev, ctx, pipeline, uniforms, bind, canvas, dpr });
  };
  ops["gpu.draw"] = ({ h, t, mx, my, p }) => {
    const s = getHandle(h);
    if (!s) return;
    const data = new Float32Array(20);
    data.set([t, s.canvas.width, s.canvas.height, mx * s.dpr, my * s.dpr]);
    for (let i = 0; i < Math.min(12, p.length); i++) data[8 + i] = p[i];
    s.dev.queue.writeBuffer(s.uniforms, 0, data);
    const enc = s.dev.createCommandEncoder();
    const pass = enc.beginRenderPass({
      colorAttachments: [{ view: s.ctx.getCurrentTexture().createView(), loadOp: "clear", storeOp: "store", clearValue: { r: 0, g: 0, b: 0, a: 1 } }],
    });
    pass.setPipeline(s.pipeline);
    pass.setBindGroup(0, s.bind);
    pass.draw(3);
    pass.end();
    s.dev.queue.submit([enc.finish()]);
  };
}
