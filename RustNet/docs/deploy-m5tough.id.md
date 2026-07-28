# Deploy Aplikasi C# ke M5Stack Tough

M5Stack Tough adalah board pertama dengan **panel fisik yang hidup** —
LCD ILI9342C 320×240 yang dayanya lewat PMIC AXP192. Aplikasi grafis
tampil langsung di layar, bukan cuma bisa dibaca lewat `display capture`.

Ini board ESP32, jadi seluruh alurnya sama dengan
[`deploy-esp32.id.md`](deploy-esp32.id.md). Dokumen ini hanya membahas
perbedaannya. Baca dokumen itu dulu kalau kamu belum pernah mem-flash
device RustNet.

Versi bahasa Inggris: [`deploy-m5tough.md`](deploy-m5tough.md).

## Bedanya dengan WROOM-32 biasa

| | Devkit WROOM-32 | M5Stack Tough |
|---|---|---|
| Build firmware | `cargo build --release` | `cargo build --release --features board-m5tough` |
| Display | tidak ada — `present_frame` no-op | ILI9342C 320×240, hidup |
| PSRAM | tidak diperlukan | **wajib** (framebuffer 150 KB) |
| Verifikasi grafis | `rustnet display capture` | lihat layarnya langsung |

Selebihnya — provisioning, `--chip esp32`, `--device serial:COMx`, tabel
partisi — identik.

---

## 1. Build dan flash firmware

Setup toolchain sekali saja, sama seperti
[`deploy-esp32.id.md` langkah 1](deploy-esp32.id.md#1-sekali-saja-toolchain-espressif).
Lalu:

```powershell
. C:\Users\mifma\export-esp.ps1
cd runtime\firmware-esp32
cargo build --release --features board-m5tough
espflash flash C:\rnesp\xtensa-esp32-espidf\release\rustnet-firmware-esp32 `
    --partition-table partitions.csv --port COM4
cd ..\..
```

> **`--features board-m5tough` inilah yang menyambungkan panelnya.**
> Tanpa flag itu, `present_frame` jatuh ke implementasi no-op dari trait
> `Board` (`runtime/firmware-esp32/src/board.rs:1389`) dan layarnya tetap
> gelap apa pun yang digambar aplikasi.

> **`--partition-table partitions.csv` tetap wajib** — alasannya sama
> seperti di WROOM: flag ini yang membuat partisi FAT `storage`, yang
> tanpanya aplikasi, provisioning, dan autostart tidak bertahan setelah
> reboot. Layout-nya dirancang untuk flash 4 MB; Tough punya lebih besar,
> jadi sisa kapasitasnya sekadar tidak dipartisi. Itu tidak masalah.

### PSRAM di sini bukan opsional

Framebuffer RGB565 320×240 berukuran 150 KB, yang tidak muat di DRAM
internal ESP32 secara kontigu. `sdkconfig.defaults` mengaktifkan PSRAM
(Tough punya 8 MB) dengan `CONFIG_SPIRAM_IGNORE_NOTFOUND=y`, sehingga
image yang sama tetap bisa boot di board tanpa PSRAM — tapi di board
seperti itu jalur panelnya tidak punya tempat untuk framebuffer.

## 2. Provisioning dan deploy aplikasi

Identik dengan alur WROOM — lihat
[`deploy-esp32.id.md` langkah 3-5](deploy-esp32.id.md#3-build-tooling-dan-provisioning-device):

```powershell
dotnet build dotnet\RustNet.slnx
$rustnet = "$PWD\dotnet\tools\RustNet.Cli\bin\Debug\net10.0\rustnet.exe"
$env:RUSTNET_SDK = "C:\Users\mifma\Documents\CodeSandbox\RustNet"

& $rustnet keys generate --out keys
& $rustnet provision --key keys\rustnet-signing.pub --device serial:COM4

& $rustnet new graphics-primitives GfxTest
dotnet build GfxTest\GfxTest.csproj -c Debug
& $rustnet flash GfxTest\bin\Debug\net10.0\GfxTest.dll `
    --name gfx --key keys\rustnet-signing.key `
    --chip esp32 --start --device serial:COM4
```

`--chip esp32` tetap nilai yang benar — Tough adalah ESP32, dan feature
board tidak mengubah keluarga chip di container bertanda tangan.

## 3. Lihat hasilnya

Layarnya sekarang harus memutar scene-scene `graphics-primitives`: judul
dengan gradient dan petak warna, garis, rect, lingkaran/elips, segitiga,
gradient, teks di beberapa skala, kubus 3D berputar, bouncing balls, dan
penutup matrix-rain yang double-buffered.

Aplikasinya juga menulis marker per scene, jadi kamu bisa mengikuti
progresnya tanpa melihat panel:

```powershell
& $rustnet logs --follow --device serial:COM4
```

Di awal harus muncul `panel 320x240` — itu `Display.Width()` /
`Height()` melaporkan ukuran panel sungguhan balik ke kode managed.

`display capture` tetap berfungsi dan menarik framebuffer yang sama,
berguna untuk menyimpan screenshot persis seperti yang tampil di panel.

Template lain yang menarik dicoba di panel sungguhan: `display-testing`,
`image-viewer` (GIF tertanam), `ui-dashboard` (UI dari XML), dan
`xox-game`.

## Cara panelnya dikendalikan

Berguna saat men-debug layar yang gelap atau kacau — semuanya ada di
`runtime/firmware-esp32/src/board.rs`:

- **Daya dulu.** AXP192 (I²C `0x34` di SDA 21 / SCL 22) mengatur rel LCD,
  rel backlight, dan reset panel. Layar tetap gelap sampai chip itu
  diprogram, jadi `m5_axp192_init` berjalan sebelum flush pertama.
  Register-nya di-read-modify-write supaya DCDC1 — jalur 3,3 V milik
  ESP32 sendiri — tidak pernah ikut terhapus.
- **Baru panelnya.** ILI9342C di SPI2, SCLK 18 / MOSI 23 / CS 5 / DC 15,
  dengan CS dan DC digerakkan manual supaya satu frame penuh mengalir
  dalam satu kali assert CS. Clock-nya 26,67 MHz: SPI2 melewatkan pin
  ini melalui GPIO matrix yang mentok di APB/3 (40 MHz butuh pin IO_MUX
  native).
- **Frame dikirim lewat DMA per pita.** 40 baris sekali jalan (25.600 B).
  Framebuffer-nya ada di PSRAM, yang tidak bisa dibaca langsung oleh DMA
  SPI, jadi tiap pita disalin dulu ke bounce buffer internal yang
  DMA-capable sekaligus ditukar ke big-endian untuk `RAMWR`. Byte
  command dan parameter memakai `tx_data` inline milik transaksi,
  sehingga tidak pernah ada buffer kecil tak selaras yang diserahkan ke
  mesin DMA.

Inisialisasinya malas (lazy): PMIC dan panel baru dinyalakan pada
`Display.Present()` yang **pertama**, bukan saat boot.

## Keterbatasan yang diketahui

- **Belum ada dukungan touch.** Panel sentuh kapasitif Tough belum
  tersambung ke HAL — belum ada trait maupun intrinsic untuk touch. Input
  harus datang dari jalur lain (UART, WiFi, GPIO).
- **`rustnet info` melaporkan `esp32-wroom-32 (esp-idf)`.**
  `Board::name()` tidak sadar feature board (`board.rs:1287`), jadi Tough
  memperkenalkan dirinya sebagai WROOM. Ini kosmetik saja.
- **Konflik pin SPI.** HAL `Spi` generik memakai SPI3/VSPI di SCLK 18 /
  MOSI 23 / MISO 19 / CS 5 — SCLK, MOSI, dan CS yang sama persis dengan
  panel. Jangan memakai `RustNet.Devices.Spi` dari aplikasi di board ini
  selama display aktif.
- **I²C bus 0 dipakai bersama PMIC.** Aplikasi yang memakai I²C di
  SDA 21 / SCL 22 berbagi bus dengan AXP192 di `0x34`. Aman untuk alamat
  lain; jangan menulis ke `0x34`.

## Pemecahan masalah

| Gejala | Penyebab | Solusi |
|---|---|---|
| Layar gelap, aplikasi jalan normal | Firmware dibangun tanpa feature board | Build ulang dengan `--features board-m5tough` |
| Layar gelap, log berisi `present_frame failed` | AXP192 tidak meng-ACK di I²C | Periksa jalur SDA 21 / SCL 22; PMIC harus menjawab di `0x34` |
| Boot loop atau gagal alokasi | PSRAM tidak ada atau nonaktif | Pastikan `CONFIG_SPIRAM=y` di `sdkconfig.defaults` dan unit-nya memang punya PSRAM |
| Warna terbalik atau tertukar | Ekspektasi MADCTL/INVON beda antar unit | `init_panel` menyetel MADCTL `0x08` (BGR) + INVON; sesuaikan untuk panel varian lain |
| Aplikasi hilang setelah dimatikan | Partisi `storage` tidak ada | Flash ulang dengan `--partition-table partitions.csv` |
| `WrongChip` saat flash | Container disegel untuk `host-sim` | Tambahkan `--chip esp32` |

## Lihat juga

- [`deploy-esp32.id.md`](deploy-esp32.id.md) — alur deploy ESP32 dasar
- `docs/drawing.md`, `docs/ui.md` — API grafis dan UI
- `docs/chips.md` — matriks dukungan untuk semua varian chip
- `runtime/firmware-esp32/README.md` — internal firmware dan status chip
