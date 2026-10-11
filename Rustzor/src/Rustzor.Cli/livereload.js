// rustzor serve: reloads the page after each successful rebuild, and shows the
// build errors over the page when a rebuild fails. Injected only by the
// development server; published sites never contain it.
(() => {
  const source = new EventSource("/_rustzor-dev/events");
  let overlay = null;
  let badge = null;

  const show = (text, kind) => {
    if (!badge) {
      badge = document.createElement("div");
      badge.style.cssText =
        "position:fixed;right:12px;bottom:12px;z-index:2147483647;font:600 12px/1 system-ui,sans-serif;" +
        "padding:8px 12px;border-radius:999px;color:#fff;box-shadow:0 4px 14px rgba(0,0,0,.25);transition:opacity .2s";
      document.body.appendChild(badge);
    }
    badge.textContent = text;
    badge.style.background = kind === "error" ? "#b91c1c" : "#c2410c";
    badge.style.opacity = "1";
  };

  const clearOverlay = () => {
    overlay?.remove();
    overlay = null;
  };

  source.addEventListener("building", () => show("Rebuilding…", "busy"));
  source.addEventListener("reload", () => location.reload());
  source.addEventListener("build-error", (e) => {
    const { text } = JSON.parse(e.data);
    show("Build failed", "error");
    clearOverlay();
    overlay = document.createElement("div");
    overlay.style.cssText =
      "position:fixed;inset:0;z-index:2147483646;background:rgba(20,10,8,.92);color:#fde2d8;overflow:auto;" +
      "padding:32px;font:13px/1.6 ui-monospace,Consolas,monospace;white-space:pre-wrap";
    const title = document.createElement("div");
    title.style.cssText = "font:700 18px system-ui,sans-serif;color:#fff;margin-bottom:12px";
    title.textContent = "Build failed — fix the errors and save; the page reloads when it builds.";
    const body = document.createElement("div");
    body.textContent = text;
    overlay.append(title, body);
    document.body.appendChild(overlay);
  });
})();
