<p align="center"><img src="assets/icon-512.png" width="128" alt="Logo JS.Net" /></p>

<h1 align="center">JS.Net</h1>

<p align="center"><b>Tulis seperti JavaScript. Rilis sebagai .NET.</b><br/>
Bahasa pemrograman modern dengan sintaks JavaScript/TypeScript yang dikompilasi menjadi CIL standar, berjalan di CoreCLR, dan memakai seluruh ekosistem .NET.</p>

<p align="center"><i>Dibuat oleh <b>Gravicode Studios</b> dipimpin oleh <b>Kang Fadhil</b>.</i></p>

<p align="center"><a href="README.md">🇬🇧 Read in English</a></p>

---

```jsnet
import System.Text;

interface Product { name: string; price: number; }

export class Cart {
    private items = new Array<Product>();

    add(item: Product): Cart { this.items.push(item); return this; }

    get total(): number { return this.items.reduce((sum, p) => sum + p.price, 0); }
}

const cart = new Cart().add({ name: "Keyboard", price: 49.9 }).add({ name: "Mouse", price: 19.5 });
console.log(`Total: ${cart.total.toFixed(2)}`);           // Total: 69.40

const sb = new StringBuilder().Append("API .NET apa pun, ").Append("tanpa binding.");
console.log(sb.ToString());
await delay(100);                                         // async/await di atas Task
```

## Kenapa JS.Net

- **Familier** — `let`/`const`, arrow function, class, interface, generic, `async`/`await`, destructuring, template literal, `console.log`, `JSON`, `Promise`, `map`/`filter`/`reduce`.
- **Warga asli .NET** — class JS.Net adalah class CLR sungguhan, generic adalah generic CLR, `Promise<T>` adalah `Task<T>`. Pakai paket NuGet apa pun; panggil JS.Net dari C#, F#, dan VB.NET, begitu pula sebaliknya.
- **Cepat** — kode bertipe dikompilasi menjadi IL yang sama dengan C#. Mendukung tiered JIT, ReadyToRun, dan **NativeAOT** (aplikasi console JS.Net bisa dipublish menjadi executable native ±2 MB).
- **Semua model aplikasi** — console, class library, ASP.NET Core (minimal API dan controller), Blazor, WPF, WinForms, Avalonia, worker service, dan test xUnit.
- **Tooling nyata** — `dotnet build/run/publish`, MSBuild SDK, error yang dipetakan ke baris `.jsn`, language server, ekstensi VS Code, dan tool native Rust.
- **AI-native** — deklarasi `ai function` dan `prompt` di atas Microsoft.Extensions.AI.

## Mulai cepat

```bash
dotnet new install JSNet.Templates          # template proyek
dotnet tool install -g JSNet.Cli            # opsional: CLI jsnet (jalankan skrip, LSP, formatter)

dotnet new jsnet -n HelloJsNet               # aplikasi console
cd HelloJsNet
dotnet run
```

Menjalankan satu file tanpa proyek:

```bash
jsnet run hello.jsn
jsnet emit hello.jsn     # tampilkan C# hasil penurunan (lowering) oleh compiler
```

File proyek JS.Net:

```xml
<Project Sdk="JSNet.Sdk/0.1.1">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

Untuk SDK lain (Web, Worker, WindowsDesktop), tambahkan JS.Net di atasnya: `<Project Sdk="Microsoft.NET.Sdk.Web"> <Sdk Name="JSNet.Sdk" Version="0.1.1" />`.

### Template

| Template | Nama pendek |
|---|---|
| Aplikasi console | `jsnet` / `jsnet-console` |
| Class library | `jsnet-classlib` |
| ASP.NET Core Web API | `jsnet-webapi` |
| ASP.NET Core web app kosong | `jsnet-web` |
| Blazor Web App | `jsnet-blazor` |
| Aplikasi WPF | `jsnet-wpf` |
| Aplikasi Windows Forms | `jsnet-winforms` |
| Aplikasi Avalonia | `jsnet-avalonia` |
| Worker service | `jsnet-worker` |
| Test xUnit | `jsnet-xunit` |

## JS.Net Gallery

Dua puluh sampel yang bisa dijalankan — fitur bahasa, async, LINQ, interop .NET, JSON, fungsi AI-native, dan kasus nyata — ditulis dalam JS.Net dan ditampilkan oleh empat front-end native. Setiap sampel bisa dibalik untuk melihat **C# hasil penurunan oleh compiler**.

| Blazor (web) | WPF |
|---|---|
| ![Beranda gallery Blazor](docs/images/web-home.png) | ![Gallery WPF](docs/images/wpf-sample.png) |
| ![Sampel Blazor](docs/images/web-sample.png) | ![Komponen WPF](docs/images/wpf-components.png) |

| Avalonia (lintas platform) | WinForms |
|---|---|
| ![Gallery Avalonia](docs/images/avalonia-sample.png) | ![Gallery WinForms](docs/images/winforms-sample.png) |
| ![Komponen Avalonia](docs/images/avalonia-components.png) | ![Komponen WinForms](docs/images/winforms-components.png) |

```bash
dotnet run --project gallery/JSNet.Gallery.Web        # http://localhost:5170
dotnet run --project gallery/JSNet.Gallery.Wpf
dotnet run --project gallery/JSNet.Gallery.WinForms
dotnet run --project gallery/JSNet.Gallery.Avalonia
```

## Cara kerjanya

```text
sumber .jsn ──► front end JS.Net ──► C# (dengan peta #line) ──► Roslyn ──► IL ──► CoreCLR (JIT / NativeAOT)
               lexer · parser · binder ·
               inferensi tipe · analyzer
```

JS.Net memakai **strategi backend Roslyn** dari dokumen desain: front end JS.Net mem-parse, mengikat (bind), dan memeriksa tipe file `.jsn`, lalu menurunkannya ke C#. Roslyn menghasilkan IL, PDB portabel (mengarah ke baris `.jsn` lewat `#line`), source generator, analyzer, dan kompatibilitas NativeAOT. Tidak ada runtime baru: program JS.Net berjalan di CoreCLR dengan GC dan JIT standar. Lihat [docs/id/architecture.md](docs/id/architecture.md).

## Struktur repositori

| Path | Isi |
|---|---|
| `src/JSNet.Syntax` · `JSNet.Parser` · `JSNet.Semantics` · `JSNet.CodeGen` · `JSNet.Compiler` | Pipeline compiler |
| `src/JSNet.Runtime` | Pustaka runtime (console, JSON, Math, Promise, Date, fetch, timer, API gaya JS, `@Service`) |
| `src/JSNet.AI` | Runtime `ai function` / `prompt` di atas Microsoft.Extensions.AI |
| `src/JSNet.Sdk` | MSBuild project SDK (`JSNet.Sdk`) |
| `src/JSNet.Scripting` | Backend Roslyn in-memory (`jsnet run`, playground, test) |
| `src/JSNet.Cli` | Tool `jsnet` (juga dibundel di SDK) |
| `src/JSNet.LanguageServer` | Implementasi Language Server Protocol |
| `rust/jsnet-tools` | Tool native Rust: formatter, pemeriksa cepat, tokenizer, statistik |
| `templates/` · `vscode/` · `gallery/` · `samples/` · `tests/` · `docs/` | Template, ekstensi VS Code, gallery, sampel, test, dokumentasi |

## Dokumentasi

| Bahasa Indonesia | English |
|---|---|
| [Memulai](docs/id/getting-started.md) | [Getting started](docs/en/getting-started.md) |
| [Panduan bahasa](docs/id/language-guide.md) | [Language guide](docs/en/language-guide.md) |
| [Interop .NET & runtime](docs/id/interop-and-runtime.md) | [.NET interop & runtime](docs/en/interop-and-runtime.md) |
| [Tooling (CLI, SDK, LSP, VS Code, Rust)](docs/id/tooling.md) | [Tooling](docs/en/tooling.md) |
| [Arsitektur](docs/id/architecture.md) | [Architecture](docs/en/architecture.md) |
| [Referensi diagnostik](docs/id/diagnostics.md) | [Diagnostics reference](docs/en/diagnostics.md) |

Roadmap: [PLAN.md](PLAN.md) · Progres: [Progress.md](Progress.md) · Desain awal: [architecture-design.md](architecture-design.md)

## Build dari source

Kebutuhan: .NET 10 SDK; Rust (opsional, untuk `jsnet-tools`); Node.js (opsional, untuk ekstensi VS Code).

```bash
dotnet build JSNet.slnx
dotnet test tests/JSNet.Tests
pwsh build/pack.ps1          # build Release + test + paket NuGet di artifacts/packages
```

## Lisensi

MIT © Gravicode Studios. JS.Net dibuat oleh **Gravicode Studios** dipimpin oleh **Kang Fadhil**.
