# Packages

Every Rustzor package at `0.1.0-preview.1`. A searchable version with copy buttons: [catalog/index.html](../catalog/index.html).

Start with `dotnet tool install -g Rustzor.Cli --prerelease`, then `rustzor new app MyApp`. Add a package with `rustzor add <short name>` or `dotnet add package <id> --version 0.1.0-preview.1`.

## Core

The SDK, the component model, the UI kit and the tools.

| Package | Short name | What it is |
| --- | --- | --- |
| [`Rustzor.Cli`](https://www.nuget.org/packages/Rustzor.Cli) |  | The rustzor command for Rustzor apps (C# Razor in the browser on RustNetRuntime): rustzor new, rustzor serve with live reload, rustzor publish, rustzor add, rustzor info. |
| [`Rustzor.Components`](https://www.nuget.org/packages/Rustzor.Components) |  | Rustzor components: a Blazor-compatible component model (ComponentBase, RenderTreeBuilder, EventCallback, @bind, routing, layouts, forms, DI) rendered by a virtual-DOM diff on RustNetRuntime in the browser. Write .razor files; no ASP.NET Core assemblies are needed. |
| [`Rustzor.Core`](https://www.nuget.org/packages/Rustzor.Core) |  | Rustzor core: the JavaScript bridge, callbacks, timers and a JSON reader/writer for C# running on RustNetRuntime in the browser. |
| [`Rustzor.Sdk`](https://www.nuget.org/packages/Rustzor.Sdk) |  | Rustzor SDK: build C# Razor apps that run in the browser on RustNetRuntime (Rust, compiled to WebAssembly). Use as <Project Sdk="Rustzor.Sdk/0.1.0-preview.1">. dotnet build produces a static site; -p:RustzorAot=true adds ahead-of-time WebAssembly. |
| [`Rustzor.Templates`](https://www.nuget.org/packages/Rustzor.Templates) |  | Project templates for Rustzor: C# Razor apps that run in the browser on RustNetRuntime (Rust, compiled to WebAssembly). dotnet new rustzor, rustzor-empty, rustzorlib. |
| [`Rustzor.UI`](https://www.nuget.org/packages/Rustzor.UI) | `ui` | Rustzor.UI: a professional component kit for Rustzor apps — app shell, data grid, SVG charts, stat cards, modals, tabs, toasts, forms and icons, themed for light and dark. |

## Browser APIs

One package per browser API; an app downloads only the ones it references.

| Package | Short name | What it is |
| --- | --- | --- |
| [`Rustzor.Browser.AI`](https://www.nuget.org/packages/Rustzor.Browser.AI) | `builtin-ai` | Rustzor.Browser.AI: the browser's AI API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Audio`](https://www.nuget.org/packages/Rustzor.Browser.Audio) | `audio` | Rustzor.Browser.Audio: the browser's Audio API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.BackgroundSync`](https://www.nuget.org/packages/Rustzor.Browser.BackgroundSync) | `sync` | Rustzor.Browser.BackgroundSync: the browser's BackgroundSync API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Bluetooth`](https://www.nuget.org/packages/Rustzor.Browser.Bluetooth) | `bluetooth` | Rustzor.Browser.Bluetooth: the browser's Bluetooth API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Canvas`](https://www.nuget.org/packages/Rustzor.Browser.Canvas) | `canvas` | Rustzor.Browser.Canvas: the browser's Canvas API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.DOM`](https://www.nuget.org/packages/Rustzor.Browser.DOM) | `dom` | Rustzor.Browser.DOM: the browser's DOM API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Fetch`](https://www.nuget.org/packages/Rustzor.Browser.Fetch) | `fetch` | Rustzor.Browser.Fetch: the browser's Fetch API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.FileSystem`](https://www.nuget.org/packages/Rustzor.Browser.FileSystem) | `files` | Rustzor.Browser.FileSystem: the browser's FileSystem API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Gamepad`](https://www.nuget.org/packages/Rustzor.Browser.Gamepad) | `gamepad` | Rustzor.Browser.Gamepad: the browser's Gamepad API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.HID`](https://www.nuget.org/packages/Rustzor.Browser.HID) | `hid` | Rustzor.Browser.HID: the browser's HID API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Location`](https://www.nuget.org/packages/Rustzor.Browser.Location) | `location` | Rustzor.Browser.Location: the browser's Location API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Media`](https://www.nuget.org/packages/Rustzor.Browser.Media) | `media` | Rustzor.Browser.Media: the browser's Media API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.NFC`](https://www.nuget.org/packages/Rustzor.Browser.NFC) | `nfc` | Rustzor.Browser.NFC: the browser's NFC API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Notifications`](https://www.nuget.org/packages/Rustzor.Browser.Notifications) | `notifications` | Rustzor.Browser.Notifications: the browser's Notifications API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.OffscreenCanvas`](https://www.nuget.org/packages/Rustzor.Browser.OffscreenCanvas) | `offscreen` | Rustzor.Browser.OffscreenCanvas: the browser's OffscreenCanvas API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Push`](https://www.nuget.org/packages/Rustzor.Browser.Push) | `push` | Rustzor.Browser.Push: the browser's Push API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Sensors`](https://www.nuget.org/packages/Rustzor.Browser.Sensors) | `sensors` | Rustzor.Browser.Sensors: the browser's Sensors API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Serial`](https://www.nuget.org/packages/Rustzor.Browser.Serial) | `serial` | Rustzor.Browser.Serial: the browser's Serial API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Storage`](https://www.nuget.org/packages/Rustzor.Browser.Storage) | `storage` | Rustzor.Browser.Storage: the browser's Storage API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.USB`](https://www.nuget.org/packages/Rustzor.Browser.USB) | `usb` | Rustzor.Browser.USB: the browser's USB API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Video`](https://www.nuget.org/packages/Rustzor.Browser.Video) | `video` | Rustzor.Browser.Video: the browser's Video API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.WebGL`](https://www.nuget.org/packages/Rustzor.Browser.WebGL) | `webgl` | Rustzor.Browser.WebGL: the browser's WebGL API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.WebGPU`](https://www.nuget.org/packages/Rustzor.Browser.WebGPU) | `webgpu` | Rustzor.Browser.WebGPU: the browser's WebGPU API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.WebRTC`](https://www.nuget.org/packages/Rustzor.Browser.WebRTC) | `webrtc` | Rustzor.Browser.WebRTC: the browser's WebRTC API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.WebSocket`](https://www.nuget.org/packages/Rustzor.Browser.WebSocket) | `websocket` | Rustzor.Browser.WebSocket: the browser's WebSocket API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.WebXR`](https://www.nuget.org/packages/Rustzor.Browser.WebXR) | `webxr` | Rustzor.Browser.WebXR: the browser's WebXR API for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Browser.Workers`](https://www.nuget.org/packages/Rustzor.Browser.Workers) | `workers` | Rustzor.Browser.Workers: the browser's Workers API for C# on Rustzor (RustNetRuntime in WebAssembly). |

## Frameworks

Data, games, graphics and AI on top of the browser APIs.

| Package | Short name | What it is |
| --- | --- | --- |
| [`Rustzor.AI`](https://www.nuget.org/packages/Rustzor.AI) | `ai` | Rustzor.AI: AI for Rustzor apps. Streaming chat with OpenAI, Azure OpenAI, Anthropic Claude, Ollama and any OpenAI-compatible endpoint; embeddings; an in-memory vector store and a retrieval-augmented generation pipeline; and local inference in the browser with transformers.js or Chrome's built-in model. |
| [`Rustzor.Data`](https://www.nuget.org/packages/Rustzor.Data) | `data` | Rustzor.Data: data in the browser for Rustzor. Typed IndexedDB object stores, SQLite (sql.js) with persistence, and DuckDB WASM for analytics over CSV, Parquet and JSON — all from C#. |
| [`Rustzor.Game`](https://www.nuget.org/packages/Rustzor.Game) | `game` | Rustzor.Game: a 2D game framework for Rustzor. Entity-component-system world, fixed-step game loop, keyboard/pointer/gamepad input, 2D physics with collisions, tweens, cameras, sound effects and multiplayer sessions over WebSocket or BroadcastChannel. |
| [`Rustzor.Graphics`](https://www.nuget.org/packages/Rustzor.Graphics) | `graphics` | Rustzor.Graphics: graphics for Rustzor. A retained 2D scene graph with pan/zoom, hit testing, selection, SVG and JSON export on Canvas; RGBA pixel operations for image editing; and a WebGL 3D renderer with primitives, OBJ loading, orbit camera and picking. |

## Integrations

Established JavaScript UI and chart libraries, driven from C#.

| Package | Short name | What it is |
| --- | --- | --- |
| [`Rustzor.Charts.ChartJs`](https://www.nuget.org/packages/Rustzor.Charts.ChartJs) | `chartjs` | Rustzor.Charts.ChartJs: Chart.js 4 charts (line, bar, pie, doughnut, radar, polar area, scatter, bubble) as Razor components for Rustzor, themed for light and dark. |
| [`Rustzor.Charts.D3`](https://www.nuget.org/packages/Rustzor.Charts.D3) | `d3` | Rustzor.Charts.D3: D3.js 7 visualisations for Rustzor — bar, line, pie, force-directed graph and treemap components fed from C#, plus D3Script for your own D3 code. |
| [`Rustzor.Charts.ECharts`](https://www.nuget.org/packages/Rustzor.Charts.ECharts) | `echarts` | Rustzor.Charts.ECharts: Apache ECharts 5 for Rustzor. Pass any ECharts option from C# (anonymous objects work) or build common charts with EChartOption helpers. |
| [`Rustzor.UI.Bootstrap`](https://www.nuget.org/packages/Rustzor.UI.Bootstrap) | `bootstrap` | Rustzor.UI.Bootstrap: Bootstrap 5.3 for Rustzor. Buttons, cards, alerts, modals, tabs, accordions, dropdowns, tables, pagination, toasts and form inputs as Razor components whose state lives in C#, so Bootstrap's own JavaScript is not needed. |
| [`Rustzor.UI.Mui`](https://www.nuget.org/packages/Rustzor.UI.Mui) | `mui` | Rustzor.UI.Mui: Material UI 5 for Rustzor. Typed Razor wrappers (buttons, text fields, selects, switches, sliders, tabs, chips, alerts, cards, app bars, progress) plus MuiIsland for any MUI component, rendered with React 18 and themed with the Rustzor palette in light and dark mode. Razor content can be nested inside MUI components. |
| [`Rustzor.UI.Tailwind`](https://www.nuget.org/packages/Rustzor.UI.Tailwind) | `tailwind` | Rustzor.UI.Tailwind: Tailwind CSS 3.4 for Rustzor. The in-browser compiler picks up any utility class your Razor markup renders, a Tw class helper builds conditional class lists, and a set of ready-made components (buttons, cards, inputs, tabs, modals, tables, stats) gets you started. |

## Networking

Real-time and RPC clients for ASP.NET Core back ends.

| Package | Short name | What it is |
| --- | --- | --- |
| [`Rustzor.Net.GrpcWeb`](https://www.nuget.org/packages/Rustzor.Net.GrpcWeb) | `grpc` | Rustzor.Net.GrpcWeb: gRPC-Web calls (unary and server streaming) with a small protobuf reader and writer, for C# on Rustzor (RustNetRuntime in WebAssembly). |
| [`Rustzor.Net.SignalR`](https://www.nuget.org/packages/Rustzor.Net.SignalR) | `signalr` | Rustzor.Net.SignalR: an ASP.NET Core SignalR client (JSON hub protocol over WebSockets) for C# on Rustzor (RustNetRuntime in WebAssembly). |

_This page is generated by `node tools/e2e/make-catalog.mjs`._
