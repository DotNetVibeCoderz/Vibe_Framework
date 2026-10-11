// Rustzor.Charts.D3 — D3.js 7 visualisations, loaded on first use.
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle, elementOf }) {
  const CDN = globalThis.RustzorCdn?.d3 ?? "https://cdn.jsdelivr.net/npm/d3@7.9.0/dist/d3.min.js";
  let loading = null;
  const load = () =>
    (loading ??= new Promise((resolve, reject) => {
      if (globalThis.d3) return resolve(globalThis.d3);
      const s = document.createElement("script");
      s.src = CDN;
      s.onload = () => resolve(globalThis.d3);
      s.onerror = () => reject(new Error("could not load D3 from " + CDN));
      document.head.appendChild(s);
    }));
  const css = (name, fallback) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
  const colors = () => [1, 2, 3, 4, 5].map((i) => css(`--rz-chart-${i}`, ["#c2410c", "#0f766e", "#a16207", "#475569", "#9f1239"][i - 1]));
  const text = () => css("--rz-muted", "#6b665f");
  const line = () => css("--rz-line", "#dcd8d2");
  const font = () => css("--rz-font-mono", "monospace");

  const fresh = (d3, el) => {
    d3.select(el).selectAll("*").remove();
    const width = el.clientWidth || 600;
    const height = el.clientHeight || 320;
    const svg = d3.select(el).append("svg").attr("viewBox", `0 0 ${width} ${height}`).attr("width", "100%").attr("height", "100%");
    svg.style("font-family", font()).style("font-size", "11px");
    return { svg, width, height };
  };
  const axisStyle = (g) => {
    g.selectAll("text").attr("fill", text());
    g.selectAll("line,path").attr("stroke", line());
  };

  const renderers = {
    bar(d3, el, spec, select) {
      const { svg, width, height } = fresh(d3, el);
      const m = { t: 12, r: 12, b: 28, l: 48 };
      const x = d3.scaleBand().domain(spec.data.map((d) => d.label)).range([m.l, width - m.r]).padding(0.25);
      const y = d3.scaleLinear().domain([0, d3.max(spec.data, (d) => d.value) || 1]).nice().range([height - m.b, m.t]);
      svg.append("g").attr("transform", `translate(0,${height - m.b})`).call(d3.axisBottom(x).tickSizeOuter(0)).call(axisStyle);
      svg.append("g").attr("transform", `translate(${m.l},0)`).call(d3.axisLeft(y).ticks(5).tickSize(-(width - m.l - m.r))).call(axisStyle).call((g) => g.select(".domain").remove());
      svg.append("g").selectAll("rect").data(spec.data).join("rect")
        .attr("x", (d) => x(d.label)).attr("width", x.bandwidth()).attr("rx", 3)
        .attr("y", y(0)).attr("height", 0).attr("fill", colors()[0]).style("cursor", select ? "pointer" : null)
        .on("click", (_, d) => select && dispatch(select, d.label))
        .transition().duration(500).attr("y", (d) => y(d.value)).attr("height", (d) => y(0) - y(d.value));
    },
    line(d3, el, spec) {
      const { svg, width, height } = fresh(d3, el);
      const m = { t: 12, r: 16, b: 28, l: 48 };
      const data = spec.series.map((p) => ({ t: new Date(p.t), value: p.value }));
      const x = d3.scaleTime().domain(d3.extent(data, (d) => d.t)).range([m.l, width - m.r]);
      const y = d3.scaleLinear().domain([0, d3.max(data, (d) => d.value) || 1]).nice().range([height - m.b, m.t]);
      svg.append("g").attr("transform", `translate(0,${height - m.b})`).call(d3.axisBottom(x).ticks(6)).call(axisStyle);
      svg.append("g").attr("transform", `translate(${m.l},0)`).call(d3.axisLeft(y).ticks(5).tickSize(-(width - m.l - m.r))).call(axisStyle).call((g) => g.select(".domain").remove());
      const c = colors()[0];
      svg.append("path").datum(data).attr("fill", c).attr("fill-opacity", 0.12)
        .attr("d", d3.area().x((d) => x(d.t)).y0(y(0)).y1((d) => y(d.value)).curve(d3.curveMonotoneX));
      svg.append("path").datum(data).attr("fill", "none").attr("stroke", c).attr("stroke-width", 2)
        .attr("d", d3.line().x((d) => x(d.t)).y((d) => y(d.value)).curve(d3.curveMonotoneX));
      const last = data[data.length - 1];
      if (last) svg.append("circle").attr("cx", x(last.t)).attr("cy", y(last.value)).attr("r", 4).attr("fill", c);
    },
    pie(d3, el, spec, select) {
      const { svg, width, height } = fresh(d3, el);
      const r = Math.min(width, height) / 2 - 8;
      const g = svg.append("g").attr("transform", `translate(${width / 2},${height / 2})`);
      const arcs = d3.pie().value((d) => d.value).sort(null)(spec.data);
      const arc = d3.arc().innerRadius(r * 0.55).outerRadius(r).cornerRadius(3).padAngle(0.01);
      const c = colors();
      g.selectAll("path").data(arcs).join("path").attr("d", arc).attr("fill", (_, i) => c[i % c.length])
        .style("cursor", select ? "pointer" : null).on("click", (_, d) => select && dispatch(select, d.data.label))
        .append("title").text((d) => `${d.data.label}: ${d.data.value}`);
      g.selectAll("text").data(arcs).join("text").attr("transform", (d) => `translate(${arc.centroid(d)})`)
        .attr("text-anchor", "middle").attr("fill", "#fff").style("font-weight", 600)
        .text((d) => (d.endAngle - d.startAngle > 0.35 ? d.data.label : ""));
    },
    force(d3, el, spec, select, box) {
      const { svg, width, height } = fresh(d3, el);
      const nodes = spec.nodes.map((n) => ({ ...n }));
      const links = spec.links.map((l) => ({ ...l }));
      const c = colors();
      const sim = d3.forceSimulation(nodes)
        .force("link", d3.forceLink(links).id((d) => d.id).distance(70))
        .force("charge", d3.forceManyBody().strength(-180))
        .force("center", d3.forceCenter(width / 2, height / 2))
        .force("collide", d3.forceCollide().radius((d) => d.size + 4));
      box.sim = sim;
      const link = svg.append("g").attr("stroke", line()).selectAll("line").data(links).join("line").attr("stroke-width", (d) => Math.sqrt(d.weight) * 1.5);
      const node = svg.append("g").selectAll("g").data(nodes).join("g").style("cursor", "pointer")
        .on("click", (_, d) => select && dispatch(select, d.id))
        .call(d3.drag()
          .on("start", (e, d) => { if (!e.active) sim.alphaTarget(0.3).restart(); d.fx = d.x; d.fy = d.y; })
          .on("drag", (e, d) => { d.fx = e.x; d.fy = e.y; })
          .on("end", (e, d) => { if (!e.active) sim.alphaTarget(0); d.fx = null; d.fy = null; }));
      node.append("circle").attr("r", (d) => d.size).attr("fill", (d) => c[d.group % c.length]).attr("stroke", css("--rz-surface", "#fff")).attr("stroke-width", 1.5);
      node.append("text").text((d) => d.label).attr("x", (d) => d.size + 4).attr("y", 4).attr("fill", text());
      sim.on("tick", () => {
        link.attr("x1", (d) => d.source.x).attr("y1", (d) => d.source.y).attr("x2", (d) => d.target.x).attr("y2", (d) => d.target.y);
        node.attr("transform", (d) => `translate(${d.x},${d.y})`);
      });
    },
    treemap(d3, el, spec, select) {
      const { svg, width, height } = fresh(d3, el);
      const root = d3.hierarchy(spec.tree).sum((d) => d.value || 0).sort((a, b) => b.value - a.value);
      d3.treemap().size([width, height]).paddingInner(2).round(true)(root);
      const c = colors();
      const leaf = svg.selectAll("g").data(root.leaves()).join("g").attr("transform", (d) => `translate(${d.x0},${d.y0})`)
        .style("cursor", select ? "pointer" : null).on("click", (_, d) => select && dispatch(select, d.data.name));
      leaf.append("rect").attr("width", (d) => d.x1 - d.x0).attr("height", (d) => d.y1 - d.y0).attr("rx", 3)
        .attr("fill", (d) => c[(d.parent ? d.parent.parent ? d.parent.parent.children.indexOf(d.parent) : 0 : 0) % c.length]).attr("fill-opacity", 0.85);
      leaf.append("text").attr("x", 6).attr("y", 16).attr("fill", "#fff").style("font-weight", 600)
        .text((d) => (d.x1 - d.x0 > 50 && d.y1 - d.y0 > 22 ? d.data.name : ""));
      leaf.append("title").text((d) => `${d.data.name}: ${d.value}`);
    },
  };

  const draw = async (box) => {
    const d3 = await load();
    box.sim?.stop();
    const render = renderers[box.spec.kind];
    if (!render) throw new Error(`unknown D3 chart kind ${box.spec.kind}`);
    render(d3, box.el, box.spec, box.select, box);
  };
  ops["d3.create"] = async ({ el, spec, select }) => {
    const box = { el: elementOf(el), spec, select };
    await draw(box);
    let t = 0;
    box.resize = new ResizeObserver(() => {
      clearTimeout(t);
      t = setTimeout(() => draw(box), 120);
    });
    box.resize.observe(box.el);
    box.theme = new MutationObserver(() => draw(box));
    box.theme.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return addHandle(box);
  };
  ops["d3.update"] = ({ h, spec }) => {
    const box = getHandle(h);
    if (!box) return;
    box.spec = spec;
    draw(box);
  };
  ops["d3.dispose"] = ({ h }) => {
    const box = getHandle(h);
    if (!box) return;
    box.sim?.stop();
    box.resize.disconnect();
    box.theme.disconnect();
    releaseHandle(h);
  };
  ops["d3.script"] = async ({ el, code, data }) => {
    const d3 = await load();
    const host = elementOf(el);
    d3.select(host).selectAll("*").remove();
    const width = host.clientWidth || 600;
    const height = host.clientHeight || 320;
    const svg = d3.select(host).append("svg").attr("viewBox", `0 0 ${width} ${height}`).attr("width", "100%").attr("height", "100%");
    new Function("d3", "svg", "data", "width", "height", code)(d3, svg, data, width, height);
    return true;
  };
}
