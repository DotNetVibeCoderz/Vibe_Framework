# JS.Net — Progress / Progres Pengembangan

> JS.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. Roadmap: [PLAN.md](PLAN.md).

## Status — 2026-10-27 (v0.1.0)

| Area | State / Keadaan | Verified by / Diverifikasi dengan |
|---|---|---|
| Compiler (Syntax, Parser, Semantics, CodeGen, Compiler) | Working / berfungsi | 88 xUnit tests (`tests/JSNet.Tests`) incl. 20 gallery samples run end-to-end |
| Runtime (`JSNet.Runtime`) | Working | Tests + gallery samples |
| AI (`JSNet.AI`) | Working (offline client by default) | `AiNative.jsn` sample, `AiFunctionsUseOfflineClientByDefault` test |
| MSBuild SDK (`JSNet.Sdk`) | Working, incremental, errors mapped to `.jsn` | `samples/HelloJsNet`, gallery projects |
| Templates (`JSNet.Templates`, 10 templates) | All 10 build from packed NuGet packages | Instantiated and built locally: console, classlib, webapi, web, blazor, wpf, winforms, avalonia, worker, xunit; console/webapi/worker run |
| NativeAOT | Console template publishes to a ~2.1 MB native exe | `dotnet publish -p:PublishAot=true` |
| CLI (`jsnet`) | run, emit, check, compile, fmt, new, lsp, info | Manual runs |
| Language server | initialize, diagnostics, completion, hover, definition, symbols, formatting | stdio smoke test |
| VS Code extension | Packaged `artifacts/vscode/jsnet-0.1.0.vsix` | `vsce package` |
| Rust `jsnet-tools` | fmt, check, tokens, stats | `cargo test` (4 tests) |
| Gallery | Blazor (+ playground), WPF, WinForms, Avalonia | Screenshots in `docs/images` |
| Docs EN/ID | README, getting started, language guide, interop & runtime, tooling, architecture, diagnostics | — |
| NuGet packages | Packed in `artifacts/packages` (Sdk, Runtime, AI, Cli, Templates) | Not yet pushed to nuget.org |

## Log / Catatan

### 2026-10-27
- Created the full solution from the design: compiler pipeline, runtime, AI runtime, SDK, CLI, LSP, scripting backend, Rust tools, templates, VS Code extension, gallery (4 front-ends), docs (EN/ID), icon.
- Fixed during gallery development (each now covered by tests or samples):
  - Lost parentheses around `(a || b) && c` in conditions.
  - Locals named like C# type keywords (`double`) not escaped.
  - Return-type inference running after scopes were discarded.
  - Lambda delegate types confused with lambda return types.
  - `reduce(..., 0)` with fractional accumulation (added a `number` overload).
  - Integer-initialised variables/fields that receive fractional values are now typed `double`.
  - Project types now win over BCL types with the same name (`Stack<T>`), via `Main` inside the namespace.
  - Array creation `new T[n]`, `typeof Class` for reflection, argument target-typing from project signatures.
  - MSBuild incrementality now tracks every compiler assembly.

### 2026-10-27 (later)
- Moved into the Vibe_Framework monorepo (`JS.Net/`). Added `.github/workflows/jsnet-ci.yml` (Windows: build, tests, Rust tests, pack at a version, package smoke test of all 10 templates and the tool, VS Code extension) and `jsnet-publish.yml` (tag `jsnet-v*` or a manual run with `dry_run`; pushes with the `NUGET_API_KEY` repository secret).
- `build/pack.ps1 -Version x.y.z` stamps every package and the templates' default SDK version; `build/smoke.ps1` verifies the packages from a clean folder. Verified locally at 0.0.0-ci.1.

### 2026-10-27 — 0.1.0 published, 0.1.1
- 0.1.0 published to nuget.org (JSNet.Runtime, JSNet.AI, JSNet.Sdk, JSNet.Cli, JSNet.Templates) by `jsnet-publish.yml`.
- 0.1.1: gallery builds warning-free (WPF `Title` element renamed `TitleText` so it no longer hides `Window.Title`; Blazor chart layout uses `300.0 /` instead of integer division). Default version, template `sdkVersion` and docs moved to 0.1.1; `pack.ps1` stamps any previous template default.

## Next steps / Langkah berikutnya
1. Publish: push the `jsnet-v0.1.0` tag (or run *Publish JS.Net to NuGet* with `dry_run` off); publish the VS Code extension to the Marketplace.
2. Roslyn-backed semantic completion for .NET members in the language server.
3. Object type literals and object spread.
4. Trim/AOT annotations for runtime helpers.
5. Gallery samples for the Gravicode supporting libraries once they are consumable packages.
