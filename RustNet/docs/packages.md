# RustNet Package Manager

NuGet-like distribution for community sensor/actuator drivers.

## Package layout

A package is a zip archive with the `.rnpkg` extension containing an
`rnpkg.json` manifest plus source/asset files:

```json
{
  "name": "rustnet-driver-neopixel",
  "version": "0.1.0",
  "description": "WS2812/NeoPixel driver for RustNet",
  "authors": ["you"],
  "files": ["*.cs"],
  "dependencies": { "rustnet-core-lib": ">=0.1" }
}
```

Driver packages ship C# source that compiles into the consuming app (the
MetadataProcessor merges everything into one RNX), so there is no binary
compatibility problem across runtime versions.

## Registry

The registry is a directory of `.rnpkg` files — a local folder by default
(`~/.rustnet/registry`), a network share or a synced bucket for teams.
Override with `RUSTNET_REGISTRY`.

## Commands

```bash
rustnet pkg init <name>      # write rnpkg.json here
rustnet pkg pack             # zip -> <name>-<version>.rnpkg
rustnet pkg publish          # copy into the registry
rustnet pkg list             # everything in the registry
rustnet pkg search <term>    # name/description search
rustnet pkg install <name> [--version v]   # resolve + extract into ./packages/
```

`install` resolves the **transitive dependency closure** and extracts every
required package into `./packages/<name>/`, dependencies first. Versions are
compared as semver (`0.10.0 > 0.9.0`); a dependency's version string is a
**minimum** — the highest available version at least that high is chosen, and a
diamond dependency is installed once at a version satisfying all requirements.
`--version` pins the root package to an exact version. Installation fails
clearly if a required package (or a satisfying version) is missing.

A sample package lives in `packages/rustnet-driver-neopixel/`.
