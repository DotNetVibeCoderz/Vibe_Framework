#!/usr/bin/env bash
# The tools a user starts with, from artifacts/packages alone: install the
# rustzor CLI, create each template, build and publish the app, and run it in
# headless Chrome.
#
# usage: bash scripts/tooling-smoke.sh <version>
#   needs: scripts/pack.sh <version> run first; node with tools/e2e installed;
#   CHROME_PATH where Chrome is not at the Windows default.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:?usage: tooling-smoke.sh <version>}"
FEED="$(cd "$ROOT/artifacts/packages" && (pwd -W 2>/dev/null || pwd))"
WORK="$(mktemp -d)"
trap 'kill "${SERVER:-0}" 2>/dev/null || true; dotnet new uninstall Rustzor.Templates >/dev/null 2>&1 || true; rm -rf "$WORK"' EXIT

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

# Packages cached from an earlier run of the same version would hide a change.
rm -rf "$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')"/rustzor.* 2>/dev/null || true

dotnet tool install Rustzor.Cli --version "$VERSION" --tool-path "$WORK/tools" --add-source "$FEED" >/dev/null
RZ="$WORK/tools/rustzor"
test "$("$RZ" --version)" = "$VERSION" || { echo "rustzor --version is not $VERSION" >&2; exit 1; }

cd "$WORK"
export RUSTZOR_FEED="$FEED" NO_COLOR=1
"$RZ" new app Shop --title "Smoke Shop"
"$RZ" new empty Tiny
"$RZ" new lib Widgets
(cd Tiny && dotnet build -nologo -v q)
(cd Widgets && dotnet build -nologo -v q)
(cd Shop && "$RZ" add fetch && "$RZ" info && "$RZ" publish)

SITE="$WORK/Shop/bin/Release/net10.0/publish/wwwroot"
test -f "$SITE/_rustzor/rustzor.wasm" || { echo "no published site at $SITE" >&2; exit 1; }
node "$ROOT/tools/dev/serve.mjs" "$SITE" 5298 >/dev/null 2>&1 &
SERVER=$!
sleep 1
node "$ROOT/tools/e2e/probe.mjs" http://localhost:5298/counter "" \
  '[{"wait":4000},{"click":".rz-btn.primary"},{"click":".rz-btn.primary"},{"wait":300},{"eval":"document.querySelector(\".count\")?.textContent + \"|\" + document.querySelector(\".rz-brand\")?.textContent.trim()"}]' \
  > "$WORK/probe.log" 2>&1 || true
if grep -q 'EVAL: "2|Smoke Shop"' "$WORK/probe.log"; then
  echo "tooling smoke test passed: rustzor new, add, info and publish made an app that runs"
else
  cat "$WORK/probe.log" >&2
  echo "tooling smoke test failed" >&2
  exit 1
fi
