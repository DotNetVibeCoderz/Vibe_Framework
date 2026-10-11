# Keterbatasan

Rustzor menjalankan IL Anda di RustNetRuntime, bukan di runtime milik Microsoft. Sebagian besar kode C# berjalan tanpa perubahan; halaman ini mendaftar yang tidak, agar Anda bisa merencanakan solusinya. Saat Anda menemukan sesuatu yang belum ada, pesan error-nya menyebutkannya dengan jelas: `no implementation for System.X::Method(...)`.

## Runtime

RustBCL, base class library-nya, mencakup apa yang paling sering dipakai aplikasi: string dan formatting (termasuk format angka dan tanggal yang culture-invariant), collection dan LINQ, `Task`, `async`/`await` dan cancellation, reflection (type, property, attribute, `Activator`), `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, `Random`, `Math`, regular expression, `StringBuilder`, `Encoding` (UTF-8 dan ASCII), `BitConverter`, `Convert` (termasuk base64), span, dan record.

Tidak tersedia:

- **File system dan process**: `System.IO.File`, `Directory`, `Process`. Gunakan `Rustzor.Browser.FileSystem` dan `Rustzor.Browser.Storage`.
- **`HttpClient` dan socket**: gunakan `FetchClient`, `WebSocketClient`, `Rustzor.Net.SignalR`, dan `Rustzor.Net.GrpcWeb`.
- **`System.Text.Json`**: gunakan `Rustzor.Interop.Json`.
- **Thread**: browser hanya memberi halaman satu thread. `Task.Run` berjalan di thread itu; gunakan `Rustzor.Browser.Workers` untuk pekerjaan paralel di JavaScript.
- **Culture**: formatting bersifat culture-invariant. Format angka dan tanggal secara eksplisit untuk locale tertentu (`ToString("N0").Replace(',', '.')` untuk rupiah).
- **Regular expression**: semuanya didukung kecuali `RegexOptions.RightToLeft`.
- **`CancellationTokenSource.CancelAfter`**: batalkan dari timer sebagai gantinya (`Scheduler.SetTimeout(cts.Cancel, ms)`).

Daftar resmi dari runtime, yang selalu diperbarui, ada di [Vibe_Runtime/RustNetRuntime/docs/limitations.md](https://github.com/DotNetVibeCoderz/Vibe_Runtime/blob/main/RustNetRuntime/docs/limitations.md).

## Model komponen

Lihat [komponen](components.md#belum-didukung): belum ada `DataAnnotationsValidator`, `InputRadio`, `InputFile`, `Virtualize`, `HeadOutlet`/`SectionOutlet`, dan route constraint selain `int`, `long`, `bool`, serta tipe-tipe angka tidak ditegakkan.

## Tooling

- Reload tidak mempertahankan state: `rustzor serve` mem-build ulang dan me-reload seluruh halaman setiap kali Anda menyimpan (lihat [tooling](tooling.md)), sehingga state di memori dimulai dari awal lagi.
- Debugging dilakukan lewat console browser: unhandled exception dicetak beserta managed stack trace-nya.

## Performa

Interpreter-nya cukup cepat untuk aplikasi bisnis, dashboard, dan game 2D pada 60 frame per detik, tetapi loop numerik yang padat berjalan beberapa kali lebih lambat dibandingkan di .NET. Untuk kasus seperti itu, [mode AOT](aot.md) mengompilasi static method yang memenuhi syarat ke WebAssembly.

Pemuatan pertama mengunduh sekitar 1,9 MB untuk runtime ditambah assembly Anda; keduanya di-cache setelahnya.
