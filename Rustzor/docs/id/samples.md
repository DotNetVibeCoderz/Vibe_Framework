# Sample

Aplikasi lengkap di `samples/`, masing-masing dengan identitas visualnya sendiri. Sample-sample ini adalah cara terbaik untuk melihat bagaimana semua bagian saling terhubung, dan setiap sample diuji secara end to end di Chrome selama dibangun.

Untuk menjalankan salah satunya dari hasil checkout (setelah [build dari source](building-from-source.md)):

```bash
dotnet build samples/Crm/Crm.csproj
node tools/dev/serve.mjs samples/Crm/bin/Debug/net10.0/wwwroot 5240
# buka http://localhost:5240
```

Setiap sample menyimpan datanya di browser, sehingga perubahan Anda tetap ada setelah reload; masing-masing punya aksi reset untuk memulai dari awal.

## Nusantara CRM

`samples/Crm` · `Rustzor.UI`, `Rustzor.Data` (SQLite)

CRM B2B untuk pemasok peralatan kopi. Halaman overview dengan funnel pipeline, forecast berbobot, dan aktivitas yang perlu ditangani hari ini; papan deal dengan drag and drop antar-tahap; daftar perusahaan dengan halaman detail (deal, orang, timeline aktivitas); kontak; aktivitas yang dikelompokkan berdasarkan tenggat; dan laporan yang dihitung dengan SQL, lengkap dengan kotak "ask the database" untuk menjalankan `select` Anda sendiri.

| | |
| --- | --- |
| ![Overview](../images/sample-crm.png) | ![Papan deal](../images/sample-crm-deals.png) |
| ![Laporan](../images/sample-crm-reports.png) | ![Tema gelap](../images/sample-crm-dark.png) |

Menunjukkan: `AppShell`, `DataGrid`, chart, event drag and drop, route parameter (`/companies/{Id:long}`), SQLite dengan persistensi, uang dengan `decimal`.

## Kedai Senja POS

`samples/Pos` · `Rustzor.UI`, `Rustzor.Data` (IndexedDB), `Rustzor.Browser.DOM`

Mesin kasir kafe yang dirancang untuk tablet di meja kasir. Grid produk per kategori; opsi minuman (ukuran, panas atau dingin, gula, tambahan); pesanan yang sedang berjalan digambar seperti potongan kertas thermal; kode promo, pajak restoran PB1 10%, dan pembulatan ke Rp 100; pembayaran tunai dengan tombol nominal dan kembalian, QRIS, atau kartu; struk 58 mm yang bisa dicetak; riwayat pesanan dengan void dan cetak ulang; laporan shift dengan penjualan per jam dan per metode, produk terlaris, serta hitungan laci kas; dan editor menu dengan sakelar habis (sold-out). F2 untuk mencari, F9 untuk menagih.

| | |
| --- | --- |
| ![Kasir](../images/sample-pos.png) | ![Struk](../images/sample-pos-receipt.png) |
| ![Laporan shift](../images/sample-pos-shift.png) | ![Pesanan](../images/sample-pos-orders.png) |

Menunjukkan: layout kustom tanpa sidebar, keyboard shortcut (`Window.OnKeyDown`), modal, CSS untuk cetak, store IndexedDB, layout ponsel dengan bar pesanan di bagian bawah.

## Jati Lestari ERP

`samples/Erp` · `Rustzor.UI`, `Rustzor.Data` (IndexedDB)

Back office sebuah workshop mebel jati di Tahunan, Jepara. Pembelian (purchase order, penerimaan barang yang menambah stok dan utang, pembayaran sesuai termin pemasok); penjualan (pesanan, surat jalan, faktur dengan PPN 11%, pembayaran; faktur yang lewat jatuh tempo ditandai); produksi (bill of materials beserta upah tukang, work order yang mengambil kayu, kain, dan perlengkapan dari gudang bahan lalu memasukkan barang jadi sebesar biayanya); persediaan di tiga gudang dengan harga pokok rata-rata bergerak, kartu stok per barang, dan transfer ke showroom Semarang; serta akuntansi berpasangan (double-entry) yang menerima jurnal dari setiap transaksi tadi: buku jurnal, neraca saldo, laba rugi per periode, dan neraca. Halaman persediaan mencocokkan nilai setiap kelompok stok dengan akunnya di buku besar. Riwayat sebelas minggu dibuat dengan menjalankan transaksi yang sama, jadi setiap angka berasal dari sebuah dokumen.

| | |
| --- | --- |
| ![Ringkasan](../images/sample-erp.png) | ![Pesanan penjualan dan jejak dokumennya](../images/sample-erp-order.png) |
| ![Produksi](../images/sample-erp-production.png) | ![Neraca, tema gelap](../images/sample-erp-dark.png) |

Menunjukkan: model domain yang setiap perubahannya membukukan jurnal seimbang, uang dengan `decimal`, laporan yang dihitung dari jurnal, parameter route (`/sales/{Id}`), query string, modal dengan baris pesanan yang bisa diubah, store IndexedDB, layout ponsel.

## Arus grid dashboard

`samples/Dashboard` · `Rustzor.UI`, `Rustzor.Charts.ECharts`

Tampilan ruang kendali untuk simulasi sistem kelistrikan Jawa–Bali yang diperbarui setiap dua detik: beban, frekuensi, spinning reserve, porsi energi terbarukan, dan intensitas karbon; pembangkitan 24 jam yang ditumpuk per sumber; diagram satu garis (single-line diagram) backbone 500 kV dengan garis putus-putus yang bergerak mengikuti aliran daya dan warna yang menunjukkan pembebanan; pembangkit, prakiraan beban, dan heatmap minggu-per-jam; log alarm; serta simulasi trip pembangkit sehingga Anda bisa melihat frekuensi turun lalu pulih. Nama pembangkit adalah nama tempat sungguhan; semua angkanya hasil simulasi.

| | |
| --- | --- |
| ![Overview](../images/sample-dashboard.png) | ![Backbone 500 kV saat terjadi trip](../images/sample-dashboard-grid.png) |
| ![Beban](../images/sample-dashboard-demand.png) | ![Tema gelap](../images/sample-dashboard-dark.png) |

Menunjukkan: pembaruan live dengan `Scheduler.SetInterval`, option ECharts yang dibangun dari objek C#, diagram SVG yang di-generate di Razor dengan animasi CSS.

## Serambi chat

`samples/Chat` · `Rustzor.UI`, `Rustzor.Data`, `Rustzor.Browser.WebSocket` (`BroadcastChannel`), `Rustzor.Browser.Storage`, `Rustzor.Browser.FileSystem`

Chat tim untuk sebuah studio kecil. Setiap tab browser masuk sebagai satu orang; buka tab kedua sebagai orang lain dan keduanya bisa ngobrol secara live tanpa server: pesan disimpan di IndexedDB dan diumumkan ke tab lain lewat `BroadcastChannel`. Channel dan direct message dengan jumlah pesan belum dibaca, thread, reaksi, pin, edit, lampiran gambar, pencarian, indikator mengetik, Markdown-lite dengan HTML yang di-escape, @mention, dan tiga rekan tim simulasi yang membalas saat Anda me-mention mereka.

| | |
| --- | --- |
| ![Sebuah thread](../images/sample-chat.png) | ![Siapa di tab ini?](../images/sample-chat-signin.png) |
| ![Tema gelap](../images/sample-chat-dark.png) | ![Ponsel](../images/sample-chat-mobile.png) |

Menunjukkan: real-time antar-tab, `MarkupString` dengan escaping, identitas per tab di `sessionStorage`, komponen yang subscribe ke sebuah store.

## Ruang Redaksi video call

`samples/Meet` · `Rustzor.Browser.WebRTC`, `Rustzor.Browser.Media`, `Rustzor.Browser.WebSocket` (`BroadcastChannel`), `Rustzor.UI`

Ruang rapat Kabar Pesisir, koperasi lima redaksi pesisir (Makassar, Ambon, Bitung, Kendari, Ternate): rapat redaksi pukul 07.00, meja biro, dan meja liputan banjir. Video call sungguhan antar-tab dalam satu browser, tanpa server: setiap tab di sebuah ruang mengumumkan dirinya lewat `BroadcastChannel`, setiap pasangan tab bertukar offer, answer, dan kandidat ICE di sana, lalu gambar, suara, dan data channel berjalan peer to peer. Buka satu ruang di dua tab, masuk sebagai dua reporter, dan keduanya saling melihat dan mendengar.

- Pemeriksaan sebelum masuk: kamera di monitor preview dengan garis title-safe, mikrofon di PPM, pemilih kamera dan mikrofon, strap nama dan biro Anda, serta siapa yang sudah ada di ruang.
- Panggilan: grid yang menyesuaikan ukuran setiap tile dengan layar; strap lower-third di setiap tile; lampu tally merah pada siapa pun yang sedang bicara (dari tingkat suara); mute, kamera, dan berbagi layar (layar yang dibagikan tampil besar); chat, antrean angkat tangan, dan reaksi lewat data channel; rundown rapat yang bisa dimajukan siapa saja untuk semua orang; timecode; keluar, atau akhiri untuk semua.
- Perekaman: panggilan digabung di tab menjadi satu video 1280×720 (gambar setiap orang dalam grid dengan namanya, dan semua suara), diumumkan ke peserta lain selama berjalan, lalu disimpan di Recordings untuk diunduh.
- Tanpa kamera Anda masuk dengan mikrofon saja dan yang lain melihat inisial Anda di slate; tanpa keduanya Anda tetap bisa menonton, mendengar, dan chat.

| | |
| --- | --- |
| ![Tiga reporter di rapat pagi](../images/sample-meet.png) | ![Pemeriksaan sebelum masuk](../images/sample-meet-prejoin.png) |
| ![Layar yang dibagikan, tema gelap](../images/sample-meet-dark.png) | ![Ponsel](../images/sample-meet-phone.png) |

Menunjukkan: `PeerConnection` dengan signalling lewat `BroadcastChannel`, aturan pasangan (id yang lebih kecil yang memanggil) sehingga tidak ada offer ganda, `ReplaceTrackAsync` untuk berbagi layar tanpa negosiasi ulang, `MediaMixer` dan `MediaRecorder`, tingkat suara untuk pembicara aktif, unit container query untuk grid.

`node tools/e2e/meet.mjs http://localhost:5260` menjalankannya di Chrome headless dengan perangkat palsu: dua tab saling melihat video, chat, mute, angkat tangan, reaksi, rundown, tally, perekaman (video yang disimpan diperiksa per frame), berbagi layar, tab ketiga tanpa kamera, keluar, mengakhiri untuk semua, dan ponsel 400 px.

## Rupa, tool desain

`samples/Rupa` · `Rustzor.UI`, `Rustzor.Browser.FileSystem`, `Rustzor.Browser.Storage`, `Rustzor.Browser.DOM`, dan modul browser miliknya sendiri (`rustzor.module.js`)

Tool desain vektor ala Figma, bergaya cetak coba (*proof*) pracetak: tampilan grafit, kanvas seperti kertas kalkir, seleksi dan garis bantu warna magenta proses, sian untuk komponen, tanda register di penggaris. Dibuka dengan Lintas, tiga layar aplikasi KRL rekaan untuk Jabodetabek (beranda, jadwal, tiket) lengkap dengan komponen dan gaya warnanya.

- Kanvas tak terbatas: geser dengan Spasi+seret, tombol tengah, trackpad atau tool tangan; zoom dengan Ctrl+roda atau cubit, ke arah penunjuk; Shift+1 memuat semuanya, Shift+2 seleksinya. Penggaris dalam satuan kanvas menandai seleksi.
- Frame, persegi, elips, garis, pena untuk path bersegmen lurus, teks yang diketik langsung di kanvas, dan gambar dari file, seret-lepas atau clipboard.
- Pilih, Shift+klik, marquee, Ctrl+klik ke dalam grup, klik ganda ke dalam frame dan grup; pindahkan dengan snapping ke tepi dan tengah tetangga serta frame, dengan garis bantu dan angka jarak; ubah ukuran dari delapan pegangan (Shift menjaga proporsi, Alt dari tengah); putar dari sedikit di luar sudut (Shift per 15°). Grup, lepas grup, urutan tumpukan, duplikat, salin, tempel, hapus, geser per piksel; undo dan redo untuk setiap perubahan.
- Panel layer bersarang dengan ganti nama, sembunyikan dan kunci; properti posisi, ukuran, rotasi, opasitas, radius, isi, garis tepi dan tipografi; auto layout untuk frame (baris atau kolom, jarak, padding, perataan, *hug*).
- Komponen: frame dijadikan komponen dengan Ctrl+Alt+K; instansnya mengikuti setiap perubahan dan tetap memakai teksnya sendiri, yang diubah di panel; instans bisa dilepas menjadi frame biasa. Gaya warna dipakai bersama oleh semua layer yang menggunakannya.
- Ekspor seleksi atau semua frame ke SVG dan PNG (1× atau 2×, dengan font yang ikut disematkan). File tersimpan di browser (*origin-private file system*) dengan simpan otomatis; simpan salinan sebagai `.rupa.json` dan buka kembali.

| | |
| --- | --- |
| ![Lintas, tiga layar, sebuah kartu terpilih](../images/sample-rupa.png) | ![Memindahkan layer: snapping dan jarak](../images/sample-rupa-guides.png) |
| ![Instans dan teks override-nya, tema gelap](../images/sample-rupa-dark.png) | ![Daftar file](../images/sample-rupa-files.png) |

Menunjukkan: SVG yang digambar dari Razor dengan satu view per layer yang hanya di-render ketika layernya berubah (memindahkan satu bentuk di antara tiga ratus hanya menggambar ulang bentuk itu), seretan yang ditampilkan browser sementara C# memegang model dan menggambar ulang saat dilepas, snapping lewat pencarian biner pada tepi yang terurut, riwayat undo seluruh dokumen, dan aplikasi yang menyertakan modul browser sendiri.

`node tools/e2e/rupa.mjs http://localhost:5310` menjalankannya di Chrome headless dengan mouse dan keyboard, memeriksa model setelah setiap langkah (menggambar, snapping, mengubah ukuran, grup, undo dan redo, perubahan komponen yang sampai ke enam instans, override, teks, pena, ekspor SVG dan PNG, simpan otomatis setelah reload), dan mengukur seretan dengan 300 bentuk di halaman: sekitar 12 ms kerja per gerakan dan 56 frame per detik (laju frame Chrome headless). `node tools/e2e/rupa-shots.mjs` mengambil screenshot di atas.

## Kamar Gelap, editor foto

`samples/KamarGelap` · `Rustzor.UI`, `Rustzor.Browser.FileSystem`, dan modul browser miliknya sendiri (`rustzor.module.js`) yang mengerjakan piksel dengan WebGL2

Editor foto berlapis ala Photoshop, bergaya kamar gelap: tampilan hitam hangat, merah *safelight* untuk apa pun yang sedang aktif, kuning developer untuk angka, dan gambar yang dipegang dalam potongan film yang tepinya mencetak nomor frame, ukuran, jumlah layer, dan zoom. Dibuka dengan *Merapi dari Kaliurang*, fajar 2000 × 1500 di atas Merapi dan sawah terasering yang dilukis oleh aplikasinya sendiri (tidak ada yang diunduh, tidak ada lisensi), lengkap dengan suntingan yang biasa ditumpuk fotografer: langit lebih hangat, Levels bermasker untuk mengangkat terasering, vignette multiply, dan keterangan foto.

- Layer dengan opasitas, delapan blend mode (normal, multiply, screen, overlay, darken, lighten, soft light, difference), tampil/sembunyi, kunci, ganti nama, seret untuk mengubah urutan, dan masker layer (tampilkan semua, dari seleksi, aktif/nonaktif, terapkan, hapus; Q berpindah antara melukis masker dan piksel). Thumbnail asli untuk layer dan masker.
- Adjustment layer yang tidak pernah mengubah piksel di bawahnya: brightness dan contrast, levels, hue dan saturation, black and white dengan tint. Masing-masing punya masker sendiri.
- Filter pada layer piksel, di dalam seleksi, dengan pratinjau langsung: Gaussian blur, sharpen (unsharp mask), add noise.
- Tool: move, marquee persegi dan elips, lasso (*marching ants*), crop (Shift menjaga proporsi), brush dan eraser dengan ukuran, kekerasan, opasitas, flow, dan tekanan pena, clone stamp (Alt-klik sumbernya), gradient (linear, radial, ke transparan), paint bucket dengan toleransi, eyedropper, layer teks, hand. Shortcut Photoshop: V M L C B E S G I T H, X, D, [ dan ], 1–0, Ctrl+J, Ctrl+D, Alt+Backspace.
- Isi dan hapus seleksi, pilih semua, inverse, crop ke seleksi; riwayat 40 langkah terakhir yang bisa diklik untuk kembali; zoom ke arah penunjuk, geser, muat layar, ukuran asli; histogram langsung; pemilih warna dengan swatch.
- Buka foto (atau seret-lepas untuk menaruhnya sebagai layer), ekspor PNG, JPEG, atau WebP dengan pengaturan kualitas, simpan dan buka proyek `.kamar` beserta semua layer dan maskernya, dan simpan otomatis di IndexedDB yang ditawarkan halaman awal untuk dilanjutkan.

| | |
| --- | --- |
| ![Merapi saat fajar, layer Levels bermasker, seleksi elips (gelap)](../images/sample-kamargelap.png) | ![Halaman awal](../images/sample-kamargelap-start.png) |
| ![Layer black & white dengan tint sepia, dan riwayat (terang)](../images/sample-kamargelap-light.png) | ![Ponsel](../images/sample-kamargelap-phone.png) |

Menunjukkan: C# memegang dokumen (layer, adjustment, seleksi, urutan riwayat) dan mengirimkannya ke modul browser sebagai JSON, sementara piksel tetap di browser: satu kanvas 2D per layer dan masker, compositor WebGL2 yang memadukan tumpukan dengan mode, opasitas, dan masker setiap layer serta menjalankan adjustment layer sebagai shader, filter sebagai pass shader, dan snapshot *copy-on-write* untuk riwayat. Layer di bawah layer aktif di-cache, sehingga satu frame brush hanya menggabungkan ulang dari situ ke atas, dan hanya di area yang dilukis. Kerja per piksel di C# yang diinterpretasi akan memakan sekitar 1,4 µs per piksel, kira-kira empat detik untuk satu kali lewat gambar ini; di GPU satu frame utuh hanya sekitar 2 ms.

`node tools/e2e/kamargelap.mjs http://localhost:5320` menjalankannya di Chrome headless dan membaca piksel kembali setelah setiap langkah: goresan brush, layer multiply yang diisi di dalam seleksi (hasil gabungan sama dengan dasar dikali warna isian di dalamnya dan tidak berubah di luarnya), layer black & white yang dinyalakan lalu dimatikan (setiap nilai kembali), Gaussian blur (varians terasering turun separuh), undo dan redo, crop ke 1000 × 700, ekspor PNG yang didekode pada ukuran itu, dan simpan otomatis setelah reload. Pada sample 2000 × 1500 dengan lima layer, dengan menunggu GPU selesai, satu frame brush sekitar 2 ms (p95 2,5 ms), Gaussian blur 20 px sekitar 50 ms, sharpen sekitar 35 ms, dan noise sekitar 25 ms (Intel UHD 620). `node tools/e2e/kamargelap-shots.mjs` mengambil screenshot di atas.

## Siaga Citarum, peta banjir

`samples/Siaga` · `Rustzor.UI`, `Rustzor.Data` (IndexedDB), `Rustzor.Browser.FileSystem`, `Rustzor.Browser.Location`, dan modul browser miliknya sendiri (`rustzor.module.js`): mesin petanya

Meja pusdalops BPBD saat banjir Citarum di Cekungan Bandung, digambar seperti lembar peta topografi: blok judul dengan nama lembar, waktu situasi, dan datum; kontur cokelat, perairan biru dengan nama bercetak miring, batas kecamatan garis-titik ungu; tepi peta berisi graticule derajat dan menit yang mengikuti tampilan; wilayah di luar lembar diarsir di balik garis tepi. Satu-satunya warna mencolok adalah empat status siaga (Normal, Waspada, Siaga, Awas) pada papan pos duga air di atas peta. Nama tempatnya nyata; semua bentuk, bacaan, dan laporan dibuat oleh generator.

- Mesin peta sendiri, tanpa internet: Web Mercator, geser dengan inersia, zoom ke arah pointer dengan scroll atau cubit, label yang saling memberi tempat, skala batang, skala angka, dan koordinat pointer dalam derajat, menit, detik. Latar OpenStreetMap bisa dinyalakan saat online, lengkap dengan atribusinya; saat offline aplikasi memberi tahu dan mematikannya.
- Sebelas layer dalam sekitar 1 MB GeoJSON (46.000 titik): kontur tiap 25 m, kecamatan, sungai, Waduk Saguling, genangan banjir, jalan, laporan warga, pompa dan kolam retensi, fasilitas kesehatan, posko pengungsian, dan pos duga air.
- Panel layer dengan visibilitas, urutan, opasitas, dan gaya: satu warna, per kategori, kelas bergradasi dari kolom angka mana pun (kuantil atau interval sama, empat ramp warna), atau heat map, masing-masing dengan legendanya.
- Identifikasi (Shift menambah seleksi); tabel atribut yang bisa difilter, diurutkan, dan selalu selaras dengan peta; pencarian nama atau koordinat.
- Gambar titik, garis, dan area, sunting verteksnya (seret, tambah di titik tengah, Alt-klik untuk menghapus), beri nama dan catatan; semuanya tersimpan di browser ini.
- Ukur jarak (Haversine) dan luas (geodesik, di atas bola); pilih semua yang ada di dalam area yang digambar; buffer fitur apa pun (sungai, pos duga air, jalan, gambar Anda) dan daftar apa saja yang ada dalam jarak itu, dari yang terdekat.
- Impor GeoJSON atau CSV dengan kolom lat dan lon (pilih, seret, atau tempel), ekspor layer Anda sebagai GeoJSON; "lokasi saya" lewat Geolocation API, dengan penjelasan bila izin ditolak.
- `/laporan`, laporan situasi yang bisa dicetak dari layer yang sama: pos duga air terhadap batas siaganya, luas genangan yang diukur geodesik, KK terdampak per kecamatan, keterisian posko, dan laporan semalam per jam.

| | |
| --- | --- |
| ![Kecamatan diwarnai menurut KK terdampak, Bojongsoang diperiksa](../images/sample-siaga.png) | ![Buffer 1 km di sekitar Citarum dan posko yang ditemukannya](../images/sample-siaga-buffer.png) |
| ![Shift malam: lembar gelap di Dayeuhkolot](../images/sample-siaga-dark.png) | ![Laporan situasi](../images/sample-siaga-report.png) |

Menunjukkan: C# memegang layer, gaya, atribut, fitur buatan pengguna, serta setiap pengukuran dan kueri, sementara modul browser yang menggambar: GeoJSON diproyeksikan sekali ke meter dan disimpan sebagai objek `Path2D`, sehingga satu frame hanyalah satu transformasi dan beberapa fill dan stroke, dan menggeser peta tidak pernah masuk ke C#. Hit test, garis bantu saat menggambar, dan pegangan verteks dijawab di modul dalam frame yang sama; klik, sketsa yang selesai, dan suntingan dikirim kembali ke C#.

`node tools/e2e/siaga.mjs http://localhost:5330` menjalankannya di Chrome headless dan mencocokkan jawabannya dengan angka yang dihitung sendiri dari file GeoJSON yang sama: proyeksi terhadap rumus EPSG:3857 dan bolak-baliknya, geser dan zoom, identifikasi pada koordinat pos duga air, tabel dan peta yang saling memilih, luas geodesik dan keliling Haversine sebuah persegi yang digambar (selisih di bawah 1%), buffer 3 km dan pilih-dengan-area terhadap uji jarak dan point-in-polygon miliknya sendiri, GeoJSON yang ditempel lalu diekspor kembali tanpa berubah, suntingan verteks, gambar dan impor yang tetap ada setelah reload, reset, dan lokasi tanpa izin. Menggeser dan zoom di atas 46.000 titik menggambar satu frame dalam sekitar 4 ms (p95 7 ms). `node tools/e2e/siaga-shots.mjs` mengambil screenshot di atas; `node samples/Siaga/data/make-data.mjs` membuat ulang datanya.

## API Playground

`samples/ApiPlayground` · `Rustzor.Browser.Fetch`, `Rustzor.Browser.WebSocket`, `Rustzor.Net.SignalR`, `Rustzor.Net.GrpcWeb`

Server kecil ASP.NET Core .NET 10 (`Server/`) dan client Rustzor (`Client/`) yang mencoba empat cara browser berkomunikasi dengan backend, dengan log "wire" untuk setiap request, response, dan frame.

- **REST**: request builder dengan preset untuk katalog buku (`/api/books`): list, search, get, create, replace, delete, error validasi, 404, panggilan lambat, status apa pun. Response yang diformat rapi beserta waktu dan header-nya, plus kode C# yang setara.
- **WebSocket**: `/ws` memantulkan (echo) apa yang Anda kirim dan mengirim tick dari server setiap dua detik.
- **SignalR**: `/hubs/chat` dengan room dan presence, `InvokeAsync<int>("Add")`, countdown yang di-stream, dan method yang selalu gagal.
- **gRPC-Web**: `playground.Inventory`: panggilan unary, NOT_FOUND, list yang di-stream, dan harga live dengan cancellation, dengan setiap frame ditampilkan dalam hex.

| | |
| --- | --- |
| ![REST](../images/sample-api-rest.png) | ![WebSocket](../images/sample-api-ws.png) |
| ![SignalR](../images/sample-api-signalr.png) | ![gRPC-Web](../images/sample-api-grpc.png) |

Cara menjalankannya (server juga menyajikan client yang sudah di-build):

```bash
dotnet build samples/ApiPlayground/Client
dotnet run --project samples/ApiPlayground/Server --urls http://localhost:5250
# buka http://localhost:5250
```

Untuk menyajikan client dari tempat lain, isi alamat server di bar atas; server mengizinkan panggilan cross-origin.

## Aplikasi lainnya

[RustzorAppGen](appgen.md) menyertakan 21 template (game, sains, bisnis, industri, pendidikan, simulator, tool) yang juga merupakan aplikasi lengkap. Empat sample lagi dari [roadmap](../../PLAN.md) sedang dikerjakan: CAD viewer, asisten AI, game multiplayer, dan digital twin.
