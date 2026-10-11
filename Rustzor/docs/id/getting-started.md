# Memulai

Panduan ini membangun aplikasi Rustzor kecil dari nol: sebuah halaman counter dengan Rustzor.UI kit. Waktunya sekitar lima menit.

## Yang Anda butuhkan

- [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Browser keluaran dua tahun terakhir (Chrome, Edge, Firefox, atau Safari).

Anda tidak perlu Rust, Node, atau toolchain WebAssembly. Runtime (`rustzor.wasm`) dan loader (`rustzor.js`) sudah termasuk di dalam NuGet package `Rustzor.Sdk`.

## 1. File proyek

Buat folder `HelloRustzor` dan isi `HelloRustzor.csproj` dengan:

```xml
<Project Sdk="Rustzor.Sdk/0.1.0-preview.1">
  <ItemGroup>
    <PackageReference Include="Rustzor.UI" Version="0.1.0-preview.1" />
  </ItemGroup>
</Project>
```

`Rustzor.Sdk` adalah MSBuild SDK. SDK ini menyiapkan proyek Razor yang menargetkan `net10.0`, menambahkan `Rustzor.Components` (model component), dan memasang bundler Rustzor ke dalam `dotnet build`.

## 2. Entry point

`Program.cs`:

```csharp
using Rustzor.Components;

var builder = RustzorHostBuilder.CreateDefault(args);
builder.RootComponents.Add<HelloRustzor.App>("#app");
await builder.Build().RunAsync();
```

Service didaftarkan di `builder.Services` sebelum `Build()`, sama seperti di Blazor:

```csharp
builder.Services.AddSingleton<CartService>();
```

## 3. Component

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

## 4. Halaman

`wwwroot/index.html` adalah halaman yang dibuka browser. Halaman ini memuat `_rustzor/rustzor.js`, yang menjalankan runtime dan me-mount root component ke `#app`:

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

Semua isi lain di `wwwroot` (stylesheet, gambar, font) disalin ke situs apa adanya. Stylesheet library, seperti milik Rustzor.UI, otomatis di-link sebelum stylesheet Anda sendiri, sehingga `app.css` Anda yang menang pada specificity yang sama.

## 5. Build dan jalankan

```bash
dotnet build
```

Situsnya sekarang ada di `bin/Debug/net10.0/wwwroot`:

```
wwwroot/
  index.html              halaman Anda
  _rustzor/rustzor.wasm   RustNetRuntime
  _rustzor/rustzor.js     loader dan bridge browser
  _rustzor/*.dll          aplikasi Anda dan library yang dipakainya
  _rustzor/modules/       JavaScript dan CSS yang dibawa library
  _rustzor/boot.json      apa yang dimuat, dalam urutan apa, berdasarkan content hash
```

Sajikan dengan static file server apa saja yang melakukan fallback ke `index.html` untuk path yang tidak dikenal (agar route seperti `/orders` tetap berfungsi saat reload). Misalnya:

```bash
npx serve -s bin/Debug/net10.0/wwwroot
# atau, dari checkout Rustzor:
node tools/dev/serve.mjs bin/Debug/net10.0/wwwroot 5080
```

Buka alamat yang ditampilkan. Counter-nya pun berjalan.

## 6. Publish

```bash
dotnet publish -c Release
```

Situs statisnya ditempatkan di `bin/Release/net10.0/publish/wwwroot`. Unggah folder tersebut ke static host mana pun: GitHub Pages, Azure Static Web Apps, Netlify, Cloudflare Pages, S3, nginx. Tidak ada bagian di sisi server.

Untuk sekaligus mengompilasi kode numerik yang sering dipanggil (hot) secara ahead of time ke WebAssembly:

```bash
dotnet publish -c Release -p:RustzorAot=true
```

Lihat [mode build](aot.md) untuk apa yang berubah dan apa yang tidak.

## Selanjutnya

- [Component, routing, dan form](components.md): bagian mana saja dari model Blazor yang tersedia.
- [Rustzor.UI](ui.md): component kit yang dipakai di atas.
- [API Browser](browser-apis.md): storage, file, notifikasi, lokasi, media, grafis, perangkat.
- [Jaringan](networking.md): Fetch, WebSocket, SignalR, dan gRPC-Web.
- [Contoh Aplikasi](samples.md): lima aplikasi lengkap untuk dipelajari.
- [Keterbatasan](limitations.md): baca ini sebelum memindahkan (port) aplikasi Blazor yang sudah ada.
