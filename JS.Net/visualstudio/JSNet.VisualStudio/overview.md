# JS.Net for Visual Studio

**JS.Net** is a language with JavaScript/TypeScript syntax that compiles to .NET IL. This extension adds `.jsn` language support to Visual Studio 2022 (17.14+) and Visual Studio 2026:

- Diagnostics from the JS.Net compiler and project-wide binding
- Completion, including every .NET member of the inferred type
- Hover with .NET signatures, go to definition, find references
- Rename and quick fixes
- Semantic colouring

## Requirements

Install the JS.Net CLI, which provides the language server:

```
dotnet tool install -g JSNet.Cli
```

Or set the `JSNET_PATH` environment variable to a `jsnet` executable.

## Get started

```
dotnet new install JSNet.Templates
dotnet new jsnet -n HelloJsNet
```

Documentation: https://github.com/DotNetVibeCoderz/Vibe_Framework/tree/main/JS.Net · Try it in the browser: https://dotnetvibecoderz.github.io/Vibe_Framework/

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
