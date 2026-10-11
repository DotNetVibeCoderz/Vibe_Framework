# Build modes: interpreted and AOT

Rustzor has two build modes. Both produce a static site and run on the same runtime.

| | Interpreted (default) | AOT (`-p:RustzorAot=true`) |
| --- | --- | --- |
| What runs your code | RustNetRuntime's IL interpreter | Hot numeric methods: compiled WebAssembly. Everything else: the interpreter |
| Build time | Fast | Adds a compile step |
| Download | `rustzor.wasm` + your DLLs | The same, plus a small `app.aot.wasm` |
| Reflection, `dynamic` dispatch, all of the BCL | Yes | Yes (those methods stay interpreted) |
| Best for | Business apps, forms, dashboards, development | Games, simulations, image processing, maths |

## Turning AOT on

```bash
dotnet build -p:RustzorAot=true
dotnet publish -c Release -p:RustzorAot=true
```

or in the project file:

```xml
<PropertyGroup>
  <RustzorAot>true</RustzorAot>
</PropertyGroup>
```

## What gets compiled

The AOT compiler (`Rustzor.Build aot`) looks at every static method in your own assemblies (not `Rustzor.*`, `System.*` or `Microsoft.*`) and compiles a method when:

- its parameters, locals and result are primitives (`bool`, `char`, the integer types, `float`, `double`) or one-dimensional arrays of `int`, `long`, `float`, `double` or `byte`;
- its body uses arithmetic, comparisons, conversions, branches, `switch`, array access, `System.Math` and calls to other compiled methods;
- it has no exception handlers, and does not touch objects, strings, fields or allocation.

Anything else is declined and keeps running on the interpreter, as is any method that calls a declined one. Because a declined method runs exactly as before, AOT can change speed but never results. Array arguments are copied into the compiled module's memory and back; index, divide-by-zero and overflow faults become the same managed exceptions the interpreter throws.

The build writes `obj/<config>/net10.0/rustzor-aot/aot-report.txt`, listing every method as compiled or interpreted, with the reason. Read it to see which of your hot paths qualify; often a small refactor (pass arrays and numbers into a static helper instead of a list of objects) moves a loop into compiled code.

```csharp
// Compiled: static, numbers and arrays only.
static void Step(double[] x, double[] v, double dt, double g)
{
    for (var i = 0; i < x.Length; i++)
    {
        v[i] += g * dt;
        x[i] += v[i] * dt;
    }
}
```

## Which mode runs

When `boot.json` names an AOT module, `rustzor.js` loads it after the assemblies. To compare, start the page with the interpreter only:

```js
start({ interpretOnly: true });
```
