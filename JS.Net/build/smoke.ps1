<#
.SYNOPSIS
  End-to-end check of the packed NuGet packages: installs the templates and the jsnet tool from
  artifacts/packages only, creates a project from every template, builds them, and runs the console app,
  the worker and a script. This is what users get from nuget.org.
.EXAMPLE
  ./build/smoke.ps1 -Version 0.2.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$packages = Join-Path $root "artifacts/packages"
$work = Join-Path ([IO.Path]::GetTempPath()) ("jsnet-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $work | Out-Null
Set-Location $work

function Check([string]$what) { if ($LASTEXITCODE -ne 0) { throw "smoke test failed: $what" } }

@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="jsnet-local" value="$packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content nuget.config -Encoding utf8

Write-Host "==> Installing JSNet.Templates $Version" -ForegroundColor Cyan
dotnet new install (Join-Path $packages "JSNet.Templates.$Version.nupkg") --force
Check "template install"

$templates = @("console", "classlib", "webapi", "web", "blazor", "worker", "xunit", "avalonia")
if ($IsWindows -or $env:OS -eq "Windows_NT") { $templates += @("wpf", "winforms") }

foreach ($t in $templates) {
    Write-Host "==> jsnet-$t" -ForegroundColor Cyan
    dotnet new "jsnet-$t" -n "Smoke_$t" -o "Smoke_$t"
    Check "dotnet new jsnet-$t"
    dotnet build "Smoke_$t" -c Release --nologo
    Check "build jsnet-$t"
}

Write-Host "==> Running the console app" -ForegroundColor Cyan
$out = dotnet run --project Smoke_console -c Release --no-build
Check "run console"
$out | Write-Host
if (-not ($out -match "Hello, World!")) { throw "console app output was unexpected" }

Write-Host "==> Running the xUnit project" -ForegroundColor Cyan
dotnet test Smoke_xunit -c Release --no-build
Check "test xunit"

Write-Host "==> Installing the jsnet tool" -ForegroundColor Cyan
dotnet tool install JSNet.Cli --version $Version --tool-path tools --add-source $packages
Check "tool install"
$jsnet = if ($IsWindows -or $env:OS -eq "Windows_NT") { "tools/jsnet.exe" } else { "tools/jsnet" }
"const xs = [1, 2, 3];`nconsole.log(xs.map(x => x * 10).join(','));" | Set-Content script.jsn -Encoding utf8
$scriptOut = & $jsnet run script.jsn
Check "jsnet run"
if ($scriptOut -ne "10,20,30") { throw "jsnet run printed '$scriptOut'" }
& $jsnet info

Set-Location $root
dotnet new uninstall JSNet.Templates | Out-Null
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Write-Host "==> Smoke test passed for $Version" -ForegroundColor Green
