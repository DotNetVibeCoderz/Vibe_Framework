<#
.SYNOPSIS
  Builds, tests and packs every JS.Net NuGet package into artifacts/packages.
  Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
.EXAMPLE
  ./build/pack.ps1                          # Release build + tests + pack at the default version
  ./build/pack.ps1 -Version 0.2.0-preview.1
  ./build/pack.ps1 -SkipTests -SkipRust
#>
param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [switch]$SkipTests,
    [switch]$SkipRust
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$versionArgs = @()
if ($Version) {
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$') { throw "'$Version' is not a semantic version" }
    $versionArgs = @("-p:JSNetVersion=$Version")
}
$effectiveVersion = if ($Version) { $Version } else { "0.2.0" }

if (-not $SkipRust -and (Get-Command cargo -ErrorAction SilentlyContinue)) {
    Write-Host "==> Building native jsnet-tools (Rust)" -ForegroundColor Cyan
    Push-Location rust/jsnet-tools
    cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
    if (-not $SkipTests) { cargo test --release; if ($LASTEXITCODE -ne 0) { throw "cargo test failed" } }
    Pop-Location
}

Write-Host "==> Building solution ($Configuration, $effectiveVersion)" -ForegroundColor Cyan
dotnet build JSNet.slnx -c $Configuration @versionArgs
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (-not $SkipTests) {
    Write-Host "==> Running tests" -ForegroundColor Cyan
    dotnet test tests/JSNet.Tests -c $Configuration --no-build @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

# Templates default their `sdkVersion` parameter to the version being released.
Write-Host "==> Staging templates at $effectiveVersion" -ForegroundColor Cyan
$staging = Join-Path $root "templates/.staging/JSNet.Templates"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force (Split-Path $staging) | Out-Null
Copy-Item -Recurse templates/JSNet.Templates $staging
Get-ChildItem $staging -Recurse -Filter template.json | ForEach-Object {
    $json = Get-Content $_.FullName -Raw
    # `sdkVersion` is the only parameter with a defaultValue in the JS.Net templates.
    $json = $json -replace '"defaultValue": "[^"]*"', "`"defaultValue`": `"$effectiveVersion`""
    [IO.File]::WriteAllText($_.FullName, $json)
}

Write-Host "==> Packing" -ForegroundColor Cyan
if (Test-Path artifacts/packages) { Remove-Item artifacts/packages/*.nupkg -ErrorAction SilentlyContinue }
$packages = @(
    "src/JSNet.Runtime/JSNet.Runtime.csproj",
    "src/JSNet.AI/JSNet.AI.csproj",
    "src/JSNet.Analyzers/JSNet.Analyzers.csproj",
    "src/JSNet.Web/JSNet.Web.csproj",
    "src/JSNet.EntityFramework/JSNet.EntityFramework.csproj",
    "src/JSNet.Blazor/JSNet.Blazor.csproj",
    "src/JSNet.Reactive/JSNet.Reactive.csproj",
    "src/JSNet.Sdk/JSNet.Sdk.csproj",
    "src/JSNet.Cli/JSNet.Cli.csproj",
    "$staging/JSNet.Templates.csproj"
)
foreach ($p in $packages) {
    dotnet pack $p -c $Configuration -o artifacts/packages @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "pack failed: $p" }
}
Get-ChildItem artifacts/packages/*.nupkg | ForEach-Object { Write-Host "  $($_.Name)" }
