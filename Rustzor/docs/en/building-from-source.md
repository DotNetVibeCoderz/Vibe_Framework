# Building from source

## Prerequisites

- .NET 10 SDK
- Rust (stable, 1.85 or later) with the WebAssembly target: `rustup target add wasm32-unknown-unknown`
- Node.js 20 or later, for the development server and the browser tests
- Chrome or Chromium, for the browser tests

RustNetRuntime comes from the [Vibe_Runtime](https://github.com/DotNetVibeCoderz/Vibe_Runtime) repository, pinned to a commit in `Cargo.toml`; Cargo fetches it. To work on the runtime and Rustzor together, copy `.cargo/config.local.toml.example` to `.cargo/config.toml` and point it at your runtime checkout.

## Build

```bash
bash scripts/build-runtime.sh               # rustzor.wasm + rustzor.js into src/Rustzor.Sdk/runtime
dotnet build src/Rustzor.Build -c Release   # the bundler and AOT compiler the build targets run
dotnet build Rustzor.slnx                   # libraries, samples, the playground server
```

`build-runtime.sh --size` builds the runtime optimised for download size instead of speed.

Apps inside the repository (samples, tests, templates) use the same build integration a NuGet user gets from `Rustzor.Sdk`, wired to the source tree by `eng/Rustzor.InRepo.targets`.

## Run a sample

```bash
node tools/dev/serve.mjs samples/Pos/bin/Debug/net10.0/wwwroot 5241
```

`serve.mjs` is a small static server with a fallback to `index.html`, so client-side routes survive a reload.

## Test

| What | Command |
| --- | --- |
| Every route of an app renders, with no console errors, and a screenshot of each | `node tools/e2e/pages.mjs http://localhost:5240/ out "" deals companies` |
| Drive an app step by step (click, fill, type, press, drag, eval, screenshot) | `node tools/e2e/probe.mjs <url> [shot.png] '<steps json>'` |
| Every AppGen template generates, builds and runs | `bash tools/RustzorAppGen/scripts/test-templates.sh` |
| An app built from the packed packages runs | `bash scripts/package-smoke.sh 0.1.0-preview.1` |
| The CLI and templates from the packed packages: new, add, info, publish, then run | `bash scripts/tooling-smoke.sh 0.1.0-preview.1` |
| The VS Code extension activates and registers its commands, inside VS Code | `code --extensionDevelopmentPath=tools/vscode-rustzor --extensionTestsPath=tools/vscode-rustzor/test/suite.js <a Rustzor app folder>`, with `RUSTZOR_EXT_REPORT=<file>` for the result |
| The runtime: unit tests, conformance against .NET, cross-assembly, no_std targets | in the Vibe_Runtime repository: `cargo test --workspace`, `bash tests/embedded.sh` |

The browser tools need `npm ci` in `tools/e2e` once, and `CHROME_PATH` when Chrome is not at its Windows default location.

## Pack

```bash
bash scripts/pack.sh                     # every package at the version in src/Directory.Build.props
bash scripts/pack.sh 0.2.0-preview.1     # or at a given version
```

The packages land in `artifacts/packages`. After adding a package, also run `node tools/e2e/make-icons.mjs` (its icon) and `node tools/e2e/make-catalog.mjs` (the catalog and `packages.md`). The VS Code extension is packed with `npx @vscode/vsce package --no-dependencies` in `tools/vscode-rustzor`. `Rustzor.Sdk` carries the runtime (`runtime/`), the build tool (`tools/`) and a `Version.props` that pins its implicit `Rustzor.Components` reference to the same version. Each package has its own icon in `assets/icons`, made by `node tools/e2e/make-icons.mjs`; a new package needs an entry there.

## Release

Releases are published from GitHub Actions in the Vibe_Framework repository:

- `rustzor-ci.yml` builds the runtime, the solution and every package; builds an app from those packages alone and runs it in headless Chrome; installs the CLI from them and creates, publishes and runs an app with it; checks the package catalog is current; and packs the VS Code extension (a `.vsix` build artifact).
- `rustzor-publish.yml` runs that CI at the release version and pushes the packages it tested to nuget.org with the `NUGET_API_KEY` secret. It starts on a tag `rustzor-v<version>` or by hand (with a dry-run option that does everything except push).

The workflows' source is `eng/workflows/`; the copies GitHub runs live in the repository root's `.github/workflows/`.
