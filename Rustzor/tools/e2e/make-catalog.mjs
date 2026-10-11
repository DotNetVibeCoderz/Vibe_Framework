// Builds the package catalog from the projects themselves:
//   docs/catalog/index.html   a searchable page (GitHub Pages-ready), icons beside it
//   docs/en/packages.md       the same list for the docs
// Each package's description comes from its .csproj, its icon from assets/icons,
// and its `rustzor add` short name from the CLI. Run after adding a package:
//   node tools/e2e/make-catalog.mjs
import { readFileSync, writeFileSync, mkdirSync, copyFileSync, readdirSync, statSync } from "node:fs";
import { join, dirname, basename, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const version = readFileSync(join(root, "src", "Directory.Build.props"), "utf8").match(/<Version>([^<]+)<\/Version>/)[1];
const aliasSource = readFileSync(join(root, "src", "Rustzor.Cli", "AddCommand.cs"), "utf8");
const aliasesOf = {};
for (const m of aliasSource.matchAll(/\["([a-z0-9-]+)"\] = "(Rustzor[.A-Za-z0-9]+)"/g)) (aliasesOf[m[2]] ??= []).push(m[1]);

const groups = [
  { key: "core", title: "Core", blurb: "The SDK, the component model, the UI kit and the tools." },
  { key: "browser", title: "Browser APIs", blurb: "One package per browser API; an app downloads only the ones it references." },
  { key: "frameworks", title: "Frameworks", blurb: "Data, games, graphics and AI on top of the browser APIs." },
  { key: "integrations", title: "Integrations", blurb: "Established JavaScript UI and chart libraries, driven from C#." },
  { key: "net", title: "Networking", blurb: "Real-time and RPC clients for ASP.NET Core back ends." },
];
const groupOf = (path) =>
  path.includes("/Browser/") ? "browser" : path.includes("/Frameworks/") ? "frameworks" : path.includes("/Integrations/") ? "integrations" : path.includes("/Net/") ? "net" : "core";

const projects = [];
const walk = (dir) => {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) { if (!["bin", "obj", "content"].includes(name)) walk(p); }
    else if (name.endsWith(".csproj") && name !== "Rustzor.Build.csproj") projects.push(p);
  }
};
walk(join(root, "src"));

const decode = (s) => s.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&").replace(/\$\(Version\)/g, version);
const packages = projects.map((p) => {
  const xml = readFileSync(p, "utf8");
  const id = xml.match(/<PackageId>([^<]+)<\/PackageId>/)?.[1] ?? basename(p, ".csproj");
  const description = decode(xml.match(/<Description>([\s\S]*?)<\/Description>/)?.[1] ?? "").trim();
  const isTool = /<PackAsTool>true<\/PackAsTool>/.test(xml);
  const isTemplate = /<PackageType>Template<\/PackageType>/.test(xml);
  const isSdk = /<PackageType>MSBuildSdk<\/PackageType>/.test(xml);
  const install = isTool ? `dotnet tool install -g ${id} --prerelease`
    : isTemplate ? `dotnet new install ${id}::${version}`
    : isSdk ? `<Project Sdk="${id}/${version}">`
    : `dotnet add package ${id} --version ${version}`;
  const aliases = aliasesOf[id] ?? [];
  return { id, description, group: groupOf(relative(root, p).replace(/\\/g, "/")), install, alias: aliases[0], aliases };
}).sort((a, b) => a.id.localeCompare(b.id));

// Icons beside the page, so it works wherever the docs folder is served.
const out = join(root, "docs", "catalog");
mkdirSync(join(out, "icons"), { recursive: true });
for (const p of packages) copyFileSync(join(root, "assets", "icons", `${p.id}.png`), join(out, "icons", `${p.id}.png`));

const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
const card = (p) => `
      <article class="pkg" data-group="${p.group}" data-text="${esc((p.id + " " + p.description + " " + p.aliases.join(" ")).toLowerCase())}">
        <img src="icons/${p.id}.png" alt="" width="48" height="48" loading="lazy">
        <div class="pkg-body">
          <h3><a href="https://www.nuget.org/packages/${p.id}" target="_blank" rel="noopener">${p.id}</a></h3>
          <p>${esc(p.description)}</p>
          <div class="cmd"><code>${esc(p.install)}</code><button type="button" class="copy" data-copy="${esc(p.install)}" aria-label="Copy the install command for ${p.id}">Copy</button></div>
          ${p.alias ? `<div class="alias">or <code>rustzor add ${p.alias}</code></div>` : ""}
        </div>
      </article>`;

const sections = groups.map((g) => {
  const items = packages.filter((p) => p.group === g.key);
  return `
    <section class="group" data-key="${g.key}">
      <header><h2>${g.title} <span>${items.length}</span></h2><p>${g.blurb}</p></header>
      <div class="grid">${items.map(card).join("")}
      </div>
    </section>`;
}).join("");

const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Rustzor packages</title>
<meta name="description" content="Every Rustzor NuGet package, with its install command. Built by Gravicode Studios, led by Kang Fadhil.">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wght@12..96,600;12..96,800&family=IBM+Plex+Sans:wght@400;500;600&family=JetBrains+Mono:wght@400;500&display=swap">
<style>
  :root {
    --bg: #f4f2ef; --surface: #ffffff; --line: #dcd8d2; --fg: #1d1b19; --muted: #6b665f;
    --rust: #c2410c; --rust-soft: #fbe5d8; --patina: #0f766e; --code: #f7f5f2;
    color-scheme: light;
  }
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) { --bg: #141312; --surface: #1c1b19; --line: #34312d; --fg: #ece7e1; --muted: #a39b91; --rust: #f97316; --rust-soft: #3a2316; --patina: #2dd4bf; --code: #232120; color-scheme: dark; }
  }
  :root[data-theme="dark"] { --bg: #141312; --surface: #1c1b19; --line: #34312d; --fg: #ece7e1; --muted: #a39b91; --rust: #f97316; --rust-soft: #3a2316; --patina: #2dd4bf; --code: #232120; color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--fg); font: 15px/1.55 "IBM Plex Sans", "Segoe UI", system-ui, sans-serif; }
  .wrap { max-width: 1180px; margin: 0 auto; padding-inline: 20px; }
  code, .cmd { font-family: "JetBrains Mono", ui-monospace, Consolas, monospace; }
  a { color: inherit; }
  .hero { padding-block: 56px 28px; display: grid; grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr); gap: 40px; align-items: end; }
  .hero h1 { font: 800 clamp(34px, 5vw, 56px)/1.02 "Bricolage Grotesque", system-ui, sans-serif; letter-spacing: -0.03em; margin: 0 0 14px; text-wrap: balance; }
  .hero h1 em { font-style: normal; color: var(--rust); }
  .hero p { margin: 0; color: var(--muted); max-width: 52ch; }
  .start { background: var(--surface); border: 1px solid var(--line); border-radius: 14px; padding: 16px 18px; }
  .start h2 { font: 600 12px/1 "IBM Plex Sans", sans-serif; letter-spacing: .1em; text-transform: uppercase; color: var(--muted); margin: 0 0 10px; }
  .start ol { margin: 0; padding: 0; list-style: none; display: grid; gap: 8px; counter-reset: s; }
  .start li { display: grid; grid-template-columns: 22px minmax(0, 1fr); gap: 8px; align-items: baseline; counter-increment: s; }
  .start li::before { content: counter(s); font: 700 12px/22px "JetBrains Mono", monospace; text-align: center; border-radius: 6px; background: var(--rust-soft); color: var(--rust); }
  .start code { font-size: 13px; overflow-wrap: anywhere; }
  .tools { position: sticky; top: 0; z-index: 2; background: color-mix(in srgb, var(--bg) 92%, transparent); backdrop-filter: blur(8px); border-bottom: 1px solid var(--line); }
  .tools .wrap { display: flex; flex-wrap: wrap; gap: 10px; align-items: center; padding-block: 12px; }
  .tools input { flex: 1 1 260px; min-width: 0; height: 40px; padding: 0 14px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); color: var(--fg); font: inherit; }
  .tools input:focus { outline: 2px solid var(--rust); outline-offset: 1px; }
  .chips { display: flex; flex-wrap: wrap; gap: 6px; }
  .chip { height: 34px; padding: 0 12px; border-radius: 999px; border: 1px solid var(--line); background: var(--surface); color: var(--fg); font: 500 13px "IBM Plex Sans", sans-serif; cursor: pointer; }
  .chip[aria-pressed="true"] { background: var(--fg); color: var(--bg); border-color: var(--fg); }
  .count { color: var(--muted); font-size: 13px; margin-left: auto; }
  .group { padding-block: 26px 6px; }
  .group header { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 16px; margin-bottom: 14px; }
  .group h2 { margin: 0; font: 800 24px "Bricolage Grotesque", system-ui, sans-serif; letter-spacing: -0.01em; }
  .group h2 span { font: 500 13px "JetBrains Mono", monospace; color: var(--muted); }
  .group header p { margin: 0; color: var(--muted); }
  .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 12px; }
  .pkg { display: grid; grid-template-columns: 48px minmax(0, 1fr); gap: 14px; padding: 16px; background: var(--surface); border: 1px solid var(--line); border-radius: 14px; }
  .pkg img { border-radius: 12px; }
  .pkg-body { min-width: 0; display: flex; flex-direction: column; }
  .pkg h3 { margin: 0 0 4px; font: 600 15px "IBM Plex Sans", sans-serif; }
  .pkg h3 a { text-decoration: none; }
  .pkg h3 a:hover { color: var(--rust); text-decoration: underline; }
  .pkg p { margin: 0 0 10px; color: var(--muted); font-size: 13.5px; flex: 1; }
  .cmd { display: flex; align-items: center; gap: 6px; background: var(--code); border: 1px solid var(--line); border-radius: 8px; padding: 6px 6px 6px 10px; font-size: 12px; }
  .cmd code { flex: 1; min-width: 0; overflow-x: auto; white-space: nowrap; }
  .copy { flex: none; height: 26px; padding: 0 10px; border: 0; border-radius: 6px; background: var(--rust); color: #fff; font: 600 12px "IBM Plex Sans", sans-serif; cursor: pointer; }
  .copy.done { background: var(--patina); }
  .alias { margin-top: 6px; font-size: 12.5px; color: var(--muted); }
  .alias code { color: var(--patina); }
  .empty { padding: 40px 0; color: var(--muted); }
  footer { padding-block: 40px; color: var(--muted); font-size: 13px; border-top: 1px solid var(--line); margin-top: 30px; }
  [hidden] { display: none !important; }
  @media (max-width: 760px) {
    .hero { grid-template-columns: minmax(0, 1fr); padding-block: 36px 20px; gap: 20px; }
    .grid { grid-template-columns: minmax(0, 1fr); }
    .count { margin-left: 0; }
  }
</style>
</head>
<body>
  <div class="wrap hero">
    <div>
      <h1>Every <em>Rustzor</em> package, ready to install.</h1>
      <p>C# Razor apps in the browser on RustNetRuntime, a .NET runtime written in Rust and compiled to WebAssembly. ${packages.length} packages at <code>${version}</code>; reference only what your app uses.</p>
    </div>
    <div class="start">
      <h2>Start in three commands</h2>
      <ol>
        <li><code>dotnet tool install -g Rustzor.Cli --prerelease</code></li>
        <li><code>rustzor new app MyApp</code></li>
        <li><code>cd MyApp &amp;&amp; rustzor serve</code></li>
      </ol>
    </div>
  </div>
  <div class="tools">
    <div class="wrap">
      <input type="search" id="q" placeholder="Search: camera, sql, charts, signalr…" aria-label="Search packages">
      <div class="chips" role="group" aria-label="Group">
        <button class="chip" data-filter="all" aria-pressed="true">All</button>
        ${groups.map((g) => `<button class="chip" data-filter="${g.key}" aria-pressed="false">${g.title}</button>`).join("\n        ")}
      </div>
      <span class="count" id="count">${packages.length} packages</span>
    </div>
  </div>
  <main class="wrap">${sections}
    <p class="empty" id="empty" hidden>No package matches. Try a browser API name such as “bluetooth”, or a library such as “echarts”.</p>
  </main>
  <footer class="wrap">
    <p>Documentation: <a href="https://github.com/DotNetVibeCoderz/Vibe_Framework/tree/main/Rustzor/docs/en">English</a> · <a href="https://github.com/DotNetVibeCoderz/Vibe_Framework/tree/main/Rustzor/docs/id">Bahasa Indonesia</a> · VS Code: search “Rustzor” in Extensions.</p>
    <p>Built by Gravicode Studios, led by Kang Fadhil. <em>Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.</em></p>
  </footer>
<script>
  const q = document.getElementById("q"), count = document.getElementById("count"), empty = document.getElementById("empty");
  let group = "all";
  function apply() {
    const words = q.value.trim().toLowerCase().split(/\\s+/).filter(Boolean);
    let shown = 0;
    document.querySelectorAll(".pkg").forEach((el) => {
      const ok = (group === "all" || el.dataset.group === group) && words.every((w) => el.dataset.text.includes(w));
      el.hidden = !ok;
      if (ok) shown++;
    });
    document.querySelectorAll(".group").forEach((s) => (s.hidden = !s.querySelector(".pkg:not([hidden])")));
    count.textContent = shown + (shown === 1 ? " package" : " packages");
    empty.hidden = shown > 0;
  }
  q.addEventListener("input", apply);
  document.querySelectorAll(".chip").forEach((c) => c.addEventListener("click", () => {
    group = c.dataset.filter;
    document.querySelectorAll(".chip").forEach((x) => x.setAttribute("aria-pressed", String(x === c)));
    apply();
  }));
  document.addEventListener("click", async (e) => {
    const b = e.target.closest(".copy");
    if (!b) return;
    try { await navigator.clipboard.writeText(b.dataset.copy); }
    catch { const r = document.createRange(); r.selectNodeContents(b.previousElementSibling); getSelection().removeAllRanges(); getSelection().addRange(r); }
    b.textContent = "Copied"; b.classList.add("done");
    setTimeout(() => { b.textContent = "Copy"; b.classList.remove("done"); }, 1400);
  });
  const params = new URLSearchParams(location.search);
  if (params.get("q")) { q.value = params.get("q"); apply(); }
</script>
</body>
</html>
`;
writeFileSync(join(out, "index.html"), html);

// The same list as Markdown, in English and in Indonesian. Descriptions stay as
// the packages state them (in English); headings and prose are translated.
const row = (p) =>
  `| [\`${p.id}\`](https://www.nuget.org/packages/${p.id}) | ${p.alias ? "`" + p.alias + "`" : ""} | ${p.description.replace(/\|/g, "\\|").replace(/\n/g, " ")} |`;

const languages = {
  en: {
    title: "Packages",
    intro: `Every Rustzor package at \`${version}\`. A searchable version with copy buttons: [catalog/index.html](../catalog/index.html).`,
    start: "Start with `dotnet tool install -g Rustzor.Cli --prerelease`, then `rustzor new app MyApp`. Add a package with `rustzor add <short name>` or `dotnet add package <id> --version " + version + "`.",
    header: "| Package | Short name | What it is |",
    groups: Object.fromEntries(groups.map((g) => [g.key, [g.title, g.blurb]])),
    footer: "_This page is generated by `node tools/e2e/make-catalog.mjs`._",
  },
  id: {
    title: "Package",
    intro: `Semua package Rustzor versi \`${version}\`. Versi yang bisa dicari, dengan tombol salin: [catalog/index.html](../catalog/index.html).`,
    start: "Mulai dengan `dotnet tool install -g Rustzor.Cli --prerelease`, lalu `rustzor new app MyApp`. Tambahkan package dengan `rustzor add <nama pendek>` atau `dotnet add package <id> --version " + version + "`.",
    header: "| Package | Nama pendek | Keterangan (dalam bahasa Inggris) |",
    groups: {
      core: ["Inti", "SDK, model component, UI kit, dan tool-nya."],
      browser: ["API browser", "Satu package per API browser; aplikasi hanya mengunduh yang direferensikannya."],
      frameworks: ["Framework", "Data, game, grafis, dan AI di atas API browser."],
      integrations: ["Integrasi", "Library UI dan chart JavaScript populer, dikendalikan dari C#."],
      net: ["Jaringan", "Client real-time dan RPC untuk back end ASP.NET Core."],
    },
    footer: "_Halaman ini dibuat oleh `node tools/e2e/make-catalog.mjs`._",
  },
};

for (const [lang, t] of Object.entries(languages)) {
  const lines = [`# ${t.title}`, "", t.intro, "", t.start, ""];
  for (const g of groups) {
    const [title, blurb] = t.groups[g.key];
    lines.push(`## ${title}`, "", blurb, "", t.header, "| --- | --- | --- |", ...packages.filter((p) => p.group === g.key).map(row), "");
  }
  lines.push(t.footer, "");
  writeFileSync(join(root, "docs", lang, "packages.md"), lines.join("\n"));
}
console.log(`${packages.length} packages -> docs/catalog/index.html, docs/en/packages.md, docs/id/packages.md`);
