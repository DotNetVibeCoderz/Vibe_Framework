# RustNet community driver packages

Each directory here is a **source package** for `rustnet pkg`: an `rnpkg.json`
manifest plus the `.cs` files that get copied into an application's
`packages/` folder by `rustnet pkg install <name>`. They are compiled into the
application and run on the device's IL interpreter, so they are written in the
RustNet C# dialect (below), not full desktop C#.

```bash
cd packages/rustnet-driver-bme280 && rustnet pkg pack && rustnet pkg publish
cd my-app && rustnet pkg install rustnet-driver-bme280   # pulls rustnet-driver-bus too
```

## The shared bus package

Every driver depends on **`rustnet-driver-bus`** and talks to hardware only
through its device classes — `I2cDevice`, `SpiDevice`, `DigitalOutput`,
`DigitalInput`, `PwmOutput`, `AnalogInput`, `SerialDevice` — and waits only
through `Timing.SleepMs` / `Timing.NowMs`. Their transfer methods are
`virtual`, and the package ships register-level simulators (`SimI2cDevice`,
`SimSpiDevice`, `SimDigitalOutput`, …), so every driver can be exercised with
no hardware: in a desktop unit test, and on the virtual device.

A driver offers two constructor shapes: a convenience one taking bus numbers
and pins, and one taking the device objects (which is what tests use):

```csharp
public Bme280(int bus, int address) : this(new I2cDevice(bus, address)) { }
public Bme280(I2cDevice device) { _dev = device; }
```

## How drivers are verified

Drivers are grouped by category under `dotnet/tests/Drivers/<Category>.App`
(on-device smoke app) and `<Category>.Tests` (xunit). Both compile the same
sources listed in `<Category>.App/Packages.props`. Each driver has a
`<Driver>Smoke.Run()` scenario that sets up simulators holding **datasheet
values** (worked examples, register defaults, documented conversions) and
checks the driver's results with `Check.Equal/Near/True`. The xunit project
runs every scenario on the desktop CLR; the E2E suite compiles the app to RNX
and runs it on the virtual device, where any `FAIL` line fails the build.

## The RustNet C# dialect

The interpreter runs most of C#, but these constructs either do not compile
to RNX or behave differently. The MetadataProcessor reports the first group.

- **No array initializers**: `new byte[] { 1, 2 }`, `new[] { … }`, collection
  expressions `[ … ]`, and `static readonly` tables with initializers all lower
  to `InitializeArray`, which is unsupported. Fill arrays element by element,
  or keep a lookup table as a hex `string` constant and decode it on first use.
- **Classes only** — no user `struct`s (by-value copies are not modelled).
- **No `ref`/`out` parameters** between methods. Return an object or an array.
- **Catch clauses are untyped**: every `catch` catches everything. Use
  `catch (Exception ex) when (ex.Message …)` to discriminate.
- **No `event` fields**: expose `Action<…>?` fields and invoke with `?.Invoke`.
- **`double.NaN` comparisons**: use `double.IsNaN(x)` (supported) rather than relying on
  NaN propagation through `Math` helpers.
- Avoid `switch` on strings, tuples, pattern-matching declarations
  (`x is Foo f`), `Span`/`stackalloc`/`unsafe`, `params`, and format strings
  (`ToString("F2")`, `string.Format`). Interpolation of plain values is fine;
  a `bool` interpolates as `1`/`0`.
- `List<byte>.ToArray()` yields an object array — never hand it to the HAL as a
  `byte[]`. Build `byte[]` buffers directly.
- `Math` provides `Sqrt Abs Sin Cos Tan Asin Acos Atan Atan2 Sinh Cosh Tanh Exp
  Log Log10 Log2 Pow Floor Ceiling Round Truncate Sign Min Max Clamp Cbrt`.
  `long`, `uint` and `ulong` arithmetic all work.

**Cost model.** A host call (one I2C/SPI transfer, one GPIO write) costs about
220 µs on a 400 MHz K210 and a managed method call about 65 µs. Batch bus
traffic into one `byte[]` per transaction, and never toggle GPIO per pixel or
per bit from C# when the HAL offers a block operation.
