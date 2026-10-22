# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

Rustzor is in preview (`0.1.0-preview.2`). The runtime integration, component model, UI kit, every `Rustzor.Browser.*` library, the four frameworks, the UI and chart integrations, the SignalR and gRPC-Web clients, RustzorAppGen and thirteen sample apps exist and are tested in Chrome. Phase 5 tooling exists too: the `rustzor` CLI (`src/Rustzor.Cli`), `dotnet new` templates (`src/Rustzor.Templates`), the VS Code extension (`tools/vscode-rustzor`) and the generated package catalog (`docs/catalog`). Not done yet: one of the thirteen sample apps in `solution-design.md` (the digital twin). `PLAN.md` has the roadmap and `Progress.md` what is done and how it was verified.

The original brief is `Rustzor.txt` (Indonesian) and the design is `solution-design.md`.

## Commands

```bash
bash scripts/build-runtime.sh               # RustNetRuntime -> src/Rustzor.Sdk/runtime/rustzor.wasm (+ js/rustzor.js)
dotnet build src/Rustzor.Build -c Release   # bundler + AOT compiler that the build targets invoke
dotnet build Rustzor.slnx                   # everything: libraries, samples, playground server
node tools/dev/serve.mjs <wwwroot> <port>   # static server with SPA fallback
node tools/e2e/pages.mjs <base> <outdir> "" route2 route3   # render routes, screenshot, fail on console errors
node tools/e2e/probe.mjs <url> [shot.png] '<steps json>'    # click/fill/type/press/drag/eval/shot/goto/dump
node tools/e2e/meet.mjs <base>              # the video-call sample: two tabs call each other (RZ_FAKE_MEDIA=1 gives probe/pages fake devices)
node tools/e2e/rupa.mjs <base>              # the design-tool sample: gestures, model checks, drag frame times with 300 shapes
node tools/e2e/kamargelap.mjs <base>        # the photo-editor sample: pixels read back after each edit, brush and filter timings
node tools/e2e/siaga.mjs <base>             # the GIS sample: identify, table sync, geodesic area, buffer, GeoJSON round trip, frame times
node tools/e2e/tilik.mjs <base>             # the CAD sample: picking, hide/isolate, section, snapped measurements, notes, BOM CSV, STL, frame times
node tools/e2e/galasin.mjs <base>           # the multiplayer game: two tabs play the rules out, a match with bots, a watcher, the referee's tab closing, frame and tick times
node tools/e2e/loket.mjs <base>             # the AI assistant: streaming, Stop, tool cards, document upload + citation, persistence, escaping (LOKET_AZURE_* env adds a live run)
bash tools/RustzorAppGen/scripts/test-templates.sh          # all 21 AppGen templates build and run
bash scripts/pack.sh [version]              # every NuGet package -> artifacts/packages
bash scripts/package-smoke.sh <version>     # build and run an app from the packed packages only
bash scripts/tooling-smoke.sh <version>     # CLI + templates from the packed packages: new, add, info, publish, run
node tools/e2e/make-catalog.mjs             # regenerate docs/catalog and docs/*/packages.md (CI checks they are current)
dotnet run --project src/Rustzor.Cli -- serve <app>   # the CLI from source
node tools/e2e/make-icons.mjs               # regenerate assets/icons/<PackageId>.png
```

The browser tools need `npm ci` in `tools/e2e` and Chrome (`CHROME_PATH` if not at the Windows default). There is no separate lint step; builds are warning-free and should stay so.

## Where things are

- `runtime/rustzor-wasm`: the browser seam over RustNetRuntime. `js/rustzor.js`: loader, core ops, DOM patcher (copied into the SDK by `build-runtime.sh`).
- `src/`: `Rustzor.Core`, `Rustzor.Components` (assembly name `Microsoft.AspNetCore.Components`, package `Rustzor.Components`), `Rustzor.UI`, `Rustzor.Build`, `Rustzor.Sdk`, and `Browser/`, `Frameworks/`, `Integrations/`, `Net/`.
- `samples/`: Crm, Erp, Pos, Dashboard, Chat, Meet (video calls), Rupa (design tool), KamarGelap (photo editor), Siaga (GIS flood map), Tilik (CAD design review), Loket (AI front-desk assistant), Galasin (multiplayer 3D gobak sodor), ApiPlayground (Client + ASP.NET Core Server).
- `tools/RustzorAppGen` (Avalonia + Semantic Kernel), `tools/e2e`, `tools/dev`.
- `docs/en`, `docs/id`, `README.md`, `README.id.md`; screenshots in `docs/images`.
- `eng/workflows`: source of the GitHub Actions workflows (the copies GitHub runs live in the monorepo root's `.github/workflows`).

## The runtime

RustNetRuntime lives in the Vibe_Runtime repository (`C:\experiment\VibeCoding\Vibe_Runtime\RustNetRuntime` locally), pinned by commit in `Cargo.toml`. `.cargo/config.toml` (git-ignored, from `.cargo/config.local.toml.example`) patches it to the local checkout. Fix runtime bugs there, not with workarounds here, and add a conformance check that runs on .NET and on RustCLR (`tests/fixtures/Conformance`, or `CrossAssembly` for two-assembly cases). Verify with `cargo test --workspace`, the conformance fixture under both runtimes, and `bash tests/embedded.sh`. Commit or push in Vibe_Runtime only when asked; when Rustzor depends on a runtime commit, update the `rev` in `Cargo.toml`.

## Project requirements (from `Rustzor.txt`)

- **Stack:** .NET 10 and Rust. Prioritize performance and memory efficiency.
- **Tracking files:** keep `PLAN.md` (development roadmap) and `Progress.md` (development progress) at the repo root, and update them as work proceeds.
- **Documentation:** write docs in both **English and Bahasa Indonesia**. Include screenshots in the README and the docs.
- **Attribution:** the docs and the apps must state "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" (English: "Built by Gravicode Studios, led by Kang Fadhil").
- **Supporting and sample apps:** give them a polished, professional UI/UX. Use the `frontend-design` skill when it is available.
- **NuGet packages:** each package needs a custom icon (`assets/icons`, generated by `tools/e2e/make-icons.mjs`; add a label there for a new package).
- **NuGet publishing:** through the `rustzor-publish.yml` workflow and the `NUGET_API_KEY` repository secret. A local key is in `C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt`, outside the repo. Read it only when publishing by hand, and never copy the key into repo files.
