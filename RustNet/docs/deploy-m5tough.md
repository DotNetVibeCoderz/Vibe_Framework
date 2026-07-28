# Deploying a C# App to an M5Stack Tough

The M5Stack Tough is the first board with a **live physical panel** — an
ILI9342C 320×240 LCD powered through an AXP192 PMIC. Graphics apps show
up on the screen instead of only being readable through `display
capture`.

It is an ESP32 board, so the whole flow matches
[`deploy-esp32.md`](deploy-esp32.md). This document covers only what
differs. Read that one first if you have never flashed a RustNet device.

Indonesian translation: [`deploy-m5tough.id.md`](deploy-m5tough.id.md).

## What differs from a plain WROOM-32

| | WROOM-32 devkit | M5Stack Tough |
|---|---|---|
| Firmware build | `cargo build --release` | `cargo build --release --features board-m5tough` |
| Display | none — `present_frame` is a no-op | ILI9342C 320×240, live |
| PSRAM | not required | **mandatory** (150 KB framebuffer) |
| Verify graphics | `rustnet display capture` | look at the screen |

Everything else — provisioning, `--chip esp32`, `--device serial:COMx`,
the partition table — is identical.

---

## 1. Build and flash the firmware

Same one-time toolchain setup as
[`deploy-esp32.md` step 1](deploy-esp32.md#1-one-time-espressif-toolchain).
Then:

```powershell
. C:\Users\mifma\export-esp.ps1
cd runtime\firmware-esp32
cargo build --release --features board-m5tough
espflash flash C:\rnesp\xtensa-esp32-espidf\release\rustnet-firmware-esp32 `
    --partition-table partitions.csv --port COM4
cd ..\..
```

> **`--features board-m5tough` is what wires the panel.** Without it
> `present_frame` falls back to the no-op default in the `Board` trait
> (`runtime/firmware-esp32/src/board.rs:1389`) and the screen stays dark
> no matter what the app draws.

> **`--partition-table partitions.csv` is still required** — same reason
> as on the WROOM: it creates the FAT `storage` partition, without which
> apps, provisioning, and autostart do not survive a reboot. The layout
> is sized for 4 MB flash; the Tough has more, so the extra capacity is
> simply left unpartitioned. That is harmless.

### PSRAM is not optional here

A 320×240 RGB565 framebuffer is 150 KB, which cannot fit in contiguous
ESP32 internal DRAM. `sdkconfig.defaults` enables PSRAM (the Tough has
8 MB) with `CONFIG_SPIRAM_IGNORE_NOTFOUND=y`, so the same image still
boots on PSRAM-less boards — but on those the panel path has nowhere to
put the framebuffer.

## 2. Provision and deploy the app

Identical to the WROOM flow — see
[`deploy-esp32.md` steps 3-5](deploy-esp32.md#3-build-the-tools-and-provision-the-device):

```powershell
dotnet build dotnet\RustNet.slnx
$rustnet = "$PWD\dotnet\tools\RustNet.Cli\bin\Debug\net10.0\rustnet.exe"
$env:RUSTNET_SDK = "C:\Users\mifma\Documents\CodeSandbox\RustNet"

& $rustnet keys generate --out keys
& $rustnet provision --key keys\rustnet-signing.pub --device serial:COM4

& $rustnet new graphics-primitives GfxTest
dotnet build GfxTest\GfxTest.csproj -c Debug
& $rustnet flash GfxTest\bin\Debug\net10.0\GfxTest.dll `
    --name gfx --key keys\rustnet-signing.key `
    --chip esp32 --start --device serial:COM4
```

`--chip esp32` is still the right value — the Tough is an ESP32, and the
board feature does not change the chip family in the signed container.

## 3. Watch it run

The screen should now cycle through the `graphics-primitives` scenes:
title with gradient and colour swatches, lines, rects, circles/ellipses,
triangles, gradients, text at several scales, a rotating 3D cube,
bouncing balls, and a double-buffered matrix-rain finale.

The app also logs a marker per scene, so you can follow along without
watching the panel:

```powershell
& $rustnet logs --follow --device serial:COM4
```

You should see `panel 320x240` early on — that is `Display.Width()` /
`Height()` reporting the real panel size back to managed code.

`display capture` still works and pulls the same framebuffer, which is
useful for filing a screenshot of exactly what the panel shows.

Other templates worth trying on a real panel: `display-testing`,
`image-viewer` (embedded GIF), `ui-dashboard` (XML-defined UI), and
`xox-game`.

## How the panel is driven

Useful when debugging a dark or garbled screen — all in
`runtime/firmware-esp32/src/board.rs`:

- **Power first.** The AXP192 (I²C `0x34` on SDA 21 / SCL 22) gates the
  LCD rail, the backlight rail, and panel reset. The screen stays dark
  until it is programmed, so `m5_axp192_init` runs before the first
  flush. Registers are read-modify-written so DCDC1 — the ESP32's own
  3.3 V — is never cleared.
- **Then the panel.** ILI9342C on SPI2, SCLK 18 / MOSI 23 / CS 5 / DC 15,
  with CS and DC driven manually so a whole frame streams under one CS
  assertion. Clock is 26.67 MHz: SPI2 routes these pins through the GPIO
  matrix, which caps at APB/3 (40 MHz would need native IO_MUX pins).
- **Frames go out by DMA in bands.** 40 rows at a time (25 600 B). The
  framebuffer lives in PSRAM, which SPI DMA cannot read directly, so each
  band is copied into a DMA-capable internal bounce buffer first and
  byte-swapped to big-endian for `RAMWR`. Command and parameter bytes use
  the transaction's inline `tx_data`, so no small unaligned buffer is
  ever handed to the DMA engine.

Initialisation is lazy: the PMIC and panel come up on the **first**
`Display.Present()`, not at boot.

## Known limitations

- **No touch support.** The Tough's capacitive touch panel is not wired
  into the HAL — there is no touch trait or intrinsic yet. Input has to
  come from elsewhere (UART, WiFi, GPIO).
- **`rustnet info` reports `esp32-wroom-32 (esp-idf)`.** `Board::name()`
  is not board-feature aware (`board.rs:1287`), so the Tough identifies
  itself as a WROOM. Cosmetic only.
- **SPI pin conflict.** The generic `Spi` HAL uses SPI3/VSPI on SCLK 18 /
  MOSI 23 / MISO 19 / CS 5 — the same SCLK, MOSI, and CS pins as the
  panel. Do not use `RustNet.Devices.Spi` from an app on this board while
  the display is active.
- **I²C bus 0 is shared with the PMIC.** Apps using I²C on SDA 21 /
  SCL 22 share the bus with the AXP192 at `0x34`. Fine for other
  addresses; do not write to `0x34`.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Screen stays dark, app runs fine | Firmware built without the board feature | Rebuild with `--features board-m5tough` |
| Screen dark, `present_frame failed` in logs | AXP192 not ACKing on I²C | Check SDA 21 / SCL 22 wiring; the PMIC must respond at `0x34` |
| Boot loop or alloc failure | PSRAM missing or disabled | Confirm `CONFIG_SPIRAM=y` in `sdkconfig.defaults` and that the unit has PSRAM |
| Colours inverted or swapped | Panel MADCTL/INVON expectations differ per unit | `init_panel` sets MADCTL `0x08` (BGR) + INVON; adjust for a variant panel |
| App gone after power-cycle | No `storage` partition | Re-flash with `--partition-table partitions.csv` |
| `WrongChip` on flash | Container sealed for `host-sim` | Add `--chip esp32` |

## See also

- [`deploy-esp32.md`](deploy-esp32.md) — the base ESP32 deployment flow
- `docs/drawing.md`, `docs/ui.md` — the graphics and UI APIs
- `docs/chips.md` — support matrix across all chip variants
- `runtime/firmware-esp32/README.md` — firmware internals and chip status
