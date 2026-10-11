# Limitations

Rustzor runs your IL on RustNetRuntime, not on Microsoft's runtime. Most C# runs unchanged; this page lists what does not, so you can plan around it. When you hit something missing, the error says so plainly: `no implementation for System.X::Method(...)`.

## The runtime

RustBCL, the base class library, covers what apps use most: strings and formatting (including culture-invariant number and date formats), collections and LINQ, `Task`, `async`/`await` and cancellation, reflection (types, properties, attributes, `Activator`), `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, `Random`, `Math`, regular expressions, `StringBuilder`, `Encoding` (UTF-8 and ASCII), `BitConverter`, `Convert` (including base64), spans and records.

Not available:

- **File system and processes**: `System.IO.File`, `Directory`, `Process`. Use `Rustzor.Browser.FileSystem` and `Rustzor.Browser.Storage`.
- **`HttpClient` and sockets**: use `FetchClient`, `WebSocketClient`, `Rustzor.Net.SignalR` and `Rustzor.Net.GrpcWeb`.
- **`System.Text.Json`**: use `Rustzor.Interop.Json`.
- **Threads**: the browser gives the page one thread. `Task.Run` runs on it; use `Rustzor.Browser.Workers` for parallel work in JavaScript.
- **Cultures**: formatting is culture-invariant. Format numbers and dates explicitly for a locale (`ToString("N0").Replace(',', '.')` for rupiah).
- **Regular expressions**: everything except `RegexOptions.RightToLeft`.
- **`CancellationTokenSource.CancelAfter`**: cancel from a timer instead (`Scheduler.SetTimeout(cts.Cancel, ms)`).

The runtime's own list, kept current, is in [Vibe_Runtime/RustNetRuntime/docs/limitations.md](https://github.com/DotNetVibeCoderz/Vibe_Runtime/blob/main/RustNetRuntime/docs/limitations.md).

## The component model

See [components](components.md#not-supported-yet): no `DataAnnotationsValidator`, `InputRadio`, `InputFile`, `Virtualize`, `HeadOutlet`/`SectionOutlet`, and route constraints beyond `int`, `long`, `bool` and the number types are not enforced.

## Tooling

- Reloading keeps no state: `rustzor serve` rebuilds and reloads the whole page on every save (see [tooling](tooling.md)), so in-memory state starts over.
- Debugging is through the browser console: unhandled exceptions are printed with their managed stack trace.

## Performance

The interpreter is fast enough for business apps, dashboards and 2D games at 60 frames a second, but tight numeric loops run several times slower than on .NET. For those, the [AOT mode](aot.md) compiles qualifying static methods to WebAssembly.

The first load downloads about 1.9 MB for the runtime plus your assemblies; both are cached afterwards.
