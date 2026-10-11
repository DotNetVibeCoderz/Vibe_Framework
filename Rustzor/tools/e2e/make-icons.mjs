// Renders a NuGet icon for every Rustzor package into assets/icons/<PackageId>.png.
// The family keeps the Rustzor mark (a hex nut on a rounded square); each
// package gets its own label, and its group its own colour, so a list of
// installed packages reads at a glance.
// usage: node tools/e2e/make-icons.mjs
import { chromium } from "playwright-core";
import { mkdirSync, readdirSync, statSync } from "node:fs";
import { join, basename, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const out = join(root, "assets", "icons");
mkdirSync(out, { recursive: true });

const groups = {
  core: ["#f97316", "#c2410c"],
  browser: ["#14b8a6", "#0f5f59"],
  frameworks: ["#8b5cf6", "#5b21b6"],
  integrations: ["#f59e0b", "#b45309"],
  net: ["#6366f1", "#3730a3"],
};

// Package id -> [label, group]. Every packable project under src/ must be listed.
const labels = {
  "Rustzor.Core": ["Rz", "core"], "Rustzor.Components": ["</>", "core"], "Rustzor.Sdk": ["SDK", "core"], "Rustzor.UI": ["UI", "core"],
  "Rustzor.Cli": [">_", "core"], "Rustzor.Templates": ["NEW", "core"],
  "Rustzor.Browser.AI": ["AI", "browser"], "Rustzor.Browser.Audio": ["AUD", "browser"], "Rustzor.Browser.BackgroundSync": ["SYNC", "browser"],
  "Rustzor.Browser.Bluetooth": ["BT", "browser"], "Rustzor.Browser.Canvas": ["2D", "browser"], "Rustzor.Browser.DOM": ["DOM", "browser"],
  "Rustzor.Browser.Fetch": ["GET", "browser"], "Rustzor.Browser.FileSystem": ["FS", "browser"], "Rustzor.Browser.Gamepad": ["PAD", "browser"],
  "Rustzor.Browser.HID": ["HID", "browser"], "Rustzor.Browser.Location": ["GEO", "browser"], "Rustzor.Browser.Media": ["CAM", "browser"],
  "Rustzor.Browser.NFC": ["NFC", "browser"], "Rustzor.Browser.Notifications": ["NTF", "browser"], "Rustzor.Browser.OffscreenCanvas": ["OFF", "browser"],
  "Rustzor.Browser.Push": ["PSH", "browser"], "Rustzor.Browser.Sensors": ["SNS", "browser"], "Rustzor.Browser.Serial": ["COM", "browser"],
  "Rustzor.Browser.Storage": ["KV", "browser"], "Rustzor.Browser.USB": ["USB", "browser"], "Rustzor.Browser.Video": ["VID", "browser"],
  "Rustzor.Browser.WebGL": ["GL", "browser"], "Rustzor.Browser.WebGPU": ["GPU", "browser"], "Rustzor.Browser.WebRTC": ["RTC", "browser"],
  "Rustzor.Browser.WebSocket": ["WS", "browser"], "Rustzor.Browser.WebXR": ["XR", "browser"], "Rustzor.Browser.Workers": ["WRK", "browser"],
  "Rustzor.AI": ["AI", "frameworks"], "Rustzor.Data": ["SQL", "frameworks"], "Rustzor.Game": ["GAME", "frameworks"], "Rustzor.Graphics": ["GFX", "frameworks"],
  "Rustzor.Charts.ChartJs": ["CJS", "integrations"], "Rustzor.Charts.D3": ["D3", "integrations"], "Rustzor.Charts.ECharts": ["EC", "integrations"],
  "Rustzor.UI.Bootstrap": ["BS", "integrations"], "Rustzor.UI.Mui": ["MUI", "integrations"], "Rustzor.UI.Tailwind": ["TW", "integrations"],
  "Rustzor.Net.SignalR": ["SR", "net"], "Rustzor.Net.GrpcWeb": ["gRPC", "net"],
};

// Check the list against the projects, so a new package cannot ship without an icon.
const projects = [];
const walk = (dir) => {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory() && !["bin", "obj", "content"].includes(name)) walk(p);
    else if (name.endsWith(".csproj") && name !== "Rustzor.Build.csproj") projects.push(basename(name, ".csproj"));
  }
};
walk(join(root, "src"));
const missing = projects.filter((p) => !labels[p]);
if (missing.length) {
  console.error("No icon label for: " + missing.join(", "));
  process.exit(1);
}

const svg = (label, [light, dark]) => {
  const size = label.length <= 2 ? 92 : label.length === 3 ? 70 : 56;
  const text = label.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  return `<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${light}"/><stop offset="1" stop-color="${dark}"/></linearGradient></defs>
  <rect x="4" y="4" width="248" height="248" rx="56" fill="url(#g)"/>
  <polygon points="128,30 213,79 213,177 128,226 43,177 43,79" fill="rgba(0,0,0,.14)" stroke="#fff" stroke-width="13" stroke-linejoin="round"/>
  <text x="128" y="${128 + size * 0.36}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-weight="800" font-size="${size}" fill="#fff" letter-spacing="-1">${text}</text>
</svg>`;
};

const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH ?? "C:/Program Files/Google/Chrome/Application/chrome.exe", headless: true });
const page = await browser.newPage({ viewport: { width: 256, height: 256 } });
for (const id of projects) {
  const [label, group] = labels[id];
  await page.setContent(`<html><body style="margin:0;background:transparent">${svg(label, groups[group])}</body></html>`);
  await page.locator("svg").screenshot({ path: join(out, `${id}.png`), omitBackground: true });
}
await browser.close();
console.log(`${projects.length} icons in ${out}`);
