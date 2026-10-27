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
| Object type literals → generated records | ✅ |
| Object spread `{ ...a, b: 1 }` (anonymous objects and class targets) | ✅ |
| Class unions with `instanceof` narrowing | ✅ |
| Regex literals `/x+/g` → `RegExp` | ✅ |
| Class expressions, local classes, labelled `break`/`continue` | ✅ |
| `out`/`ref` parameters and arguments (`out let x`, `ref this.field`) | ✅ |
| Union coverage warning for `instanceof` chains (JSN2006) | ✅ |

## Phase 2 — Generics, async, LINQ, NuGet, AOT ✅

| Item | Status |
|---|---|
| Generic classes/functions/constraints | ✅ |
| async/await, Promise interop, generators, `for await` | ✅ |
| LINQ (lower-case API) for `IEnumerable` and `IQueryable` | ✅ |
| MSBuild SDK (`JSNet.Sdk`), NuGet packages, templates (13) | ✅ |
| NativeAOT publish; runtime trim/AOT-annotated, AOT-safe `JSON` overloads | ✅ |

## Phase 3 — Tooling / IDE ✅

| Item | Status |
|---|---|
| `jsnet` CLI (run, emit, check, compile, fmt, new, lsp) | ✅ |
| Language Server (diagnostics, completion, hover, definition, symbols, formatting, semantic tokens) | ✅ |
| Roslyn-backed completion and hover for .NET members | ✅ |
| Rename, find references, quick fixes | ✅ |
| VS Code extension (grammar, snippets, LSP client) — Marketplace `GravicodeStudios.jsnet` | ✅ |
| Visual Studio extension (VisualStudio.Extensibility, LSP) — Marketplace `GravicodeStudios.jsnet-visualstudio` | ✅ |
| JetBrains Rider / IntelliJ via LSP4IJ template + TextMate bundle | ✅ |
| Native Rust tools (formatter, checker, tokenizer, stats) | ✅ |
| Debugging through `#line` PDB mapping | ✅ |
| `rule` declarations → custom analyzers (`JSNet.Analyzers`, `JSNetAnalyzer` items, `--analyzer`) | ✅ |

## Phase 4 — AI-native ✅

| Item | Status |
|---|---|
| `ai function` and `prompt` declarations | ✅ |
| Microsoft.Extensions.AI `IChatClient` runtime, offline echo client | ✅ |
| Tools (`@AiTool`, `AiRuntime.useTools`) with automatic function invocation | ✅ |
| Semantic Kernel plugins from JS.Net classes (`@KernelFunction`) | ✅ |
| Structured output (JSON schema from interfaces/classes) | ✅ |
| Streaming (`ai function*` + `for await`) | ✅ |

## Phase 5 — Platforms / Cloud ✅

| Item | Status |
|---|---|
| ASP.NET Core (minimal API, MVC), Blazor, worker services | ✅ |
| WPF, WinForms, Avalonia | ✅ |
| .NET MAUI template (Android, iOS, macOS, Windows) | ✅ |
| Orleans grains, Aspire app host templates | ✅ |
| Packages `JSNet.Web`, `JSNet.EntityFramework`, `JSNet.Blazor`, `JSNet.Reactive` | ✅ |

## Gallery / Galeri ✅

| Item | Status |
|---|---|
| Shared sample library (27 samples) written in JS.Net | ✅ |
| Blazor, WPF, WinForms, Avalonia front-ends | ✅ |
| Browser playground (server-side, in the Blazor gallery) | ✅ |
| Ecosystem samples: RLNet, Matplotlib.Net (Visual.Net), ThreeNet, GraviLearn, HF tokenizers, MediaPipeNet, IoTCom.Net | ✅ |
| WebAssembly playground (client-side compiler, `gallery/JSNet.Playground`) — live at https://dotnetvibecoderz.github.io/Vibe_Framework/ | ✅ |

## Backlog / Selanjutnya

| Item | Notes |
|---|---|
| Incremental, in-process build server | Avoid one compiler process per build. |

## Release / Rilis

1. `pwsh build/pack.ps1 -Version x.y.z` → packages in `artifacts/packages`; `pwsh build/smoke.ps1 -Version x.y.z`.
2. Push tag `jsnet-vx.y.z` (or run *Publish JS.Net to NuGet*); the workflow pushes with the `NUGET_API_KEY` secret.
