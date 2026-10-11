#!/usr/bin/env bash
# Builds an app from artifacts/packages alone, as a NuGet user would, and runs
# it in headless Chrome: the package path end to end, before anything is
# published.
#
# usage: bash scripts/package-smoke.sh <version>
#   needs: scripts/pack.sh run first; node with tools/e2e installed (npm ci);
#   CHROME_PATH pointing at Chrome or Chromium where it is not the Windows default.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# What dotnet understands: a Windows path under Git Bash, the same path elsewhere.
FEED="$(cd "$ROOT/artifacts/packages" && (pwd -W 2>/dev/null || pwd))"
VERSION="${1:?usage: package-smoke.sh <version>}"
WORK="$(mktemp -d)"
trap 'kill "${SERVER:-0}" 2>/dev/null || true; rm -rf "$WORK"' EXIT

cp -r "$ROOT/tests/PackageSmoke/HelloRustzor" "$WORK/"
APP="$WORK/HelloRustzor"
sed "s/@VERSION@/$VERSION/g" "$APP/HelloRustzor.csproj.in" > "$APP/HelloRustzor.csproj"
rm "$APP/HelloRustzor.csproj.in"
cat > "$WORK/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

# A package cached from an earlier run of the same version would hide a change.
dotnet nuget locals global-packages --list >/dev/null
rm -rf "$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')"/rustzor.* 2>/dev/null || true

(cd "$APP" && dotnet build -c Release --nologo -v q)
SITE="$APP/bin/Release/net10.0/wwwroot"
test -f "$SITE/_rustzor/rustzor.wasm" || { echo "no rustzor.wasm in $SITE" >&2; exit 1; }

node "$ROOT/tools/dev/serve.mjs" "$SITE" 5299 >/dev/null 2>&1 &
SERVER=$!
sleep 1
node "$ROOT/tools/e2e/probe.mjs" http://localhost:5299/ "" \
  '[{"wait":4000},{"click":".rz-btn"},{"click":".rz-btn"},{"wait":300},{"eval":"document.querySelector(\"#count\")?.textContent"}]' \
  > "$WORK/probe.log" 2>&1 || true
if grep -q 'EVAL: "Clicked 2 times"' "$WORK/probe.log"; then
  echo "package smoke test passed: an app built from the packages runs and responds"
else
  cat "$WORK/probe.log" >&2
  echo "package smoke test failed" >&2
  exit 1
fi
