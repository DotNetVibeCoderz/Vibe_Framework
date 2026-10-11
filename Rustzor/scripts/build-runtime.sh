#!/usr/bin/env bash
# Builds RustNetRuntime for the browser (runtime/rustzor-wasm) and stages it,
# with the JS loader, where the Rustzor SDK packs it from.
#
# usage: bash scripts/build-runtime.sh [--size]
#   --size   optimise for download size (profile wasm-size) instead of speed
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
PROFILE=release
[[ "${1:-}" == "--size" ]] && PROFILE=wasm-size
cargo build -p rustzor-wasm --target wasm32-unknown-unknown --profile "$PROFILE"
OUT="target/wasm32-unknown-unknown/$PROFILE/rustzor_wasm.wasm"
mkdir -p src/Rustzor.Sdk/runtime
cp "$OUT" src/Rustzor.Sdk/runtime/rustzor.wasm
cp js/rustzor.js src/Rustzor.Sdk/runtime/rustzor.js
ls -la src/Rustzor.Sdk/runtime
