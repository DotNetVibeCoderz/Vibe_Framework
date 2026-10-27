# Diagnostics reference

> JS.Net — created by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/diagnostics.md)

JS.Net reports its own diagnostics (`JSNxxxx`) during code generation; C# compiler errors (`CSxxxx`) are mapped back to `.jsn` lines. Suppress warnings with `<JSNetNoWarn>JSN2001;JSN2002</JSNetNoWarn>` or `jsnet compile --nowarn`.

## Syntax (JSN0xxx)

| Code | Meaning |
|---|---|
| JSN0001 | Unexpected character. |
| JSN0002 | Unterminated string or template literal. |
| JSN0003 | Unterminated block comment. |
| JSN0010 | Unexpected token. |
| JSN0011 | Expected a specific token (e.g. `)` or `;`). |
| JSN0012 | Modifiers or attributes not followed by a declaration. |

## Semantics (JSN1xxx, errors)

| Code | Meaning | Fix |
|---|---|---|
| JSN1001 | Assignment to a `const`. | Use `let`. |
| JSN1002 | Duplicate declaration in the same scope. | Rename one of them. |
| JSN1003 | `await` outside an `async` function or the program's top level. | Mark the function `async`. |
| JSN1004 | `break`/`continue` outside a loop or switch. | |
| JSN1005 | `super` in a class without `extends`. | |
| JSN1006 | `super(...)` is not the first statement of the constructor. | Move it to the top. |
| JSN1007 | More than one file contains top-level statements. | Keep statements in one entry file. |
| JSN1008 | Top-level statements in a class library. | Move them into a function. |
| JSN1009 | Unsupported feature (object type literals, class expressions, object spread). | See the language guide. |

## Analyzers (JSN2xxx, warnings)

| Code | Meaning |
|---|---|
| JSN2001 | Variable declared but never used. Prefix with `_` to silence. |
| JSN2002 | Missing type information; the declaration is compiled as `dynamic`. Add annotations for full CLR performance. |
| JSN2003 | Integer division (`/` on two integers truncates). Use `number` or a decimal literal. |
| JSN2004 | Unreachable code after `return`/`throw`/`break`/`continue`. |
| JSN2005 | Union type without a CLR equivalent, lowered to `object`. |

## Driver and tools

| Code | Meaning |
|---|---|
| JSN3000 | Command-line or internal error. |
| JSN3001 | Source file not found / no input files. |
| JSN3002 | Script has no entry point. |
| JSN3003 | Native formatter `jsnet-tools` not found. |
| JSN3004 | MSBuild could not find the JS.Net compiler. |
| JSNR001–JSNR005 | `jsnet-tools check`: unterminated literal/comment, unexpected character, mismatched or unclosed brackets. |
