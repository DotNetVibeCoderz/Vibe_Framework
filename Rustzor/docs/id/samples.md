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

[RustzorAppGen](appgen.md) menyertakan 21 template (game, sains, bisnis, industri, pendidikan, simulator, tool) yang juga merupakan aplikasi lengkap. Delapan sample lagi dari [roadmap](../../PLAN.md) sedang dikerjakan: video conference, tool desain mirip Figma, editor mirip Photoshop, GIS, CAD viewer, asisten AI, game multiplayer, dan digital twin.
