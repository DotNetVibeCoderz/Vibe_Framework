# JS.Net language guide

> JS.Net — created by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/language-guide.md)

JS.Net reads like TypeScript and compiles like C#. This guide lists what is supported and how each construct maps to the CLR. When in doubt, run `jsnet emit file.jsn` to see the C# the compiler generates.

## Files, modules and entry points

- Every `.jsn` file in a project is compiled together. Declarations are visible across files without imports.
- Top-level **functions and variables** of `utils.jsn` become static members of a class named `Utils` (or `UtilsModule` if a type is already called `Utils`) in the file's namespace. They are imported everywhere via `global using static`.
- The namespace is `RootNamespace` + folder path (like C#), or set explicitly with `namespace My.Company;`.
- **Programs**: exactly one file may contain top-level statements. They become `Main` (async, with `args` available) and the process waits for pending `setTimeout`/`setInterval` timers before exiting, like Node.js.
- A program without top-level statements can declare `export function main(args: string[]): void`; it becomes the CLR entry point. Use `@STAThread` for WinForms/Avalonia.
- **Libraries** (`OutputType=Library`) may not contain top-level statements.
- Everything top-level is `public` unless marked `private`/`internal`. `export` is accepted and documents intent.

## Variables and types

```jsnet
let count = 0;                 // inferred: int
let price: number = 9.99;      // number → double
const name = "Ada";            // string
dynamic anything = 42;         // late-bound
let maybe: string | null = null;
```

| JS.Net | CLR |
|---|---|
| `number` | `double` |
| `int`, `long`, `float`, `decimal`, `byte`, `short`, `char` | same CLR primitive |
| `bool`, `boolean` | `bool` |
| `string` | `string` |
| `object`, `unknown` | `object` |
| `any`, `dynamic` | `dynamic` |
| `void` | `void` |
| `T[]` | `T[]` (fixed-size CLR array) |
| `Array<T>` | `List<T>` (growable) |
| `Map<K, V>`, `Record<K, V>` | `Dictionary<K, V>` |
| `Set<T>` | `HashSet<T>` |
| `Promise<T>` / `Promise<void>` | `Task<T>` / `Task` |
| `Iterable<T>`, `Generator<T>` | `IEnumerable<T>` |
| `(a: A) => R` / `() => void` | `Func<A, R>` / `Action` |
| `[A, B]` | `(A, B)` value tuple |
| `T \| null` | `T?` |

Notes:

- **Integer literals are `int`** (CLR semantics), so `7 / 2` is `3`. The compiler warns (JSN2003) on integer division. Use `number`, a decimal point (`7.0 / 2`) or let the compiler infer: a variable or field initialised with an integer but later assigned a fractional value is automatically typed `double`.
- Numeric suffixes from .NET work: `10L`, `9.5f`, `19.99m`, `1d`.
- Unannotated parameters and un-inferable return types fall back to `dynamic` with warning JSN2002. Annotate hot paths.
- Unions other than `T | null` become `object` (warning JSN2005). String-literal unions (`"a" | "b"`) become `string`.

## Functions

```jsnet
function greet(name: string, greeting = "Hello"): string { return `${greeting}, ${name}!`; }
function total(...values: number[]): number { return values.reduce((s, v) => s + v, 0); }
function find(id: int, fallback?: string): string { return fallback ?? "none"; }
const square = (x: number) => x * x;
const makeCounter = () => { let n = 0; return () => ++n; };
function* range(n: int): Iterable<int> { for (let i = 0; i < n; i++) yield i; }
async function load(url: string): Promise<string> { const r = await fetch(url); return await r.text(); }
```

- Return types are inferred when possible (`function add(a: number, b: number) { return a + b; }` → `double`).
- Rest parameters become `params`. Optional parameters become nullable with a default.
- Non-constant defaults (`items: string[] = []`) are applied at the start of the call.
- Destructured parameters work: `function show({ name, age }: Person) { … }`.
- `async` functions return `Task`/`Task<T>`. An `async` method annotated `: void` becomes `async void` (for event handlers).
- Arrow functions are C# lambdas; when stored in a variable without annotation, untyped parameters become `dynamic` so the variable has a delegate type.

## Classes

```jsnet
export class Animal {
    static count = 0;                         // public static property
    protected sound = "...";                  // private/protected members are fields
    constructor(public readonly name: string) { Animal.count++; }   // parameter property
    speak(): string { return `${this.name} says ${this.sound}`; }
    get label(): string { return this.name.toUpperCase(); }
    set label(v: string) { /* … */ }
    toString() { return `Animal(${this.name})`; }   // also overrides ToString()
}

export class Dog extends Animal {
    constructor(name: string) { super(name); this.sound = "woof"; }
    speak(): string { return "🐶 " + super.speak(); }    // override inferred
}
```

- Public fields become auto-properties (`{ get; set; }`; `readonly` → `{ get; }`) so they work with data binding and serializers.
- `super(...)` must be the first statement of a constructor; it becomes `: base(...)`.
- Overriding a method of a JS.Net base class is detected automatically. Methods of classes that are extended in the project are emitted `virtual`. For .NET base classes write `override` explicitly (`protected override async ExecuteAsync(...)`).
- Modifiers: `public private protected internal static readonly abstract override virtual sealed partial async`. Classes are always emitted `partial`, so Razor, WPF and Avalonia code-behind merges automatically.
- `struct Name { … }` and `record Name { … }` declare CLR structs and records.
- `#private` names are supported (emitted as private fields).

## Interfaces, generics and enums

```jsnet
export interface Entity { id: int; }
export interface Product extends Entity { name: string; price: number; }

const p: Product = { id: 1, name: "Pen", price: 2.5 };   // data-only interfaces accept object literals

export class Repository<T extends Entity> {
    private items = new Map<int, T>();
    add(item: T): void { this.items.set(item.id, item); }
}

function first<T>(xs: T[]): T { return xs[0]; }
const s = first<string>(["a"]);

enum Color { Red, Green = 5, Blue }          // CLR enum
enum Status { Open = "open", Closed = "closed" }   // string constants
```

## Objects and arrays

- Object literals become **anonymous types** (fast, typed, immutable): `const u = { name: "Ada", age: 36 }`.
- Typed object literals create the target type: `const p: Point = { x: 1, y: 2 }` → `new Point { x = 1, y = 2 }`; for a data-only interface a hidden implementation is used; for `Map`/`Record` targets a dictionary initializer is generated.
- Annotate `dynamic` for a **mutable** JavaScript-style object: `let o: dynamic = { a: 1 }; o.b = 2;` (backed by `JsObject`).
- Array literals without a target type become `List<T>`; with a target type they use C# collection expressions (`const xs: int[] = [1, 2]`). Spread works: `[...a, 4]`, `f(...args)`.
- `new T[size]` creates a CLR array; `new Array<T>()` a list.

## Control flow

`if/else`, `for`, `for...of` (→ `foreach`), `for...in` (keys), `for await`, `while`, `do...while`, `switch`, `break`, `continue`, `return`, `throw`, `try/catch/finally`, `using`.

- **Truthiness**: conditions use JavaScript rules (`if (name)`, `if (!list.length)`, `if (obj)`); for `bool` values this costs nothing.
- `a || b` and `a && b` return values like JavaScript (`const n = input || "anon"`).
- `switch` supports fall-through, stacked cases and non-constant cases (`switch (true) { case score > 90: … }`).
- `try { } catch (e: IOException) { } catch (e) { }` — multiple typed catch clauses are allowed; an untyped `catch (e)` catches `Exception`. Any value can be thrown (`throw "oops"` throws an `Error`).
- `using (const f = File.OpenRead(p)) { … }` and `using x = expr;` dispose deterministically.

## Destructuring, spread and templates

```jsnet
const { name, role } = user;
const [first, second] = list;
[a, b] = [b, a];                                // tuple swap
for (const [key, value] of map) { … }
console.log(`Total: ${total.toFixed(2)} (${items.length} items)`);
```

## Attributes and decorators

Both TypeScript decorators and C# attribute syntax lower to CLR attributes:

```jsnet
@ApiController
@Route("api/[controller]")
export class TodosController extends ControllerBase {
    [HttpGet("{id}")]
    get(id: int): object { … }
}

@SupplyParameterFromQuery({ Name: "run" })     // a trailing object literal becomes named arguments
autoRun: string | null = null;
```

`@Service` / `@Service(ServiceScope.Singleton)` registers a class in dependency injection (see the [runtime guide](interop-and-runtime.md#dependency-injection)).

## Imports

```jsnet
import System.Text;                                  // using System.Text;
import static System.Math;                           // using static System.Math;
import { List, Dictionary as Dict } from "System.Collections.Generic";   // type aliases
import * as IO from "System.IO";                     // namespace alias
import Box = System.Windows.Shapes.Rectangle;        // alias (resolves name clashes)
import { Greeter } from "./Greeter";                  // relative module import: accepted, no-op
type Id = int;                                       // global alias
```

## AI-native declarations

```jsnet
ai function summarize(text: string): Promise<string> {
    `Summarize in one sentence: ${text}`
}

prompt Translator {
    text: string;
    language: string;
    output: string;
    template: `Translate "${text}" into ${language}.`;
}

console.log(await summarize(article));
console.log(await Translator.run("Terima kasih", "Japanese"));
```

Requires the `JSNet.AI` package (`<JSNetEnableAI>true</JSNetEnableAI>`). Configure a model with `AiRuntime.configure(chatClient)` using any `Microsoft.Extensions.AI` `IChatClient` (OpenAI, Azure OpenAI, Ollama, Semantic Kernel). Without configuration an offline echo client answers. Non-string return types are requested as JSON and deserialized.

## Known limitations (v0.1)

- No object type literals (`{ a: string }` as a type) — declare an interface.
- No class expressions, object spread (`{ ...o }`), labelled statements or regex literals (use `new Regex(...)`).
- `this` inside a `function` expression refers to the enclosing class (arrow-function semantics).
- `Map.get` on a missing key returns the default value (`0`, `null`); use `has` first for value types.
- Iterating a string with `for...of` yields `char` values.

See [PLAN.md](../../PLAN.md) for the roadmap.
