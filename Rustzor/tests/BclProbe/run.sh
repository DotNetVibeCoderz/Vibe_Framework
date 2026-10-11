#!/usr/bin/env bash
# Runs every probe check on dotnet and on RustCLR, one process per check, and
# prints the checks whose output differs.
# usage: bash tests/BclProbe/run.sh [path/to/rustnet.exe]
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
R="${1:-/c/experiment/VibeCoding/Vibe_Runtime/RustNetRuntime/target/release/rustnet.exe}"
DLL="$HERE/bin/Release/net10.0/BclProbe.dll"
N=$(dotnet "$DLL" count)
bad=0
for ((i=0; i<N; i++)); do
  want=$(dotnet "$DLL" $i 2>&1 | tr -d '\r')
  got=$("$R" run "$DLL" -- $i 2>&1 | tr -d '\r' | grep -v '^$' | head -2 | tr '\n' ' ' | sed 's/ $//')
  if [ "$want" != "$got" ]; then
    bad=$((bad+1)); echo "[$i] want: $want"; echo "     got:  $got"
  fi
done
echo "checks=$N mismatches=$bad"
