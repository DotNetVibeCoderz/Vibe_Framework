# JS.Net — Roadmap / Rencana Pengembangan

> JS.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. Current progress: [Progress.md](Progress.md). Design: [architecture-design.md](architecture-design.md).

Legend / Keterangan: ✅ done / selesai · 🟡 partial / sebagian · ⬜ planned / direncanakan

## Phase 1 — Core language / Bahasa inti ✅

| Item | Status |
|---|---|
| Lexer, parser (ASI, generics, decorators, templates), AST | ✅ |
| Binder, scopes, JS rule checks, analyzers, type inference | ✅ |
| Lowering to C# + Roslyn IL backend with `#line` mapping | ✅ |
| CLR interop (types, generics, events, attributes, imports) | ✅ |
| Runtime library (console, JSON, Math, Promise, Date, fetch, timers, JS APIs) | ✅ |

## Phase 2 — Generics, async, LINQ, NuGet ✅

| Item | Status |
|---|---|
| Generic classes/functions/constraints | ✅ |
| async/await, Promise interop, generators, `for await` | ✅ |
| LINQ (lower-case API) and JS array/Map/Set APIs | ✅ |
| MSBuild SDK (`JSNet.Sdk`), NuGet packages, templates (10) | ✅ |
| NativeAOT publish | ✅ (typed code; reflection-based helpers are not trim-annotated yet) |

## Phase 3 — Tooling / IDE 🟡

| Item | Status |
|---|---|
| `jsnet` CLI (run, emit, check, compile, fmt, new, lsp) | ✅ |
| Language Server (diagnostics, completion, hover, definition, symbols, formatting) | ✅ |
| VS Code extension (grammar, snippets, LSP client) | ✅ |
| Native Rust tools (formatter, checker, tokenizer, stats) | ✅ |
| Debugging through `#line` PDB mapping | ✅ (stepping maps to `.jsn` lines) |
| Semantic completion on .NET members (Roslyn-backed) | ⬜ |
| Rename / find references / code actions | ⬜ |
| Visual Studio extension (VSIX) with project system | ⬜ |
| Rider plugin | ⬜ |

## Phase 4 — AI-native 🟡

| Item | Status |
|---|---|
| `ai function` and `prompt` declarations | ✅ |
| Microsoft.Extensions.AI `IChatClient` runtime, offline echo client | ✅ |
| Semantic Kernel plugins from JS.Net classes (`@KernelFunction`) | ⬜ |
| Structured output schemas from interfaces | ⬜ |
| Streaming (`for await` over `ai function*`) | ⬜ |

## Phase 5 — Platforms / Cloud 🟡

| Item | Status |
|---|---|
| ASP.NET Core (minimal API, MVC), Blazor, worker services | ✅ |
| WPF, WinForms, Avalonia | ✅ |
| .NET MAUI template | ⬜ |
| Orleans grains, Aspire app host templates | ⬜ |
| Packages `JSNet.Web`, `JSNet.EntityFramework`, `JSNet.Blazor`, `JSNet.Reactive` | ⬜ |

## Language backlog / Daftar fitur bahasa

| Feature | Notes |
|---|---|
| Object type literals (`{ a: string }`) | Lower to generated records. |
| Object spread `{ ...a, b: 1 }` | Records `with` / dictionary merge. |
| Discriminated unions (`type Shape = Circle \| Square`) | Closed hierarchies + `switch` exhaustiveness. |
| `out`/`ref` parameters, `checked`, operators overloading | CLR parity. |
| Regex literals `/x+/g` | Lower to `Regex`. |
| Class expressions, labelled statements | |
| Trim/AOT annotations for runtime reflection helpers | Silence trimming warnings. |
| `rule` declarations for custom analyzers (design §22) | Compile to Roslyn analyzers. |
| Incremental, in-process build server | Avoid one process per build. |

## Gallery / Galeri

| Item | Status |
|---|---|
| Shared sample library (20 samples) written in JS.Net | ✅ |
| Blazor, WPF, WinForms, Avalonia front-ends | ✅ |
| Browser playground (server-side) | ✅ |
| Samples using RLNet, Visual.Net, ThreeNet, GravicodeScience, HF.Net, MediaPipeNet, IoTComNet | ⬜ (needs those packages on NuGet / as project references) |
| WebAssembly playground (client-side compiler) | ⬜ |

## Release / Rilis

1. `pwsh build/pack.ps1` → packages in `artifacts/packages`.
2. Push to nuget.org (API key kept outside the repo).
3. Publish the VS Code extension to the Marketplace.
4. Tag `v0.1.0`.
