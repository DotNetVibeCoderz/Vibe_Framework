# Interop .NET dan runtime JS.Net

> JS.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/interop-and-runtime.md)

## Memakai .NET dari JS.Net

Semua tipe .NET tersedia. Import namespace-nya lalu pakai API aslinya (PascalCase):

```jsnet
import System.Text.RegularExpressions;
import System.Security.Cryptography;

const emails = new Regex("[\\w.]+@[\\w.]+\\.\\w+");
const hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("JS.Net")));
const words = File.ReadAllLines("catatan.txt").Where(l => l.Length > 0).ToList();
```

- Pemanggilan generic: `JsonSerializer.Deserialize<Order>(json)`, `services.AddHostedService<Worker>()`.
- Cast: `x as Rectangle` adalah cast CLR yang diperiksa `((Rectangle)x)`; `x instanceof T` / `x is T` adalah uji tipe; `(typeof MyClass).Assembly` memberi `Type` CLR dari tipe proyek.
- Event: `button.Click += (sender, e) => this.onClick();`
- Argumen `out`/`ref`: `int.TryParse(s, out let n)`, `Interlocked.Increment(ref this.count)`; deklarasikan dengan parameter `out x: T` / `ref x: T`.
- Paket NuGet bekerja sama persis seperti di C#: tambahkan `PackageReference`.

## Memakai JS.Net dari C#

Class library JS.Net adalah assembly biasa:

```csharp
var calc = new JsNetLib.Calculator();
double sum = calc.add(2, 3);           // anggota JS.Net mempertahankan nama camelCase
string[] log = calc.operations;       // getter adalah property
// Fungsi top-level berada di class static bernama sesuai file (Calculator.jsn → "Calculator",
// atau "CalculatorModule" bila nama itu sudah dipakai tipe).
double clamped = JsNetLib.CalculatorModule.clamp(5, 0, 1);
```

Fungsi JS.Net yang asinkron mengembalikan `Task`/`Task<T>`, generator mengembalikan `IEnumerable<T>`.

## Runtime (`JSNet.Runtime`)

Diimpor otomatis ke setiap file JS.Net.

| API | Catatan |
|---|---|
| `console.log/info/warn/error/debug/table/time/timeEnd/group/assert/count` | Format gaya Node.js (`{ a: 1, tags: [ 'x' ] }`). `console.capture(fn)` mengembalikan output yang ditangkap. |
| `JSON.stringify(value, null, 2)`, `JSON.parse(text)`, `JSON.parse<T>(text)` | System.Text.Json. Parse tanpa tipe menghasilkan `JsObject`/list/double. |
| `Math.*` | Semantik JavaScript (`Math.round(2.5) == 3`); `Math.Sqrt` dsb. tetap menuju `System.Math`. |
| `Number.*`, `Object.keys/values/entries/assign`, `Array.isArray/from/of/range`, `String.fromCharCode` | |
| `parseInt`, `parseFloat`, `isNaN`, `isFinite`, `String(x)`, `Number(x)`, `Boolean(x)` | |
| `setTimeout`, `setInterval`, `clearTimeout`, `delay(ms)`, `queueMicrotask` | Program menunggu timer yang tertunda sebelum keluar. |
| `Promise.all/allSettled/race/any/resolve/reject/delay`, `new Promise((resolve, reject) => …)` | Promise bisa di-`await` dan dikonversi ke `Task`. |
| `fetch(url, { method, headers, body })` | `HttpClient` bersama; respons memiliki `ok`, `status`, `text()`, `json()`, `json<T>()`. |
| `Date` | Gaya JS (bulan mulai 0), konversi implisit dari/ke `DateTime`. |
| `Error`, `TypeError`, `RangeError`, `SyntaxError` | Exception sungguhan; `e.message`, `e.name`, `e.stack` berlaku untuk exception apa pun. |
| `process.argv/env/platform/cwd()/exit()` | |
| `JsObject` | Objek dinamis yang bisa diubah (`let o: dynamic = {}`). |

### Anggota gaya JavaScript pada tipe .NET

Diimplementasikan sebagai extension member C# 14 — pemanggilan static, tanpa wrapper:

- **string**: `length`, `toUpperCase`, `toLowerCase`, `includes`, `startsWith`, `endsWith`, `indexOf`, `slice`, `substring`, `split`, `trim`, `padStart`, `padEnd`, `repeat`, `replace`, `replaceAll`, `charAt`, `at`, `match`.
- **semua sequence**: `map`, `filter`, `reduce`, `forEach`, `find`, `findIndex`, `some`, `every`, `includes`, `indexOf`, `join`, `flatMap`, `concat`, `toSorted`, `toReversed` (eager, mengembalikan list) serta LINQ huruf kecil `where`, `select`, `orderBy`, `groupBy`, `distinct`, `take`, `skip`, `first`, `count`, `sum`, `average`, `min`, `max`, `toList`, `toArray`, `toSet` (lazy seperti LINQ).
- **List / array**: `length`, `push`, `pop`, `shift`, `unshift`, `splice`, `slice`, `sort` (in place), `reverse`, `at`, `fill`.
- **Map / Set**: `size`, `get`, `set`, `has`, `delete`, `keys`, `values`, `entries`, `forEach`, `add`, `clear`.
- **angka**: `toFixed`, `toPrecision`, `toString(radix)`, `toLocaleString`.

## Dependency injection

```jsnet
@Service(ServiceScope.Singleton)
export class TodoStore implements ITodoStore { … }

builder.Services.addJsNetServices();   // mendaftarkan setiap class @Service sebagai dirinya dan interface-nya
app.MapGet("/api/todos", (store: TodoStore) => store.all());
```

Parameter property di constructor membuat constructor injection ringkas:

```jsnet
export class Worker extends BackgroundService {
    constructor(private readonly logger: ILogger<Worker>) { super(); }
}
```

## Framework UI

- **Blazor**: letakkan logika komponen di `Page.razor.jsn` sebagai `export partial class Page extends ComponentBase`. Pakai `@Parameter`, `@Inject`, `@SupplyParameterFromQuery`, dan override lifecycle (`protected override async OnInitializedAsync(): Promise<void>`).
- **WPF**: `MainWindow.xaml.jsn` → `export partial class MainWindow extends Window { constructor() { super(); this.InitializeComponent(); } }`. Elemen bernama adalah field (`this.CounterText`).
- **Avalonia**: pola yang sama dengan `.axaml.jsn`; name generator Avalonia menyediakan `InitializeComponent`.
- **WinForms**: bangun UI lewat kode; pakai `export function main()` dengan `@STAThread`.

## Paket pendamping

| Paket | Yang ditambahkan |
|---|---|
| `JSNet.Web` | Routing gaya Express di atas ASP.NET Core: `app.get("/hi/{name}", (name: string) => ...)`, `post/put/patch/delete/route`, `app.use(async (ctx, next) => ...)`, `serveStatic`, `listen`, dan `res.json/text/status/redirect`. |
| `JSNet.EntityFramework` | Operator async EF Core huruf kecil: `toListAsync`, `firstAsync`, `countAsync`, `include`, `add`, `remove`, `save`, `ensureCreated`. Lambda tetap menjadi expression tree, jadi `where(t => !t.done)` diterjemahkan ke SQL. |
| `JSNet.Blazor` | Base class `JsComponent` dengan hook `mounted`/`updated`/`unmounted`, `setState`, timer `every`/`after`, dan `navigate`. |
| `JSNet.Reactive` | Observable gaya RxJS di atas System.Reactive: `rx.of/from/interval/subject`, `map`, `filter`, `scan`, `debounceTime`, `subscribe`, `toArrayAsync`. |
| `JSNet.Analyzers` | API yang dipakai deklarasi `rule` kustom (`Rule`, `RuleContext`, `RuleRunner`). |

`IQueryable<T>` mendapat LINQ huruf kecil yang sama (`where`, `select`, `orderBy`, ...) seperti array, sehingga query di atas tetap bisa diterjemahkan.

## NativeAOT dan trimming

Runtime sudah diberi anotasi trim dan AOT. Kode bertipe dipublikasikan tanpa peringatan. Helper berbasis reflection (`JSON.stringify(obj)`, `JSON.parse<T>(text)`, `fetch(...).json<T>()`, `AddJsNetServices`) ditandai `RequiresUnreferencedCode`; untuk AOT pakai overload yang menerima `JsonTypeInfo` dari `JsonSerializerContext`:

```jsnet
const json = JSON.stringify(point, AppJson.Default.Point);
const back = JSON.parse(json, AppJson.Default.Point);
```
