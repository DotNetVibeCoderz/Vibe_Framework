# Tooling

> JS.Net — created by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/tooling.md)

## The `jsnet` CLI (`JSNet.Cli`)

```bash
dotnet tool install -g JSNet.Cli
```

| Command | Description |
|---|---|
| `jsnet run <file.jsn \| dir> [-- args]` | Compile in memory with Roslyn and run. Set `JSNET_TRACE=1` for stack traces. |
| `jsnet emit <file.jsn> [--line] [--all]` | Print the generated C#. |
| `jsnet check <files \| dirs> [--library]` | Diagnostics only. |
| `jsnet compile --out <dir> [--root-namespace N] [--project-dir D] [--library] [--no-line] [--nowarn codes] <files \| @rsp>` | Code generation used by the MSBuild SDK. |
| `jsnet fmt <files \| dirs> [--check]` | Format with the native Rust formatter. |
| `jsnet new <template> [args]` | Shortcut for `dotnet new jsnet-<template>`. |
| `jsnet lsp` | Start the language server on stdio. |
| `jsnet info`, `jsnet --version` | Version information. |

## The MSBuild SDK (`JSNet.Sdk`)

```xml
<Project Sdk="JSNet.Sdk/0.1.0"> … </Project>
<!-- or on top of another SDK -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <Sdk Name="JSNet.Sdk" Version="0.1.0" />
</Project>
```

What it does on `dotnet build`:

1. Collects `**/*.jsn` into `@(JSNetCompile)`.
2. Runs `jsnet compile` incrementally (only when a `.jsn` file, the project or the compiler changes) into `obj/<config>/<tfm>/jsnet/*.g.cs`.
3. Adds the generated files to `@(Compile)`. Roslyn compiles them with any C# files in the project — mixing JS.Net and C# in one project is supported.
4. References `JSNet.Runtime` implicitly.

| Property | Default | Meaning |
|---|---|---|
| `EnableDefaultJSNetItems` | `true` | Include `**/*.jsn` automatically. |
| `JSNetRootNamespace` | `$(RootNamespace)` | Base namespace for folder namespaces. |
| `JSNetNoWarn` | — | Semicolon-separated JS.Net warnings to hide (e.g. `JSN2001;JSN2002`). |
| `JSNetEmitLineDirectives` | `true` | Map errors, PDBs and stack traces to `.jsn` lines. |
| `JSNetEnableAI` | `false` | Reference `JSNet.AI` for `ai function`/`prompt`. |
| `JSNetDisableImplicitRuntimeReference` | `false` | Do not reference `JSNet.Runtime` automatically. |
| `JSNetCompilerPath` | bundled | Use a different compiler build. |

Errors appear in MSBuild/IDE format against the original file, e.g. `Program.jsn(12,7): error CS0029: Cannot implicitly convert type 'string' to 'double'`.

## Language server and VS Code

`jsnet lsp` implements the Language Server Protocol over stdio: diagnostics (JS.Net and project-wide binding), completion (keywords, runtime globals, members, declarations, snippets), hover, go to definition, document symbols and formatting (through `jsnet-tools`).

The VS Code extension (`vscode/jsnet-vscode`, packaged as `artifacts/vscode/jsnet-0.1.0.vsix`) adds the TextMate grammar, language configuration, snippets and the LSP client. Settings: `jsnet.server.command` (default `jsnet`) and `jsnet.server.args` (default `["lsp"]`).

```bash
cd vscode/jsnet-vscode
npm install
npx vsce package --allow-missing-repository
```

## Native tools in Rust (`jsnet-tools`)

A dependency-free Rust binary for jobs that benefit from native speed and parallelism:

| Command | Description |
|---|---|
| `jsnet-tools fmt <files \| dirs> [--check] [--stdin] [--indent N]` | Token-aware formatter: re-indents by bracket depth (with `case` bodies and leading-dot chains), trims trailing whitespace, collapses blank lines; never touches string/template/comment contents. |
| `jsnet-tools check <files \| dirs>` | Parallel lexical validation: unterminated strings/templates/comments, unbalanced brackets. |
| `jsnet-tools tokens <file>` | Dump tokens. |
| `jsnet-tools stats <files \| dirs>` | Line, token, type and function counts with lexer throughput. |

```bash
cd rust/jsnet-tools
cargo build --release
cargo test
```

`jsnet fmt` and the language server find the binary via `JSNET_TOOLS_PATH`, next to the CLI (`native/`), or on `PATH`. The SDK package bundles the Windows build.

## Templates

`dotnet new install JSNet.Templates` installs ten templates (see the [README](../../README.md#templates)). Every template accepts `--sdkVersion` to pin the JS.Net version.

## Building and packing the repository

```bash
dotnet build JSNet.slnx
dotnet test tests/JSNet.Tests          # compiler, runtime and all gallery samples
pwsh build/pack.ps1 -Version 0.1.0     # artifacts/packages/*.nupkg
pwsh build/smoke.ps1 -Version 0.1.0    # install the packages in a clean folder, build every template
```

Releases are published by GitHub Actions in the Vibe_Framework repository: push a tag `jsnet-v0.1.0`, or run **Publish JS.Net to NuGet** manually (with `dry_run` on to rehearse). The workflow runs the full CI at that version and pushes exactly the tested packages using the `NUGET_API_KEY` repository secret.

Packages: `JSNet.Sdk`, `JSNet.Runtime`, `JSNet.AI`, `JSNet.Cli`, `JSNet.Templates`.
