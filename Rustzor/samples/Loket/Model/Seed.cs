namespace Loket.Model;

/// <summary>
/// The office's own documents the desk answers from. Kelurahan Kemuning Raya and Kota Sentosa
/// are invented for this sample; the procedures are written to read like real ones but are not
/// legal guidance.
/// </summary>
public static class Seed
{
    public const string Office = "Kelurahan Kemuning Raya";
    public const string City = "Kota Sentosa";

    public static readonly (string Id, string Title, string Lang, string Text)[] Documents =
    {
        ("sop-ktp", "SOP Layanan KTP-el", "id", """
# SOP Layanan KTP-el

## Siapa yang wajib memiliki KTP-el
Setiap warga negara Indonesia yang sudah berumur 17 tahun, sudah menikah, atau pernah menikah wajib memiliki KTP-el. Perekaman dilakukan sekali seumur hidup; KTP-el berlaku seumur hidup selama datanya tidak berubah.

## Persyaratan perekaman baru
Bawa Kartu Keluarga (KK) asli yang mencantumkan nama pemohon. Tidak perlu surat pengantar RT/RW untuk perekaman di kelurahan. Pemohon datang sendiri karena foto, sidik jari, dan iris mata direkam langsung.

## Alur dan waktu layanan
Ambil nomor antrean di loket 1, lalu perekaman biometrik di loket 3. Perekaman memakan waktu sekitar 15 menit. KTP-el dicetak oleh Disdukcapil Kota Sentosa dan dapat diambil di kelurahan paling lambat 7 hari kerja setelah perekaman; warga akan menerima pesan WhatsApp saat kartu siap.

## KTP-el hilang atau rusak
Untuk KTP-el yang hilang, bawa surat keterangan kehilangan dari kepolisian dan fotokopi KK. Untuk KTP-el rusak, bawa kartu yang rusak dan KK asli. Pencetakan ulang selesai paling lambat 3 hari kerja.

## Biaya
Semua layanan KTP-el gratis. Petugas tidak boleh memungut biaya apa pun; laporkan pungutan ke nomor pengaduan 0800-1-KEMUNING.
"""),
        ("sop-kk-akta", "SOP Kartu Keluarga dan Akta Kelahiran", "id", """
# SOP Kartu Keluarga dan Akta Kelahiran

## Kartu Keluarga baru (setelah menikah)
Persyaratan: buku nikah atau akta perkawinan, KK lama kedua mempelai, dan formulir F-1.01 yang diisi di loket. KK baru terbit paling lambat 3 hari kerja dan dikirim dalam bentuk PDF bertanda tangan elektronik ke alamat email pemohon; warga mencetaknya sendiri di kertas HVS A4.

## Perubahan data KK
Untuk menambah anggota keluarga, mengganti alamat dalam kota, atau memperbaiki data, bawa KK lama dan dokumen pendukung perubahan (akta kelahiran, surat pindah, atau ijazah untuk perbaikan pendidikan). Proses paling lambat 3 hari kerja.

## Akta kelahiran
Laporkan kelahiran paling lambat 60 hari setelah bayi lahir. Persyaratan: surat keterangan lahir dari rumah sakit, bidan, atau puskesmas; KK orang tua; buku nikah orang tua; dan KTP-el dua orang saksi. Akta kelahiran selesai pada hari yang sama bila berkas lengkap sebelum pukul 12.00 WIB.

## Terlambat lapor kelahiran
Kelahiran yang dilaporkan lewat dari 60 hari tetap dilayani tanpa denda, tetapi pemohon harus mengisi Surat Pernyataan Tanggung Jawab Mutlak (SPTJM) kebenaran data kelahiran.

## Biaya
Penerbitan KK dan akta kelahiran gratis.
"""),
        ("sop-surat", "SOP Surat Keterangan Usaha dan Domisili", "id", """
# SOP Surat Keterangan Usaha dan Domisili

## Surat Keterangan Usaha (SKU)
SKU diberikan kepada warga yang menjalankan usaha mikro di wilayah Kelurahan Kemuning Raya, misalnya untuk pengajuan kredit usaha rakyat. Persyaratan: surat pengantar RT dan RW, fotokopi KTP-el dan KK, serta foto tempat usaha. SKU ditandatangani lurah dan selesai dalam 1 hari kerja. SKU berlaku 6 bulan.

## Surat Keterangan Domisili
Untuk warga yang tinggal di Kemuning Raya tetapi KTP-elnya beralamat di tempat lain, misalnya mahasiswa atau pekerja. Persyaratan: surat pengantar RT dan RW, fotokopi KTP-el, dan surat keterangan dari pemilik rumah atau kos. Selesai dalam 1 hari kerja dan berlaku 6 bulan.

## Surat pindah keluar
Ajukan surat pindah di kelurahan asal dengan membawa KK asli dan KTP-el semua anggota yang pindah. Surat Keterangan Pindah Warga Negara Indonesia (SKPWNI) terbit paling lambat 1 hari kerja dan dibawa ke kelurahan tujuan dalam 30 hari.

## Biaya
Seluruh surat keterangan gratis.
"""),
        ("jam-layanan", "Jam Layanan dan Kontak", "id", """
# Jam Layanan dan Kontak

## Jam layanan tatap muka
Senin sampai Kamis pukul 08.00 sampai 15.00 WIB, istirahat pukul 12.00 sampai 13.00 WIB. Jumat pukul 08.00 sampai 15.30 WIB, istirahat pukul 11.30 sampai 13.00 WIB. Sabtu, Minggu, dan hari libur nasional kantor tutup.

## Nomor antrean
Nomor antrean dibagikan mulai pukul 07.45 WIB, maksimal 80 nomor per hari untuk layanan kependudukan. Warga lansia, penyandang disabilitas, dan ibu hamil dilayani di loket prioritas tanpa antre.

## Layanan daring
Pengajuan SKU, surat domisili, dan perubahan KK bisa dikirim lewat WhatsApp 0812-3000-4455 di luar jam layanan; berkas diproses pada hari kerja berikutnya.

## Alamat
Kantor Kelurahan Kemuning Raya, Jalan Flamboyan Nomor 12, Kota Sentosa. Parkir sepeda motor gratis di halaman samping.
"""),
        ("visitor-guide", "Visitor guide (English)", "en", """
# Visitor guide for non-Indonesian speakers

## What this office does
Kelurahan Kemuning Raya is the neighbourhood office of Kota Sentosa. It records identity cards (KTP-el) for Indonesian citizens, issues family cards (Kartu Keluarga), birth certificates, and letters of domicile and business.

## Foreign residents
Foreign residents do not get a KTP-el here. Holders of a KITAS or KITAP who live in the area can request a domicile letter (Surat Keterangan Domisili): bring your passport, your KITAS or KITAP, a letter from your landlord, and a covering letter from the neighbourhood heads (RT and RW). The letter is ready in one working day and is free of charge.

## Registering a birth
If your child was born in Kota Sentosa and one parent is an Indonesian citizen, register the birth within 60 days with the hospital's birth letter, the family card, the marriage certificate, and the identity cards of two witnesses. A birth certificate is issued the same day when the documents are complete before noon.

## Opening hours
Monday to Thursday 08:00 to 15:00, Friday 08:00 to 15:30, Western Indonesia Time, with a lunch break. The office is closed on weekends and public holidays. Every service at this office is free of charge.
"""),
    };
}
