# Tooling: CLI, templates, VS Code and the package catalog

## The `rustzor` command

```bash
dotnet tool install -g Rustzor.Cli --prerelease
```

| Command | What it does |
| --- | --- |
| `rustzor new [app\|empty\|lib] [Name] [--title "My App"] [-o dir]` | Creates a project from the Rustzor templates, installing the templates of the CLI's own version first when they are missing |
| `rustzor serve [project] [-p 5080] [--open] [--no-watch]` | Builds, serves the site, and on every save rebuilds and reloads the open pages |
| `rustzor publish [project] [--aot] [-o dir]` | `dotnet publish -c Release` (with `RustzorAot` when asked) and a report of the download size, raw and gzipped |
| `rustzor add <package...>` | Adds Rustzor packages by short name (`ui`, `fetch`, `storage`, `data`, `signalr`, `grpc`, `camera`…) or full id, at the CLI's version |
| `rustzor info [project]` | Versions, the project's packages, and the size of the built site |

`[project]` is a `.csproj` or a folder with one; by default, the current folder.

### Serve and live reload

```
$ rustzor serve
▸ Building Shop.csproj
✓ Serving http://localhost:5080/
▸ Watching for changes. Press Ctrl+C to stop.
▸ Change detected, rebuilding
✓ Rebuilt in 2904 ms, reloaded 1 page
```

The development server answers every path that is not a file with `index.html`, so client-side routes survive a reload, and sends no-cache headers so a reload always gets the new build. It injects a small script into `index.html` (never into a published site) that listens for the server's events:

- after a successful rebuild, the page reloads;
- after a failed one, the build errors are shown over the page, with project-relative paths; the next good build clears them.

Changes to `.razor`, `.cs`, `.css`, `.html`, `.js`, `.json`, `.csproj` and image files trigger a rebuild; `bin/` and `obj/` are ignored, and a burst of saves is folded into one build.

### Publish

```
$ rustzor publish --aot
▸ Publishing Shop.csproj with AOT

  WebAssembly     2 files     1.78 MB  552.3 KB gzipped
  assemblies      5 files    245.5 KB  102.7 KB gzipped
  JavaScript      3 files     26.7 KB    8.6 KB gzipped
  ...
  total          19 files     2.07 MB  671.0 KB gzipped

✓ Ready to upload: …/bin/Release/net10.0/publish/wwwroot
```

## `dotnet new` templates

`rustzor new` uses these. They also work on their own, and once installed they appear in Rider's New Solution dialog and in Visual Studio's New Project dialog (with "Show all .NET CLI templates" turned on).

```bash
dotnet new install Rustzor.Templates
dotnet new rustzor -n Shop --Title "Kedai Senja"
dotnet new rustzor-empty -n Tiny
dotnet new rustzorlib -n Widgets
```

| Template | Short name | What you get |
| --- | --- | --- |
| Rustzor App | `rustzor` | The Rustzor.UI app shell with Home, Counter and Tasks (saved in `localStorage`) pages, ready to grow |
| Rustzor App (empty) | `rustzor-empty` | One page, no UI kit |
| Rustzor Library | `rustzorlib` | A Razor class library with a component, C# over a browser API, and its `rustzor.module.js` |

A template asks for the Rustzor packages of the version it was released with.

## Visual Studio Code

The **Rustzor** extension (`tools/vscode-rustzor`) puts the CLI into the editor:

- Commands (Command Palette, `Rustzor:`): New Project, Serve with Live Reload, Stop Serving, Publish, Publish with AOT, Add Package… (a searchable list of every package), Show Project Info, Install or Update the CLI, Open Documentation.
- A **Rustzor: serve** button in the status bar when the workspace has a Rustzor app, and Serve, Publish and Add Package on a `.csproj` in the explorer.
- Snippets: `rzpage`, `rzcomp`, `rzlayout`, `rzgrid`, `rzmodal`, `rztimer` (Razor); `rzprogram`, `rzjs`, `rzjsobject`, `rzfetch`, `rzsignalr`, `rzproto`, `rzsqlite` (C#); `rzmodule`, `rzop` (JavaScript).

Install it from the Marketplace: search for **Rustzor** in the Extensions view, or run `code --install-extension GravicodeStudios.rustzor`. The `.vsix` that CI builds also installs by hand (Extensions view, `…` menu, **Install from VSIX**).

## The package catalog

[`docs/catalog/index.html`](../catalog/index.html) lists every package with its icon, description and install command, with search, filters by group and copy buttons; [packages.md](packages.md) is the same list in Markdown. Both are generated from the projects by `node tools/e2e/make-catalog.mjs`, and CI fails when they are out of date.
