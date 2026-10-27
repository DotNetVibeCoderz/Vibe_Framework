# .NET interop and the JS.Net runtime

> JS.Net — created by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/interop-and-runtime.md)

## Using .NET from JS.Net

Every .NET type is available. Import a namespace and use it with its normal (PascalCase) API:

```jsnet
import System.Text.RegularExpressions;
import System.Security.Cryptography;

const emails = new Regex("[\\w.]+@[\\w.]+\\.\\w+");
const hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("JS.Net")));
const words = File.ReadAllLines("notes.txt").Where(l => l.Length > 0).ToList();
```

- Generic calls: `JsonSerializer.Deserialize<Order>(json)`, `services.AddHostedService<Worker>()`.
- Casts: `x as Rectangle` is a checked CLR cast `((Rectangle)x)`; `x instanceof T` / `x is T` are type tests; `(typeof MyClass).Assembly` gives the CLR `Type` of a project type.
- Events: `button.Click += (sender, e) => this.onClick();`
- `out`/`ref` arguments: `int.TryParse(s, out let n)`, `Interlocked.Increment(ref this.count)`; declare them with `out x: T` / `ref x: T` parameters.
- NuGet packages work exactly like in C#: add a `PackageReference`.

## Using JS.Net from C#

A JS.Net class library is a normal assembly:

```csharp
var calc = new JsNetLib.Calculator();
double sum = calc.add(2, 3);           // JS.Net members keep their camelCase names
string[] log = calc.operations;       // getters are properties
// Top-level functions live in a static class named after the file (Calculator.jsn → "Calculator",
// or "CalculatorModule" when a type already uses that name).
double clamped = JsNetLib.CalculatorModule.clamp(5, 0, 1);
```

Asynchronous JS.Net functions return `Task`/`Task<T>`, generators return `IEnumerable<T>`.

## The runtime (`JSNet.Runtime`)

Imported automatically into every JS.Net file.

| API | Notes |
|---|---|
| `console.log/info/warn/error/debug/table/time/timeEnd/group/assert/count` | Node.js-style formatting (`{ a: 1, tags: [ 'x' ] }`). `console.capture(fn)` returns captured output. |
| `JSON.stringify(value, null, 2)`, `JSON.parse(text)`, `JSON.parse<T>(text)` | System.Text.Json. Untyped parse returns `JsObject`/lists/doubles. |
| `Math.*` | JavaScript semantics (`Math.round(2.5) == 3`); `Math.Sqrt` etc. still reach `System.Math`. |
| `Number.*`, `Object.keys/values/entries/assign`, `Array.isArray/from/of/range`, `String.fromCharCode` | |
| `parseInt`, `parseFloat`, `isNaN`, `isFinite`, `String(x)`, `Number(x)`, `Boolean(x)` | |
| `setTimeout`, `setInterval`, `clearTimeout`, `delay(ms)`, `queueMicrotask` | Programs wait for pending timers before exiting. |
| `Promise.all/allSettled/race/any/resolve/reject/delay`, `new Promise((resolve, reject) => …)` | Promises are awaitable and convert to `Task`. |
| `fetch(url, { method, headers, body })` | Shared `HttpClient`; response has `ok`, `status`, `text()`, `json()`, `json<T>()`. |
| `Date` | JS-style (0-based months), converts implicitly to/from `DateTime`. |
| `Error`, `TypeError`, `RangeError`, `SyntaxError` | Real exceptions; `e.message`, `e.name`, `e.stack` work on any exception. |
| `process.argv/env/platform/cwd()/exit()` | |
| `JsObject` | Mutable dynamic object (`let o: dynamic = {}`). |

### JavaScript-style members on .NET types

Implemented as C# 14 extension members — static calls, no wrappers:

- **string**: `length`, `toUpperCase`, `toLowerCase`, `includes`, `startsWith`, `endsWith`, `indexOf`, `slice`, `substring`, `split`, `trim`, `padStart`, `padEnd`, `repeat`, `replace`, `replaceAll`, `charAt`, `at`, `match`.
- **any sequence**: `map`, `filter`, `reduce`, `forEach`, `find`, `findIndex`, `some`, `every`, `includes`, `indexOf`, `join`, `flatMap`, `concat`, `toSorted`, `toReversed` (eager, return lists) and lower-case LINQ `where`, `select`, `orderBy`, `groupBy`, `distinct`, `take`, `skip`, `first`, `count`, `sum`, `average`, `min`, `max`, `toList`, `toArray`, `toSet` (lazy where LINQ is lazy).
- **List / array**: `length`, `push`, `pop`, `shift`, `unshift`, `splice`, `slice`, `sort` (in place), `reverse`, `at`, `fill`.
- **Map / Set**: `size`, `get`, `set`, `has`, `delete`, `keys`, `values`, `entries`, `forEach`, `add`, `clear`.
- **numbers**: `toFixed`, `toPrecision`, `toString(radix)`, `toLocaleString`.

## Dependency injection

```jsnet
@Service(ServiceScope.Singleton)
export class TodoStore implements ITodoStore { … }

builder.Services.addJsNetServices();   // registers every @Service class as itself and its interfaces
app.MapGet("/api/todos", (store: TodoStore) => store.all());
```

Constructor parameter properties make constructor injection concise:

```jsnet
export class Worker extends BackgroundService {
    constructor(private readonly logger: ILogger<Worker>) { super(); }
}
```

## UI frameworks

- **Blazor**: put component logic in `Page.razor.jsn` as `export partial class Page extends ComponentBase`. Use `@Parameter`, `@Inject`, `@SupplyParameterFromQuery`, lifecycle overrides (`protected override async OnInitializedAsync(): Promise<void>`).
- **WPF**: `MainWindow.xaml.jsn` → `export partial class MainWindow extends Window { constructor() { super(); this.InitializeComponent(); } }`. Named elements are fields (`this.CounterText`).
- **Avalonia**: same pattern with `.axaml.jsn`; the Avalonia name generator provides `InitializeComponent`.
- **WinForms**: build the UI in code; use `export function main()` with `@STAThread`.

## Companion packages

| Package | What it adds |
|---|---|
| `JSNet.Web` | Express-style routing on ASP.NET Core: `app.get("/hi/{name}", (name: string) => ...)`, `post/put/patch/delete/route`, `app.use(async (ctx, next) => ...)`, `serveStatic`, `listen`, and `res.json/text/status/redirect`. |
| `JSNet.EntityFramework` | Lower-case async EF Core operators: `toListAsync`, `firstAsync`, `countAsync`, `include`, `add`, `remove`, `save`, `ensureCreated`. Lambdas stay expression trees, so `where(t => !t.done)` translates to SQL. |
| `JSNet.Blazor` | `JsComponent` base class with `mounted`/`updated`/`unmounted` hooks, `setState`, `every`/`after` timers and `navigate`. |
| `JSNet.Reactive` | RxJS-style observables on System.Reactive: `rx.of/from/interval/subject`, `map`, `filter`, `scan`, `debounceTime`, `subscribe`, `toArrayAsync`. |
| `JSNet.Analyzers` | The API custom `rule` declarations compile against (`Rule`, `RuleContext`, `RuleRunner`). |

`IQueryable<T>` gets the same lower-case LINQ (`where`, `select`, `orderBy`, ...) as arrays, so the queries above stay translatable.

## NativeAOT and trimming

The runtime is trim- and AOT-annotated. Typed code publishes with no warnings. Reflection-based helpers (`JSON.stringify(obj)`, `JSON.parse<T>(text)`, `fetch(...).json<T>()`, `AddJsNetServices`) say so with `RequiresUnreferencedCode`; for AOT use the overloads that take a `JsonTypeInfo` from a `JsonSerializerContext`:

```jsnet
const json = JSON.stringify(point, AppJson.Default.Point);
const back = JSON.parse(json, AppJson.Default.Point);
```
