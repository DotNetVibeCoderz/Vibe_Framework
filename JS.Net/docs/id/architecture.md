# Arsitektur

> JS.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/architecture.md)

JS.Net mengikuti desain di [architecture-design.md](../../architecture-design.md): bahasa yang mengutamakan JavaScript, menghasilkan IL CLR standar, memakai runtime yang sudah ada, dan terintegrasi dengan seluruh ekosistem .NET. JS.Net mengimplementasikan **strategi integrasi Roslyn** — front end JS.Net dengan back end Roslyn.

```text
            ┌───────────────────────── front end JS.Net ─────────────────────────┐
 .jsn ───►  │ Lexer ─► Parser ─► AST ─► Binder / inferensi tipe ─► emitter C#     │ ───► .g.cs (+ #line)
            └──────────────────────────────────────────────────────────────────────┘
                                                                         │
                         Roslyn (csc / in-memory) ◄──────────────────────┘
                                   │
                     IL + PDB portabel ─► CoreCLR: GC, tiered JIT, ReadyToRun, NativeAOT
```

## Komponen

| Proyek | Tanggung jawab |
|---|---|
| `JSNet.Syntax` | `SourceText` (peta baris), token, lexer, AST, diagnostik, AST walker. Lexer selalu mengeluarkan `>` sebagai satu token agar `List<List<int>>` bisa di-parse; parser menggabungkan kembali `>>`/`>=` di ekspresi. |
| `JSNet.Parser` | Parser recursive-descent dengan automatic semicolon insertion, parsing spekulatif (arrow function, argumen tipe pada pemanggilan generic), pembeda attribute vs array, dan pemulihan error per statement. |
| `JSNet.Semantics` | Pengumpulan simbol seluruh proyek, scope, pemeriksaan aturan JavaScript (assignment ke const, await di luar async, break di luar loop, posisi super), analyzer (variabel tak terpakai, pembagian integer, kode tak terjangkau, fallback dynamic), dan inferensi tipe (literal, operator, pemanggilan, field, lambda, generator, async). Juga memetakan tipe JS.Net ke tipe CLR. |
| `JSNet.CodeGen` | Menurunkan unit yang sudah di-bind ke C#: modul → class static, top-level statement → `Main`, truthiness JavaScript, semantik nilai `&&`/`||`, fall-through switch, destructuring, spread, template literal, decorator → attribute, interface data → implementasi tersembunyi, `ai function`/`prompt` → pemanggilan `JSNet.AI`. |
| `JSNet.Compiler` | Driver: parse paralel, satu kali bind untuk seluruh proyek, emit paralel, global using, namespace dari folder, diagnostik. |
| `JSNet.Runtime` | Pustaka runtime kecil: `console`, `JSON`, `Math`, `Promise`, `Date`, timer, `fetch`, `JsObject`, anggota gaya JavaScript sebagai extension member C# 14, DI `@Service`. |
| `JSNet.AI` | `AiRuntime` dan `Prompt<T>` di atas `Microsoft.Extensions.AI.IChatClient`. |
| `JSNet.Sdk` | MSBuild SDK: pembangkitan kode inkremental sebelum `CoreCompile`. |
| `JSNet.Scripting` | Kompilasi Roslyn di memori ke `AssemblyLoadContext` yang bisa di-unload (`jsnet run`, playground Gallery, test). |
| `JSNet.Cli` | Perintah `jsnet`. |
| `JSNet.LanguageServer` | Host LSP; completion/hover member .NET berasal dari kompilasi probe Roslyn atas kode hasil penurunan dan referensi proyek. |
| `JSNet.Analyzers` | API untuk deklarasi `rule`; aturan dimuat dari assembly dan dijalankan atas AST yang sudah di-bind. |
| `JSNet.Web` · `JSNet.EntityFramework` · `JSNet.Blazor` · `JSNet.Reactive` | Paket pendamping (extension member untuk ASP.NET Core, EF Core, Blazor, dan System.Reactive). |
| `gallery/JSNet.Playground` | Front end yang sama ditambah Roslyn yang berjalan di Blazor WebAssembly; referensi metadata diambil dari assembly yang sudah dimuat (`Assembly.TryGetRawMetadata`). |
| `rust/jsnet-tools` | Formatter, checker, tokenizer, dan statistik native. |

## Keputusan desain

- **Kenapa diturunkan ke C# alih-alih langsung mengeluarkan IL?** Roslyn sudah menyediakan IL yang benar untuk generic, state machine async, iterator, closure, pattern matching, PDB, source generator (Razor, WPF, Avalonia, WinForms, `System.Text.Json`), analyzer, dan kompatibilitas NativeAOT. Menurunkan ke C# memberi JS.Net semua itu sejak hari pertama; front end tetap fokus pada semantik bahasa. Direktif `#line` menjaga diagnostik dan debugging tetap di baris `.jsn`.
- **Statis bila memungkinkan, dinamis bila diminta.** Anotasi dan inferensi menghasilkan primitif dan generic CLR; `dynamic` bersifat opt-in dan dilaporkan (JSN2002) bila menjadi fallback.
- **Semantik JavaScript di tempat yang penting.** Truthiness, nilai `||`/`&&`, `typeof`, `throw` nilai apa pun, format `console.log`, serta API array/string mengikuti JavaScript; aritmetika, pembagian integer, dan koleksi mengikuti CLR demi performa dan interop.
- **Helper runtime tanpa biaya.** Anggota JavaScript pada string dan koleksi adalah extension member C# 14 (pemanggilan static). Overload `JS.truthy` diselesaikan saat kompilasi.
- **Visibilitas modul global.** Fungsi top-level setiap file bisa dijangkau dari file lain (`global using static`), sesuai cara proyek JS.Net dikompilasi sebagai satu assembly.
- **Paralelisme.** Parsing dan emit berjalan paralel per file; binding adalah satu tahap untuk seluruh proyek.

## Integrasi build

`JSNet.Sdk.targets` menjalankan `jsnet compile` sebelum `BeforeCompile`/`CoreCompile`/`XamlPreCompile` dengan `Inputs`/`Outputs` untuk inkrementalitas, hanya menulis file yang berubah, menghapus output basi, dan menambahkan `obj/…/jsnet/*.g.cs` ke `@(Compile)`. Karena kode yang dihasilkan adalah C# biasa, komponen Razor, code-behind XAML WPF/Avalonia, dan WinForms semuanya tergabung dengan partial class JS.Net.
