# Memulai dengan JS.Net

> JS.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/getting-started.md)

## 1. Instalasi

Anda membutuhkan **.NET 10 SDK**.

```bash
dotnet new install JSNet.Templates     # template dotnet new
dotnet tool install -g JSNet.Cli       # perintah jsnet (opsional tapi disarankan)
```

`JSNet.Sdk` dan `JSNet.Runtime` di-restore otomatis oleh proyek yang dibuat dari template.

> **Sebelum paket tersedia di nuget.org** (atau saat bekerja dari source), build paket secara lokal lalu tambahkan foldernya sebagai sumber paket:
>
> ```bash
> pwsh build/pack.ps1                                        # → artifacts/packages
> dotnet new install artifacts/packages/JSNet.Templates.0.2.1.nupkg
> dotnet nuget add source <repo>/artifacts/packages -n jsnet-local
> ```

## 2. Program pertama

```bash
dotnet new jsnet -n Hello
cd Hello
dotnet run
```

`Program.jsn`:

```jsnet
import { Greeter } from "./Greeter";

const greeter = new Greeter("JS.Net");
console.log(greeter.greet());

async function fetchGreeting(name: string): Promise<string> {
    await delay(100);
    return `Halo, ${name}!`;
}

console.log(await fetchGreeting("Dunia"));
```

`Greeter.jsn`:

```jsnet
export class Greeter {
    constructor(private readonly name: string) {}

    greet(): string {
        return `Selamat datang di ${this.name}`;
    }
}
```

Semua file `.jsn` di folder proyek ikut dikompilasi. Setiap file otomatis bisa melihat deklarasi file lain; `import { X } from "./file"` diterima demi keterbacaan tetapi tidak wajib.

## 3. Skrip tanpa proyek

```bash
jsnet run script.jsn -- arg1 arg2     # kompilasi di memori lalu jalankan
jsnet emit script.jsn                 # tampilkan C# yang dihasilkan
jsnet check src/                      # hanya diagnostik
```

## 4. Jenis aplikasi lain

```bash
dotnet new jsnet-webapi   -n Api        # ASP.NET Core minimal API + controller + DI @Service
dotnet new jsnet-blazor   -n Web        # Blazor Web App dengan code-behind .razor.jsn
dotnet new jsnet-wpf      -n Desktop    # WPF dengan code-behind .xaml.jsn
dotnet new jsnet-winforms -n Forms      # Windows Forms yang dibangun lewat kode
dotnet new jsnet-avalonia -n CrossApp   # Avalonia (Windows/macOS/Linux)
dotnet new jsnet-worker   -n Worker     # background service
dotnet new jsnet-classlib -n Lib        # library yang bisa dipakai dari C#
dotnet new jsnet-xunit    -n Tests      # test xUnit dalam JS.Net
```

## 5. Publish

Semua yang didukung `dotnet publish` berfungsi, termasuk NativeAOT:

```bash
dotnet publish -c Release -r win-x64 -p:PublishAot=true
```

> NativeAOT membutuhkan linker platform (di Windows: Visual Studio "Desktop development with C++"). Kode yang memakai `dynamic`, `JSON.parse` tanpa tipe, atau `console.log` pada objek sembarang bergantung pada reflection; utamakan kode bertipe untuk aplikasi AOT.

## 6. Dukungan editor

- **VS Code**: pasang [JS.Net](https://marketplace.visualstudio.com/items?itemName=GravicodeStudios.jsnet) dari Marketplace (`code --install-extension GravicodeStudios.jsnet`). Membutuhkan CLI `jsnet` di PATH.
- **Visual Studio**: pasang [JS.Net for Visual Studio](https://marketplace.visualstudio.com/items?itemName=GravicodeStudios.jsnet-visualstudio) (*Extensions → Manage Extensions*).
- **Rider / IntelliJ**: LSP4IJ ditambah template di [`jetbrains/`](../../jetbrains/README.md).
- Di mana pun: file `.jsn` di-build dan di-debug lewat SDK; breakpoint dan stack trace mengarah ke baris `.jsn` berkat pemetaan `#line`.
- **Tanpa instalasi**: [playground WebAssembly](https://dotnetvibecoderz.github.io/Vibe_Framework/) mengompilasi dan menjalankan JS.Net sepenuhnya di browser.

## Selanjutnya

- [Panduan bahasa](language-guide.md)
- [Interop .NET & runtime](interop-and-runtime.md)
- [Tooling](tooling.md)
