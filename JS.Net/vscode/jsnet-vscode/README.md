# JS.Net for Visual Studio Code

Language support for **JS.Net** (`.jsn`) — JavaScript/TypeScript syntax compiled to .NET IL.

- Syntax highlighting (TextMate grammar)
- Diagnostics, completion, hover, go to definition and outline via the JS.Net language server (`jsnet lsp`)
- Formatting via the native Rust formatter (`jsnet-tools fmt`) when installed
- Snippets: `fn`, `afn`, `cls`, `iface`, `forof`, `log`, `try`, `service`, `aifn`

## Requirements

```bash
dotnet tool install -g JSNet.Cli    # provides the `jsnet` command
```

Set `jsnet.server.command` if `jsnet` is not on your PATH.

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
