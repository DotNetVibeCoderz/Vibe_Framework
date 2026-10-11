# Getting started

This guide builds a small Rustzor app from nothing: a counter page with the Rustzor.UI kit. It takes about five minutes.

## What you need

- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- A browser from the last two years (Chrome, Edge, Firefox or Safari).

You do not need Rust, Node or a WebAssembly toolchain. The runtime (`rustzor.wasm`) and the loader (`rustzor.js`) come inside the `Rustzor.Sdk` NuGet package.

## The fast way

```bash
dotnet tool install -g Rustzor.Cli --prerelease
rustzor new app HelloRustzor
cd HelloRustzor
rustzor serve --open
```

That creates the app below (with a few more pages), builds it, serves it on `http://localhost:5080`, and rebuilds and reloads the page every time you save. See [tooling](tooling.md). The rest of this guide builds the same thing by hand, so you know what each file does.

## 1. The project file

Make a folder `HelloRustzor` and put this in `HelloRustzor.csproj`:

```xml
<Project Sdk="Rustzor.Sdk/0.1.0-preview.1">
  <ItemGroup>
    <PackageReference Include="Rustzor.UI" Version="0.1.0-preview.1" />
  </ItemGroup>
</Project>
```

`Rustzor.Sdk` is an MSBuild SDK. It sets up a Razor project targeting `net10.0`, adds `Rustzor.Components` (the component model) and hooks the Rustzor bundler into `dotnet build`.

## 2. The entry point

`Program.cs`:

```csharp
using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<HelloRustzor.App>("#app");
await builder.Build().RunAsync();
```

Services go on `builder.Services` before `Build()`, as in Blazor:

```csharp
builder.Services.AddSingleton<CartService>();
```

## 3. Components

`_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Routing
@using Rustzor.UI
@using HelloRustzor
```

`App.razor`:

```razor
<Router AppAssembly="typeof(App).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" />
    </Found>
    <NotFound><p>There is nothing at this address.</p></NotFound>
</Router>
```

`Pages/Home.razor`:

```razor
@page "/"

<Card Title="Hello from Rustzor">
    <p>Clicked @count times</p>
    <button class="rz-btn primary" @onclick="() => count++">Click</button>
</Card>
<Toaster />

@code {
    private int count;
}
```

## 4. The page

`wwwroot/index.html` is the page the browser opens. It loads `_rustzor/rustzor.js`, which starts the runtime and mounts the root component on `#app`:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <base href="/" />
  <title>Hello Rustzor</title>
  <link rel="stylesheet" href="app.css" />
</head>
<body>
  <div id="app">Loading…</div>
  <script type="module">
    import { start } from "./_rustzor/rustzor.js";
    start().catch((e) => {
      document.getElementById("app").textContent = e.message;
      console.error(e);
    });
  </script>
</body>
</html>
```

Everything else in `wwwroot` (stylesheets, images, fonts) is copied to the site as is. Library stylesheets, such as Rustzor.UI's, are linked automatically before your own, so your `app.css` wins at equal specificity.

## 5. Build and run

```bash
dotnet build
```

The site is now in `bin/Debug/net10.0/wwwroot`:

```
wwwroot/
  index.html              your page
  _rustzor/rustzor.wasm   RustNetRuntime
  _rustzor/rustzor.js     loader and browser bridge
  _rustzor/*.dll          your app and the libraries it uses
  _rustzor/modules/       JavaScript and CSS the libraries ship
  _rustzor/boot.json      what to load, in which order, by content hash
```

Serve it with `rustzor serve` (which also rebuilds and reloads on save), or with any static file server that falls back to `index.html` for unknown paths, so that routes like `/orders` work on reload:

```bash
rustzor serve
# or
npx serve -s bin/Debug/net10.0/wwwroot
```

Open the address it prints. The counter counts.

## 6. Publish

```bash
rustzor publish        # or: dotnet publish -c Release
```

The static site goes to `bin/Release/net10.0/publish/wwwroot`. Upload that folder to any static host: GitHub Pages, Azure Static Web Apps, Netlify, Cloudflare Pages, S3, nginx. There is no server-side part.

To also compile hot numeric code ahead of time to WebAssembly:

```bash
dotnet publish -c Release -p:RustzorAot=true
```

See [build modes](aot.md) for what that does and does not change.

## Where next

- [Components, routing and forms](components.md): what of Blazor's model is here.
- [Rustzor.UI](ui.md): the component kit used above.
- [Browser APIs](browser-apis.md): storage, files, notifications, location, media, graphics, devices.
- [Networking](networking.md): Fetch, WebSocket, SignalR and gRPC-Web.
- [Samples](samples.md): five complete apps to read.
- [Limitations](limitations.md): read this before porting an existing Blazor app.
