# JS.Net in JetBrains Rider and IntelliJ IDEA

> JS.Net — created by Gravicode Studios, led by Kang Fadhil. · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

JetBrains IDEs get full JS.Net support from two generic, free building blocks — no JS.Net-specific plugin is required.

## 1. Language server (diagnostics, completion, hover, navigation, rename, quick fixes, colouring)

1. Install the JS.Net CLI: `dotnet tool install -g JSNet.Cli` (provides `jsnet`).
2. Install the **LSP4IJ** plugin (Settings → Plugins → Marketplace → "LSP4IJ").
3. Settings → Languages & Frameworks → Language Servers → **+** → *Template* → **Import from custom template…** and pick
   the [`lsp4ij-template`](lsp4ij-template) folder of this repository.
4. Open a `.jsn` file.

The template starts `jsnet lsp` for `*.jsn` files. Semantic tokens give syntax colouring even without step 2 below.

## 2. TextMate grammar (optional, instant colouring)

Settings → Editor → TextMate Bundles → **+** → select the [`vscode/jsnet-vscode`](../vscode/jsnet-vscode) folder.
JetBrains IDEs read VS Code extension folders as TextMate bundles.

---

# JS.Net di JetBrains Rider dan IntelliJ IDEA

1. Pasang CLI: `dotnet tool install -g JSNet.Cli`.
2. Pasang plugin **LSP4IJ** dari Marketplace.
3. Settings → Languages & Frameworks → Language Servers → **+** → *Template* → **Import from custom template…**, pilih folder
   [`lsp4ij-template`](lsp4ij-template).
4. (Opsional) Settings → Editor → TextMate Bundles → **+** → pilih folder [`vscode/jsnet-vscode`](../vscode/jsnet-vscode).
