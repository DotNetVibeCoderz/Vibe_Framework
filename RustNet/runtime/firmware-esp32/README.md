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
espflash flash --port COM4 target/xtensa-esp32-espidf/release/rustnet-firmware-esp32
```

Then, from the repo root:

```bash
rustnet probe --port COM4 --log                       # watch it boot
rustnet info --device serial:COM4
rustnet provision --key keys/rustnet-signing.pub --device serial:COM4
rustnet flash app.dll --name blinky --chip esp32 --key keys/rustnet-signing.key \
        --device serial:COM4 --start
rustnet logs -n 50 --device serial:COM4
```

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
