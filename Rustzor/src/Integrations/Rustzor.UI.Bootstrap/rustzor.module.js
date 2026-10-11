// Rustzor.UI.Bootstrap — adds the Bootstrap 5.3 stylesheet and keeps
// data-bs-theme in step with Rustzor's data-theme (light/dark).
export function install({ ops }) {
  const CSS = globalThis.RustzorCdn?.bootstrap ?? "https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css";
  if (!document.querySelector("link[data-rz-bootstrap]")) {
    const link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = CSS;
    link.dataset.rzBootstrap = "";
    // Ahead of the app's own stylesheets, so they can override Bootstrap.
    document.head.insertBefore(link, document.head.querySelector("link[rel=stylesheet],style"));
  }
  const root = document.documentElement;
  const dark = matchMedia("(prefers-color-scheme: dark)");
  const sync = () => {
    const t = root.dataset.theme;
    root.setAttribute("data-bs-theme", t === "dark" || t === "light" ? t : dark.matches ? "dark" : "light");
  };
  sync();
  new MutationObserver(sync).observe(root, { attributes: true, attributeFilter: ["data-theme"] });
  dark.addEventListener("change", sync);
  ops["bs.theme"] = ({ theme }) => {
    root.dataset.theme = theme;
    sync();
  };
}
