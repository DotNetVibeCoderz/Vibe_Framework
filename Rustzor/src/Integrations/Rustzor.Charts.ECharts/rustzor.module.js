// Rustzor.Charts.ECharts — Apache ECharts 5, loaded on first use, following the page theme.
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle, elementOf }) {
  const CDN = globalThis.RustzorCdn?.echarts ?? "https://cdn.jsdelivr.net/npm/echarts@5.5.1/dist/echarts.min.js";
  let loading = null;
  const load = () =>
    (loading ??= new Promise((resolve, reject) => {
      if (globalThis.echarts) return resolve(globalThis.echarts);
      const s = document.createElement("script");
      s.src = CDN;
      s.onload = () => resolve(globalThis.echarts);
      s.onerror = () => reject(new Error("could not load ECharts from " + CDN));
      document.head.appendChild(s);
    }));
  const css = (name, fallback) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
  const dark = () => {
    const t = document.documentElement.getAttribute("data-theme");
    return t ? t === "dark" : matchMedia("(prefers-color-scheme: dark)").matches;
  };
  // Colours and type from the Rustzor tokens, so ECharts matches the app.
  const base = () => ({
    color: [1, 2, 3, 4, 5].map((i) => css(`--rz-chart-${i}`, ["#c2410c", "#0f766e", "#a16207", "#475569", "#9f1239"][i - 1])),
    backgroundColor: "transparent",
    textStyle: { fontFamily: css("--rz-font-body", "system-ui"), color: css("--rz-muted", "#6b665f") },
  });

  function create(box) {
    box.chart?.dispose();
    box.chart = globalThis.echarts.init(box.el, dark() ? "dark" : null, { renderer: "canvas" });
    box.chart.setOption({ ...base(), ...box.option });
    if (box.click) box.chart.on("click", (p) => dispatch(box.click, { series: p.seriesName ?? "", name: p.name ?? "", value: Number(Array.isArray(p.value) ? p.value[p.value.length - 1] : p.value) || 0, index: p.dataIndex ?? 0 }));
  }

  ops["echarts.create"] = async ({ el, option, click }) => {
    await load();
    const box = { el: elementOf(el), option, click, chart: null };
    create(box);
    box.resize = new ResizeObserver(() => box.chart?.resize());
    box.resize.observe(box.el);
    box.theme = new MutationObserver(() => create(box));
    box.theme.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return addHandle(box);
  };
  ops["echarts.set"] = ({ h, option, notMerge }) => {
    const box = getHandle(h);
    if (!box) return;
    box.option = notMerge ? option : { ...box.option, ...option };
    box.chart.setOption(notMerge ? { ...base(), ...option } : option, notMerge);
  };
  ops["echarts.image"] = ({ h }) => getHandle(h).chart.getDataURL({ pixelRatio: 2, backgroundColor: css("--rz-surface", "#fff") });
  ops["echarts.dispose"] = ({ h }) => {
    const box = getHandle(h);
    if (!box) return;
    box.resize.disconnect();
    box.theme.disconnect();
    box.chart.dispose();
    releaseHandle(h);
  };
}
