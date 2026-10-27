# Architecture

> JS.Net — created by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/architecture.md)

JS.Net follows the design in [architecture-design.md](../../architecture-design.md): a JavaScript-first language that produces standard CLR IL, uses the existing runtime and integrates with the whole .NET ecosystem. It implements the **Roslyn integration strategy** — a JS.Net front end with a Roslyn back end.

```text
            ┌──────────────────────── JS.Net front end ────────────────────────┐
 .jsn ───►  │ Lexer ─► Parser ─► AST ─► Binder / type inference ─► C# emitter  │ ───► .g.cs (+ #line)
            └────────────────────────────────────────────────────────────────────┘
                                                                         │
                         Roslyn (csc / in-memory) ◄──────────────────────┘
                                   │
                     IL + portable PDB ─► CoreCLR: GC, tiered JIT, ReadyToRun, NativeAOT
```

## Components

| Project | Responsibility |
|---|---|
| `JSNet.Syntax` | `SourceText` (line map), tokens, lexer, AST, diagnostics, AST walker. The lexer emits `>` as a single token so `List<List<int>>` parses; the parser re-joins `>>`/`>=` in expressions. |
| `JSNet.Parser` | Recursive-descent parser with automatic semicolon insertion, speculative parsing (arrow functions, generic call arguments), attribute-vs-array disambiguation and statement-level error recovery. |
| `JSNet.Semantics` | Project-wide symbol collection, scopes, JavaScript rule checks (const reassignment, await outside async, break outside loops, super placement), analyzers (unused variables, integer division, unreachable code, dynamic fallback) and best-effort type inference (literals, operators, calls, fields, lambdas, generators, async). Also maps JS.Net types to CLR types. |
| `JSNet.CodeGen` | Lowers bound units to C#: modules → static classes, top-level statements → `Main`, JavaScript truthiness, `&&`/`||` value semantics, switch fall-through, destructuring, spread, template literals, decorators → attributes, data interfaces → hidden implementations, `ai function`/`prompt` → `JSNet.AI` calls. |
| `JSNet.Compiler` | Driver: parallel parse, single project-wide bind, parallel emit, global usings, namespaces from folders, diagnostics. |
| `JSNet.Runtime` | Small runtime library: `console`, `JSON`, `Math`, `Promise`, `Date`, timers, `fetch`, `JsObject`, JavaScript-style members as C# 14 extension members, `@Service` DI. |
| `JSNet.AI` | `AiRuntime` and `Prompt<T>` on `Microsoft.Extensions.AI.IChatClient`. |
| `JSNet.Sdk` | MSBuild SDK: incremental code generation before `CoreCompile`. |
| `JSNet.Scripting` | Roslyn in-memory compilation into a collectible `AssemblyLoadContext` (`jsnet run`, Gallery playground, tests). |
| `JSNet.Cli` | `jsnet` command. |
| `JSNet.LanguageServer` | Dependency-free LSP host. |
| `rust/jsnet-tools` | Native formatter, checker, tokenizer and statistics. |

## Design decisions

- **Why lower to C# instead of emitting IL directly?** Roslyn already provides correct IL for generics, async state machines, iterators, closures, pattern matching, PDBs, source generators (Razor, WPF, Avalonia, WinForms, `System.Text.Json`), analyzers and NativeAOT compatibility. Lowering to C# gives JS.Net all of them on day one; the front end stays focused on language semantics. `#line` directives keep diagnostics and debugging on `.jsn` lines.
- **Static where possible, dynamic where asked.** Annotations and inference produce CLR primitives and generics; `dynamic` is opt-in and reported (JSN2002) when it is a fallback.
- **JavaScript semantics where they matter.** Truthiness, `||`/`&&` values, `typeof`, `throw` of any value, `console.log` formatting and the array/string API follow JavaScript; arithmetic, integer division and collections follow the CLR for performance and interop.
- **Zero-cost runtime helpers.** JavaScript members on strings and collections are C# 14 extension members (static calls). `JS.truthy` overloads are resolved at compile time.
- **Global module visibility.** Every file's top-level functions are reachable from every other file (`global using static`), matching how JS.Net projects are compiled as one assembly.
- **Parallelism.** Parsing and emission run in parallel per file; binding is one project-wide pass.

## Build integration

`JSNet.Sdk.targets` runs `jsnet compile` before `BeforeCompile`/`CoreCompile`/`XamlPreCompile` with `Inputs`/`Outputs` for incrementality, writes only changed files, deletes stale outputs and adds `obj/…/jsnet/*.g.cs` to `@(Compile)`. Because generated code is ordinary C#, Razor components, WPF/Avalonia XAML code-behind and WinForms all merge with JS.Net partial classes.
