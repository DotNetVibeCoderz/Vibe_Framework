# Publishing RustNet

The managed API ships as a single NuGet package, **[RustNet]**, and CI does
the publishing. This page is what to know before cutting a release, and what
to do when something about the package looks wrong.

[RustNet]: https://www.nuget.org/packages/RustNet

## What is in the package

One package, sixteen assemblies: everything under `dotnet/src/RustNet.*`, with
their XML documentation, plus a symbol package. It has **no dependencies** —
these libraries reference nothing but each other.

They are not split into sixteen packages because splitting would buy nothing.
Every member is an `[InternalCall]` façade whose behaviour lives in the Rust
interpreter, they are versioned by one `<Version>` in
`dotnet/Directory.Build.props`, and an application that uses one of them almost
always uses several.

The gathering is done by `dotnet/src/RustNet/RustNet.csproj`, which has no code
of its own — it project-references the sixteen with `PrivateAssets="all"` (so
they do not become package *dependencies*, which would point at packages that
do not exist) and copies their build output into `lib/net10.0`.

## Cutting a release

1. Set the version in `dotnet/Directory.Build.props` and commit it. That value
   is what a local `dotnet pack` produces; CI overrides it from the tag, and
   the two disagreeing is confusing later.
2. Tag it and push the tag:

   ```bash
   git tag rustnet-v0.2.0
   git push origin rustnet-v0.2.0
   ```

3. `Publish RustNet to NuGet` runs: the full CI suite first, then pack and
   push. It appears on nuget.org within a few minutes of the run going green.

To rehearse without publishing, run the workflow manually from the Actions tab
— **Run workflow**, type a version, leave **dry run** ticked. It builds and
checks the package and stops before the push.

### Why the tag is `rustnet-v`, not `v`

RustNet lives in a subdirectory of the Vibe_Framework monorepo. A bare `v1.2.3`
tag would fire this workflow for a release of any other project in that
repository, and publish RustNet under that project's version number. nuget.org
lets a version be unlisted but never re-uploaded, so that mistake is permanent.

## Where the workflows live

`.github/workflows/rustnet-ci.yml` and `.github/workflows/rustnet-publish.yml`
belong at the **repository root** — GitHub does not look for workflows
anywhere else, so a copy under `RustNet/.github/` never runs. Both locate the
RustNet directory themselves, so the same files work in a standalone checkout
and in the monorepo.

`rustnet-publish.yml` calls `rustnet-ci.yml` as a reusable workflow rather than
restating its steps, so the tests that gate a release cannot drift from the
tests that run on every push.

## Secrets

| | |
|---|---|
| `NUGET_API_KEY` | An nuget.org API key scoped to push `RustNet`. The publish job fails with a clear message if it is missing rather than letting `dotnet nuget push` report a confusing 401. |

## The check that earns its place

CI packs on every push and then counts the assemblies inside the result:

```bash
unzip -Z1 artifacts/*.nupkg 'lib/*/RustNet.*.dll' | wc -l   # must be >= 16
```

This is not ceremony. `IncludeBuildOutput=false` looks like the right way to
keep an empty `RustNet.dll` out of the package — and it also gates the whole
build-output collection that the copy target hooks into, so it silently drops
the sixteen real assemblies too. The result was a 5 KB package that restored
without a warning and provided nothing. No test fails on that; only counting
the files finds it.

## Building the package locally

```bash
dotnet pack dotnet/src/RustNet/RustNet.csproj -c Release -o artifacts
```

To try it as a consumer would, point a scratch project at the folder:

```xml
<RestoreSources>$(RestoreSources);/path/to/artifacts</RestoreSources>
<PackageReference Include="RustNet" Version="0.1.0" />
```

Bump the version between attempts, or clear `~/.nuget/packages/rustnet` — NuGet
caches a version by number and will not notice that the file changed.
