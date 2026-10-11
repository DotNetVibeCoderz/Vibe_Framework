// Rustzor.Charts.ChartJs — Chart.js 4, loaded on first use, themed from Rustzor tokens.
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle, elementOf }) {
  const CDN = globalThis.RustzorCdn?.chartjs ?? "https://cdn.jsdelivr.net/npm/chart.js@4.4.4/dist/chart.umd.min.js";
  let loading = null;
  const load = () =>
    (loading ??= new Promise((resolve, reject) => {
      if (globalThis.Chart) return resolve(globalThis.Chart);
      const s = document.createElement("script");
      s.src = CDN;
      s.onload = () => resolve(globalThis.Chart);
      s.onerror = () => reject(new Error("could not load Chart.js from " + CDN));
      document.head.appendChild(s);
    }));

  const css = (name, fallback) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
  const palette = () => [1, 2, 3, 4, 5].map((i) => css(`--rz-chart-${i}`, ["#c2410c", "#0f766e", "#a16207", "#475569", "#9f1239"][i - 1]));
  const alpha = (color, a) => {
    if (color.startsWith("#") && color.length === 7) return color + Math.round(a * 255).toString(16).padStart(2, "0");
    return color;
  };
  const merge = (base, extra) => {
    if (!extra || typeof extra !== "object") return base;
    for (const [k, v] of Object.entries(extra)) {
      base[k] = v && typeof v === "object" && !Array.isArray(v) && base[k] && typeof base[k] === "object" ? merge(base[k], v) : v;
    }
    return base;
  };

  const build = (cfg, clickId) => {
    const colors = palette();
    const round = ["pie", "doughnut", "polarArea"].includes(cfg.type);
    const datasets = cfg.datasets.map((d, i) => {
      const c = colors[i % colors.length];
      const out = { ...d };
      if (round && !d.backgroundColor) out.backgroundColor = d.data.map((_, j) => colors[j % colors.length]);
      if (!out.backgroundColor) out.backgroundColor = cfg.type === "line" || d.type === "line" ? alpha(c, 0.15) : alpha(c, 0.85);
      if (!out.borderColor) out.borderColor = round ? css("--rz-surface", "#fff") : c;
      if (!d.fill) out.fill = false;
      return out;
    });
    const fg = css("--rz-muted", "#6b665f");
    const grid = css("--rz-line", "#dcd8d2");
    const font = { family: css("--rz-font-body", "system-ui") };
    const scales = round || cfg.type === "radar"
      ? undefined
      : {
          x: { stacked: cfg.stacked, grid: { color: grid, display: !cfg.horizontal ? false : true }, ticks: { color: fg, font } },
          y: { stacked: cfg.stacked, grid: { color: grid }, ticks: { color: fg, font }, beginAtZero: true },
        };
    const options = merge(
      {
        responsive: true,
        maintainAspectRatio: false,
        indexAxis: cfg.horizontal ? "y" : "x",
        animation: matchMedia("(prefers-reduced-motion: reduce)").matches ? false : { duration: 400 },
        plugins: { legend: { display: cfg.legend, labels: { color: fg, font, usePointStyle: true } }, tooltip: { mode: "index", intersect: false } },
        scales,
        onClick: clickId
          ? (evt, els, chart) => {
              const e = els[0];
              if (!e) return;
              dispatch(clickId, {
                dataset: e.datasetIndex,
                index: e.index,
                label: String(chart.data.labels?.[e.index] ?? ""),
                value: Number(chart.data.datasets[e.datasetIndex].data[e.index]?.y ?? chart.data.datasets[e.datasetIndex].data[e.index]) || 0,
              });
            }
          : undefined,
      },
      cfg.options,
    );
    return { type: cfg.type, data: { labels: cfg.labels, datasets }, options };
  };

  ops["chartjs.create"] = async ({ el, config, click }) => {
    const Chart = await load();
    const chart = new Chart(elementOf(el), build(config, click));
    const box = { chart, config, click };
    // Re-theme when the page switches between light and dark.
    box.observer = new MutationObserver(() => {
      const next = build(box.config, box.click);
      chart.options = next.options;
      chart.data.datasets = next.data.datasets;
      chart.update("none");
    });
    box.observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return addHandle(box);
  };
  ops["chartjs.update"] = ({ h, config }) => {
    const box = getHandle(h);
    if (!box) return;
    box.config = config;
    const next = build(config, box.click);
    box.chart.config.type = next.type;
    box.chart.data.labels = next.data.labels;
    box.chart.data.datasets = next.data.datasets;
    box.chart.options = next.options;
    box.chart.update();
  };
  ops["chartjs.destroy"] = ({ h }) => {
    const box = getHandle(h);
    box?.observer.disconnect();
    box?.chart.destroy();
    releaseHandle(h);
  };
}
