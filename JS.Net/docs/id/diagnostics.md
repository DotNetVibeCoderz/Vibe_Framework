# Referensi diagnostik

> JS.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/diagnostics.md)

JS.Net melaporkan diagnostiknya sendiri (`JSNxxxx`) saat pembangkitan kode; error compiler C# (`CSxxxx`) dipetakan kembali ke baris `.jsn`. Sembunyikan peringatan dengan `<JSNetNoWarn>JSN2001;JSN2002</JSNetNoWarn>` atau `jsnet compile --nowarn`.

## Sintaks (JSN0xxx)

| Kode | Arti |
|---|---|
| JSN0001 | Karakter tidak terduga. |
| JSN0002 | String atau template literal tidak ditutup. |
| JSN0003 | Komentar blok tidak ditutup. |
| JSN0010 | Token tidak terduga. |
| JSN0011 | Diharapkan token tertentu (mis. `)` atau `;`). |
| JSN0012 | Modifier atau attribute tidak diikuti deklarasi. |

## Semantik (JSN1xxx, error)

| Kode | Arti | Perbaikan |
|---|---|---|
| JSN1001 | Assignment ke `const`. | Pakai `let`. |
| JSN1002 | Deklarasi ganda di scope yang sama. | Ganti salah satu nama. |
| JSN1003 | `await` di luar fungsi `async` atau top level program. | Tandai fungsi dengan `async`. |
| JSN1004 | `break`/`continue` di luar loop atau switch. | |
| JSN1005 | `super` di class tanpa `extends`. | |
| JSN1006 | `super(...)` bukan statement pertama di constructor. | Pindahkan ke paling atas. |
| JSN1007 | Lebih dari satu file berisi top-level statement. | Simpan statement di satu file entry. |
| JSN1008 | Top-level statement di class library. | Pindahkan ke dalam fungsi. |
| JSN1009 | Fitur belum didukung (object type literal, class expression, object spread). | Lihat panduan bahasa. |

## Analyzer (JSN2xxx, peringatan)

| Kode | Arti |
|---|---|
| JSN2001 | Variabel dideklarasikan tetapi tidak pernah dipakai. Awali dengan `_` untuk membungkam. |
| JSN2002 | Informasi tipe kurang; deklarasi dikompilasi sebagai `dynamic`. Tambahkan anotasi untuk performa CLR penuh. |
| JSN2003 | Pembagian integer (`/` pada dua integer membulatkan ke bawah). Pakai `number` atau literal desimal. |
| JSN2004 | Kode tidak terjangkau setelah `return`/`throw`/`break`/`continue`. |
| JSN2005 | Tipe union tanpa padanan CLR, diturunkan menjadi `object`. |

## Driver dan tool

| Kode | Arti |
|---|---|
| JSN3000 | Error baris perintah atau internal. |
| JSN3001 | File sumber tidak ditemukan / tidak ada file input. |
| JSN3002 | Skrip tidak memiliki entry point. |
| JSN3003 | Formatter native `jsnet-tools` tidak ditemukan. |
| JSN3004 | MSBuild tidak menemukan compiler JS.Net. |
| JSNR001–JSNR005 | `jsnet-tools check`: literal/komentar tidak ditutup, karakter tidak terduga, kurung tidak cocok atau tidak ditutup. |
