# Mode build: interpreted dan AOT

Rustzor punya dua mode build. Keduanya menghasilkan situs statis dan berjalan di runtime yang sama.

| | Interpreted (default) | AOT (`-p:RustzorAot=true`) |
| --- | --- | --- |
| Yang menjalankan kode Anda | IL interpreter milik RustNetRuntime | Method numerik yang sering dipanggil (hot): WebAssembly hasil kompilasi. Sisanya: interpreter |
| Waktu build | Cepat | Ada tambahan langkah kompilasi |
| Ukuran unduhan | `rustzor.wasm` + DLL Anda | Sama, ditambah `app.aot.wasm` yang kecil |
| Reflection, dispatch `dynamic`, seluruh BCL | Ya | Ya (method-method tersebut tetap di-interpret) |
| Paling cocok untuk | Aplikasi bisnis, form, dashboard, development | Game, simulasi, pengolahan gambar, matematika |

## Mengaktifkan AOT

```bash
dotnet build -p:RustzorAot=true
dotnet publish -c Release -p:RustzorAot=true
```

atau di file project:

```xml
<PropertyGroup>
  <RustzorAot>true</RustzorAot>
</PropertyGroup>
```

## Apa yang dikompilasi

Compiler AOT (`Rustzor.Build aot`) memeriksa setiap static method di assembly Anda sendiri (bukan `Rustzor.*`, `System.*`, atau `Microsoft.*`) dan mengompilasi sebuah method jika:

- parameter, variabel lokal, dan nilai kembaliannya berupa tipe primitif (`bool`, `char`, tipe-tipe integer, `float`, `double`) atau array satu dimensi dari `int`, `long`, `float`, `double`, atau `byte`;
- body-nya hanya memakai aritmetika, perbandingan, konversi, percabangan, `switch`, akses array, `System.Math`, dan pemanggilan ke method lain yang juga dikompilasi;
- tidak memiliki exception handler, dan tidak menyentuh objek, string, field, atau alokasi.

Selain itu akan ditolak dan tetap berjalan di interpreter, begitu juga method apa pun yang memanggil method yang ditolak. Karena method yang ditolak berjalan persis seperti sebelumnya, AOT bisa mengubah kecepatan tetapi tidak pernah mengubah hasil. Argumen array disalin ke memori modul hasil kompilasi lalu disalin kembali; kesalahan index, pembagian dengan nol, dan overflow menjadi managed exception yang sama dengan yang dilempar interpreter.

Build menulis `obj/<config>/net10.0/rustzor-aot/aot-report.txt`, yang mendaftar setiap method sebagai compiled atau interpreted beserta alasannya. Baca file ini untuk melihat hot path mana saja yang memenuhi syarat; sering kali refactor kecil (mengoper array dan angka ke static helper alih-alih list berisi objek) sudah cukup untuk memindahkan sebuah loop ke kode hasil kompilasi.

```csharp
// Dikompilasi: static, hanya angka dan array.
static void Step(double[] x, double[] v, double dt, double g)
{
    for (var i = 0; i < x.Length; i++)
    {
        v[i] += g * dt;
        x[i] += v[i] * dt;
    }
}
```

## Mode mana yang berjalan

Jika `boot.json` menyebutkan sebuah modul AOT, `rustzor.js` memuatnya setelah assembly. Untuk membandingkan, jalankan halaman hanya dengan interpreter:

```js
start({ interpretOnly: true });
```
