# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

JS.Net is a language with JavaScript/TypeScript syntax (`.jsn` files) that compiles to CLR IL. The original brief is in `JS.Net.txt` (Indonesian) and the design in `architecture-design.md`. User-facing docs are in `docs/en` and `docs/id`; keep them **bilingual** and in sync when behavior changes. Docs and apps carry the credit line "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil". Update `Progress.md` (and `PLAN.md` when scope changes) as work lands.

## Commands

```bash
dotnet build JSNet.slnx                         # everything (compiler, runtime, SDK, CLI, LSP, gallery, samples, tests)
dotnet test tests/JSNet.Tests                   # all tests
dotnet test tests/JSNet.Tests --filter "FullyQualifiedName~EndToEndTests"          # one class
dotnet test tests/JSNet.Tests --filter "FullyQualifiedName~GalleryTests.SampleRunsWithoutErrors"
dotnet run --project src/JSNet.Cli -- run file.jsn      # compile+run a script in memory
dotnet run --project src/JSNet.Cli -- emit file.jsn     # show the generated C# (best debugging tool)
pwsh build/pack.ps1 [-Version x.y.z]            # Release build + tests + nupkgs into artifacts/packages
pwsh build/smoke.ps1 -Version x.y.z             # install packed templates/tool in a temp folder, build every template
cd rust/jsnet-tools && cargo test && cargo build --release
cd vscode/jsnet-vscode && npm install && npx vsce package --allow-missing-repository
```

Gallery: `dotnet run --project gallery/JSNet.Gallery.Web` (port 5170; `/samples/<id>?run=1` auto-runs). Desktop galleries (`JSNet.Gallery.Wpf|WinForms|Avalonia`) accept `--screenshot <png> [--sample <id>] [--components]` to regenerate `docs/images/*`; web screenshots come from headless Edge.

WebAssembly playground: `gallery/JSNet.Playground` (Blazor WASM, untrimmed) runs the JS.Net front end + Roslyn in the browser (`BrowserCompiler.cs`: references from `Assembly.TryGetRawMetadata`; awaits an async `Main` directly since the browser can't block). `?example=N&run=1` auto-runs; verify with `msedge --headless=new --virtual-time-budget=60000 --dump-dom`. Kill the old dev server before restarting (it holds the port).

## Architecture (big picture)

Pipeline: `.jsn` → `JSNet.Syntax` (lexer/AST) → `JSNet.Parser` → `JSNet.Semantics` (`Binder`: scopes, JS rule checks, analyzers, type inference; `TypeMapper`: JS.Net→CLR types) → `JSNet.CodeGen` (`CSharpEmitter`, split into `.cs`/`.Statements.cs`/`.Expressions.cs`) → C# with `#line` maps → Roslyn → IL. `JSNet.Compiler.JsNetCompiler` drives it (parallel parse/emit, one project-wide bind) and adds `JSNet.GlobalUsings.g.cs`.

Key lowering rules that span files:
- Top-level functions/vars of `foo.jsn` → `public static partial class Foo` (or `FooModule` on name clash / `Main`), made global via `global using static`. Folder → namespace.
- The single file with top-level statements becomes `Main` *inside* that module class (so project types beat same-named BCL types); functions capturing its top-level variables become local functions. `function main()` becomes the entry point when there are no top-level statements.
- The binder records inferred types in `SemanticModel.Types` keyed by node. For a `FunctionExpression` the stored value is its **return** type, not the delegate type (use `Binder.InferType` for that). Integer-literal declarations later assigned fractional values are "promoted" to `double` (`SemanticModel.IsPromotedToNumber`).
- Conditions go through `Cond()` (JS truthiness via `JS.truthy` overloads); `&&`/`||` in value position become conditional expressions. Argument array/object literals are target-typed from project function/ctor signatures when known.

Runtime (`JSNet.Runtime`) gives JS APIs as C# 14 extension members (`extension(...)` blocks) — no wrappers. `JSNet.AI` implements `ai function`/`prompt` on `Microsoft.Extensions.AI`.

Build integration: `src/JSNet.Sdk/build/JSNet.Sdk.targets` runs `jsnet compile` (incremental, inputs include all `JSNet*.dll` of the compiler) into `obj/.../jsnet/*.g.cs` before `CoreCompile`. In-repo projects under `gallery/` and `samples/` use `build/JSNet.InRepo.props/.targets` (via their `Directory.Build.*`) to compile against the source tree; templates use the packaged `JSNet.Sdk`. Changing the compiler requires rebuilding `src/JSNet.Cli` before building gallery/samples (the Exec runs the built `jsnet.dll`).

Every gallery sample (`gallery/JSNet.Gallery.Samples/Samples/*.jsn`, registered in `Catalog.jsn`) runs in `GalleryTests`, so it doubles as a compiler regression test. Add a sample or an `EndToEndTests` case when fixing lowering bugs.

## Constraints

- .NET 10 (`net10.0`), C# 14 for generated code. Rust only for native tooling (`rust/jsnet-tools`, std-only).
- This project lives in the `Vibe_Framework` monorepo under `JS.Net/`. CI and publish workflows are at the repo root: `.github/workflows/jsnet-ci.yml` and `jsnet-publish.yml` (tag `jsnet-v<version>`, `dry_run` input, `NUGET_API_KEY` repository secret). Publishing is irreversible — confirm with the user first.
- The Bash tool's heredocs sometimes break on large quoted content in this environment; write scripts with the Write tool instead.
- `rea` MCP (reverse engineering) can inspect .NET assemblies if IL-level investigation is needed.
