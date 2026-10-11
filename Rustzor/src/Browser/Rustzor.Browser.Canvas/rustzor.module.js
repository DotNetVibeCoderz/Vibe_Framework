// Rustzor.Browser.Canvas — 2D drawing from a command buffer, one call per frame.
export function install({ ops, elementOf, addHandle, getHandle, releaseHandle, dispatch }) {
  const gradient = (ctx, g, stops) => {
    for (let i = 0; i < stops.length; i += 2) g.addColorStop(stops[i], stops[i + 1]);
    ctx.fillStyle = g;
  };

  function run(ctx, cmds) {
    for (const c of cmds) {
      switch (c[0]) {
        case 1: ctx.fillStyle = c[1]; break;
        case 2: ctx.strokeStyle = c[1]; break;
        case 3: ctx.lineWidth = c[1]; break;
        case 4: ctx.font = c[1]; break;
        case 5: ctx.textAlign = c[1]; break;
        case 6: ctx.textBaseline = c[1]; break;
        case 7: ctx.globalAlpha = c[1]; break;
        case 8: ctx.lineCap = c[1]; break;
        case 9: ctx.lineJoin = c[1]; break;
        case 10: ctx.shadowColor = c[1]; ctx.shadowBlur = c[2]; ctx.shadowOffsetX = c[3]; ctx.shadowOffsetY = c[4]; break;
        case 11: ctx.globalCompositeOperation = c[1]; break;
        case 12: ctx.setLineDash(c[1]); break;
        case 13: ctx.imageSmoothingEnabled = c[1]; break;
        case 14: ctx.filter = c[1]; break;
        case 15: gradient(ctx, ctx.createLinearGradient(c[1], c[2], c[3], c[4]), c[5]); break;
        case 16: gradient(ctx, ctx.createRadialGradient(c[1], c[2], c[3], c[4], c[5], c[6]), c[7]); break;
        case 20: ctx.fillRect(c[1], c[2], c[3], c[4]); break;
        case 21: ctx.strokeRect(c[1], c[2], c[3], c[4]); break;
        case 22: ctx.clearRect(c[1], c[2], c[3], c[4]); break;
        case 23: ctx.beginPath(); break;
        case 24: ctx.closePath(); break;
        case 25: ctx.moveTo(c[1], c[2]); break;
        case 26: ctx.lineTo(c[1], c[2]); break;
        case 27: ctx.arc(c[1], c[2], c[3], c[4], c[5], c[6]); break;
        case 28: ctx.arcTo(c[1], c[2], c[3], c[4], c[5]); break;
        case 29: ctx.quadraticCurveTo(c[1], c[2], c[3], c[4]); break;
        case 30: ctx.bezierCurveTo(c[1], c[2], c[3], c[4], c[5], c[6]); break;
        case 31: ctx.rect(c[1], c[2], c[3], c[4]); break;
        case 32: ctx.roundRect ? ctx.roundRect(c[1], c[2], c[3], c[4], c[5]) : ctx.rect(c[1], c[2], c[3], c[4]); break;
        case 33: ctx.ellipse(c[1], c[2], c[3], c[4], c[5], c[6], c[7]); break;
        case 34: ctx.fill(); break;
        case 35: ctx.stroke(); break;
        case 36: ctx.clip(); break;
        case 37: {
          const p = c[3];
          ctx.beginPath();
          for (let i = 0; i < p.length; i += 2) (i === 0 ? ctx.moveTo : ctx.lineTo).call(ctx, p[i], p[i + 1]);
          if (c[1]) ctx.closePath();
          c[2] ? ctx.fill() : ctx.stroke();
          break;
        }
        case 40: ctx.fillText(c[1], c[2], c[3]); break;
        case 41: ctx.strokeText(c[1], c[2], c[3]); break;
        case 42: ctx.drawImage(getHandle(c[1]), c[2], c[3]); break;
        case 43: ctx.drawImage(getHandle(c[1]), c[2], c[3], c[4], c[5]); break;
        case 44: ctx.drawImage(getHandle(c[1]), c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9]); break;
        case 45: ctx.drawImage(getHandle(c[1]).canvas, c[2], c[3]); break;
        case 50: ctx.save(); break;
        case 51: ctx.restore(); break;
        case 52: ctx.translate(c[1], c[2]); break;
        case 53: ctx.rotate(c[1]); break;
        case 54: ctx.scale(c[1], c[2]); break;
        case 55: ctx.setTransform(c[1], c[2], c[3], c[4], c[5], c[6]); break;
        case 56: ctx.setTransform(ctx.__rzBase || new DOMMatrix()); break;
      }
    }
  }

  function fit(ctx) {
    const canvas = ctx.canvas;
    const dpr = ctx.__rzHiDpi ? devicePixelRatio || 1 : 1;
    const w = canvas.clientWidth || canvas.width;
    const h = canvas.clientHeight || canvas.height;
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(h * dpr);
    }
    ctx.__rzBase = new DOMMatrix([dpr, 0, 0, dpr, 0, 0]);
    ctx.setTransform(ctx.__rzBase);
    return { w, h };
  }

  ops["canvas.ctx"] = ({ el, hiDpi, alpha }) => {
    const canvas = elementOf(el);
    const ctx = canvas.getContext("2d", { alpha });
    ctx.__rzHiDpi = hiDpi;
    const size = fit(ctx);
    return { h: addHandle(ctx), w: size.w, h2: size.h };
  };
  ops["canvas.fit"] = ({ h }) => fit(getHandle(h));
  ops["canvas.batch"] = ({ h, c }) => run(getHandle(h), c);
  ops["canvas.measure"] = ({ h, text, font }) => {
    const ctx = getHandle(h);
    if (font) {
      ctx.save();
      ctx.font = font;
    }
    const w = ctx.measureText(text).width;
    if (font) ctx.restore();
    return w;
  };
  const toB64 = (bytes) => {
    let s = "";
    for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(s);
  };
  ops["canvas.getPixels"] = ({ h, x, y, w, h2 }) => toB64(getHandle(h).getImageData(x, y, w, h2).data);
  ops["canvas.putPixels"] = ({ h, data, x, y, w, h2 }) => {
    const bin = atob(data);
    const img = new ImageData(w, h2);
    for (let i = 0; i < bin.length; i++) img.data[i] = bin.charCodeAt(i);
    const ctx = getHandle(h);
    ctx.save();
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.putImageData(img, x, y);
    ctx.restore();
  };
  ops["canvas.dataUrl"] = ({ h, type, q }) => getHandle(h).canvas.toDataURL(type, q);
  ops["canvas.layer"] = ({ w, h }) => {
    const c = typeof OffscreenCanvas !== "undefined" ? new OffscreenCanvas(w, h) : Object.assign(document.createElement("canvas"), { width: w, height: h });
    return addHandle(c.getContext("2d"));
  };
  ops["canvas.release"] = ({ h }) => releaseHandle(h);
  ops["canvas.loadImage"] = ({ url }) =>
    new Promise((resolve, reject) => {
      const img = new Image();
      img.crossOrigin = "anonymous";
      img.onload = () => resolve({ h: addHandle(img), w: img.naturalWidth, hh: img.naturalHeight });
      img.onerror = () => reject(new Error(`could not load image ${url}`));
      img.src = url;
    });
  ops["canvas.pointer"] = ({ el, id }) => {
    const canvas = elementOf(el);
    canvas.style.touchAction = "none";
    const send = (kind) => (e) => {
      const r = canvas.getBoundingClientRect();
      if (kind === "down") canvas.setPointerCapture?.(e.pointerId);
      dispatch(id, { kind, x: e.clientX - r.left, y: e.clientY - r.top, pressure: e.pressure || (e.buttons ? 0.5 : 0), buttons: e.buttons, pointerId: e.pointerId });
    };
    const down = send("down");
    const move = send("move");
    const up = send("up");
    canvas.addEventListener("pointerdown", down);
    canvas.addEventListener("pointermove", move);
    canvas.addEventListener("pointerup", up);
    canvas.addEventListener("pointercancel", up);
    return addHandle(() => {
      canvas.removeEventListener("pointerdown", down);
      canvas.removeEventListener("pointermove", move);
      canvas.removeEventListener("pointerup", up);
      canvas.removeEventListener("pointercancel", up);
    });
  };
}
