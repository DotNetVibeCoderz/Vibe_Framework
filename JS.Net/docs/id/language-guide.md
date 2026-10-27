# Panduan bahasa JS.Net

> JS.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/language-guide.md)

JS.Net dibaca seperti TypeScript dan dikompilasi seperti C#. Panduan ini mencantumkan apa saja yang didukung dan bagaimana setiap konstruksi dipetakan ke CLR. Jika ragu, jalankan `jsnet emit file.jsn` untuk melihat C# yang dihasilkan compiler.

## File, modul, dan entry point

- Semua file `.jsn` dalam proyek dikompilasi bersama. Deklarasi terlihat lintas file tanpa import.
- **Fungsi dan variabel** top-level di `utils.jsn` menjadi anggota static dari class bernama `Utils` (atau `UtilsModule` bila nama `Utils` sudah dipakai tipe lain) di namespace file tersebut. Semuanya diimpor ke mana-mana lewat `global using static`.
- Namespace = `RootNamespace` + path folder (seperti C#), atau ditetapkan eksplisit dengan `namespace My.Company;`.
- **Program**: hanya satu file yang boleh berisi top-level statement. Statement tersebut menjadi `Main` (async, dengan `args` tersedia) dan proses menunggu timer `setTimeout`/`setInterval` yang masih tertunda sebelum keluar, seperti Node.js.
- Program tanpa top-level statement dapat mendeklarasikan `export function main(args: string[]): void`; fungsi ini menjadi entry point CLR. Pakai `@STAThread` untuk WinForms/Avalonia.
- **Library** (`OutputType=Library`) tidak boleh berisi top-level statement.
- Semua deklarasi top-level bersifat `public` kecuali ditandai `private`/`internal`. `export` diterima dan menegaskan maksud.

## Variabel dan tipe

```jsnet
let count = 0;                 // diinferensi: int
let price: number = 9.99;      // number → double
const name = "Ada";            // string
dynamic anything = 42;         // late-bound
let maybe: string | null = null;
```

| JS.Net | CLR |
|---|---|
| `number` | `double` |
| `int`, `long`, `float`, `decimal`, `byte`, `short`, `char` | primitif CLR yang sama |
| `bool`, `boolean` | `bool` |
| `string` | `string` |
| `object`, `unknown` | `object` |
| `any`, `dynamic` | `dynamic` |
| `void` | `void` |
| `T[]` | `T[]` (array CLR berukuran tetap) |
| `Array<T>` | `List<T>` (bisa bertambah) |
| `Map<K, V>`, `Record<K, V>` | `Dictionary<K, V>` |
| `Set<T>` | `HashSet<T>` |
| `Promise<T>` / `Promise<void>` | `Task<T>` / `Task` |
| `Iterable<T>`, `Generator<T>` | `IEnumerable<T>` |
| `(a: A) => R` / `() => void` | `Func<A, R>` / `Action` |
| `[A, B]` | value tuple `(A, B)` |
| `T \| null` | `T?` |

Catatan:

- **Literal bilangan bulat bertipe `int`** (semantik CLR), jadi `7 / 2` bernilai `3`. Compiler memberi peringatan (JSN2003) untuk pembagian integer. Gunakan `number`, titik desimal (`7.0 / 2`), atau biarkan compiler menginferensi: variabel atau field yang diinisialisasi bilangan bulat tetapi kemudian diberi nilai pecahan otomatis bertipe `double`.
- Sufiks numerik .NET berlaku: `10L`, `9.5f`, `19.99m`, `1d`.
- Parameter tanpa anotasi dan tipe return yang tidak bisa diinferensi menjadi `dynamic` dengan peringatan JSN2002. Beri anotasi pada jalur kode yang sering dieksekusi.
- Union selain `T | null` menjadi `object` (peringatan JSN2005). Union literal string (`"a" | "b"`) menjadi `string`.

## Fungsi

```jsnet
function greet(name: string, greeting = "Halo"): string { return `${greeting}, ${name}!`; }
function total(...values: number[]): number { return values.reduce((s, v) => s + v, 0); }
function find(id: int, fallback?: string): string { return fallback ?? "tidak ada"; }
const square = (x: number) => x * x;
const makeCounter = () => { let n = 0; return () => ++n; };
function* range(n: int): Iterable<int> { for (let i = 0; i < n; i++) yield i; }
async function load(url: string): Promise<string> { const r = await fetch(url); return await r.text(); }
```

- Tipe return diinferensi bila memungkinkan (`function add(a: number, b: number) { return a + b; }` → `double`).
- Rest parameter menjadi `params`. Parameter opsional menjadi nullable dengan nilai default.
- Default yang tidak konstan (`items: string[] = []`) diterapkan di awal pemanggilan.
- Parameter destructuring berfungsi: `function show({ name, age }: Person) { … }`.
- Fungsi `async` mengembalikan `Task`/`Task<T>`. Method `async` dengan anotasi `: void` menjadi `async void` (untuk event handler).
- Arrow function adalah lambda C#; bila disimpan di variabel tanpa anotasi, parameter tanpa tipe menjadi `dynamic` agar variabel memiliki tipe delegate.

## Class

```jsnet
export class Animal {
    static count = 0;                         // properti static publik
    protected sound = "...";                  // anggota private/protected menjadi field
    constructor(public readonly name: string) { Animal.count++; }   // parameter property
    speak(): string { return `${this.name} berkata ${this.sound}`; }
    get label(): string { return this.name.toUpperCase(); }
    set label(v: string) { /* … */ }
    toString() { return `Animal(${this.name})`; }   // sekaligus override ToString()
}

export class Dog extends Animal {
    constructor(name: string) { super(name); this.sound = "guk"; }
    speak(): string { return "🐶 " + super.speak(); }    // override diinferensi
}
```

- Field publik menjadi auto-property (`{ get; set; }`; `readonly` → `{ get; }`) sehingga bekerja dengan data binding dan serializer.
- `super(...)` harus menjadi statement pertama di constructor; diturunkan menjadi `: base(...)`.
- Override method milik base class JS.Net terdeteksi otomatis. Method dari class yang diturunkan di dalam proyek dikeluarkan sebagai `virtual`. Untuk base class .NET, tulis `override` secara eksplisit (`protected override async ExecuteAsync(...)`).
- Modifier: `public private protected internal static readonly abstract override virtual sealed partial async`. Class selalu dikeluarkan sebagai `partial`, sehingga code-behind Razor, WPF, dan Avalonia tergabung otomatis.
- `struct Name { … }` dan `record Name { … }` mendeklarasikan struct dan record CLR.
- Nama `#private` didukung (dikeluarkan sebagai field private).

## Interface, generic, dan enum

```jsnet
export interface Entity { id: int; }
export interface Product extends Entity { name: string; price: number; }

const p: Product = { id: 1, name: "Pena", price: 2.5 };   // interface khusus data menerima object literal

export class Repository<T extends Entity> {
    private items = new Map<int, T>();
    add(item: T): void { this.items.set(item.id, item); }
}

function first<T>(xs: T[]): T { return xs[0]; }
const s = first<string>(["a"]);

enum Color { Red, Green = 5, Blue }                // enum CLR
enum Status { Open = "open", Closed = "closed" }   // konstanta string
```

## Objek dan array

- Object literal menjadi **anonymous type** (cepat, bertipe, immutable): `const u = { name: "Ada", age: 36 }`.
- Object literal bertipe membuat tipe target: `const p: Point = { x: 1, y: 2 }` → `new Point { x = 1, y = 2 }`; untuk interface khusus data dipakai implementasi tersembunyi; untuk target `Map`/`Record` dibuat inisialisasi dictionary.
- Beri anotasi `dynamic` untuk objek gaya JavaScript yang **bisa diubah**: `let o: dynamic = { a: 1 }; o.b = 2;` (didukung `JsObject`).
- Array literal tanpa tipe target menjadi `List<T>`; dengan tipe target memakai collection expression C# (`const xs: int[] = [1, 2]`). Spread berfungsi: `[...a, 4]`, `f(...args)`.
- `new T[size]` membuat array CLR; `new Array<T>()` membuat list.

## Alur kontrol

`if/else`, `for`, `for...of` (→ `foreach`), `for...in` (kunci), `for await`, `while`, `do...while`, `switch`, `break`, `continue`, `return`, `throw`, `try/catch/finally`, `using`.

- **Truthiness**: kondisi memakai aturan JavaScript (`if (name)`, `if (!list.length)`, `if (obj)`); untuk nilai `bool` tanpa biaya tambahan.
- `a || b` dan `a && b` mengembalikan nilai seperti JavaScript (`const n = input || "anonim"`).
- `switch` mendukung fall-through, case bertumpuk, dan case tidak konstan (`switch (true) { case score > 90: … }`).
- `try { } catch (e: IOException) { } catch (e) { }` — boleh beberapa catch bertipe; `catch (e)` tanpa tipe menangkap `Exception`. Nilai apa pun bisa dilempar (`throw "oops"` melempar `Error`).
- `using (const f = File.OpenRead(p)) { … }` dan `using x = expr;` melakukan dispose secara deterministik.

## Destructuring, spread, dan template

```jsnet
const { name, role } = user;
const [first, second] = list;
[a, b] = [b, a];                                // tukar nilai lewat tuple
for (const [key, value] of map) { … }
console.log(`Total: ${total.toFixed(2)} (${items.length} item)`);
```

## Object type, spread, dan regex

```jsnet
type Point = { x: number; y: number };               // object type literal → record yang dibangkitkan
function len(p: { x: number, y: number }): number { return Math.sqrt(p.x * p.x + p.y * p.y); }
const p: Point = { x: 3, y: 4 };                      // record dibandingkan berdasarkan nilai

const merged = { ...base, role: "lead" };             // object spread: properti terakhir menang
const s: Settings = { ...defaults, size: 14 };        // spread ke dalam class

const re = /(\d+)-(\d+)/g;                            // regex literal → RegExp (System.Text.RegularExpressions)
"a1b22".replace(/\d+/g, "#");  "a, b;c".split(/[,;]\s*/);  re.test(text);
```

## Union dan narrowing

Union dari beberapa class menjadi marker interface yang diimplementasikan semua anggotanya; `instanceof` mempersempit tipe variabel di dalam cabang:

```jsnet
type Shape = Circle | Square;
function area(shape: Shape): number {
    if (shape instanceof Circle) return Math.PI * shape.radius ** 2;
    if (shape instanceof Square && shape.side > 0) return shape.side ** 2;
    return 0;
}
```

## Class expression, label, `out`/`ref`

```jsnet
const Greeter = class { constructor(public name: string) {} };   // dinamai sesuai variabelnya
function make(): int { class Counter { n: int = 0; } return new Counter().n; }   // class lokal di-hoist

outer: for (const row of grid) {
    for (const cell of row) { if (cell == 0) continue outer; if (cell < 0) break outer; }
}

function swap(ref a: int, ref b: int): void { const t = a; a = b; b = t; }
swap(ref x, ref y);
if (int.TryParse(text, out let n)) console.log(n + 1);   // out mendeklarasikan variabel baru
```

## Aturan analyzer kustom

Deklarasi `rule` dikompilasi menjadi analyzer yang berjalan di dalam compiler JS.Net (lihat [panduan tooling](tooling.md#aturan-kustom)):

```jsnet
rule NoConsoleLog {
    id: "APP001";
    severity: "warning";
    description: "console.log tidak boleh ada di library";
    check(node: Node, ctx: RuleContext): void {
        if (node instanceof CallExpression && ctx.text(node.Callee) == "console.log")
            ctx.report(node, "Pakai logger, bukan console.log");
    }
}
```

## Attribute dan decorator

Sintaks decorator TypeScript maupun attribute C# diturunkan menjadi attribute CLR:

```jsnet
@ApiController
@Route("api/[controller]")
export class TodosController extends ControllerBase {
    [HttpGet("{id}")]
    get(id: int): object { … }
}

@SupplyParameterFromQuery({ Name: "run" })     // object literal di akhir menjadi named argument
autoRun: string | null = null;
```

`@Service` / `@Service(ServiceScope.Singleton)` mendaftarkan class ke dependency injection (lihat [panduan runtime](interop-and-runtime.md#dependency-injection)).

## Import

```jsnet
import System.Text;                                  // using System.Text;
import static System.Math;                           // using static System.Math;
import { List, Dictionary as Dict } from "System.Collections.Generic";   // alias tipe
import * as IO from "System.IO";                     // alias namespace
import Box = System.Windows.Shapes.Rectangle;        // alias (menyelesaikan bentrok nama)
import { Greeter } from "./Greeter";                  // import modul relatif: diterima, tanpa efek
type Id = int;                                       // alias global
```

## Deklarasi AI-native

```jsnet
ai function summarize(text: string): Promise<string> {
    `Ringkas dalam satu kalimat: ${text}`
}

prompt Translator {
    text: string;
    language: string;
    output: string;
    template: `Terjemahkan "${text}" ke ${language}.`;
}

console.log(await summarize(artikel));
console.log(await Translator.run("Terima kasih", "Japanese"));
```

Membutuhkan paket `JSNet.AI` (`<JSNetEnableAI>true</JSNetEnableAI>`). Konfigurasikan model dengan `AiRuntime.configure(chatClient)` memakai `IChatClient` apa pun dari `Microsoft.Extensions.AI` (OpenAI, Azure OpenAI, Ollama, Semantic Kernel). Tanpa konfigurasi, klien echo offline yang menjawab. Tipe kembalian selain string diminta sebagai JSON lalu dideserialisasi.

```jsnet
interface Weather { city: string; celsius: number; }
ai function forecast(city: string): Promise<Weather> { `Cuaca untuk ${city}` }   // output terstruktur (JSON schema)

ai function* tell(topic: string): AsyncIterable<string> { `Ceritakan tentang ${topic}` }
for await (const chunk of tell("CoreCLR")) Console.Write(chunk);         // streaming

class WeatherTools {
    @AiTool("Cuaca terkini di sebuah kota")
    getWeather(city: string): string { return `${city} 24°C`; }
}
AiRuntime.useTools(new WeatherTools());                                          // model boleh memanggil tool
```

Class JS.Net juga bisa menjadi plugin Semantic Kernel: tandai method dengan `@KernelFunction` dan `@Description`, lalu berikan objeknya ke `KernelPluginFactory.CreateFromObject`.

## Keterbatasan yang diketahui

- Object type literal bersifat struktural hanya di dalam satu proyek: dua literal dengan anggota yang sama adalah record hasil bangkitan yang sama.
- `this` di dalam ekspresi `function` merujuk ke class yang melingkupinya (semantik arrow function).
- `Map.get` untuk kunci yang tidak ada mengembalikan nilai default (`0`, `null`); untuk tipe nilai, periksa `has` terlebih dahulu.
- Iterasi string dengan `for...of` menghasilkan nilai `char`.

Lihat [PLAN.md](../../PLAN.md) untuk roadmap.
