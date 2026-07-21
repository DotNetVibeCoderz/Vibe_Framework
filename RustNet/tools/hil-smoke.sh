#!/usr/bin/env bash
# Hardware-in-the-loop smoke test: provision, flash and run a sample app
# on a REAL device over serial, then assert its log markers.
#
# Requires actual silicon on a serial port — CI skips this unless
# RUSTNET_HIL_PORT is set (e.g. "COM5" / "/dev/ttyUSB0").
#
#   RUSTNET_HIL_PORT=COM5 RUSTNET_HIL_CHIP=esp32c3 ./tools/hil-smoke.sh
set -euo pipefail

PORT="${RUSTNET_HIL_PORT:-}"
CHIP="${RUSTNET_HIL_CHIP:-esp32c3}"
BAUD="${RUSTNET_HIL_BAUD:-115200}"
APP="${RUSTNET_HIL_APP:-dotnet/tests/SampleApp/bin/Debug/net10.0/SampleApp.dll}"

if [ -z "$PORT" ]; then
    echo "hil-smoke: RUSTNET_HIL_PORT not set — no hardware attached, skipping."
    exit 0
fi

RUSTNET="${RUSTNET_CLI:-dotnet/tools/RustNet.Cli/bin/Debug/net10.0/rustnet}"
DEVICE="serial:${PORT}:${BAUD}"
KEYDIR="$(mktemp -d)"
trap 'rm -rf "$KEYDIR"' EXIT

# ---- stage 0: identify the silicon via its ROM bootloader -------------
# Works on any Espressif board today, before RustNet firmware exists for
# the chip — proves port wiring, reset circuit and the serial link.
echo "hil-smoke: stage 0 — ROM probe on $PORT"
"$RUSTNET" probe --port "$PORT" --baud "$BAUD"

# ---- stage 1: RNDP smoke (needs RustNet firmware flashed on-chip) -----
if [ -z "${RUSTNET_HIL_RNDP:-}" ]; then
    echo "hil-smoke: stage 1 (RNDP flash+run) skipped — set RUSTNET_HIL_RNDP=1"
    echo "hil-smoke: once the chip runs rustnet-firmware. Stage 0 PASS."
    exit 0
fi

echo "hil-smoke: stage 1 — RNDP smoke, device $DEVICE chip $CHIP"
"$RUSTNET" keys generate --out "$KEYDIR"
"$RUSTNET" provision --key "$KEYDIR/rustnet-signing.pub" --device "$DEVICE" || true
"$RUSTNET" flash "$APP" --name hilsmoke --chip "$CHIP" \
    --key "$KEYDIR/rustnet-signing.key" --device "$DEVICE" --start

sleep 5
LOGS="$("$RUSTNET" logs -n 100 --device "$DEVICE")"
echo "$LOGS" | tail -20

echo "$LOGS" | grep -q "SampleApp finished" || {
    echo "hil-smoke: FAILED — app did not finish"; exit 1;
}
echo "$LOGS" | grep -qv "crashed" || {
    echo "hil-smoke: FAILED — app crashed"; exit 1;
}
echo "hil-smoke: PASS"
