# RustNet Tools for VSCode

Flash, run and monitor RustNet devices without leaving the editor.

## Commands (Ctrl+Shift+P)

- **RustNet: Flash Current Project** — builds nothing itself; flashes `bin/Debug/net10.0/<Project>.dll` (compile + sign + upload + start)
- **RustNet: Device Info / List Apps / Start / Stop / Erase**
- **RustNet: Show / Follow Device Logs** — live log viewer in the Output panel
- **RustNet: Profiler Snapshot** — CPU/heap/GC/instruction counters
- **RustNet: Capture Display** — saves the device framebuffer as `display.ppm`
- **RustNet: Start Virtual Device** — launches the host firmware in a terminal
- **RustNet: New Project From Template**

## Setup

1. Build the CLI: `dotnet build dotnet/tools/RustNet.Cli`
2. Settings → `rustnet.cliPath` → path to `rustnet.exe`
3. `rustnet keys generate --out keys` → set `rustnet.signingKey` to `keys/rustnet-signing.key`
4. `npm install && npm run compile` in this folder, then F5 (Extension Development Host)
