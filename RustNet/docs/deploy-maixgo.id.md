# Deploy Aplikasi C# ke Sipeed Maix Go

Prosedur lengkap untuk menjalankan aplikasi .NET di Sipeed Maix Go
(Kendryte K210, RISC-V 64-bit) — lengkap dengan grafis di panel 320×240-nya
dan file di SPI flash-nya.

Ada **dua fase**, dan biayanya sangat berbeda:

| | Caranya | Kapan |
|---|---|---|
| **A. Flash firmware** | `kflash` lewat port USB board | Sekali, dan setiap kali firmware berubah |
| **B. Ganti aplikasi** | `rustnet flash` lewat port yang sama | Setiap kali kode C#-mu berubah |

Fase B yang akan kamu ulang terus. Tanpa tombol, tanpa jumper, tanpa kabel
kedua: soket USB yang sama membawa ISP loader di ROM *dan* konsol RNDP
firmware.

Aplikasi yang diunggah disimpan di flash dan **jalan kembali sendiri setelah
dimatikan dan dinyalakan**, berikut apa pun yang ia tulis ke filesystem.

Versi bahasa Inggris: [`deploy-maixgo.md`](deploy-maixgo.md).

## Board-nya

| | |
|---|---|
| SoC | Kendryte K210 — dual RV64GC, **390 MHz** terukur dari PLL0 milik ROM |
| RAM | 6 MB SRAM (4 MB + 2 MB bersambung); tanpa flash internal |
| Flash | 16 MB SPI NOR (GigaDevice `c8:60:18`) |
| Panel | MaixLCD 320×240, ST7789V, paralel 8-bit di SPI0 mode octal |
| LED | IO14 merah, **IO12 biru, IO13 hijau** |
| Konsol | UARTHS di IO4/IO5, lewat jembatan FT2232H board |
| Pemrograman | ISP di ROM lewat port yang sama — tanpa probe, tanpa kombinasi tombol |

Data pin diambil dari *Maix Go Datasheet v1.1* milik Sipeed dan skematik
board-nya. Perhatikan LED-nya: `config_maix_go.py` milik Sipeed sendiri menukar
hijau dan biru, dan tabel pin di datasheet-lah yang cocok dengan hardware,
bukan skrip itu.

**Tidak ada flash internal.** Mask ROM menyalin image dari SPI NOR ke SRAM lalu
melompat ke sana, jadi ini link RAM-only dan image yang rusak tidak pernah
mematikan board — ISP-nya tidak bergantung pada isi flash.

### Pembagian flash-nya

| Rentang | Isi |
|---|---|
| `0x000000`… | image firmware (~360 KB) |
| `0x100000`–`0xFC0000` | filesystem (`rustnet-flashfs`), ~15 MB |
| `0xFC0000`–`0x1000000` | kunci provisioning, aplikasi terunggah, namanya |

# Fase A — flash firmware

## A1. Sekali saja: perkakas

```bash
rustup target add riscv64gc-unknown-none-elf
rustup component add llvm-tools
cargo install cargo-binutils --locked
pip install kflash
```

## A2. Build, konversi, flash

`kflash` minta binary mentah, jadi objcopy dulu:

```bash
cd runtime/firmware-k210
cargo build --release
rust-objcopy -O binary \
    target/riscv64gc-unknown-none-elf/release/rustnet-firmware-k210 \
    target/fw.bin

kflash -p COM10 -b 1500000 -B goE -n target/fw.bin
```

**`-B goE` wajib.** Itu memilih urutan reset untuk jembatan USB board ini;
tanpanya `kflash` tidak bisa memasukkan chip ke ISP-nya.

# Fase B — ganti aplikasi lewat kabel

## B1. Sekali per mesin: pasangan kunci

```bash
rustnet keys generate --out keys
```

`keys/` sudah di-gitignore. Kehilangan bagian privatnya berarti device menolak
setiap unggahan berikutnya — lihat B2.

## B2. Sekali per device: provisioning

```bash
rustnet provision --key keys/rustnet-signing.pub --device serial:COM10
```

Device menyimpan kunci publiknya dan memverifikasi setiap image terhadapnya.
Provisioning ulang dengan kunci berbeda masih mungkin di sini (tidak seperti
eFuse ESP32 yang sekali tulis), tapi hanya selama window record belum penuh.

## B3. Build dan flash aplikasimu

```bash
dotnet build MyApp.csproj
rustnet flash bin/Debug/net10.0/MyApp.dll --name myapp \
    --chip k210 --key keys/rustnet-signing.key --start --device serial:COM10
```

**`--chip k210` penting.** Kontainer bertanda tangan mencatat keluarga chip dan
device menolak yang disegel untuk chip lain; defaultnya `host-sim`.

## B4. Mengamatinya

```bash
rustnet logs -n 40 --device serial:COM10
rustnet info --device serial:COM10
```

`info` melaporkan lebih dari yang terlihat: `cpu_hz` itu hasil *pengukuran*,
`rx_dropped` dan `rx_irqs` menunjukkan apakah konsolnya sanggup mengikuti, dan
`max_poll_gap_us` adalah seberapa lama aplikasi tidak melayani tools.

## B5. Demo grafisnya

`runtime/firmware-k210/demo/Showcase` adalah contoh jadi — starfield 2D, kubus
dan oktahedron wireframe berputar di atas lantai perspektif, ditutup judul yang
terbakar. Ia menulis penghitung run lewat `RustNet.IO.FileSystem` dan mencetak
waktu frame-nya sendiri:

```bash
dotnet build runtime/firmware-k210/demo/Showcase/Showcase.csproj
rustnet flash runtime/firmware-k210/demo/Showcase/bin/Debug/net10.0/Showcase.dll \
    --name showcase --chip k210 --key keys/rustnet-signing.key \
    --start --device serial:COM10
```

```
[fs] run #17, files under /showcase: runs.txt
[intro] 93 frames in 5046 ms — 54 ms/frame, ~18 fps
[3d]   196 frames in 9037 ms — 46 ms/frame, ~21 fps
```

## Yang kamu dapat

- **Grafis** — seluruh permukaan `RustNet.Graphics.Display` di atas framebuffer
  `rustnet-gfx`, dikirim ke panel oleh `Display.Present()`.
- **File** — `RustNet.IO.FileSystem` di ~15 MB flash board. Blob bernama dalam
  log, bukan FAT: tanpa handle, tanpa seek, file ditulis ulang utuh.
- **Bahasanya** — inheritance dan interface dengan virtual dispatch,
  async/await, generics, LINQ, `catch when`. Lihat `docs/dotnet-support.md`.

## Yang belum kamu dapat

OTA, debugger di device, dan config terenkripsi — semuanya masih terikat `std`
di `runtime/firmware`. WiFi juga belum: ESP8285 board dimuxkan ke UART1 dan
belum ada drivernya. Kamera dan mic array juga belum.

## Menulis aplikasi yang kencang

Dua angka, terukur di board ini, yang mengubah cara menulis kode:

- **Satu host call ≈ 220 µs**, karena interpreter mencocokkan nama method
  kanoniknya sebagai string. Kerja per-piksel di C# jelas tidak mungkin, begitu
  pula satu `FillRect` per sel grid — gabungkan jadi runs.
- **Satu pemanggilan method statik managed ≈ 65 µs**, cuma tiga kali lebih
  murah. Memisahkan helper satu baris jadi method di dalam loop panas bisa
  lebih mahal daripada kerja yang dipisahkannya. Inline-kan ke loop yang memang
  sudah berjalan.

Batasi animasi dengan `Uptime.Ms()`, bukan hitungan frame: virtual device kira-
kira empat puluh kali lebih cepat, jadi hitungan tetap akan berjalan hitungan
detik di satu sisi dan hitungan menit di sisi lain.

## Pemecahan masalah

| Gejala | Sebab | Solusi |
|---|---|---|
| Semua perintah timeout | Board reboot saat port dibuka, request pertama hilang | Tools sudah ping sampai dijawab; kalau tetap, matikan-nyalakan lalu ulangi |
| Board diam, layar putih, konsol kosong | Ada yang memberi pulsa **RTS** — itu jalan masuk ISP-nya `kflash`, dan ROM loader tidak bicara apa-apa | Beri pulsa **DTR** dengan RTS diam untuk boot aplikasi. Reset aktif setiap kali DTR dan RTS *berbeda* |
| `flash` timeout di tengah unggahan | Compaction storage, atau aplikasi mengelaparkan service loop | `rustnet apps stop` dulu, baru flash |
| `flash` ditolak karena chip salah | `--chip` defaultnya `host-sim` | Pakai `--chip k210` |
| `flash` bilang *not provisioned* | Belum pernah provisioning, atau window record terhapus | Jalankan `rustnet provision` lagi |
| App hasil restore rusak setelah power cycle | Menulis ke flash yang belum terhapus penuh | Sudah diperbaiki — firmware memeriksa seluruh rentang dan membaca balik record. Kalau muncul, laporkan offset `flash verify failed`-nya |
| Filesystem dan app hilang setelah flash MaixPy | Image MaixPy ~2 MB dan menimpa window di `0x100000` | Memang begitu. Provisioning dan flash ulang |
| Layar putih rata | Panel bangun tapi tidak menerima piksel | Itu tampilan `spi_ctrlr0` yang salah; lihat `runtime/firmware-k210/README.md` |
| Gambar berputar — tepi kanan muncul di kiri | Frame dikirim lebih dari satu transfer | Sudah diperbaiki; `present` mengirim frame dalam satu transfer |
| `rx_dropped` naik terus di `info` | Ring penerima meluap | Cek `max_poll_gap_us`; aplikasi grafis melebarkannya jauh |

## Lihat juga

- `runtime/firmware-k210/README.md` — internal firmware, dan fakta-fakta panel
  selengkapnya
- [`deploy-netduino3.id.md`](deploy-netduino3.id.md) — tujuan yang sama di
  bare-metal ARM
- [`deploy-esp32.id.md`](deploy-esp32.id.md) — tujuan yang sama di ESP32
- `docs/chips.md` — matriks dukungan seluruh varian chip
- `docs/dotnet-support.md` — fitur C# mana saja yang diimplementasikan runtime
