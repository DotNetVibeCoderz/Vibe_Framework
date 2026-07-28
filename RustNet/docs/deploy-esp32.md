# Deploying a C# App to an ESP32-WROOM-32

End-to-end procedure for getting a .NET application running on real
ESP32 silicon. `docs/getting-started.md` covers the same flow against
the **virtual device**; this document covers the chip, where three things
differ: the firmware must be flashed with the vendor tool, every RustNet
command needs `--device serial:COMx`, and the app container must be
signed for `--chip esp32`.

Indonesian translation: [`deploy-esp32.id.md`](deploy-esp32.id.md).

## What you need

- An ESP32-WROOM-32 devkit on a USB port (this guide assumes `COM4`)
- .NET SDK 10
- Rust, plus the Espressif toolchain (step 1)

Steps 1-2 are one-time per machine, step 3 is one-time per device.
Day-to-day you only repeat steps 5-7.

---

## 1. One-time: Espressif toolchain

Upstream rustc has no Xtensa backend, so ESP32 needs Espressif's fork:

```bash
cargo install espup ldproxy espflash --locked
espup install --targets esp32
```

`espup` writes an env script — `%USERPROFILE%\export-esp.ps1` on
Windows, `~/export-esp.sh` elsewhere. Source it in **every shell that
builds firmware**:

```powershell
. C:\Users\mifma\export-esp.ps1
```

## 2. One-time: build and flash the firmware

```powershell
cd runtime\firmware-esp32
cargo build --release
```

The first build downloads ESP-IDF v5.2.3 and takes 10-20 minutes;
incremental builds are 10-30 seconds. Output goes to `C:/rnesp`, not
`./target` — `.cargo/config.toml` sets `target-dir = "C:/rnesp"` because
esp-idf-sys refuses long output paths on Windows.

```powershell
espflash flash C:\rnesp\xtensa-esp32-espidf\release\rustnet-firmware-esp32 `
    --partition-table partitions.csv --port COM4
cd ..\..
```

> **`--partition-table partitions.csv` is not optional.** It creates the
> ~1.9 MB FAT `storage` partition (`runtime/firmware-esp32/partitions.csv`).
> Without it the firmware falls back to an in-RAM MemFs, and your app,
> provisioning key, and autostart setting all vanish on every reboot.

Confirm it booted:

```powershell
rustnet probe --port COM4 --log
```

> **UART0 is the RNDP transport.** Do not leave `espflash monitor` or any
> other serial monitor attached while running `rustnet` commands — the
> port is exclusive and they will fight over it.

## 3. Build the tools and provision the device

From the repo root:

```powershell
dotnet build dotnet\RustNet.slnx
$rustnet = "$PWD\dotnet\tools\RustNet.Cli\bin\Debug\net10.0\rustnet.exe"

& $rustnet keys generate --out keys
& $rustnet provision --key keys\rustnet-signing.pub --device serial:COM4
& $rustnet info --device serial:COM4
```

`keys generate` writes `rustnet-signing.key` (private, used to sign) and
`rustnet-signing.pub` (public, burned into the device). After
provisioning the device accepts only images signed with that key.

Device spec syntax is `serial:COM4[:baud]`, default baud 115200
(`dotnet/tools/RustNet.Deploy/Transport.cs:77`). The `--device` flag goes
**after** the subcommand, not before.

## 4. Create and build the app

```powershell
$env:RUSTNET_SDK = "C:\Users\mifma\Documents\CodeSandbox\RustNet"
& $rustnet new graphics-primitives GfxTest
dotnet build GfxTest\GfxTest.csproj -c Debug
```

`RUSTNET_SDK` is required — template csprojs resolve the `RustNet.*`
class libraries through it. `rustnet new` creates the project in the
current directory. Run `rustnet templates` to list all templates.

Build in **Debug**: Release-config async is not supported, and the entry
point must be `static void Main()` (top-level statements emit
`Main(string[])` and are rejected).

## 5. Flash the app

```powershell
& $rustnet flash GfxTest\bin\Debug\net10.0\GfxTest.dll `
    --name gfx --key keys\rustnet-signing.key `
    --chip esp32 --start --device serial:COM4
```

> **`--chip esp32` is mandatory.** The flag defaults to `host-sim`
> (`dotnet/tools/RustNet.Cli/BuildCommands.cs:33`), and the device
> rejects a mismatched container with `BootError::WrongChip`
> (`runtime/rustnet-secureboot/src/lib.rs:180`). Use `--chip any` if you
> want one container that boots on any device.

This compiles the DLL to RNX, seals it in a signed RNSB container, sends
it over RNDP, and `--start` runs it immediately.

## 6. Verify

```powershell
& $rustnet logs -n 60 --device serial:COM4
& $rustnet apps list --device serial:COM4
& $rustnet profile --device serial:COM4
```

For graphics apps, pull the framebuffer as a PPM image:

```powershell
& $rustnet display capture -o frame.ppm --device serial:COM4
```

> **The WROOM-32 has no wired panel.** `present_frame` is gated behind
> `#[cfg(feature = "board-m5tough")]`
> (`runtime/firmware-esp32/src/board.rs:1389`), so the default build uses
> the no-op default from the `Board` trait. Graphics apps still render
> fully into the in-memory framebuffer — you verify them with `display
> capture`, not with your eyes. For a real screen, see
> [Boards with a panel](#boards-with-a-panel) below.

## 7. Optional: survive reboots and get online

```powershell
& $rustnet apps autostart gfx --device serial:COM4     # or: autostart off
& $rustnet wifi --ssid MyNetwork --psk secret --device serial:COM4
```

Autostart runs the named app on power-up. Once WiFi credentials are
stored the firmware also serves RNDP over TCP on port 7878, so you can
switch from `--device serial:COM4` to `--device tcp:<device-ip>:7878`.
Both require the `storage` partition from step 2.

---

## Boards with a panel

The ESP32-WROOM-32 devkit has no display. The **M5Stack Tough** does —
an ILI9342C 320×240 panel powered through an AXP192 PMIC. Full guide:
[`deploy-m5tough.md`](deploy-m5tough.md). The short version:

```powershell
cd runtime\firmware-esp32
cargo build --release --features board-m5tough
espflash flash C:\rnesp\xtensa-esp32-espidf\release\rustnet-firmware-esp32 `
    --partition-table partitions.csv --port COM4
```

Everything from step 3 onward is identical. The 320×240 RGB565
framebuffer is 150 KB, which cannot fit contiguous ESP32 DRAM, so this
build enables PSRAM in `sdkconfig.defaults` — PSRAM is mandatory for it.

The `graphics-primitives` template adapts to whatever size `Display`
reports, so the same app fills a 320×240 M5 Tough or a 160×128 TFT.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `WrongChip` on flash | Container sealed for `host-sim` | Add `--chip esp32` |
| App gone after power-cycle | No `storage` partition | Re-flash firmware with `--partition-table partitions.csv` |
| Port busy / garbled frames | Serial monitor attached | Close `espflash monitor`; UART0 is RNDP |
| Signature verification failed | Device provisioned with a different key | Re-run `provision`, or sign with the matching `.key` |
| Template won't restore packages | `RUSTNET_SDK` unset | `$env:RUSTNET_SDK = <repo root>` |
| esp-idf-sys path errors | Long build path on Windows | Keep `target-dir = "C:/rnesp"` from `.cargo/config.toml` |
| Firmware build fails in JPEG code | `image` crate can't compile for Xtensa | Already handled: `image` is behind the `image-codecs` feature, off for `chip-esp32` |

## See also

- [`deploy-m5tough.md`](deploy-m5tough.md) — the same flow on a board
  with a live panel (M5Stack Tough)
- `runtime/firmware-esp32/README.md` — firmware internals and chip status
- `docs/chips.md` — support matrix across all chip variants
- `docs/getting-started.md` — the same flow against the virtual device
- `docs/protocol.md` — RNDP frame and command reference
- `docs/debugging.md` — source-level debugging over RNDP
