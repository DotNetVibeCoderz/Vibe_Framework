# .NET language & BCL support

What C# code runs on the RustNet interpreter (runtime v0.4, RNX format v3).

## Supported

| Area | Details |
|---|---|
| Core IL | full integer/float arithmetic, branches, arrays, objects, statics + `.cctor`, strings |
| Inheritance | base classes, inherited fields, base ctor calls, `virtual`/`override` with true dynamic dispatch (RNX v3 override tables), `ToString`/`Equals`/`GetHashCode` overrides |
| Interfaces | user interfaces (incl. interface inheritance), implicit + explicit implementations, interface-typed calls and `is`/casts |
| Casts | `is` / `as` / cast walk the full class chain and interface lists |
| Generics (user) | user generic classes and methods via erasure (type args fold to object) |
| async/await | full await flow on the green-thread scheduler: `Task`/`Task<T>`-returning async methods, `Task.Delay`, `Task.FromResult`, `Task.Yield`, exception propagation through faulted tasks, cooperative `Wait()`/`Result`. Compile with the **Debug** configuration (class state machines); Release struct machines are not supported |
| Exceptions | `try` / `catch` / `finally`, **`catch when` filters**, `throw`, `rethrow`, runtime faults become catchable managed exceptions |
| Delegates & lambdas | `Func<>`/`Action<>`, closures, method groups (`ldftn`/`ldvirtftn`) |
| Generics (BCL) | `List<T>`, `Dictionary<K,V>`, `Queue<T>`, `Stack<T>`, `KeyValuePair`, `Tuple` — arity-canonicalized, type-argument-agnostic dispatch |
| `foreach` | arrays, `List<T>`, `Dictionary<K,V>` (struct enumerators handled) |
| LINQ-to-objects | `Where Select Sum Count Min Max Average Any All First(OrDefault) Last(OrDefault) Take Skip OrderBy(Descending) Distinct Reverse Contains ElementAt ToArray ToList Range` |
| Strings | interpolation (`$"..."`), `Concat` (any part count — 5+ parts lower to an inline-array `ReadOnlySpan` and are handled), `Substring IndexOf Replace Trim/TrimStart/TrimEnd Split StartsWith ToCharArray`, `StringBuilder` (incl. `Append(char)`) |
| Text/encoding | `Encoding.UTF8`, `Convert` (incl. Base64), `BitConverter` (incl. `DoubleToInt64Bits`), `char` classification, numeric `Parse`/`ToString` (bool prints `True`/`False`) |
| Regex | `System.Text.RegularExpressions.Regex` subset: literals, classes, `. * + ? | ( ) ^ $ \d \w \s`, `IsMatch Match Replace Split` (compact backtracking engine in Rust) |
| Threading | green threads (`Thread`), `Interlocked`, `Monitor` (no-op: cooperative scheduler), `Task` subset (`Run`, `Delay`, `Wait`), `RustNet.Timers.Timer` |
| Math/random | `System.Math`, `System.Random` |
| Serialization | `RustNet.Serialization` JSON / XML / binary DOM (reflection-free) |
| Streams | `RustNet.IO.MemoryStream / FileStream / BinaryPacker` |

## Partially supported

- **Reflection**: `object.GetType()` returns a `System.Type`; `Type.Name`,
  `FullName`, `Namespace`, `BaseType` (walks the type hierarchy) and
  `ToString()` work for user types and BCL values. **Method enumeration**
  works too: `Type.GetMethods()` → `MethodInfo[]`, `Type.GetMethod(name)`,
  `MethodInfo.Name`, and `==`/`!=` on reflection handles. **`typeof(T)`** works
  (via `ldtoken` + `Type.GetTypeFromHandle`) and returns the same `System.Type`
  identity as `GetType()` (so `typeof(T) == obj.GetType()`). **`MethodInfo.Invoke`**
  works — call a user method reflectively with a boxed `object[]` of arguments
  (value-type args are unboxed for you; a value-type return flows back as
  `object`; void returns `null`). **Field enumeration** works too:
  `Type.GetFields()` → `FieldInfo[]` (public, own + inherited),
  `Type.GetField(name)`, `FieldInfo.Name`, and `FieldInfo.GetValue(obj)` /
  `SetValue(obj, value)` for instance and static fields (RNX v5 carries the
  field descriptors). **Property enumeration** works: `Type.GetProperties()` →
  `PropertyInfo[]`, `Type.GetProperty(name)`, `PropertyInfo.Name`, and
  `GetValue(obj)` / `SetValue(obj, value)` (discovered from `get_`/`set_`
  accessors and dispatched through them). **Custom attributes** on **types**
  work: `Type.GetCustomAttributes(bool)` / `GetCustomAttributes(Type, bool)`
  instantiate the user-defined attributes applied to a type (constructor
  positional args + named field/property args; arg constants of type
  bool/char/int/long/float/double/string/enum). Attributes on methods/fields,
  and framework attributes, are not surfaced.

## Not (yet) supported — MetadataProcessor reports these clearly

- reflection attributes on **methods/fields** (type-level custom attributes
  are supported) / `ldtoken` of a **method or field** (array initializers via
  `InitializeArray`; `ldtoken` of a **type** for `typeof`, `MethodInfo.Invoke`,
  `GetFields`/`FieldInfo`, `GetProperties`/`PropertyInfo`, and type
  `GetCustomAttributes`, are supported)
- fault handlers (`catch`-less `fault` blocks; C# never emits these)
- catch-by-exception-TYPE narrowing: catch clauses match every exception
  (exception objects are message strings) — use `catch when` filters on
  `ex.Message` to discriminate
- generic params `ReadOnlySpan<T>` **user** methods: the inline-array
  lowering is modelled (the buffer becomes a heap array), and `string.Concat`
  with 5+ parts works, but consuming the span through its indexer inside a
  user method is not wired up yet
- `ref` **locals** passed across method calls (a byref to a local/arg is
  same-frame only) — but `ref` to a **field or array element** works across
  calls now (ldind/stind implemented); interpolated-string handlers and
  struct enumerators work because they stay in-frame)
- async methods compiled in **Release** configuration (struct state
  machines); build apps with Debug (the default `dotnet build`)
- user-defined value types with by-value copy semantics (classes cover
  the dialect); `Task.Run(Func<T>)` returns a value-less Task

## Dialect guidance for app code

Prefer: concrete classes, BCL collections, delegates, interpolation,
`StringBuilder`, explicit serializer DOMs. The templates under
`templates/` are the reference dialect.
