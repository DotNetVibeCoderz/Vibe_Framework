#!/usr/bin/env bash
# Packs every Rustzor NuGet package into artifacts/packages.
#
# usage: bash scripts/pack.sh [version]
#   version   e.g. 0.1.0-preview.1; defaults to <Version> in src/Directory.Build.props
#
# The browser runtime must be built first (scripts/build-runtime.sh): it goes
# into Rustzor.Sdk. Rustzor.Build is not a package of its own; it ships inside
# Rustzor.Sdk's tools/ folder.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
OUT="$ROOT/artifacts/packages"
rm -rf "$OUT"
mkdir -p "$OUT"
VERSION_ARG=()
[[ -n "${1:-}" ]] && VERSION_ARG=("-p:Version=$1")

if [[ ! -f src/Rustzor.Sdk/runtime/rustzor.wasm ]]; then
  echo "src/Rustzor.Sdk/runtime/rustzor.wasm is missing: run scripts/build-runtime.sh first" >&2
  exit 1
fi

count=0
while IFS= read -r project; do
  [[ "$(basename "$project")" == "Rustzor.Build.csproj" ]] && continue
  dotnet pack "$project" -c Release --nologo -v q -o "$OUT" -p:ContinuousIntegrationBuild=true "${VERSION_ARG[@]}"
  count=$((count + 1))
done < <(find src -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' | sort)

echo "packed $count projects:"
ls "$OUT"
