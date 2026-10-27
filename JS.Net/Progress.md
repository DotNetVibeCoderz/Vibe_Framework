# JS.Net — Progress / Progres Pengembangan

> JS.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. Roadmap: [PLAN.md](PLAN.md).

## Status — 2026-10-27 (v0.2.0 candidate)

| Area | State / Keadaan | Verified by / Diverifikasi dengan |
|---|---|---|
| Compiler (Syntax, Parser, Semantics, CodeGen, Compiler) | Working; object types, spread, unions, regex, class expressions, labels, `out`/`ref` | 117 xUnit tests (`tests/JSNet.Tests`) incl. 27 gallery samples run end-to-end |
| Runtime (`JSNet.Runtime`) | Working, trim/AOT-annotated | Tests, gallery samples, NativeAOT publish of `samples/HelloJsNet` with zero IL warnings |
| AI (`JSNet.AI`) | Tools, streaming, structured output, Semantic Kernel plugins | `AiTests` |
| Analyzers (`JSNet.Analyzers`) | `rule` declarations as custom analyzers | `AnalyzerRuleTests`, `samples/RulesDemo` |
| Packages Web / EntityFramework / Blazor / Reactive | Working | `PackageTests` (real Kestrel, SQLite, HtmlRenderer, Rx) |
| MSBuild SDK (`JSNet.Sdk`) | Incremental, errors mapped to `.jsn`, `JSNetAnalyzer` items, MAUI `Platforms/<platform>` | `samples/*`, gallery projects |
| Templates (13) | console, classlib, webapi, web, blazor, wpf, winforms, avalonia, worker, xunit, maui, orleans, aspire | `build/smoke.ps1` from packed packages (maui when the workload is installed) |
| CLI (`jsnet`) | run, emit, check (`--analyzer`), compile, fmt, new, lsp, info | Tests + smoke |
| Language server | Roslyn-backed .NET completion/hover, references, rename, quick fixes, semantic tokens | `LanguageServerTests` |
| Editors | VS Code VSIX, Visual Studio VSIX, JetBrains LSP4IJ template | CI artifacts |
| Rust `jsnet-tools` | fmt, check, tokens, stats | `cargo test` |
| Gallery | Blazor (+ server playground), WPF, WinForms, Avalonia, WebAssembly playground | Screenshots in `docs/images` |
| Docs EN/ID | Updated for all of the above | — |
| NuGet | 0.1.0 and 0.1.1 published (5 packages); 0.2.0 adds Analyzers, Web, EntityFramework, Blazor, Reactive | — |

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

### 2026-10-27 — toward 0.2.0
- Language: object type literals (generated records), object spread, class unions with `instanceof` narrowing, regex literals, class expressions and local classes, labelled `break`/`continue`, `out`/`ref`.
- NativeAOT: runtime marked `IsAotCompatible`; reflection helpers annotated; AOT-safe `JSON.stringify/parse(JsonTypeInfo)`.
- AI: `@AiTool` tools, `ai function*` streaming, JSON-schema structured output, Semantic Kernel plugin test.
- Language server: Roslyn-backed .NET member completion and hover, references, rename, quick fixes, semantic tokens; parser keeps the next statement after a trailing dot.
- `rule` declarations + `JSNet.Analyzers`; packages `JSNet.Web`, `JSNet.EntityFramework`, `JSNet.Blazor`, `JSNet.Reactive`; templates MAUI, Orleans, Aspire; Visual Studio extension and JetBrains setup.
- Gallery: seven ecosystem samples on real NuGet packages (Gravicode.RLNet, Gravicode.Matplotlib.Net, Gravicode.Science.GraviLearn, Gravicode.HFNet.GraviTokenizers, Gravicode.MediaPipeNet, IoTCom.Net, ThreeNet); WebAssembly playground (`gallery/JSNet.Playground`) compiling with Roslyn in the browser — an async `Main` is awaited directly because the browser runtime cannot block.

## Next steps / Langkah berikutnya
1. Release 0.2.0 (tag `jsnet-v0.2.0`) after confirmation.
2. Host the WebAssembly playground as a static site.
3. Publish the VS Code and Visual Studio extensions to their marketplaces.
4. Union exhaustiveness checks; in-process build server.
