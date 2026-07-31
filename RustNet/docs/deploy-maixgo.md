# Deploying a C# App to a Sipeed Maix Go

End-to-end procedure for running a .NET application on a Sipeed Maix Go
(Kendryte K210, 64-bit RISC-V) — with graphics on its 320×240 panel and files
in its SPI flash.

There are **two phases**, and they cost very different amounts:

| | How | When |
|---|---|---|
| **A. Flash the firmware** | `kflash` over the board's USB port | Once, and again whenever the firmware changes |
| **B. Replace the application** | `rustnet flash` over the same port | Every time you change your C# code |

Phase B is the one you do repeatedly. No buttons, no jumpers, no second cable:
the same USB socket carries the ROM's ISP loader *and* the firmware's RNDP
console.

An uploaded application is kept in flash and **starts again by itself after a
power cycle**, along with anything it wrote to the filesystem.

Indonesian translation: [`deploy-maixgo.id.md`](deploy-maixgo.id.md).

## The board

| | |
|---|---|
| SoC | Kendryte K210 — dual RV64GC, **390 MHz** measured off the ROM's PLL0 |
| RAM | 6 MB SRAM (4 MB + 2 MB contiguous); no internal flash |
| Flash | 16 MB SPI NOR (GigaDevice `c8:60:18`) |
| Panel | MaixLCD 320×240, ST7789V, 8-bit parallel on SPI0 in octal mode |
| LEDs | IO14 red, **IO12 blue, IO13 green** |
| Console | UARTHS on IO4/IO5, through the board's FT2232H bridge |
| Programming | ROM ISP over the same port — no probe, no button combination |

Pin facts come from Sipeed's *Maix Go Datasheet v1.1* and the board schematic.
Note the LEDs: Sipeed's own `config_maix_go.py` has green and blue the other
way round, and the datasheet's pin table agrees with the hardware, not the
script.

**There is no internal flash.** The mask ROM copies the image out of the SPI
NOR into SRAM and jumps there, so this is a RAM-only link and a bad image is
never a brick — the ISP does not depend on the flash contents.

### How the flash is divided

| Range | What |
|---|---|
| `0x000000`… | the firmware image (~360 KB) |
| `0x100000`–`0xFC0000` | filesystem (`rustnet-flashfs`), ~15 MB |
| `0xFC0000`–`0x1000000` | provisioned key, uploaded app, its name |

# Phase A — flash the firmware

## A1. One-time: tools

```bash
rustup target add riscv64gc-unknown-none-elf
rustup component add llvm-tools
cargo install cargo-binutils --locked
pip install kflash
```

## A2. Build, convert, flash

`kflash` wants a raw binary, so objcopy first:

```bash
cd runtime/firmware-k210
cargo build --release
rust-objcopy -O binary \
    target/riscv64gc-unknown-none-elf/release/rustnet-firmware-k210 \
    target/fw.bin

kflash -p COM10 -b 1500000 -B goE -n target/fw.bin
```

**`-B goE` is required.** It selects the reset sequence for this board's USB
bridge; without it `kflash` cannot get the chip into its ISP.

# Phase B — replace the application over the wire

## B1. One-time per machine: a signing keypair

```bash
rustnet keys generate --out keys
```

`keys/` is gitignored. Losing the private half means the device will refuse
every future upload — see B2.

## B2. Once per device: provision

```bash
rustnet provision --key keys/rustnet-signing.pub --device serial:COM10
```

The device stores the public key and verifies every image against it.
Re-provisioning with a different key is possible here (unlike the ESP32's
write-once eFuse), but only until the record window fills.

## B3. Build and flash your application

```bash
dotnet build MyApp.csproj
rustnet flash bin/Debug/net10.0/MyApp.dll --name myapp \
    --chip k210 --key keys/rustnet-signing.key --start --device serial:COM10
```

**`--chip k210` matters.** The signed container records a chip family and the
device refuses one sealed for anything else; the default is `host-sim`.

## B4. Watch it

```bash
rustnet logs -n 40 --device serial:COM10
rustnet info --device serial:COM10
```

`info` reports more than it looks: `cpu_hz` is *measured*, `rx_dropped` and
`rx_irqs` say whether the console is keeping up, and `max_poll_gap_us` is how
long the application went between servicing the tools.

## B5. The graphics demo

`runtime/firmware-k210/demo/Showcase` is a worked example — a 2D starfield, a
rotating wireframe cube and octahedron over a perspective floor, and a closing
title that catches fire. It writes a run counter through
`RustNet.IO.FileSystem` and prints its own frame times:

```bash
dotnet build runtime/firmware-k210/demo/Showcase/Showcase.csproj
rustnet flash runtime/firmware-k210/demo/Showcase/bin/Debug/net10.0/Showcase.dll \
    --name showcase --chip k210 --key keys/rustnet-signing.key \
    --start --device serial:COM10
```

```
[fs] run #17, files under /showcase: runs.txt
[intro] 93 frames in 5046 ms — 54 ms/frame, ~18 fps
[3d]   196 frames in 9037 ms — 46 ms/frame, ~21 fps
```

## What you get

- **Graphics** — the full `RustNet.Graphics.Display` surface over a
  `rustnet-gfx` framebuffer, blitted to the panel by `Display.Present()`.
- **Files** — `RustNet.IO.FileSystem` over ~15 MB of the board's flash. Named
  blobs in a log, not FAT: no handles, no seeking, a file is rewritten whole.
- **The language** — inheritance and interfaces with virtual dispatch,
  async/await, generics, LINQ, `catch when`. See `docs/dotnet-support.md`.

## What you do not get yet

OTA, the on-device debugger and secure config — all still `std`-bound in
`runtime/firmware`. WiFi is not wired either: the board's ESP8285 is muxed onto
UART1 and has no driver. The camera and microphone array have none.

## Writing an app that performs

Two numbers, measured on this board, that change how you write code:

- **A host call costs about 220 µs**, because the interpreter dispatches it by
  matching the canonical method name as a string. Per-pixel work in C# is out
  of the question, and so is one `FillRect` per cell of a grid — batch into
  runs.
- **A managed static method call costs about 65 µs**, only three times less.
  Extracting a one-line helper into a method inside a hot loop can cost more
  than the work it factors out. Inline it into the loop that already runs.

Bound animation by `Uptime.Ms()`, not by a frame count: the virtual device is
roughly forty times faster, so a fixed count runs for seconds on one and
minutes on the other.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Every command times out | The board reboots when the port opens, and the first request is lost | The tools already ping until answered; if it persists, power-cycle and retry |
| Board silent, screen white, nothing on the console | Something pulsed **RTS** — that is `kflash`'s ISP entry, and the ROM loader says nothing | Pulse **DTR** with RTS idle to boot the application. Reset is asserted whenever DTR and RTS *differ* |
| `flash` times out mid-upload | Storage compaction, or the app starving the service loop | `rustnet apps stop` first, then flash |
| `flash` rejected for the wrong chip | `--chip` defaults to `host-sim` | Pass `--chip k210` |
| `flash` says *not provisioned* | Never provisioned, or the record window was erased | Run `rustnet provision` again |
| App restored after a power cycle is corrupt | Writing into flash that was not fully erased | Fixed — the firmware now checks the whole span and reads records back. If you see it, report the `flash verify failed` offset |
| Filesystem and app gone after flashing MaixPy | MaixPy's image is ~2 MB and overwrites the window at `0x100000` | Expected. Re-provision and re-flash |
| Screen uniformly white | The panel woke but received no pixels | This is what a wrong `spi_ctrlr0` looks like; see `runtime/firmware-k210/README.md` |
| Image rotated — right edge appears on the left | The frame was sent in more than one transfer | Fixed; `present` sends the frame in one |
| `rx_dropped` climbing in `info` | The receive ring overflowed | Check `max_poll_gap_us`; a graphics app widens it a lot |

## See also

- `runtime/firmware-k210/README.md` — firmware internals, and the panel's
  hard-won facts in full
- [`deploy-netduino3.md`](deploy-netduino3.md) — the same goal on bare-metal ARM
- [`deploy-esp32.md`](deploy-esp32.md) — the same goal on ESP32
- `docs/chips.md` — support matrix across all chip variants
- `docs/dotnet-support.md` — which C# features the runtime implements
