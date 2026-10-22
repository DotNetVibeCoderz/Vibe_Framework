# Build dari source

## Prasyarat

- .NET 10 SDK
- Rust (stable, 1.85 atau lebih baru) dengan target WebAssembly: `rustup target add wasm32-unknown-unknown`
- Node.js 20 atau lebih baru, untuk development server dan browser test
- Chrome atau Chromium, untuk browser test

RustNetRuntime berasal dari repository [Vibe_Runtime](https://github.com/DotNetVibeCoderz/Vibe_Runtime), di-pin ke sebuah commit di `Cargo.toml`; Cargo yang akan mengambilnya. Untuk mengerjakan runtime dan Rustzor bersamaan, salin `.cargo/config.local.toml.example` ke `.cargo/config.toml` dan arahkan ke checkout runtime Anda.

## Build

```bash
bash scripts/build-runtime.sh               # rustzor.wasm + rustzor.js ke src/Rustzor.Sdk/runtime
dotnet build src/Rustzor.Build -c Release   # bundler dan compiler AOT yang dijalankan oleh build target
dotnet build Rustzor.slnx                   # library, sample, server playground
```

`build-runtime.sh --size` mem-build runtime yang dioptimalkan untuk ukuran unduhan, bukan kecepatan.

Aplikasi di dalam repository (sample, test, template) memakai integrasi build yang sama dengan yang didapat pengguna NuGet dari `Rustzor.Sdk`, yang dihubungkan ke source tree oleh `eng/Rustzor.InRepo.targets`.

## Menjalankan sample

```bash
node tools/dev/serve.mjs samples/Pos/bin/Debug/net10.0/wwwroot 5241
```

`serve.mjs` adalah static server kecil dengan fallback ke `index.html`, sehingga route di sisi client tetap berfungsi setelah reload.

## Test

| Apa | Perintah |
| --- | --- |
| Setiap route aplikasi ter-render tanpa error di console, dan screenshot untuk masing-masing | `node tools/e2e/pages.mjs http://localhost:5240/ out "" deals companies` |
| Mengendalikan aplikasi langkah demi langkah (click, fill, type, press, drag, eval, screenshot) | `node tools/e2e/probe.mjs <url> [shot.png] '<steps json>'` |
| Setiap template AppGen berhasil di-generate, di-build, dan dijalankan | `bash tools/RustzorAppGen/scripts/test-templates.sh` |
| Aplikasi yang di-build dari package hasil pack bisa berjalan | `bash scripts/package-smoke.sh 0.1.0-preview.3` |
| CLI dan template dari package hasil pack: new, add, info, publish, lalu dijalankan | `bash scripts/tooling-smoke.sh 0.1.0-preview.3` |
| Extension VS Code aktif dan mendaftarkan command-nya, di dalam VS Code | `code --extensionDevelopmentPath=tools/vscode-rustzor --extensionTestsPath=tools/vscode-rustzor/test/suite.js <a Rustzor app folder>`, dengan `RUSTZOR_EXT_REPORT=<file>` untuk hasilnya |
| Runtime: unit test, conformance terhadap .NET, lintas assembly, target no_std | di repository Vibe_Runtime: `cargo test --workspace`, `bash tests/embedded.sh` |

Tool browser membutuhkan `npm ci` di `tools/e2e` sekali saja, dan `CHROME_PATH` jika Chrome tidak berada di lokasi default Windows.

## Pack

```bash
bash scripts/pack.sh                     # semua package dengan versi di src/Directory.Build.props
bash scripts/pack.sh 0.2.0-preview.1     # atau dengan versi tertentu
```

Package-package hasilnya ada di `artifacts/packages`. Setelah menambahkan package, jalankan juga `node tools/e2e/make-icons.mjs` (untuk ikonnya) dan `node tools/e2e/make-catalog.mjs` (untuk katalog dan `packages.md`). Extension VS Code di-pack dengan `npx @vscode/vsce package --no-dependencies` di `tools/vscode-rustzor`. `Rustzor.Sdk` membawa runtime (`runtime/`), build tool (`tools/`), dan `Version.props` yang mem-pin referensi implisit `Rustzor.Components` ke versi yang sama. Setiap package punya ikon sendiri di `assets/icons`, yang dibuat oleh `node tools/e2e/make-icons.mjs`; package baru perlu ditambahkan entrinya di sana.

## Rilis

Rilis dipublikasikan dari GitHub Actions di repository Vibe_Framework:

- `rustzor-ci.yml` mem-build runtime, solution, dan setiap package; mem-build sebuah aplikasi hanya dari package-package tersebut dan menjalankannya di Chrome headless; menginstal CLI dari package tersebut lalu memakainya untuk membuat, mem-publish, dan menjalankan sebuah aplikasi; memeriksa bahwa katalog package sudah mutakhir; dan mem-pack extension VS Code (sebuah build artifact `.vsix`).
- `rustzor-publish.yml` menjalankan CI tersebut pada versi rilis dan mem-push package yang sudah diuji ke nuget.org dengan secret `NUGET_API_KEY`. Workflow ini dimulai oleh tag `rustzor-v<version>` atau secara manual (dengan opsi dry-run yang melakukan semuanya kecuali push).

Source workflow ada di `eng/workflows/`; salinan yang dijalankan GitHub berada di `.github/workflows/` pada root repository.
