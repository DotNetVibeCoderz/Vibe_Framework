# Deploy Aplikasi C# ke Raspberry Pi Pico

Prosedur lengkap untuk menjalankan aplikasi .NET di Raspberry Pi Pico (RP2040,
dual Cortex-M0+) — lewat **soket USB board itu sendiri**, tanpa adapter
serial, tanpa debug probe, dan tanpa tool dari vendor.

Ada **dua fase**, dan biayanya sangat berbeda:

| | Caranya | Kapan |
|---|---|---|
| **A. Flash firmware** | menyalin UF2 ke board | Sekali, dan setiap kali firmware berubah |
| **B. Ganti aplikasi** | `rustnet flash` lewat port USB yang sama | Setiap kali kode C#-mu berubah |

Fase B yang akan kamu ulang terus, dan di board ini murahnya luar biasa: Pico
muncul sebagai COM port biasa, `rustnet flash` mengganti aplikasi yang sedang
berjalan **tanpa reboot**, dan aplikasi yang sudah diunggah menyala lagi
sendiri setelah listrik mati — berikut apa pun yang ditulisnya ke filesystem.

Fase A hampir sama murahnya, karena **hanya board ini yang bisa memasukkan
dirinya sendiri ke bootloader**. Begitu firmware RustNet ada di dalamnya,
tombol BOOTSEL tidak perlu disentuh lagi — tidak untuk firmware berikutnya,
tidak juga untuk yang sesudahnya.

Versi Inggris: [`deploy-pico.md`](deploy-pico.md).

## Board-nya

| | |
|---|---|
| SoC | RP2040 — dual Cortex-M0+ di **125 MHz** |
| RAM | 264 KB SRAM; 128 KB di antaranya jadi heap interpreter |
| Flash | 2 MB QSPI NOR, eksternal — chip-nya tidak punya flash sendiri |
| Konsol | USB CDC langsung dari SoC (`2E8A:000A`) |
| Pemrograman | bootloader UF2 di ROM, lewat soket yang sama |
| LED pengguna | GP25 |

**RP2040 mengeksekusi program langsung dari flash eksternal itu** (execute in
place). Tidak ada memori program internal, jadi image, filesystem, dan
aplikasi semuanya tinggal di satu keping QSPI — itulah sebabnya menulisnya
butuh kehati-hatian yang dijelaskan di bagian pembagian flash di bawah.

### Pembagian flash

| Rentang | Isi |
|---|---|
| `0x000000`… | bootloader tahap dua + image firmware (~290 KB) |
| `0x100000`–`0x200000` | filesystem (`rustnet-flashfs`), 1 MB |

Jendela penyimpanan dimulai di 1 MB, bukan tepat di atas image. Image akan
tumbuh; memulai di situ memberi ruang sampai kira-kira tiga kali lipat tanpa
harus digeser — dan menggesernya akan membatalkan semua yang sudah tersimpan
di atasnya.

Di dalam filesystem, firmware memakai empat nama untuk dirinya sendiri di
bawah `/.sys/`: kunci penanda tangan, aplikasi, namanya, dan penanda
autostart. Selain itu semuanya milikmu.

# Fase A — flash firmware

## A1. Sekali saja: tool

```bash
rustup target add thumbv6m-none-eabi
```

Itu saja daftarnya. Pengemas UF2-nya sebuah skrip Python di dalam repo
(`runtime/firmware-rp2040/tools/elf2uf2.py`), jadi tidak ada yang perlu
di-`cargo install` dan tidak ada SDK vendor yang perlu diunduh.

## A2. Build dan flash

Cara termudah, biar tool yang mengerjakan semuanya:

```bash
rustnet firmware build --board pico
rustnet firmware flash --board pico --device serial:COM12
```

`--device` itulah yang membuatnya bebas tangan: firmware yang sedang berjalan
diminta lewat RNDP untuk reboot ke bootloader ROM-nya, board muncul kembali
sebagai drive bernama `RPI-RP2`, lalu UF2-nya disalin ke situ. Board reboot ke
firmware baru dengan sendirinya.

Hal yang sama tersedia sebagai panel di Workbench — **FIRMWARE ▸ BOARD
FIRMWARE**, pilih `pico`, tekan BUILD + FLASH.

### Khusus yang pertama kali

Board yang belum menjalankan firmware RustNet tentu belum bisa diminta masuk
bootloader, jadi flash pertama dilakukan manual, tepat sekali:

1. Cabut Pico-nya.
2. Tahan **BOOTSEL**, lalu colokkan kembali.
3. `rustnet firmware flash --board pico` — tanpa `--device`. Tool menunggu
   drive `RPI-RP2` muncul lalu menyalin ke situ.

Hal yang sama berlaku setelah kamu menulis firmware *lain* ke board itu.

### Manual, kalau lebih suka

```bash
cd runtime/firmware-rp2040
cargo build --release
python tools/elf2uf2.py \
    target/thumbv6m-none-eabi/release/rustnet-firmware-rp2040 rustnet-pico.uf2
# lalu salin rustnet-pico.uf2 ke drive RPI-RP2
```

## A3. Pastikan hidup

Board muncul sebagai COM port beberapa detik setelah penyalinan:

```bash
rustnet info --device serial:COM12
{"chip":"rp2040","board":"Raspberry Pi Pico","protocol":1,"heap_used":10176,
 "active_app":"blink (embedded)","running":true,"transport":"usb-cdc",
 "cpu_hz":125000000}

rustnet logs -n 5 --device serial:COM12
RustNet on Raspberry Pi Pico @ 125 MHz (peri 125 MHz), heap 128 KB
app: 67 methods, 19 types, 91 strings
[C#] blinking the user LED
```

LED-nya berkedip dua kali, berhenti sejenak, lalu mengulang — pola itu adalah
demo C# bawaan yang menggerakkan GP25 lewat HAL, bukan loop native.

> Nomor COM tidak stabil antar mesin maupun antar port. Jangan diasumsikan,
> enumerasi saja: coba `rustnet info --device serial:COMn`, atau cari `USB
> Serial Device` di Device Manager.

# Fase B — aplikasimu

## B1. Sekali saja: provisioning kunci

```bash
rustnet keys generate --out keys
rustnet provision --key keys/rustnet-signing.pub --device serial:COM12
```

**Perangkat menerima satu kunci, satu kali.** Percobaan kedua ditolak dengan
`already provisioned`. Itu disengaja: perangkat yang kuncinya bisa diganti
akan menerima apa pun yang ditandatangani pemilik barunya. Pulih dari kunci
privat yang hilang berarti menghapus jendela penyimpanan lewat bootloader —
yang menuntut akses fisik, dan justru itu maksudnya.

Simpan `keys/rustnet-signing.key` baik-baik; folder `keys/` sudah di-gitignore.

## B2. Tulis aplikasinya

```bash
rustnet new blinky --template blink
cd blinky
dotnet build
```

Interpreter di sini sama dengan yang dijalankan port lain, jadi permukaan
bahasanya sama — lihat [`dotnet-support.md`](dotnet-support.md). Yang khas
board ini adalah ukurannya: heap 128 KB dan flash 2 MB, jadi cocok untuk
kendali dan sensor, bukan untuk sesuatu yang butuh framebuffer.

## B3. Flash aplikasinya

```bash
rustnet flash bin/Debug/net10.0/blinky.dll --name blinky \
    --key ../keys/rustnet-signing.key --chip rp2040 --device serial:COM12
```

`--chip rp2040` itu penting: kontainernya mencatat keluarga chip, dan
**perangkat menolak image yang dibuat untuk chip lain**. Menandatangani untuk
`esp32` lalu mem-flash ke sini akan gagal di papannya, bukan di tool-nya.

Aplikasi baru langsung menggantikan yang sedang berjalan, tanpa reboot:

```
rustnet logs -n 10 --device serial:COM12
[app] switching to blinky
app: 67 methods, 19 types, 91 strings
[C#] blinking the user LED
```

## B4. Supaya bertahan melewati mati listrik

```bash
rustnet apps autostart blinky --device serial:COM12
```

Setelah itu log-nya dibuka dengan aplikasi yang sudah dimuat dari flash:

```
RustNet on Raspberry Pi Pico @ 125 MHz (peri 125 MHz), heap 128 KB
[sec] provisioned
[app] blinky from flash (12763 bytes)
```

Tanpa penanda autostart, aplikasi yang di-flash tetap *disimpan* tapi tidak
dijalankan — jalankan dengan `rustnet apps start blinky`, hentikan dengan
`apps stop`. Perangkat yang aplikasinya berhenti tetap sepenuhnya bisa
dihubungi, dan itulah yang membuat aplikasi rusak bisa diganti lewat kabel
tanpa perlu flash ulang firmware.

## B5. File

```bash
rustnet data push settings.json /cfg/settings.json --device serial:COM12
rustnet data pull /cfg/settings.json out.json --device serial:COM12
```

Isinya bertahan melewati reboot **dan melewati flash ulang firmware** — image
tinggal di bawah jendela penyimpanan, jadi menggantinya tidak menyentuh
filesystem.

# Kalau ada yang salah

**Port terbuka tapi semua perintah timeout.** Hampir selalu board sedang di
BOOTSEL, bukan menjalankan firmware: cek apakah ada drive `RPI-RP2`. Pico yang
sedang di bootloader adalah perangkat penyimpanan, bukan COM port — jadi kalau
kamu melihat keduanya sekaligus, berarti ada dua board.

**`error: already provisioned`.** Memang seharusnya — lihat B1. Pakai kunci
privat yang dulu dipakai, atau hapus penyimpanan lewat bootloader dan mulai
dari awal.

**`error: signature check failed`.** Aplikasinya ditandatangani dengan kunci
lain, atau untuk chip lain. Pastikan `--chip rp2040` dan `.key`-nya berpasangan
dengan `.pub` yang ada di perangkat.

**Penyalinan UF2 "gagal" persis di ujung.** Begitulah wujud keberhasilannya:
bootloader langsung reboot ke image baru begitu blok terakhir mendarat, jadi
drive-nya hilang di tengah penulisan. Tool menganggapnya selesai.

**Windows bilang "the semaphore timeout period has expired" saat membuka
port.** Firmware berhenti melayani bus USB — dulu penyebabnya sesuatu di jalur
boot yang memblokir. Kalau firmware buatanmu berperilaku begini, cari
penungguan yang tidak ikut mem-poll; lihat bagian *Every wait has to serve the
bus* di
[`runtime/firmware-rp2040/README.md`](../runtime/firmware-rp2040/README.md).

# Yang tidak ada di port ini

Tidak ada WiFi, layar, maupun MQTT — RP2040 tidak punya radio dan board ini
tidak punya panel. Permukaan perangkat yang tidak membutuhkan keduanya (GPIO,
timing, file, dan seluruh inti bahasa) semuanya tersedia.

Matriks lengkapnya ada di [`chips.md`](chips.md) dan
[`dotnet-support.md`](dotnet-support.md).
