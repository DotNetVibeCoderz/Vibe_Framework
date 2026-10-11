// Rustzor.UI — theme switching and the persisted theme choice.
export function install({ ops }) {
  const saved = (() => {
    try {
      return localStorage.getItem("rz-theme");
    } catch {
      return null;
    }
  })();
  if (saved === "light" || saved === "dark") document.documentElement.setAttribute("data-theme", saved);
  ops["ui.theme"] = () => {
    const explicit = document.documentElement.getAttribute("data-theme");
    if (explicit) return explicit;
    return matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  };
  ops["ui.setTheme"] = ({ theme }) => {
    document.documentElement.setAttribute("data-theme", theme);
    try {
      localStorage.setItem("rz-theme", theme);
    } catch {}
  };
}
