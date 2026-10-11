// Rustzor.UI.Tailwind — the Tailwind 3.4 play compiler. It watches the DOM and
// generates CSS for every utility class that appears, so classes rendered by
// Razor components just work. Dark mode follows Rustzor's data-theme.
export function install({ ops }) {
  const SRC = globalThis.RustzorCdn?.tailwind ?? "https://cdn.tailwindcss.com/3.4.17";
  const base = {
    darkMode: ["selector", '[data-theme="dark"]'],
    theme: {
      extend: {
        colors: {
          // The Rustzor palette: oxidised iron and patina.
          rust: { 50: "#fff7ed", 100: "#ffedd5", 200: "#fed7aa", 300: "#fdba74", 400: "#fb923c", 500: "#f97316", 600: "#ea580c", 700: "#c2410c", 800: "#9a3412", 900: "#7c2d12", 950: "#431407" },
          patina: { 50: "#f0fdfa", 100: "#ccfbf1", 200: "#99f6e4", 300: "#5eead4", 400: "#2dd4bf", 500: "#14b8a6", 600: "#0d9488", 700: "#0f766e", 800: "#115e59", 900: "#134e4a", 950: "#042f2e" },
        },
        fontFamily: {
          display: ['"Bricolage Grotesque"', "ui-sans-serif", "system-ui", "sans-serif"],
          sans: ['"IBM Plex Sans"', "ui-sans-serif", "system-ui", "sans-serif"],
          mono: ['"JetBrains Mono"', "ui-monospace", "monospace"],
        },
      },
    },
  };
  const merge = (a, b) => {
    for (const [k, v] of Object.entries(b ?? {})) {
      a[k] = v && typeof v === "object" && !Array.isArray(v) && a[k] && typeof a[k] === "object" && !Array.isArray(a[k]) ? merge(a[k], v) : v;
    }
    return a;
  };
  const config = merge(base, globalThis.RustzorTailwind);
  const apply = () => {
    if (globalThis.tailwind) globalThis.tailwind.config = config;
  };
  if (!document.querySelector("script[data-rz-tailwind]")) {
    const s = document.createElement("script");
    s.src = SRC;
    s.dataset.rzTailwind = "";
    s.onload = apply;
    document.head.appendChild(s);
  } else apply();

  const root = document.documentElement;
  if (!root.dataset.theme) root.dataset.theme = matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";

  ops["tw.config"] = ({ config: extra }) => {
    merge(config, extra);
    apply();
  };
  ops["tw.theme"] = ({ theme }) => {
    root.dataset.theme = theme;
  };
}
