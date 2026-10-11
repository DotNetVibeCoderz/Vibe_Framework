// Runs .NET assemblies on rustzor.wasm under Node, printing the console.
// usage: node tests/node/run-dll.mjs <rustzor.wasm> <lib.dll>... <app.dll>
import { readFileSync } from "node:fs";
import { createRuntime } from "../../js/rustzor.js";

const [wasmPath, ...dlls] = process.argv.slice(2);
const out = [];
const rt = await createRuntime(readFileSync(wasmPath), {
  log: (level, text) => (level >= 2 ? process.stderr.write(text + "\n") : out.push(text)),
});
let last = -1;
for (const dll of dlls) last = rt.load(readFileSync(dll));
// RZ_AOT=<dir with app.aot.wasm and app.aot.txt> installs the AOT tier.
if (process.env.RZ_AOT) {
  const dir = process.env.RZ_AOT;
  const n = await rt.loadAot(readFileSync(dir + "/app.aot.wasm"), readFileSync(dir + "/app.aot.txt", "utf8"));
  console.error("aot methods: " + n);
}
const t0 = performance.now();
let code;
try {
  code = rt.runMain(last);
} catch (e) {
  console.error("FAILED:", e.message);
  code = -1;
}
const ms = performance.now() - t0;
process.stdout.write(out.join("\n") + "\n");
if (process.env.RZ_STATS) console.error(JSON.stringify({ ms, code, ...rt.stats() }));
process.exitCode = code < 0 ? 1 : 0;
