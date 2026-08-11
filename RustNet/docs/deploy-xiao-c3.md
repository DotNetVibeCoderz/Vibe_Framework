# Deploying a C# App to a Seeed XIAO ESP32C3

The XIAO is the smallest board RustNet runs on, and the only ESP32 here that
needs **no bridge chip**: the USB socket goes straight to a USB Serial/JTAG
controller inside the SoC, so one cable carries flashing, RNDP and the console.

Verified on hardware (COM13): firmware flashed, provisioning, a signed app
running on-chip, and autostart still running it after a reboot.

Everything about applications — `provision`, `flash`, `apps`, `data`, `logs` —
works exactly as on any other board; [`deploy-esp32.md`](deploy-esp32.md)
covers that. What follows is only what is particular to this one.

## The board

| | |
|---|---|
| SoC | ESP32-C3 — single-core **RISC-V** RV32IMC at 160 MHz |
| RAM | ~400 KB SRAM, no PSRAM |
| Flash | 4 MB |
| Console | USB Serial/JTAG inside the SoC (`303A:1001`) |
| GPIO | 0..21 only |

No PSRAM means no 320×240 framebuffer — that needs 150 KB contiguous and this
chip cannot offer it. GPIO, timing, files, networking and the whole language
core are all present.

## Build and flash

```bash
rustnet firmware build --board esp32c3
rustnet firmware flash --board esp32c3 --port COM13
```

Or in the Workbench: **FIRMWARE ▸ BOARD FIRMWARE**, pick `esp32c3`. By hand it
is one command, and all three parts of it matter:

```bash
cd runtime/firmware-esp32
MCU=esp32c3 cargo build --release --no-default-features \
    --features chip-esp32c3 --target riscv32imc-esp-espidf
```

`MCU` tells ESP-IDF which SoC to build for; the target is RISC-V rather than
Xtensa; and the feature picks both the chip identity the device reports and the
peripheral RNDP runs over. Get one wrong and the image is quietly for another
board.

## Three things that are particular to this chip

**RNDP runs over the SoC's USB Serial/JTAG, not UART0.** The classic ESP32
boards reach a host through a bridge wired to UART0; here the socket goes to
the controller inside the chip, and UART0 sits on bare GPIOs with nothing
attached. A UART0 firmware on this board talks to nobody — the port enumerates,
the tool opens it, and every request times out. `src/link.rs` is that
difference and nothing else.

**ESP-IDF's secondary console is off** (`sdkconfig.defaults.esp32c3`). It
defaults to the same controller RNDP uses, so any log line ESP-IDF emitted
would land in the middle of a protocol frame. The primary console stays on
UART0.

**Only one of ESP-IDF's two I2C drivers may be linked.** The legacy driver
carries a constructor that aborts the image before `main` if it finds
`driver_ng` present, and `esp-idf-hal` pulls the legacy one in from under
`esp-idf-svc`. The board layer therefore uses the legacy driver too. This was
found the useful way rather than the expensive one: the boot produced no output
at all, and `riscv32-esp-elf-addr2line` on the abort address named
`check_i2c_driver_conflict` while the linker map named what had pulled the
object in. Two commands.

## Flashing an app

```bash
rustnet provision --key keys/rustnet-signing.pub --device serial:COM13
rustnet flash bin/Debug/net10.0/MyApp.dll --name myapp \
    --key keys/rustnet-signing.key --chip esp32c3 --device serial:COM13 --start
rustnet apps autostart myapp --device serial:COM13
```

`--chip esp32c3` matters: a device refuses an image built for another chip.

After a reboot the log opens with the app already running:

```
[   5] INFO  boot: RustNet ESP32-C3 DevKit (RISC-V) (chip esp32c3)
[ 381] INFO  runtime: app 'calc' started
[ 383] INFO  app: C3Demo calculator
[ 403] INFO  app: 1+2*3 = 7
[ 509] INFO  boot: autostart 'calc' running
```

## What can go wrong

**Every command times out, but the port exists.** The image is probably the
Xtensa one, or was built without `--features chip-esp32c3` — either way it is
talking to UART0, which on this board goes nowhere.

**Changing `sdkconfig.defaults.esp32c3` appears to do nothing.** Cargo does not
notice it. `cargo clean -p esp-idf-sys` is what picks it up, and that is a full
ESP-IDF rebuild.

**No output at all, not even a panic.** Watch the raw port through a reset
rather than guessing — the ROM and bootloader print there — and decode any
abort address with `riscv32-esp-elf-addr2line -e <elf> -f <pc>`.
