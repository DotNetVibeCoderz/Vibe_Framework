// Rustzor.Browser.OffscreenCanvas — worker rendering and off-screen exports.
export function install({ ops, addHandle, getHandle, releaseHandle, elementOf }) {
  const toDataUrl = (blob) =>
    new Promise((resolve) => {
      const r = new FileReader();
      r.onload = () => resolve(r.result);
      r.readAsDataURL(blob);
    });
  ops["off.supported"] = () => typeof OffscreenCanvas !== "undefined";
  ops["off.transfer"] = ({ el, url }) => {
    const offscreen = elementOf(el).transferControlToOffscreen();
    const worker = new Worker(url, { type: "module" });
    worker.postMessage({ canvas: offscreen }, [offscreen]);
    return addHandle(worker);
  };
  ops["off.post"] = ({ h, data }) => getHandle(h)?.postMessage(data);
  ops["off.stop"] = ({ h }) => {
    getHandle(h)?.terminate();
    releaseHandle(h);
  };
  ops["off.render"] = async ({ w, h, draw, type }) => {
    const canvas = new OffscreenCanvas(w, h);
    const ctx = canvas.getContext("2d");
    new Function("ctx", "w", "h", draw)(ctx, w, h);
    return toDataUrl(await canvas.convertToBlob({ type }));
  };
  ops["off.svg"] = async ({ svg, w, h }) => {
    const img = new Image();
    const url = URL.createObjectURL(new Blob([svg], { type: "image/svg+xml" }));
    await new Promise((resolve, reject) => {
      img.onload = resolve;
      img.onerror = () => reject(new Error("invalid SVG"));
      img.src = url;
    });
    const canvas = new OffscreenCanvas(w, h);
    canvas.getContext("2d").drawImage(img, 0, 0, w, h);
    URL.revokeObjectURL(url);
    return toDataUrl(await canvas.convertToBlob({ type: "image/png" }));
  };
}
