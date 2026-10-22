// Loket's browser side: copy buttons and citations inside rendered answers, keeping the
// thread scrolled, downloads, speech recognition, image shrinking and PDF text.
// Built by Gravicode Studios, led by Kang Fadhil.
export function install({ ops, dispatch }) {
  let bound = 0;
  ops["lk.bind"] = ({ cb }) => {
    bound = cb;
    if (document.body.dataset.lkBound) return;
    document.body.dataset.lkBound = "1";
    document.addEventListener("click", async (e) => {
      const copy = e.target.closest?.(".lk-copy");
      if (copy) {
        const code = copy.closest(".lk-code")?.querySelector("code")?.textContent ?? "";
        try {
          await navigator.clipboard.writeText(code);
        } catch {
          const range = document.createRange();
          range.selectNodeContents(copy.closest(".lk-code").querySelector("code"));
          getSelection().removeAllRanges();
          getSelection().addRange(range);
        }
        const label = copy.textContent;
        copy.textContent = copy.dataset.done || "Tersalin";
        copy.classList.add("done");
        setTimeout(() => {
          copy.textContent = label;
          copy.classList.remove("done");
        }, 1400);
        return;
      }
      const cite = e.target.closest?.(".lk-cite");
      if (cite && bound) dispatch(bound, { kind: "cite", msg: Number(cite.dataset.msg), n: Number(cite.dataset.n) });
    });
  };

  // Keeps the thread pinned to the newest text unless the reader scrolled up.
  ops["lk.scroll"] = ({ sel, force }) => {
    const el = document.querySelector(sel);
    if (!el) return;
    const near = el.scrollHeight - el.scrollTop - el.clientHeight < 140;
    if (force || near) el.scrollTop = el.scrollHeight;
  };

  ops["lk.focus"] = ({ sel }) => document.querySelector(sel)?.focus();
  ops["lk.reveal"] = ({ sel }) => document.querySelector(sel)?.scrollIntoView({ block: "nearest", behavior: "smooth" });

  ops["lk.download"] = ({ name, text, type }) => {
    const url = URL.createObjectURL(new Blob([text], { type: type || "text/plain" }));
    const a = Object.assign(document.createElement("a"), { href: url, download: name });
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
  };

  // ── Voice ────────────────────────────────────────────────────────────────
  const Recognition = globalThis.SpeechRecognition || globalThis.webkitSpeechRecognition;
  let rec = null;
  ops["lk.speech"] = () => ({ listen: !!Recognition, speak: "speechSynthesis" in globalThis });
  ops["lk.listen"] = ({ cb, lang }) => {
    if (!Recognition) return false;
    rec?.abort();
    rec = new Recognition();
    rec.lang = lang || "id-ID";
    rec.interimResults = true;
    rec.continuous = false;
    rec.onresult = (e) => {
      let text = "";
      let final = false;
      for (const r of e.results) {
        text += r[0].transcript;
        final ||= r.isFinal;
      }
      dispatch(cb, { kind: final ? "final" : "interim", text });
    };
    rec.onerror = (e) => dispatch(cb, { kind: "error", text: e.error });
    rec.onend = () => dispatch(cb, { kind: "end" });
    rec.start();
    return true;
  };
  ops["lk.listenStop"] = () => rec?.stop();
  ops["lk.speakStop"] = () => globalThis.speechSynthesis?.cancel();

  // ── Images: shrink before they travel to a model ─────────────────────────
  ops["lk.shrink"] = async ({ url, max }) => {
    const img = new Image();
    img.src = url;
    await img.decode();
    const scale = Math.min(1, max / Math.max(img.naturalWidth, img.naturalHeight));
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(img.naturalWidth * scale);
    canvas.height = Math.round(img.naturalHeight * scale);
    canvas.getContext("2d").drawImage(img, 0, 0, canvas.width, canvas.height);
    return { url: canvas.toDataURL("image/jpeg", 0.85), width: canvas.width, height: canvas.height };
  };

  // ── PDF text, with pdf.js loaded on first use ────────────────────────────
  const PDFJS = globalThis.RustzorCdn?.pdfjs ?? "https://cdn.jsdelivr.net/npm/pdfjs-dist@4.10.38/build/pdf.min.mjs";
  let pdfjs = null;
  ops["lk.pdfText"] = async ({ b64 }) => {
    pdfjs ??= await import(/* @vite-ignore */ PDFJS);
    pdfjs.GlobalWorkerOptions.workerSrc ||= PDFJS.replace("pdf.min.mjs", "pdf.worker.min.mjs");
    const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
    const doc = await pdfjs.getDocument({ data: bytes }).promise;
    const pages = [];
    for (let p = 1; p <= doc.numPages; p++) {
      const page = await doc.getPage(p);
      const content = await page.getTextContent();
      let line = "";
      const lines = [];
      let lastY = null;
      for (const item of content.items) {
        const y = item.transform?.[5];
        if (lastY !== null && Math.abs(y - lastY) > 2) {
          lines.push(line);
          line = "";
        }
        line += item.str;
        lastY = y;
      }
      lines.push(line);
      pages.push(lines.join("\n"));
    }
    return pages.join("\n\n");
  };
}
