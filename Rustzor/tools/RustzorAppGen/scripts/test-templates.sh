#!/usr/bin/env bash
# Creates every RustzorAppGen template, builds it, and opens it in headless
# Chrome to catch runtime errors. Screenshots go to $OUT/<template>.png.
#
# usage: bash tools/RustzorAppGen/scripts/test-templates.sh [template-id ...]
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
APPGEN="$ROOT/tools/RustzorAppGen"
OUT="${OUT:-$ROOT/tools/RustzorAppGen/bin/template-tests}"
WORK="$(mktemp -d)"
PORT=5190

dotnet build "$APPGEN" -c Release -nologo -v q > /dev/null || { echo "RustzorAppGen does not build"; exit 1; }
EXE="$APPGEN/bin/Release/net10.0/RustzorAppGen.exe"
mkdir -p "$OUT"
ids=("$@")
if [ ${#ids[@]} -eq 0 ]; then
  for d in "$APPGEN"/Templates/*/; do ids+=("$(basename "$d")"); done
fi

pass=0; fail=0; failed=()
for id in "${ids[@]}"; do
  folder="$("$EXE" --new "$id" "Test $id" "$(cygpath -w "$WORK" 2>/dev/null || echo "$WORK")" | tr -d '\r')"
  if [ -z "$folder" ]; then echo "FAIL $id: not created"; fail=$((fail+1)); failed+=("$id"); continue; fi
  folder="$(cygpath -u "$folder" 2>/dev/null || echo "$folder")"
  log="$OUT/$id.build.log"
  if ! (cd "$folder" && dotnet build -nologo -clp:NoSummary > "$log" 2>&1); then
    echo "FAIL $id: build"; grep -E " error " "$log" | sort -u | head -8; fail=$((fail+1)); failed+=("$id"); continue
  fi
  site="$(find "$folder/bin/Debug" -type d -name wwwroot | head -1)"
  node "$ROOT/tools/dev/serve.mjs" "$(cygpath -w "$site" 2>/dev/null || echo "$site")" $PORT > /dev/null 2>&1 &
  server=$!
  sleep 1
  probe="$(node "$ROOT/tools/e2e/probe.mjs" "http://localhost:$PORT/" "$OUT/$id.png" '[{"wait":4000}]' 2>&1)"
  kill $server 2>/dev/null; wait $server 2>/dev/null
  errors="$(echo "$probe" | grep -E '^\[(pageerror|error)\]' | grep -v 'Failed to load resource' | grep -v 'favicon')"
  # A page that never rendered is a failure too: still loading, an error panel, or nothing served.
  dom="$(echo "$probe" | grep '^DOM:' | tail -1)"
  if echo "$dom" | grep -qE 'app-loading|app-error|^DOM: not found'; then errors="${errors}
page did not render: ${dom:0:200}"; fi
  if [ -n "$errors" ]; then
    echo "FAIL $id: runtime"; echo "$errors" | head -6; fail=$((fail+1)); failed+=("$id")
  else
    echo "ok   $id"; pass=$((pass+1))
  fi
  PORT=$((PORT+1))
done
rm -rf "$WORK"
echo "passed $pass, failed $fail ${failed[*]:-}"
[ $fail -eq 0 ]
