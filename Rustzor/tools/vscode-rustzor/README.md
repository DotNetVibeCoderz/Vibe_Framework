# Rustzor for Visual Studio Code

Build C# Razor apps that run in the browser on [RustNetRuntime](https://github.com/DotNetVibeCoderz/Vibe_Runtime), a .NET runtime written in Rust and compiled to WebAssembly.

## Commands

Open the Command Palette (`Ctrl+Shift+P`) and type **Rustzor**:

| Command | What it does |
| --- | --- |
| **Rustzor: New Project** | Creates an app (with the Rustzor.UI shell), an empty app, or a library, and opens it |
| **Rustzor: Serve with Live Reload** | Builds, serves on `http://localhost:5080` and rebuilds on every save; the browser reloads, or shows the build errors over the page |
| **Rustzor: Publish** / **Publish with AOT** | Publishes the static site and reports its size |
| **Rustzor: Add Package…** | Pick Rustzor packages from a searchable list |
| **Rustzor: Show Project Info** | Versions, packages and the size of the built site |
| **Rustzor: Install or Update the rustzor CLI** | `dotnet tool update -g Rustzor.Cli` |
| **Rustzor: Open Documentation** | The docs on GitHub |

In a workspace with a Rustzor app, a **Rustzor: serve** button sits in the status bar, and the explorer's right-click menu on a `.csproj` offers Serve, Publish and Add Package.

## Snippets

| Prefix | In | Gives you |
| --- | --- | --- |
| `rzpage` | Razor | A routed page with a header and a card |
| `rzcomp` | Razor | A component with a parameter and an event callback |
| `rzlayout` | Razor | A layout with the Rustzor.UI app shell |
| `rzgrid` | Razor | A searchable, sortable, paged data grid |
| `rzmodal` | Razor | A modal bound to a `bool` |
| `rztimer` | Razor | A refresh on an interval, disposed with the component |
| `rzprogram` | C# | `Program.cs` for a Rustzor app |
| `rzjs`, `rzjsobject` | C# | Calling the browser from C# |
| `rzfetch` | C# | A JSON list with `FetchClient` |
| `rzsignalr` | C# | A SignalR hub connection |
| `rzproto` | C# | A protobuf message for gRPC-Web |
| `rzsqlite` | C# | A persisted SQLite database |
| `rzmodule`, `rzop` | JavaScript | The browser half of a Rustzor library |

## Requirements

- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- The `rustzor` command: `dotnet tool install -g Rustzor.Cli --prerelease` (the extension offers to install it).
- For Razor editing and IntelliSense, Microsoft's C# Dev Kit or the C# extension.

## Settings

| Setting | Default | |
| --- | --- | --- |
| `rustzor.port` | `5080` | Port for Serve |
| `rustzor.openBrowser` | `true` | Open the browser when serving starts |
| `rustzor.cliPath` | `rustzor` | The `rustzor` command, if it is not on `PATH` |

---

Built by Gravicode Studios, led by Kang Fadhil. *Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
