# Tooling: CLI, template, VS Code, dan katalog package

## Perintah `rustzor`

```bash
dotnet tool install -g Rustzor.Cli --prerelease
```

| Perintah | Fungsinya |
| --- | --- |
| `rustzor new [app\|empty\|lib] [Name] [--title "My App"] [-o dir]` | Membuat proyek dari template Rustzor; jika template-nya belum ada, template dengan versi yang sama dengan CLI diinstal terlebih dahulu |
| `rustzor serve [project] [-p 5080] [--open] [--no-watch]` | Melakukan build, menyajikan situs, lalu setiap kali Anda menyimpan, mem-build ulang dan me-reload halaman yang sedang terbuka |
| `rustzor publish [project] [--aot] [-o dir]` | `dotnet publish -c Release` (dengan `RustzorAot` bila diminta) beserta laporan ukuran unduhan, mentah dan setelah gzip |
| `rustzor add <package...>` | Menambahkan package Rustzor berdasarkan nama pendek (`ui`, `fetch`, `storage`, `data`, `signalr`, `grpc`, `camera`…) atau id lengkap, dengan versi yang sama dengan CLI |
| `rustzor info [project]` | Versi, package yang dipakai proyek, dan ukuran situs hasil build |

`[project]` adalah file `.csproj` atau folder yang berisi satu file `.csproj`; secara default, folder saat ini.

### Serve dan live reload

```
$ rustzor serve
▸ Building Shop.csproj
✓ Serving http://localhost:5080/
▸ Watching for changes. Press Ctrl+C to stop.
▸ Change detected, rebuilding
✓ Rebuilt in 2904 ms, reloaded 1 page
```

Development server menjawab setiap path yang bukan file dengan `index.html`, sehingga route di sisi client tetap berfungsi setelah reload, dan mengirim header no-cache agar reload selalu mendapatkan build terbaru. Server ini menyisipkan sebuah script kecil ke `index.html` (tidak pernah ke situs hasil publish) yang mendengarkan event dari server:

- setelah rebuild berhasil, halaman di-reload;
- setelah rebuild gagal, error build ditampilkan di atas halaman, dengan path relatif terhadap proyek; build berikutnya yang berhasil akan menghapusnya.

Perubahan pada file `.razor`, `.cs`, `.css`, `.html`, `.js`, `.json`, `.csproj`, dan file gambar memicu rebuild; `bin/` dan `obj/` diabaikan, dan serangkaian penyimpanan beruntun digabung menjadi satu build.

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

## Template `dotnet new`

`rustzor new` memakai template-template ini. Template ini juga bisa dipakai secara langsung, dan setelah diinstal akan muncul di dialog New Solution milik Rider serta di dialog New Project milik Visual Studio (dengan opsi "Show all .NET CLI templates" diaktifkan).

```bash
dotnet new install Rustzor.Templates
dotnet new rustzor -n Shop --Title "Kedai Senja"
dotnet new rustzor-empty -n Tiny
dotnet new rustzorlib -n Widgets
```

| Template | Nama pendek | Yang Anda dapatkan |
| --- | --- | --- |
| Rustzor App | `rustzor` | App shell Rustzor.UI dengan halaman Home, Counter, dan Tasks (disimpan di `localStorage`), siap dikembangkan |
| Rustzor App (empty) | `rustzor-empty` | Satu halaman, tanpa UI kit |
| Rustzor Library | `rustzorlib` | Razor class library berisi sebuah component, kode C# di atas sebuah API browser, dan `rustzor.module.js`-nya |

Sebuah template meminta package Rustzor dengan versi yang sama dengan saat template itu dirilis.

## Visual Studio Code

Extension **Rustzor** (`tools/vscode-rustzor`) membawa CLI ke dalam editor:

- Command (Command Palette, `Rustzor:`): New Project, Serve with Live Reload, Stop Serving, Publish, Publish with AOT, Add Package… (daftar semua package yang bisa dicari), Show Project Info, Install or Update the CLI, Open Documentation.
- Tombol **Rustzor: serve** di status bar saat workspace berisi aplikasi Rustzor, serta Serve, Publish, dan Add Package pada file `.csproj` di explorer.
- Snippet: `rzpage`, `rzcomp`, `rzlayout`, `rzgrid`, `rzmodal`, `rztimer` (Razor); `rzprogram`, `rzjs`, `rzjsobject`, `rzfetch`, `rzsignalr`, `rzproto`, `rzsqlite` (C#); `rzmodule`, `rzop` (JavaScript).

Instal dari Marketplace: cari **Rustzor** di tampilan Extensions, atau jalankan `code --install-extension GravicodeStudios.rustzor`. File `.vsix` yang di-build oleh CI juga bisa diinstal manual (tampilan Extensions, menu `…`, **Install from VSIX**).

## Katalog package

[`docs/catalog/index.html`](../catalog/index.html) mendaftar setiap package beserta ikon, deskripsi, dan perintah instalasinya, dilengkapi pencarian, filter per kelompok, dan tombol salin; [packages.md](packages.md) adalah daftar yang sama dalam format Markdown. Keduanya di-generate dari proyek-proyeknya oleh `node tools/e2e/make-catalog.mjs`, dan CI akan gagal jika keduanya tidak mutakhir.
