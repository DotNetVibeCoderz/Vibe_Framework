# Tooling

> JS.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/tooling.md)

## CLI `jsnet` (`JSNet.Cli`)

```bash
dotnet tool install -g JSNet.Cli
```

| Perintah | Deskripsi |
|---|---|
| `jsnet run <file.jsn \| dir> [-- args]` | Kompilasi di memori dengan Roslyn lalu jalankan. Set `JSNET_TRACE=1` untuk stack trace. |
| `jsnet emit <file.jsn> [--line] [--all]` | Cetak C# yang dihasilkan. |
| `jsnet check <file \| dir> [--library]` | Hanya diagnostik. |
| `jsnet compile --out <dir> [--root-namespace N] [--project-dir D] [--library] [--no-line] [--nowarn kode] <file \| @rsp>` | Pembangkitan kode yang dipakai MSBuild SDK. |
| `jsnet fmt <file \| dir> [--check]` | Format dengan formatter native Rust. |
| `jsnet new <template> [args]` | Pintasan untuk `dotnet new jsnet-<template>`. |
| `jsnet lsp` | Menjalankan language server di stdio. |
| `jsnet server [--stop]` | Menjalankan (atau menghentikan) build server yang dipakai SDK (lihat di bawah). |
| `jsnet info`, `jsnet --version` | Informasi versi. |

## MSBuild SDK (`JSNet.Sdk`)

```xml
<Project Sdk="JSNet.Sdk/0.2.1"> … </Project>
<!-- atau di atas SDK lain -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <Sdk Name="JSNet.Sdk" Version="0.2.1" />
</Project>
```

Yang terjadi saat `dotnet build`:

1. Mengumpulkan `**/*.jsn` ke `@(JSNetCompile)`.
2. Menjalankan `jsnet compile` secara inkremental (hanya bila file `.jsn`, proyek, atau compiler berubah) ke `obj/<config>/<tfm>/jsnet/*.g.cs`.
3. Menambahkan file hasil ke `@(Compile)`. Roslyn mengompilasinya bersama file C# di proyek — mencampur JS.Net dan C# dalam satu proyek didukung.
4. Mereferensikan `JSNet.Runtime` secara implisit.

| Properti | Default | Arti |
|---|---|---|
| `EnableDefaultJSNetItems` | `true` | Sertakan `**/*.jsn` secara otomatis. |
| `JSNetRootNamespace` | `$(RootNamespace)` | Namespace dasar untuk namespace folder. |
| `JSNetNoWarn` | — | Peringatan JS.Net yang disembunyikan, dipisah titik koma (mis. `JSN2001;JSN2002`). |
| `JSNetEmitLineDirectives` | `true` | Petakan error, PDB, dan stack trace ke baris `.jsn`. |
| `JSNetEnableAI` | `false` | Referensikan `JSNet.AI` untuk `ai function`/`prompt`. |
| `JSNetDisableImplicitRuntimeReference` | `false` | Jangan referensikan `JSNet.Runtime` secara otomatis. |
| `JSNetCompilerPath` | bawaan | Pakai build compiler yang lain. |
| `JSNetUseBuildServer` | `true` | Kompilasi lewat build server JS.Net yang sudah hangat. |

Error tampil dalam format MSBuild/IDE terhadap file aslinya, mis. `Program.jsn(12,7): error CS0029: Cannot implicitly convert type 'string' to 'double'`.

### Build server

SDK mengompilasi file `.jsn` dengan task MSBuild (`JsNetCompile`) yang meneruskan pekerjaan ke **build server JS.Net**: proses `jsnet server` yang tetap termuat dan sudah di-JIT di antara build, dihubungi lewat named pipe yang hanya bisa dibuka pengguna yang sama. Build pertama menyalakannya; server berhenti sendiri setelah 10 menit tanpa build (`JSNET_SERVER_IDLE_SECONDS`). Kompilasi proyek kecil saat server sudah hangat hanya beberapa milidetik, bukan proses compiler baru (≈0,2–1,6 detik).

- Server berjalan dari salinan pribadi compiler di `%TEMP%/jsnet-server/`, jadi pembaruan SDK atau build ulang compiler tidak pernah terbentur file terkunci; setiap build compiler punya server sendiri.
- Assembly analyzer kustom dimuat dari memori, jadi juga tidak pernah terkunci.
- Berfungsi di `dotnet build` dan di MSBuild Visual Studio (task menargetkan netstandard2.0). Jika server tidak bisa dihubungi, task menjalankan compiler sebagai proses anak seperti sebelumnya.
- Matikan dengan `<JSNetUseBuildServer>false</JSNetUseBuildServer>` atau `JSNET_NO_BUILD_SERVER=1`; hentikan server dengan `jsnet server --stop`.

## Language server dan editor

`jsnet lsp` mengimplementasikan Language Server Protocol lewat stdio: diagnostik (JS.Net dan binding seluruh proyek), completion (keyword, global runtime, deklarasi, snippet, dan **semua member .NET** dari tipe hasil inferensi, diselesaikan oleh Roslyn atas kode hasil penurunan dan referensi proyek), hover dengan signature .NET, go to definition, find references, rename, quick fix (`const` → `let`, hapus deklarasi tak terpakai), semantic token, document symbol, dan formatting (melalui `jsnet-tools`).

Ekstensi VS Code ([`GravicodeStudios.jsnet`](https://marketplace.visualstudio.com/items?itemName=GravicodeStudios.jsnet) di Marketplace; sumber `vscode/jsnet-vscode`) menambahkan grammar TextMate, konfigurasi bahasa, snippet, dan klien LSP. Pengaturan: `jsnet.server.command` (default `jsnet`) dan `jsnet.server.args` (default `["lsp"]`).

```bash
cd vscode/jsnet-vscode
npm install
npx vsce package --allow-missing-repository
```

### Visual Studio

`visualstudio/JSNet.VisualStudio` adalah ekstensi VisualStudio.Extensibility (VS 2022 17.14+) yang menjalankan `jsnet lsp` untuk file `.jsn` (atau compiler yang ditunjuk variabel lingkungan `JSNET_PATH`). Tersedia di Marketplace sebagai [JS.Net for Visual Studio](https://marketplace.visualstudio.com/items?itemName=GravicodeStudios.jsnet-visualstudio); CI juga membangun `JSNet.VisualStudio.vsix`. Publikasikan dengan `VsixPublisher.exe publish -payload <vsix> -publishManifest publishManifest.json`.

### JetBrains Rider / IntelliJ

Pasang plugin gratis **LSP4IJ** lalu impor folder [`jetbrains/lsp4ij-template`](../../jetbrains/lsp4ij-template) sebagai template language server kustom; opsional tambahkan `vscode/jsnet-vscode` sebagai TextMate bundle. Lihat [jetbrains/README.md](../../jetbrains/README.md).

## Aturan kustom

Tulis aturan tim dalam JS.Net (deklarasi `rule`, lihat [panduan bahasa](language-guide.md#aturan-analyzer-kustom)) di class library yang mereferensikan `JSNet.Analyzers`, lalu pakai dari proyek JS.Net mana pun:

```xml
<ItemGroup>
  <JSNetAnalyzer Include="../TeamRules/bin/$(Configuration)/net10.0/TeamRules.dll" />
</ItemGroup>
```

Dari CLI: `jsnet check src --analyzer TeamRules.dll`. Temuan muncul sebagai warning atau error build biasa (`Program.jsn(4,5): warning TEAM001: ...`). `samples/JsNetRules` dan `samples/RulesDemo` menunjukkan penyiapannya.

## Tool native Rust (`jsnet-tools`)

Binary Rust tanpa dependensi untuk pekerjaan yang diuntungkan kecepatan native dan paralelisme:

| Perintah | Deskripsi |
|---|---|
| `jsnet-tools fmt <file \| dir> [--check] [--stdin] [--indent N]` | Formatter yang sadar token: indentasi ulang berdasarkan kedalaman kurung (termasuk badan `case` dan rantai yang diawali titik), menghapus spasi di akhir baris, merapatkan baris kosong; tidak pernah mengubah isi string/template/komentar. |
| `jsnet-tools check <file \| dir>` | Validasi leksikal paralel: string/template/komentar yang tidak ditutup, kurung tidak seimbang. |
| `jsnet-tools tokens <file>` | Tampilkan daftar token. |
| `jsnet-tools stats <file \| dir>` | Jumlah baris, token, tipe, dan fungsi beserta throughput lexer. |

```bash
cd rust/jsnet-tools
cargo build --release
cargo test
```

`jsnet fmt` dan language server menemukan binary lewat `JSNET_TOOLS_PATH`, di samping CLI (`native/`), atau di `PATH`. Paket SDK menyertakan build Windows.

## Template

`dotnet new install JSNet.Templates` memasang tiga belas template (lihat [README](../../README.id.md#template)). Setiap template menerima `--sdkVersion` untuk mengunci versi JS.Net.

## Build dan pack repositori

```bash
dotnet build JSNet.slnx
dotnet test tests/JSNet.Tests          # compiler, runtime, dan semua sampel gallery
pwsh build/pack.ps1 -Version 0.2.1     # artifacts/packages/*.nupkg
pwsh build/smoke.ps1 -Version 0.2.1    # pasang paket di folder bersih, build semua template
```

Rilis dipublikasikan oleh GitHub Actions di repositori Vibe_Framework: push tag `jsnet-v0.2.1`, atau jalankan **Publish JS.Net to NuGet** secara manual (aktifkan `dry_run` untuk gladi). Workflow menjalankan CI lengkap pada versi tersebut dan mem-push paket yang persis sudah diuji memakai secret repositori `NUGET_API_KEY`.

Playground WebAssembly (https://dotnetvibecoderz.github.io/Vibe_Framework/) di-deploy ke GitHub Pages dengan workflow manual **JS.Net playground (GitHub Pages)** (`jsnet-playground-pages.yml`; aktifkan Pages dengan sumber *GitHub Actions* terlebih dahulu).

Paket: `JSNet.Sdk`, `JSNet.Runtime`, `JSNet.AI`, `JSNet.Analyzers`, `JSNet.Web`, `JSNet.EntityFramework`, `JSNet.Blazor`, `JSNet.Reactive`, `JSNet.Cli`, `JSNet.Templates`.
