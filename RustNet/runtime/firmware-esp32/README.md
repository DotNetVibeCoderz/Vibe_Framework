# RustNet firmware for ESP32 (Xtensa, ESP-IDF)

The same `DeviceService` stack as the virtual device, running as Rust
`std` on ESP-IDF, serving RNDP over UART0 — so every RustNet tool works
against the chip through `--device serial:COMx`.

## One-time toolchain setup

Upstream rustc has no Xtensa backend; Espressif's fork provides it:

```bash
cargo install espup ldproxy espflash --locked
espup install --targets esp32        # installs the "esp" toolchain
```

`espup` writes an env script (`%USERPROFILE%\export-esp.ps1` on Windows,
`~/export-esp.sh` elsewhere) — source it in the shell that builds.

## Build & flash

```bash
cd runtime/firmware-esp32
cargo build --release                # first build downloads ESP-IDF (~10-20 min)
# Output lands in the short target-dir from .cargo/config.toml, NOT ./target —
# esp-idf-sys refuses long build paths on Windows. Always pass the partition
# table, or the FAT `storage` partition is missing and nothing survives reboot.
espflash flash C:/rnesp/xtensa-esp32-espidf/release/rustnet-firmware-esp32 \
    --partition-table partitions.csv --port COM4
```

Then, from the repo root (full step-by-step walkthrough, including app
creation and troubleshooting: `docs/deploy-esp32.md`, or
`docs/deploy-esp32.id.md` in Indonesian):

```bash
rustnet probe --port COM4 --log                       # watch it boot
rustnet info --device serial:COM4
rustnet provision --key keys/rustnet-signing.pub --device serial:COM4
rustnet flash app.dll --name blinky --chip esp32 --key keys/rustnet-signing.key \
        --device serial:COM4 --start
rustnet logs -n 50 --device serial:COM4
```

## Two things that made a working device look broken

Both were found on an M5Stack Tough whose application had simply stopped
starting by itself, and both are worth knowing because neither symptom points
anywhere near its cause.

### The crash-loop guard counted power cuts as crashes

`DeviceService::try_autostart` guards against an application that takes the
device down on the way up: it counts unattended boots, and after three it
stops trying. The counter was incremented *before* each launch and cleared
only by an explicit `flash`, `apps start`, or `autostart set` — never by an
application that simply ran.

So three ordinary power cuts were indistinguishable from three crashes, and a
device stopped autostarting an application that had never failed once. The
device reports the state plainly, which is how it was confirmed:

```json
{"active_app":null,"running":false,"autostart":"m5mqtt"}
```

Autostart configured, autostart skipped. `confirm_autostart_healthy` closes
this: the firmware calls it thirty seconds after boot, and it clears the
counter if the application is still running. Counting the attempt before the
launch is what keeps the guard working — an app that reboots the device never
reaches the confirmation, so its count survives.

### WiFi was joined before the serial lifeline came up

`main` joined the stored network before starting the RNDP loop. Three attempts
against an unreachable access point take **fifty-nine seconds** on this board,
measured:

```text
(0.5s)  bootloader
(58.9s) RustNet ESP32 firmware ready; RNDP on UART0
```

For all of that the device answers nothing. And because opening a serial port
resets an ESP32, every `rustnet` invocation restarted the wait and timed out
long before it ended — so the device could not be reached at all, and the
autostart above could not be re-enabled by the tools that exist to do it. A
device you cannot talk to is a device you cannot fix.

WiFi now runs on its own thread. RNDP answers in about six seconds, whatever
the network is doing.

## Status

Phase 2: persistent FAT storage on the `storage` partition (apps and
provisioning survive reboots — flash with `--partition-table
partitions.csv`), WiFi STA from `rustnet wifi set` credentials with an
RNDP TCP listener on :7878, live GPIO / delay / RTC / light-sleep /
watchdog / ADC1 / LEDC PWM (channel = GPIO number) / I2C bus 0
(SDA 21 / SCL 22), and a real chip reboot on `rustnet reboot`.
Phase 3 done (verified on-chip): UART1/2, TWAI→CAN (self-test round
trip), RMT→Signal generate, SNTP real-time clock. Remaining (phase 4):
SPI master, RMT capture/PulseFeedback, OTA A/B, GPIO edge interrupts.
