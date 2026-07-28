# Deploy Aplikasi C# ke ESP32-WROOM-32

Prosedur lengkap untuk menjalankan aplikasi .NET di silikon ESP32
sungguhan. `docs/getting-started.md` menjelaskan alur yang sama untuk
**virtual device**; dokumen ini untuk chip fisik, di mana ada tiga
perbedaan: firmware harus di-flash dengan tool vendor, setiap perintah
RustNet perlu `--device serial:COMx`, dan container aplikasi harus
ditandatangani untuk `--chip esp32`.

Versi bahasa Inggris: [`deploy-esp32.md`](deploy-esp32.md).

## Yang dibutuhkan

- Devkit ESP32-WROOM-32 terpasang di port USB (panduan ini memakai `COM4`)
- .NET SDK 10
- Rust, plus toolchain Espressif (langkah 1)

Langkah 1-2 cukup sekali per komputer, langkah 3 sekali per device.
Sehari-hari kamu hanya mengulang langkah 5-7.

---

## 1. Sekali saja: toolchain Espressif

rustc resmi tidak punya backend Xtensa, jadi ESP32 memerlukan fork dari
Espressif:

```bash
cargo install espup ldproxy espflash --locked
espup install --targets esp32
```

`espup` membuat skrip environment — `%USERPROFILE%\export-esp.ps1` di
Windows, `~/export-esp.sh` di OS lain. Source skrip itu di **setiap
shell yang dipakai membangun firmware**:

```powershell
. C:\Users\mifma\export-esp.ps1
```

## 2. Sekali saja: build dan flash firmware

```powershell
cd runtime\firmware-esp32
cargo build --release
```

Build pertama mengunduh ESP-IDF v5.2.3 dan memakan 10-20 menit; build
inkremental 10-30 detik. Hasilnya masuk ke `C:/rnesp`, bukan `./target`
— `.cargo/config.toml` menyetel `target-dir = "C:/rnesp"` karena
esp-idf-sys menolak path output yang panjang di Windows.

```powershell
espflash flash C:\rnesp\xtensa-esp32-espidf\release\rustnet-firmware-esp32 `
    --partition-table partitions.csv --port COM4
cd ..\..
```

> **`--partition-table partitions.csv` itu wajib, bukan opsional.** Flag
> ini yang membuat partisi FAT `storage` berukuran ~1,9 MB
> (`runtime/firmware-esp32/partitions.csv`). Tanpanya firmware jatuh ke
> MemFs di RAM, dan aplikasi, kunci provisioning, serta setelan autostart
> semuanya hilang setiap kali reboot.

Pastikan sudah booting:

```powershell
rustnet probe --port COM4 --log
```

> **UART0 adalah jalur transport RNDP.** Jangan biarkan `espflash
> monitor` atau serial monitor lain terpasang saat menjalankan perintah
> `rustnet` — port-nya eksklusif dan keduanya akan berebut.

## 3. Build tooling dan provisioning device

Dari root repo:

```powershell
dotnet build dotnet\RustNet.slnx
$rustnet = "$PWD\dotnet\tools\RustNet.Cli\bin\Debug\net10.0\rustnet.exe"

& $rustnet keys generate --out keys
& $rustnet provision --key keys\rustnet-signing.pub --device serial:COM4
& $rustnet info --device serial:COM4
```

`keys generate` menulis `rustnet-signing.key` (privat, untuk
menandatangani) dan `rustnet-signing.pub` (publik, ditanam ke device).
Setelah provisioning, device hanya menerima image yang ditandatangani
kunci tersebut.

Format device spec adalah `serial:COM4[:baud]`, baud default 115200
(`dotnet/tools/RustNet.Deploy/Transport.cs:77`). Flag `--device` selalu
diletakkan **sesudah** subcommand, bukan sebelumnya.

## 4. Buat dan build aplikasinya

```powershell
$env:RUSTNET_SDK = "C:\Users\mifma\Documents\CodeSandbox\RustNet"
& $rustnet new graphics-primitives GfxTest
dotnet build GfxTest\GfxTest.csproj -c Debug
```

`RUSTNET_SDK` wajib diisi — csproj template me-resolve pustaka
`RustNet.*` lewat variabel itu. `rustnet new` membuat proyek di
direktori kerja saat ini. Jalankan `rustnet templates` untuk melihat
semua template yang tersedia.

Build memakai **Debug**: async pada konfigurasi Release belum didukung,
dan entry point harus `static void Main()` (top-level statement
menghasilkan `Main(string[])` dan akan ditolak).

## 5. Flash aplikasinya

```powershell
& $rustnet flash GfxTest\bin\Debug\net10.0\GfxTest.dll `
    --name gfx --key keys\rustnet-signing.key `
    --chip esp32 --start --device serial:COM4
```

> **`--chip esp32` itu wajib.** Nilai default flag ini `host-sim`
> (`dotnet/tools/RustNet.Cli/BuildCommands.cs:33`), dan device akan
> menolak container yang tidak cocok dengan `BootError::WrongChip`
> (`runtime/rustnet-secureboot/src/lib.rs:180`). Pakai `--chip any` kalau
> kamu ingin satu container yang bisa jalan di device mana pun.

Perintah ini meng-compile DLL menjadi RNX, menyegelnya dalam container
RNSB bertanda tangan, mengirimnya lewat RNDP, dan `--start` langsung
menjalankannya.

## 6. Verifikasi

```powershell
& $rustnet logs -n 60 --device serial:COM4
& $rustnet apps list --device serial:COM4
& $rustnet profile --device serial:COM4
```

Untuk aplikasi grafis, tarik framebuffer-nya sebagai gambar PPM:

```powershell
& $rustnet display capture -o frame.ppm --device serial:COM4
```

> **WROOM-32 tidak punya panel terpasang.** `present_frame` di-gate
> `#[cfg(feature = "board-m5tough")]`
> (`runtime/firmware-esp32/src/board.rs:1389`), jadi build default memakai
> implementasi no-op dari trait `Board`. Aplikasi grafis tetap merender
> penuh ke framebuffer di memori — verifikasinya lewat `display capture`,
> bukan lewat mata. Untuk layar sungguhan, lihat
> [Board yang punya panel](#board-yang-punya-panel) di bawah.

## 7. Opsional: bertahan setelah reboot dan terhubung ke jaringan

```powershell
& $rustnet apps autostart gfx --device serial:COM4     # atau: autostart off
& $rustnet wifi --ssid MyNetwork --psk secret --device serial:COM4
```

Autostart menjalankan aplikasi bernama itu setiap kali device dinyalakan.
Begitu kredensial WiFi tersimpan, firmware juga melayani RNDP lewat TCP
di port 7878, sehingga kamu bisa berpindah dari `--device serial:COM4` ke
`--device tcp:<ip-device>:7878`. Keduanya memerlukan partisi `storage`
dari langkah 2.

---

## Board yang punya panel

Devkit ESP32-WROOM-32 tidak punya display. **M5Stack Tough** punya —
panel ILI9342C 320×240 yang dayanya lewat PMIC AXP192. Panduan lengkap:
[`deploy-m5tough.id.md`](deploy-m5tough.id.md). Versi singkatnya:

```powershell
cd runtime\firmware-esp32
cargo build --release --features board-m5tough
espflash flash C:\rnesp\xtensa-esp32-espidf\release\rustnet-firmware-esp32 `
    --partition-table partitions.csv --port COM4
```

Semua langkah mulai nomor 3 identik. Framebuffer RGB565 320×240 berukuran
150 KB, yang tidak muat di DRAM ESP32 secara kontigu, jadi build ini
mengaktifkan PSRAM di `sdkconfig.defaults` — PSRAM wajib ada untuknya.

Template `graphics-primitives` menyesuaikan diri dengan ukuran apa pun
yang dilaporkan `Display`, jadi aplikasi yang sama mengisi penuh M5 Tough
320×240 maupun TFT 160×128.

## Pemecahan masalah

| Gejala | Penyebab | Solusi |
|---|---|---|
| `WrongChip` saat flash | Container disegel untuk `host-sim` | Tambahkan `--chip esp32` |
| Aplikasi hilang setelah dimatikan | Partisi `storage` tidak ada | Flash ulang firmware dengan `--partition-table partitions.csv` |
| Port sibuk / frame kacau | Serial monitor masih terpasang | Tutup `espflash monitor`; UART0 dipakai RNDP |
| Verifikasi tanda tangan gagal | Device di-provision dengan kunci lain | Jalankan ulang `provision`, atau tandatangani dengan `.key` yang cocok |
| Template gagal restore package | `RUSTNET_SDK` belum diset | `$env:RUSTNET_SDK = <root repo>` |
| Error path di esp-idf-sys | Path build terlalu panjang di Windows | Pertahankan `target-dir = "C:/rnesp"` dari `.cargo/config.toml` |
| Build firmware gagal di kode JPEG | Crate `image` tidak bisa di-compile untuk Xtensa | Sudah ditangani: `image` ada di balik feature `image-codecs`, mati untuk `chip-esp32` |

## Lihat juga

- [`deploy-m5tough.id.md`](deploy-m5tough.id.md) — alur yang sama di
  board dengan panel hidup (M5Stack Tough)
- `runtime/firmware-esp32/README.md` — internal firmware dan status chip
- `docs/chips.md` — matriks dukungan untuk semua varian chip
- `docs/getting-started.md` — alur yang sama untuk virtual device
- `docs/protocol.md` — referensi frame dan command RNDP
- `docs/debugging.md` — debugging level source lewat RNDP
