# Deploy Aplikasi C# ke Netduino 3 WiFi

Prosedur lengkap untuk menjalankan aplikasi .NET di Netduino 3 WiFi
(STM32F427VIT6).

Ada **dua fase**, dan biayanya sangat berbeda:

| | Caranya | Kapan |
|---|---|---|
| **A. Flash firmware** | USB DFU, kombinasi tombol di board | Sekali, dan setiap kali firmware berubah |
| **B. Ganti aplikasi** | `rustnet flash` lewat konsol serial | Setiap kali kode C#-mu berubah |

Fase B yang akan kamu ulang terus, dan tidak butuh tekan tombol maupun
hardware tambahan: firmware melayani RNDP lewat soket USB board itu
sendiri sebagai CDC serial port, jadi satu kabel menanggung kedua fase.

Aplikasi yang diunggah disimpan di flash dan **jalan kembali sendiri
setelah dimatikan dan dinyalakan** — board tidak perlu terhubung ke PC
untuk berjalan.

Versi bahasa Inggris: [`deploy-netduino3.md`](deploy-netduino3.md).

## Board-nya

| | |
|---|---|
| MCU | STM32F427VIT6 — Cortex-M4F, 168 MHz |
| Flash / RAM | 2 MB / 256 KB (192 KB kontigu + 64 KB CCM) |
| Sumber clock | Kristal 25 MHz di PH0/PH1 |
| LED user | PA10 (`USR_LED`) |
| Konsol | USART2 — PA2 TX / PA3 RX, di header digital sebagai **D3 / D2** |
| Pemrograman | USB DFU (ROM bootloader ST), **RDP level 0** |
| Debug probe | tidak ada di board |

Data pin dan clock berasal dari definisi board `NETDUINO3_WIFI` milik
nanoFramework sendiri, pada tag `nf-interpreter` `v1.7.2.5`.

UART7 di konektor goPort2 secara elektrik juga bisa, tapi itu soket GoBus
yang butuh kabel khusus, dan port-nya punya jalur enable daya serta
pull-up TX sendiri (PD10, PE10) yang harus diaktifkan lebih dulu. D2/D3
cukup dengan jumper biasa.

---

> ## Baca ini sebelum mem-flash apa pun
>
> Mem-flash RustNet **menghapus firmware nanoFramework** bawaan board.
> Itu bisa dipulihkan, tapi jalur pemulihannya lebih sempit daripada yang
> terlihat: target `NETDUINO3_WIFI` sudah **dipensiunkan** dari
> `nf-interpreter`, dan build terakhir yang dipublikasikan adalah
> **1.7.2.6, Desember 2021**. Tidak akan ada yang lebih baru.
>
> Unduh dan simpan di tempat permanen **sebelum** memulai:
>
> ```
> https://dl.cloudsmith.io/public/net-nanoframework/nanoframework-images/raw/names/NETDUINO3_WIFI/versions/1.7.2.6/NETDUINO3_WIFI-1.7.2.6.zip
> ```
>
> File `nanobooter-nanoclr.dfu` di dalamnya memulihkan board:
>
> ```bash
> dfu-util -d 0483:df11 -a 0 -D nanobooter-nanoclr.dfu
> ```
>
> Tanpa `-s` — container DFU sudah memuat alamatnya sendiri.
>
> Backup byte-per-byte dari flash yang ada **tidak** praktis dengan
> `dfu-util` 0.11: ia menulis ke bootloader ini dengan baik, tapi macet
> saat membaca lebih dari sekitar 14 KB. Pakai STM32CubeProgrammer kalau
> kamu memang menginginkannya.

---

## Opsional: konsol serial di header

Firmware sudah melayani RNDP lewat USB, jadi ini cadangan — berguna kalau
USB tidak ter-enumerasi, atau untuk melihat banner boot sebelum enumerasi
terjadi. Kedua transport tetap terkonfigurasi; USB yang diutamakan kalau
tersedia.

Adapter USB-serial dengan level logika 3,3V, di tiga pin header digital.
**Jalur datanya menyilang** — RX adapter ke TX board:

| Adapter USB-serial | Header Netduino 3 | STM32 |
|---|---|---|
| RX | **D3** | PA2 (TX board) |
| TX | **D2** | PA3 (RX board) |
| GND | GND mana saja | — |

115200 8N1. Jangan sambungkan VCC adapter — board sudah bertenaga dari
USB-nya sendiri.

Memasang RX dan TX searah adalah kesalahan yang paling sering terjadi:
banner board tidak akan pernah muncul, dan semua perintah `rustnet`
timeout.

---

# Fase A — flash firmware (DFU)

## A1. Sekali saja: tooling

```bash
rustup target add thumbv7em-none-eabihf
rustup component add llvm-tools
cargo install cargo-binutils --locked
```

Ditambah `dfu-util`, .NET SDK 10, dan di Windows sebuah driver WinUSB
yang ter-bind ke perangkat DFU (Zadig, atau apa pun yang sudah dipasang
`nanoff`). Pastikan board terjangkau:

```bash
dfu-util -l
```

Masuk ke DFU dilakukan lewat kombinasi tombol di board itu sendiri. Kamu
akan melihat empat alt setting dan, di alt 0, peta sektor flash-nya:

```
Found DFU: [0483:df11] ... alt=0, name="@Internal Flash /0x08000000/04*016Kg,01*064Kg,07*128Kg,..."
```

Peta yang terulang dua kali itu adalah flash 2 MB dual-bank milik F427 —
konfirmasi berguna bahwa kamu bicara dengan chip yang benar.

## A2. Build firmware-nya

Image membawa satu feature board dan satu feature aplikasi. Aplikasi yang
dikompilasi ke dalamnya adalah cadangan: board menjalankannya hanya kalau
belum ada yang diunggah, atau setelah storage dihapus.

```bash
cd runtime/firmware-stm32
cargo build --release --no-default-features \
    --features board-netduino3-wifi,app-language-tour
```

Ganti `app-language-tour` dengan `app-blink` untuk demo minimal. Menaruh
aplikasi lain ke dalam image dibahas di Fase B — untuk kerja sehari-hari
kamu sama sekali tidak perlu build ulang firmware.

## A3. Konversi ke binary mentah — lalu periksa

```bash
rust-objcopy -O binary \
    target/thumbv7em-none-eabihf/release/rustnet-firmware-stm32 fw.bin
od -A x -t x4 -N 8 fw.bin
```

> **Pastikan word pertama sebelum flash.** Kedua varian board menghasilkan
> ELF di path yang sama, jadi yang terakhir dibangun yang menang. Stack
> pointer awal memberi tahu image mana yang sebenarnya kamu pegang:
>
> | Word pertama | Image |
> |---|---|
> | `20030000` | RAM 192 KB — F427, si Netduino |
> | `20018000` | RAM 96 KB — F401RE, sebuah Nucleo |
>
> Jangan pakai `cargo objcopy <flag cargo> -- -O binary`; dalam praktiknya
> ia pernah mengeluarkan artifact feature default alih-alih yang diminta.

## A4. Flash

Masuk ke DFU mode di board, lalu:

```bash
dfu-util -d 0483:df11 -a 0 -s 0x08000000:leave -D fw.bin
```

`:leave` me-restart board ke firmware baru begitu download selesai.
Peringatan "Invalid DFU suffix signature" wajar untuk binary mentah dan
tidak berbahaya.

---

# Fase B — ganti aplikasi lewat kabel

Ini loop cepatnya. Tanpa DFU, tanpa tombol, tanpa build ulang firmware.

## B1. Sekali per komputer: pasangan kunci

```bash
rustnet keys generate --out keys
```

`rustnet-signing.key` bersifat privat dan menandatangani image-mu;
`rustnet-signing.pub` yang dikirim ke device. Jauhkan yang privat dari
version control.

## B2. Sekali per device: provisioning

```bash
rustnet provision --key keys/rustnet-signing.pub --device serial:COM9
```

Kuncinya ditulis ke sektor flash internal yang dicadangkan, jadi bertahan
setelah reset. Jalankan ulang hanya kalau mau ganti kunci, atau setelah
menghapus storage.

## B3. Build dan flash aplikasimu

```bash
dotnet build MyApp/MyApp.csproj -c Debug
rustnet flash MyApp/bin/Debug/net10.0/MyApp.dll \
    --name myapp --key keys/rustnet-signing.key \
    --chip stm32 --start --device serial:COM9
```

```
flashed 'myapp' (6480 bytes, signed, chip=Stm32)
started
```

Build memakai **Debug**: async pada konfigurasi Release belum didukung,
dan entry point harus `static void Main()`.

> **`--chip stm32` itu wajib.** Nilai default flag ini `host-sim`, dan
> device akan menolak container yang disegel untuk chip yang salah. Tanda
> tangannya diverifikasi di STM32 itu sendiri — sekitar 67 ms untuk
> RSA-2048 pada 84 MHz.

Device memeriksa tanda tangan, memastikan container-nya image App, dan
mem-parse RNX-nya **sebelum** menerima — jadi modul rusak tidak bisa
menggantikan aplikasi yang sedang bekerja.

## B4. Mengamatinya

```bash
rustnet info       --device serial:COM9
rustnet apps list  --device serial:COM9
rustnet logs -n 30 --device serial:COM9
```

`info` melaporkan lebih dari sekadar dasar, dan field tambahannya bersifat
diagnostik:

```json
{"chip":"stm32","board":"Netduino 3 WiFi","active_app":"myapp","running":true,
 "heap_used":22704,"rx_dropped":0,"max_poll_gap_us":78333,"last_verify_us":66709}
```

- **`rx_dropped`** — byte yang tidak kebagian tempat di ring penerima.
  Seharusnya 0. Selain itu berarti frame tiba dalam keadaan rusak.
- **`max_poll_gap_us`** — jarak terburuk antar-giliran service loop,
  direset setiap kali kamu membaca `info`. Ring menampung ~355 ms pada
  115200, jadi angka yang mendekati itu adalah tanda bahaya.
- **`last_verify_us`** — lama pemeriksaan tanda tangan terakhir di chip.

## B5. Uji dulu di host

Setiap perjalanan bolak-balik ke hardware lebih lambat daripada ke PC, dan
dua utilitas menjawab pertanyaan yang paling sering muncul tanpa menyentuh
board:

```bash
cargo run -p rustnet-core --example run_rnx    -- app.rnx  # jalan tidak?
cargo run -p rustnet-core --example heap_probe -- app.rnx  # muat tidak?
cargo run -p rustnet-core --example host_calls -- app.rnx  # firmware harus jawab apa?
```

`heap_probe` melaporkan puncak heap — 49 KB untuk language tour, melawan
96 KB yang dicadangkan board ini. `host_calls` mendaftar setiap nama
kanonik yang dipanggil aplikasi **beserta varian `HostValue` yang
benar-benar dilewatkan**; baca kolom kedua itu, karena `bool` C# tiba
sebagai `I32`.

---

## Verifikasi tanpa adapter serial

Firmware mengedipkan kemajuan boot-nya di LED user sebagai hitungan
menaik, dengan jeda jelas antar kelompok. **Urutan menghitung berjalan
sekali; kelompok yang berulang tanpa henti berarti kegagalan.**

| Sinyal | Arti |
|---|---|
| 1 | mencapai entry point |
| 2 | PLL terkunci ke kristal 25 MHz |
| 3 | board dan konsol terkonfigurasi |
| 4 | banner terkirim — UART benar-benar transmit |
| 6 | RNX ter-parse, interpreter terbentuk, RNDP mendengarkan |
| 9 | panggilan pertama aplikasi ke host |
| 5 *berulang* | panic Rust, termasuk kegagalan alokasi |
| 7 *berulang* | modul RNX tertanam ditolak |
| 8 *berulang* | hard fault — kemungkinan besar stack overflow |
| 2 / 3 / 4 *berulang* | interpreter mengembalikan Completed / Paused / Error |

Setelah itu aplikasi mengambil alih LED:

- **`app-language-tour`** — denyut tenang 1 Hz berarti semua pemeriksaan
  lulus. Selain itu, ia mengedipkan jumlah kegagalannya.
- **`app-blink`** — dua kedip cepat, lalu jeda panjang.

> **Boot memakan sekitar sepuluh detik**, karena sinyal-sinyal itu berjalan
> sebelum service loop dimulai. Perintah `rustnet` pertama setelah reset
> akan timeout; ulangi saja.

## Yang belum kamu dapatkan

RNDP dilayani oleh firmware ini sendiri, bukan oleh `runtime/firmware`
yang masih terikat `std`. Yang dijawab: `ping`, `info`, `logs`,
`apps list`, `start`, `stop`, `reboot`, `provision`, `flash`.

Yang bertahan: kunci provisioning, aplikasi yang diunggah, dan namanya —
disimpan di satu sektor 128 KB flash internal yang dicadangkan, dipulihkan
saat boot. Tapi ini **bukan filesystem**: tidak ada path, tidak ada
direktori, dan API `RustNet.IO.FileSystem` di C# belum diimplementasikan di
sini. OTA, secure config, dan debugger on-device juga belum ada; semuanya
ada di `runtime/firmware` yang masih terikat `std`.

Storage-nya berbentuk log, jadi unggahan berulang menambahkan alih-alih
menghapus. Saat sektornya penuh, satu kali compaction menghapus dan
menulis ulang isi yang masih hidup — dan penghapusan itu membekukan core
beserta seluruh interupsi sekitar satu detik, karena flash controller
memblokir semua akses ke flash, dan di situlah kodenya berada.

Alur ini diverifikasi di Nucleo-F401RE yang punya virtual COM port bawaan.
Netduino menjalankan firmware yang sama dan berbicara protokol yang sama,
lewat adapter yang dirangkai di atas.

## Pemecahan masalah

| Gejala | Penyebab | Solusi |
|---|---|---|
| Semua perintah timeout, banner tidak pernah muncul | RX/TX tidak disilang | RX adapter ke **D3**, TX-nya ke **D2** |
| Perintah pertama setelah reset timeout | Sinyal boot masih berjalan | Tunggu ~10 detik lalu ulangi |
| `flash` bilang *not provisioned* | Belum pernah provisioning, atau storage dihapus | Jalankan `rustnet provision` |
| `flash` ditolak karena chip salah | `--chip` default-nya `host-sim` | Tambahkan `--chip stm32` |
| `rx_dropped` naik di `info` | Frame tiba dalam keadaan rusak | Cek `max_poll_gap_us`; angka mendekati 355 ms berarti service loop kelaparan |
| Urutan berhenti setelah 1 | PLL tidak pernah lock | Cek feature board — build F401RE memakai HSI, yang ini butuh kristal |
| Urutan berhenti setelah 3 | UART tidak pernah selesai transmit | Indeks konsol salah; USART2 adalah `CONSOLE = 1` |
| **5 berulang** | Panic, biasanya kehabisan memori | Jalankan ulang `heap_probe`; naikkan `HEAP_SIZE` di `src/main.rs` |
| **4 berulang** | Interpreter mengembalikan error | Baca pesannya dengan `rustnet logs`, atau cocokkan ulang output `host_calls` dengan arm `invoke` di firmware |
| **8 berulang** | Hard fault | Stack overflow — turunkan `HEAP_SIZE` agar stack lebih lega |
| Tidak ada apa-apa, bahkan 1 kedip pun tidak | Tidak pernah mencapai `main` | Salah image; periksa ulang stack pointer di A3 |
| `dfu-util` tidak bisa attach | Interface stall akibat transfer yang putus | Cabut, colok ulang, masuk DFU lagi |
| Board gelap dan tidak merespons setelah flash | — | ROM bootloader tidak tersentuh oleh apa pun yang kamu tulis; masuk DFU lagi dan flash ulang |

## Lihat juga

- `runtime/firmware-stm32/README.md` — internal firmware dan catatan penting
- `docs/chips.md` — matriks dukungan untuk semua varian chip
- [`deploy-esp32.id.md`](deploy-esp32.id.md) — tujuan yang sama di ESP32,
  di mana firmware punya filesystem dan provisioning bertahan setelah reset
- `docs/dotnet-support.md` — fitur C# mana yang diimplementasikan runtime
