# JS.Net for Visual Studio

Language support for `.jsn` files in Visual Studio 2022 (17.14+) and Visual Studio 2026, built on
VisualStudio.Extensibility. The extension starts the JS.Net language server (`jsnet lsp`) and provides diagnostics,
completion (including .NET members), hover, go to definition, find references, rename, quick fixes and semantic colouring.

Requirements: `dotnet tool install -g JSNet.Cli` (or set `JSNET_PATH` to a `jsnet` executable).

Build: `dotnet build visualstudio/JSNet.VisualStudio -c Release` → `bin/Release/net8.0-windows8.0/JSNet.VisualStudio.vsix`.

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
