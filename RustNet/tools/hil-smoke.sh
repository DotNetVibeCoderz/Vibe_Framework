#!/usr/bin/env bash
# Hardware-in-the-loop smoke test: provision, flash and run an app on a REAL
# device over serial, then assert its log markers.
#
# Requires actual silicon on a serial port — CI skips this unless
# RUSTNET_HIL_PORT is set (e.g. "COM5" / "/dev/ttyUSB0").
#
#   RUSTNET_HIL_PORT=COM5 RUSTNET_HIL_CHIP=esp32c3 ./tools/hil-smoke.sh
#   RUSTNET_HIL_PORT=COM7 RUSTNET_HIL_CHIP=rp2040 RUSTNET_HIL_RNDP=1 \
#       RUSTNET_HIL_KEYS=keys ./tools/hil-smoke.sh            # a provisioned Pico
#
# Settings:
#   RUSTNET_HIL_CHIP     esp32 | esp32c3 | rp2040 | stm32 (Meadow F7)   [esp32c3]
#   RUSTNET_HIL_RNDP=1   run stage 1 (the board already runs RustNet firmware)
#   RUSTNET_HIL_FIRMWARE=1  (rp2040, stm32) reflash the RustNet firmware first,
#                        through `rustnet firmware flash` — no buttons on a Pico
#   RUSTNET_HIL_KEYS     directory holding rustnet-signing.{key,pub}. A device
#                        accepts one provisioning key for life, so a board that
#                        is already provisioned needs its own key here; without
#                        it a throwaway pair is generated (fresh boards only).
#   RUSTNET_HIL_APP      app to flash               [SampleApp]
#   RUSTNET_HIL_EXPECT   log line that means success [SampleApp finished]
set -euo pipefail

PORT="${RUSTNET_HIL_PORT:-}"
CHIP="${RUSTNET_HIL_CHIP:-esp32c3}"
BAUD="${RUSTNET_HIL_BAUD:-115200}"
APP="${RUSTNET_HIL_APP:-dotnet/tests/SampleApp/bin/Debug/net10.0/SampleApp.dll}"
EXPECT="${RUSTNET_HIL_EXPECT:-SampleApp finished}"

if [ -z "$PORT" ]; then
    echo "hil-smoke: RUSTNET_HIL_PORT not set — no hardware attached, skipping."
    exit 0
fi

RUSTNET="${RUSTNET_CLI:-dotnet/tools/RustNet.Cli/bin/Debug/net10.0/rustnet}"
# The Pico and the Meadow speak RNDP over their own USB CDC port, where the
# baud rate is meaningless; the ESP32 boards go through a USB-serial bridge.
case "$CHIP" in
    rp2040|stm32) DEVICE="serial:${PORT}" ;;
    *)            DEVICE="serial:${PORT}:${BAUD}" ;;
esac

# ---- stage 0: identify the silicon -----------------------------------
case "$CHIP" in
    esp32|esp32c3)
        # Via the ROM bootloader — works before RustNet firmware exists on the
        # chip, and proves port wiring, reset circuit and the serial link.
        echo "hil-smoke: stage 0 — ROM probe on $PORT"
        "$RUSTNET" probe --port "$PORT" --baud "$BAUD"
        ;;
    rp2040|stm32)
        if [ -n "${RUSTNET_HIL_FIRMWARE:-}" ]; then
            board=$([ "$CHIP" = rp2040 ] && echo pico || echo meadow-f7)
            echo "hil-smoke: stage 0 — flashing RustNet firmware (board $board) through $PORT"
            "$RUSTNET" firmware flash --board "$board" --device "$DEVICE"
            sleep 3
        fi
        echo "hil-smoke: stage 0 — RNDP info on $PORT"
        "$RUSTNET" info --device "$DEVICE"
        ;;
    *)
        echo "hil-smoke: unknown chip '$CHIP'"; exit 2 ;;
esac

# ---- stage 1: RNDP smoke (needs RustNet firmware flashed on-chip) -----
if [ -z "${RUSTNET_HIL_RNDP:-}" ]; then
    echo "hil-smoke: stage 1 (RNDP flash+run) skipped — set RUSTNET_HIL_RNDP=1"
    echo "hil-smoke: once the chip runs rustnet-firmware. Stage 0 PASS."
    exit 0
fi

if [ -n "${RUSTNET_HIL_KEYS:-}" ]; then
    KEYDIR="$RUSTNET_HIL_KEYS"
else
    KEYDIR="$(mktemp -d)"
    trap 'rm -rf "$KEYDIR"' EXIT
    "$RUSTNET" keys generate --out "$KEYDIR"
fi

echo "hil-smoke: stage 1 — RNDP smoke, device $DEVICE chip $CHIP"
# Provisioning is write-once: on a board that already holds this key it is
# refused, which is fine; with any other key the flash below fails, loudly.
"$RUSTNET" provision --key "$KEYDIR/rustnet-signing.pub" --device "$DEVICE" || true
"$RUSTNET" flash "$APP" --name hilsmoke --chip "$CHIP" \
    --key "$KEYDIR/rustnet-signing.key" --device "$DEVICE" --start

sleep 5
LOGS="$("$RUSTNET" logs -n 100 --device "$DEVICE")"
echo "$LOGS" | tail -20

if echo "$LOGS" | grep -q "crashed"; then
    echo "hil-smoke: FAILED — app crashed"; exit 1
fi
echo "$LOGS" | grep -q "$EXPECT" || {
    echo "hil-smoke: FAILED — '$EXPECT' not in the log"; exit 1;
}
echo "hil-smoke: PASS"
