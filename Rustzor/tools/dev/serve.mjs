// A tiny static server with SPA fallback, for development and tests.
// usage: node tools/dev/serve.mjs <dir> [port]
import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { extname, join, normalize } from "node:path";

const root = process.argv[2] || ".";
const port = Number(process.argv[3] || 5080);
const types = { ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".mjs": "text/javascript", ".wasm": "application/wasm", ".json": "application/json", ".css": "text/css", ".dll": "application/octet-stream", ".png": "image/png", ".svg": "image/svg+xml", ".ico": "image/x-icon", ".txt": "text/plain" };

createServer(async (req, res) => {
  const url = new URL(req.url, "http://x");
  let path = normalize(join(root, decodeURIComponent(url.pathname)));
  try {
    if ((await stat(path)).isDirectory()) path = join(path, "index.html");
  } catch {
    if (!extname(path)) path = join(root, "index.html");
  }
  try {
    const body = await readFile(path);
    res.writeHead(200, { "content-type": types[extname(path)] || "application/octet-stream", "cache-control": "no-cache" });
    res.end(body);
  } catch {
    res.writeHead(404).end("not found");
  }
}).listen(port, () => console.log(`serving ${root} on http://localhost:${port}/`));
