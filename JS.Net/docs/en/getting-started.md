# Getting started with JS.Net

> JS.Net — created by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/getting-started.md)

## 1. Install

You need the **.NET 10 SDK**.

```bash
dotnet new install JSNet.Templates     # dotnet new templates
dotnet tool install -g JSNet.Cli       # the jsnet command (optional but recommended)
```

`JSNet.Sdk` and `JSNet.Runtime` are restored automatically by projects created from the templates.

> **Before the packages are on nuget.org** (or when working from a source checkout) build them locally and add the folder as a package source:
>
> ```bash
> pwsh build/pack.ps1                                        # → artifacts/packages
> dotnet new install artifacts/packages/JSNet.Templates.0.1.1.nupkg
> dotnet nuget add source <repo>/artifacts/packages -n jsnet-local
> ```

## 2. Your first program

```bash
dotnet new jsnet -n Hello
cd Hello
dotnet run
```

`Program.jsn`:

```jsnet
import { Greeter } from "./Greeter";

const greeter = new Greeter("JS.Net");
console.log(greeter.greet());

async function fetchGreeting(name: string): Promise<string> {
    await delay(100);
    return `Hello, ${name}!`;
}

console.log(await fetchGreeting("World"));
```

`Greeter.jsn`:

```jsnet
export class Greeter {
    constructor(private readonly name: string) {}

    greet(): string {
        return `Welcome to ${this.name}`;
    }
}
```

Every `.jsn` file in the project folder is compiled. Files see each other's declarations automatically; `import { X } from "./file"` is accepted for readability but not required.

## 3. Scripts without a project

```bash
jsnet run script.jsn -- arg1 arg2     # compile in memory and run
jsnet emit script.jsn                 # print the generated C#
jsnet check src/                      # diagnostics only
```

## 4. Other application types

```bash
dotnet new jsnet-webapi   -n Api        # ASP.NET Core minimal API + controller + @Service DI
dotnet new jsnet-blazor   -n Web        # Blazor Web App with .razor.jsn code-behind
dotnet new jsnet-wpf      -n Desktop    # WPF with .xaml.jsn code-behind
dotnet new jsnet-winforms -n Forms      # Windows Forms written in code
dotnet new jsnet-avalonia -n CrossApp   # Avalonia (Windows/macOS/Linux)
dotnet new jsnet-worker   -n Worker     # background service
dotnet new jsnet-classlib -n Lib        # library usable from C#
dotnet new jsnet-xunit    -n Tests      # xUnit tests written in JS.Net
```

## 5. Publish

Everything `dotnet publish` supports works, including NativeAOT:

```bash
dotnet publish -c Release -r win-x64 -p:PublishAot=true
```

> NativeAOT needs the platform linker (on Windows: Visual Studio "Desktop development with C++"). Code that uses `dynamic`, `JSON.parse` without a type, or `console.log` on arbitrary objects relies on reflection; prefer typed code in AOT apps.

## 6. Editor support

- **VS Code**: install `artifacts/vscode/jsnet-0.1.0.vsix` (*Extensions → … → Install from VSIX*). It needs the `jsnet` CLI on your PATH.
- **Visual Studio / Rider**: `.jsn` files build and debug through the SDK; breakpoints and stack traces point at `.jsn` lines thanks to `#line` mapping. Dedicated editor integration is on the [roadmap](../../PLAN.md).

## Next

- [Language guide](language-guide.md)
- [.NET interop & runtime](interop-and-runtime.md)
- [Tooling](tooling.md)
