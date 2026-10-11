# Rustzor

**C# web apps in the browser, on a .NET runtime written in Rust and compiled to WebAssembly.**

Rustzor is a web platform for C#. You write Razor components and plain C#; `dotnet build` turns them into a static site. In the browser, [RustNetRuntime](https://github.com/DotNetVibeCoderz/Vibe_Runtime) — an ECMA-335 runtime written in Rust — loads your assemblies and runs them. There is no server-side runtime and no Mono: the IL interpreter, the garbage collector and the base class library are Rust.

This package is one of a family: the SDK, the component model, a UI kit, a library per browser API, frameworks for data, games, graphics and AI, integrations with Bootstrap, Tailwind, Material UI, Chart.js, ECharts and D3, and clients for SignalR and gRPC-Web. Reference only what an app uses; each library ships the browser code it needs inside its own assembly.

## Start an app

```xml
<!-- MyApp.csproj -->
<Project Sdk="Rustzor.Sdk/0.1.0-preview.1">
  <ItemGroup>
    <PackageReference Include="Rustzor.UI" Version="0.1.0-preview.1" />
  </ItemGroup>
</Project>
```

```csharp
// Program.cs
using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<MyApp.App>("#app");
await builder.Build().RunAsync();
```

`dotnet build` writes the site to `bin/Debug/net10.0/wwwroot`; serve that folder with any static file server. `dotnet publish` puts it in `publish/wwwroot`. Add `-p:RustzorAot=true` to compile hot code ahead of time to WebAssembly.

## The packages

| Group | Packages |
| --- | --- |
| Core | `Rustzor.Sdk`, `Rustzor.Components` (Blazor-compatible component model), `Rustzor.Core` (JS bridge, JSON, timers), `Rustzor.UI` (component kit) |
| Browser APIs | `Rustzor.Browser.*`: DOM, Fetch, WebSocket, Storage (localStorage, IndexedDB), FileSystem, Notifications, Push, BackgroundSync, Workers, Location, Sensors, Media, Audio, Video, Canvas, OffscreenCanvas, WebGL, WebGPU, WebRTC, WebXR, Bluetooth, USB, Serial, HID, NFC, Gamepad, AI |
| Frameworks | `Rustzor.Data` (SQLite and IndexedDB), `Rustzor.Game`, `Rustzor.Graphics`, `Rustzor.AI` |
| Integrations | `Rustzor.UI.Bootstrap`, `Rustzor.UI.Tailwind`, `Rustzor.UI.Mui`, `Rustzor.Charts.ChartJs`, `Rustzor.Charts.ECharts`, `Rustzor.Charts.D3` |
| Networking | `Rustzor.Net.SignalR` (hub client, JSON protocol), `Rustzor.Net.GrpcWeb` (gRPC-Web with a protobuf reader and writer) |

Documentation (English and Bahasa Indonesia), samples and screenshots: <https://github.com/DotNetVibeCoderz/Vibe_Framework/tree/main/Rustzor>

This is a preview. The APIs may change before 1.0.

---

**Bahasa Indonesia.** Rustzor adalah platform web untuk C#: komponen Razor dan C# biasa dijalankan di browser oleh RustNetRuntime, runtime .NET yang ditulis dengan Rust dan dikompilasi ke WebAssembly. Mulai dengan `<Project Sdk="Rustzor.Sdk/0.1.0-preview.1">`, lalu `dotnet build`. Dokumentasi lengkap ada di tautan di atas.

Built by Gravicode Studios, led by Kang Fadhil. *Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
