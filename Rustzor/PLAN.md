# Rustzor development plan

The roadmap from `solution-design.md`, broken into deliverables. `Progress.md`
records what is done and how it was verified.

## Phase 1: foundation

| Deliverable | Where |
| --- | --- |
| RustNetRuntime compiled to WebAssembly, with a plain `rz_*` ABI | `runtime/rustzor-wasm` |
| JavaScript host: loader, interop ops, DOM patcher, module system | `js/rustzor.js` |
| Interop core: `Js.Call/Send/CallAsync`, JSON, callbacks, scheduler | `src/Rustzor.Core` |
| Razor components (Blazor-compatible component model) | `src/Rustzor.Components` |
| Routing, `NavLink`, `NavigationManager` | `src/Rustzor.Components/Routing` |
| Dependency injection and hosting | `src/Rustzor.Components/Hosting` |
| Forms and validation | `src/Rustzor.Components/Forms` |
| Fetch API | `src/Browser/Rustzor.Browser.Fetch` |
| MSBuild SDK: `dotnet build` produces a runnable site | `src/Rustzor.Sdk`, `src/Rustzor.Build` |

## Phase 2: offline and system APIs

IndexedDB (in `Rustzor.Browser.Storage`), Service Worker and PWA (`Rustzor.Browser.Workers`),
Notifications, File System Access, Push, Background Sync.

## Phase 3: devices and graphics

WebRTC, WebGPU, WebGL, OffscreenCanvas, WebXR, Bluetooth, USB, Serial, HID, NFC,
Sensors, Gamepad, Media, Audio, Video.

## Phase 4: performance and frameworks

| Deliverable | Where |
| --- | --- |
| AOT compiler: IL to WebAssembly for hot static methods | `src/Rustzor.Build/Aot` |
| WASM optimizer: size profile, `wasm-opt` pass when available | `scripts/build-runtime.sh` |
| AI runtime: LLM client, embeddings, RAG, local inference | `src/Frameworks/Rustzor.AI` |
| Game framework: ECS, loop, input, physics, audio, networking | `src/Frameworks/Rustzor.Game` |
| Data: IndexedDB store, SQLite WASM, DuckDB WASM | `src/Frameworks/Rustzor.Data` |
| Graphics: scene and 2D/3D helpers over Canvas, WebGL, WebGPU | `src/Frameworks/Rustzor.Graphics` |

## Supporting libraries

| Library | Contents |
| --- | --- |
| `Rustzor.UI` | Rustzor's own design system: shell, cards, grids, charts, forms, modals, toasts |
| `Rustzor.UI.Bootstrap` | Bootstrap 5.3 as Razor components, state in C# |
| `Rustzor.UI.Tailwind` | Tailwind 3.4 in-browser compiler, class helper, components |
| `Rustzor.UI.Mui` | Material UI 5 islands rendered with React 18, typed wrappers |
| `Rustzor.Charts.ChartJs` | Chart.js 4 |
| `Rustzor.Charts.ECharts` | Apache ECharts 5 with an option builder |
| `Rustzor.Charts.D3` | D3 7: bar, line, pie, force graph, treemap, custom scripts |
| `Rustzor.Net.SignalR` | ASP.NET Core SignalR client: JSON hub protocol over WebSockets |
| `Rustzor.Net.GrpcWeb` | gRPC-Web (unary, server streaming) with a protobuf reader and writer |

## Phase 5: tooling and ecosystem

| Deliverable | Approach |
| --- | --- |
| `rustzor` CLI | .NET tool: `new`, `serve` (live reload), `publish`, `info`, `add`. **Done** (`src/Rustzor.Cli`) |
| Project templates | `dotnet new` templates; Visual Studio and Rider pick them up from the template engine. **Done** (`src/Rustzor.Templates`: `rustzor`, `rustzor-empty`, `rustzorlib`) |
| VS Code extension | Commands for new project, serve and publish; snippets for Razor and interop. **Done** (`tools/vscode-rustzor`, `.vsix` from CI; Marketplace listing pending) |
| Marketplace | A catalog page listing every Rustzor package with its install command. **Done** (`docs/catalog/index.html`, `docs/*/packages.md`, generated) |

## Supporting tool: RustzorAppGen

A desktop code editor built with Avalonia UI (`tools/RustzorAppGen`) whose AI
assistant, **Jack - The Code Bender**, generates Rustzor apps from a prompt.

- Semantic Kernel with OpenAI (and Azure OpenAI), Claude, Gemini and Ollama;
  model, API key, endpoint, temperature and system prompt live in `app.config`
  and are editable in a settings dialog.
- Kernel functions: project files (list/read/write/edit/search), Rustzor API
  guide, templates, build with error feedback, Tavily web search, web page
  scraping, math, date/time.
- Layout: explorer on the left, code editor with line numbers and highlighting
  in the middle, resizable and hideable chat on the right (model picker, image
  attachments, Ctrl+Enter to send, clear thread), logs panel and status bar at
  the bottom.
- Menu and toolbar: New Project (blank or from template), Open Project/File,
  Close Project, Go To Line, Format Code, Build, Run, Deploy, Exit; toggle
  line numbers.
- Templates across games, science, work, industry, education, simulators and tools.
- Tested against a real LLM.

## Sample applications

CRM, ERP, POS, Dashboard, Chat, Video Conference, Figma clone, Photoshop clone,
GIS, CAD viewer, AI Assistant, Multiplayer game, Digital Twin. Each uses
`Rustzor.UI` (or one of the UI integrations) and has a screenshot in the docs.
Done: CRM, ERP, POS, Dashboard, Chat, Video Conference, Figma clone (Rupa), Photoshop clone (Kamar Gelap), GIS (Siaga Citarum), CAD viewer (Tilik), AI Assistant (Loket). Added beyond the list: API Playground (REST,
WebSocket, SignalR and gRPC-Web against an ASP.NET Core server).

## Documentation and release

- `README.md` and `README.id.md` with screenshots; `docs/en` and `docs/id`.
- Every doc and app credits "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil".
- Custom NuGet icon for every package.
- RustNetRuntime gets its own NuGet package(s) too, published the same way
  (GitHub Actions with the `NUGET_API_KEY` repository secret).
- Before publishing: move the repo into `C:\experiment\VibeCoding\Vibe_Framework\Rustzor`
  (monorepo), then publish from GitHub Actions using the `NUGET_API_KEY` secret.
