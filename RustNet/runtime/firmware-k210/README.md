# RustNet firmware — Kendryte K210 (bare-metal RV64GC)

**Runs a C# application on bare-metal 64-bit RISC-V.** The IL interpreter
(`rustnet-core`) is `no_std + alloc`, so it links here directly; an app is
either compiled to an RNX module on the PC and embedded with
`include_bytes!`, or delivered over the wire by `rustnet flash` and kept in
the board's SPI flash.

| Feature | Board | SoC | Clock | SRAM / flash | LEDs | Console |
|---|---|---|---|---|---|---|
| `board-maix-go` (default) | Sipeed Maix Go | Kendryte K210, 2× RV64GC | read from SYSCTL (**390 MHz** measured off the ROM's PLL0) | 6 MB SRAM / 16 MB SPI NOR | IO14/IO12/IO13 (R/G/B) | UARTHS on IO4/IO5, via the board's own USB bridge |

> **Verified on hardware, 2026-07-31.** A Sipeed Maix Go boots this firmware,
> answers the tools over RNDP, accepts a signed C# application over the wire
> and runs it — including after a power cycle, from the copy in its SPI NOR.
> The measured boot line is:
>
> ```
> RustNet on Sipeed Maix Go @ 390 MHz (APB0 195 MHz), heap 4 MB
> [storage] JEDEC c8:60:18, 16 MB; 256 KB reserved at 0xfc0000
> ```
>
> The clock tree really is read rather than assumed — the part reports
> 390 MHz, not the 403 MHz the defaults guessed. The panel is live too: the
> `demo/Showcase` graphics demo animates on it at around 20 fps.

## Status

| Surface | State |
|---|---|
| IL interpreter | **runs on chip** — `rustnet-core`, `no_std + alloc`, 4 MB heap; the LanguageTour demo passes 9/9 (LINQ, generics, interface dispatch, `catch when`) |
| ST7789V panel, 320×240 | **runs on chip** — SPI0 in octal mode; `Display.Present()` reaches glass, and the demo animates at ~20 fps |
| `RustNet.Graphics.Display` | **runs on chip** — the full primitive set over a `rustnet-gfx` framebuffer |
| `RustNet.IO.FileSystem` | **runs on chip** — `rustnet-flashfs` over ~15 MB of the board's SPI NOR; files survive a power cycle |
| RNDP over UARTHS | **runs on chip** — `ping`, `info`, `logs`, `apps list`, `start`, `stop`, `reboot`, `provision`, `flash` |
| Secure boot | **runs on chip** — RSA-2048 verified on-chip, timing reported by `info` |
| FPIOA pad muxing | **runs on chip** — Kendryte's own per-function pad table |
| GPIOHS (32 channels, mode/read/write/toggle, software open-drain) | **runs on chip** — drives the LEDs and the panel's reset and `dcx` |
| UARTHS | **runs on chip** — the console and the RNDP transport |
| UART1..3 | **written** — including the 16550's fractional divisor; untested |
| Delay / monotonic clock | **runs on chip** — `mcycle`, 64-bit, no wrap handling needed |
| SPI3 (the boot flash) | **runs on chip** — DesignWare SSI, register level, `tmod` in the bits that move on this controller |
| SPI0 | **runs on chip** — octal frame format, per-transfer `spi_ctrlr0`, driving the panel |
| SPI1 | **written** — same driver, nothing wired to test against |
| Persistent storage | **runs on chip** — a 256 KB window at the top of the board's SPI NOR, key + app + name surviving power cycles |
| I²C0..2 | **runs on chip** — DesignWare masters; bus 2 reaches the camera |
| Camera control channel | **runs on chip** — the OV2640 identifies itself over I²C2, `0x2642` at address `0x30` |
| Camera capture (DVP) | **runs on chip** — 320x240 RGB565 frames into SRAM; `RustNet.Media.Camera` photographs on device |
| Buttons | **runs on chip** — up / middle / down on GPIOHS, exposed as `Board::Button*` |
| ESP8285 WiFi | **runs on chip** — joins an access point over AT 1.6.2 on UART1 at 115200; `RustNet.Net.Wifi` reports the DHCP address |
| Everything else in the HAL | `NotSupported`, integration point named in the source |
| Microphone array, KPU | **not here** — pads recorded, no drivers |
| OTA, on-device debugger | **not here** — still `std`-bound in `runtime/firmware` |

## The panel

The Maix panel is an ST7789V, and it is **not** wired the way an ST7789V
usually is. There is no MOSI: it hangs off an 8-bit parallel bus, which the
K210 drives by putting SPI0 into **octal** frame format and switching its eight
data lines onto the DVP (camera-interface) pins with
`sysctl.misc.spi_dvp_data_enable`. Only chip select and the clock go through
FPIOA pads.

| Pad | Signal |
|---|---|
| IO36 | `SPI0_SS3` — chip select |
| IO37 | reset, driven as GPIOHS |
| IO38 | `dcx` (data/command), driven as GPIOHS |
| IO39 | `SPI0_SCLK` |
| — | the eight data lines, switched to the DVP pins by SYSCTL |

Three things follow. A byte costs **one clock**, because eight bits go out
across eight lines, so a 320×240 frame at 15 MHz is about 10 ms of wire time.
The camera shares those pins, so a DVP driver and the panel can never stream at
once. And `dcx` is not part of the bus — the panel samples it per transfer — so
every command is its own SPI transaction.

Pixels go out as 32-bit frames, two pixels each. Not for the wire, where a byte
costs the same either way, but for the FIFO: 32 entries of 32-bit frames is
128 bytes of slack between refills instead of 32, and a transmit FIFO that runs
dry drops chip select in the middle of the pixel run.

### The trick: reconfigure per transfer

The panel took a long time to light, and the reason is one line of the
datasheet nobody writes down: **`spi_ctrlr0` is not one value.** MaixPy's
`st7789.c` re-runs `spi_init` *and* `spi_init_non_standard` before every single
transfer, because each kind carries a different unit:

| Transfer | Frame width | `spi_ctrlr0` | Meaning |
|---|---|---|---|
| command byte | 8 | `0x202` | 8-bit instruction, no address |
| command parameters | 8 | `0x00A` | no instruction, 8-bit units |
| pixels | 32 | `0x022` | no instruction, 32-bit units |

The encoding is `(wait << 11) | (inst_code << 8) | ((addr_bits / 4) << 2) |
trans`, with `trans = 2` meaning instruction and address go out in the same
wide format as the data rather than in one-bit SPI.

Setting a single value for everything gives a panel that accepts `SLPOUT` and
`DISPON` — it wakes, and its backlight comes on — and then shows one flat
colour forever. Every control line works, every transfer completes, every call
returns `Ok`. Reading the register out of a *running* MaixPy and copying it is
worse than useless: `0x22` is whatever its last pixel write left behind, and
using it for a command is meaningless.

The init sequence is mirrored from `lcd_mcu.c` too, including the 120 ms after
leaving sleep and `NORMAL_DISPLAY_ON`, which this driver was missing. Inversion
is a *board* property and is now only sent when the board asks for it.

And **the frame goes out in one transfer**, as `lcd_draw_picture` does. Sending
it a row at a time — a leftover from when nothing reached the panel at all —
looks fine on a flat colour and rotates a real image horizontally, because each
transfer declares a 32-bit address unit and whatever the controller takes off
the front of one then costs every row instead of the frame once. The symptom is
distinctive: what belongs at the right edge reappears on the left, so `RustNet`
reads as `tRustNe`. `fill` still sends bands, which is safe only because a
uniform colour is the one case where the difference cannot show.

### What that cost, and what it settled

Before the source was consulted, these were all swept on hardware and none of
them was the answer: 24 permutations of the four control pads, all 39 free pads
as a one-bit data line, `spi_dvp_data_enable` both ways, SPI mode 0 and 3,
15 MHz and 1 MHz, 8-bit versus 32-bit pixel frames, and interference from a
panel ID read.

Two things did come out of it. `misc.spi_dvp_data_enable` and
`power_sel = 0xc0` (banks 6 and 7 at 1.8 V) both come from a live MaixPy and
are load-bearing. And the sweeps proved the hardware innocent long before the
driver was: stock MaixPy was flashed, driven from its REPL, and put text on the
screen, which is what turned "maybe the module is faulty" into "the driver is
wrong" — and made reading its source the obvious next move rather than a last
resort.

## The filesystem

`rustnet-fs` — the VFS and FAT the virtual device uses — is `std`-bound, and
`fatfs` does not come apart from `std` without a `core_io` shim. So bare metal
gets `runtime/rustnet-flashfs` instead: named blobs in a log, newest wins,
tombstones for deletes, directories as name prefixes. It backs the whole
`RustNet.IO.FileSystem` surface and lives in ~15 MB of the board's SPI NOR
between the image and the record window.

It is a workspace member depending on nothing but `rustnet-hal`, so its
scan/compact logic is covered by ordinary `cargo test` against a fake NOR part
that models the thing that actually bites: **programming only clears bits**.

## The demo

`demo/Showcase` is a C# graphics showcase — a 2D starfield, a rotating
wireframe cube and octahedron over a perspective floor, and a closing title
that catches fire. Build and send it with:

```bash
dotnet build runtime/firmware-k210/demo/Showcase/Showcase.csproj
rustnet flash .../Showcase.dll --name showcase --chip k210 \
    --key keys/rustnet-signing.key --start --device serial:COMn
```

Measured on the board, at 390 MHz:

| Scene | ms/frame | fps |
|---|---|---|
| starfield | 54 | 18 |
| 3D solids | 46 | 21 |
| burning title | ~150 | ~7 |

Those numbers are printed by the app itself into `rustnet logs`, which is the
point: the demo reports on its own performance rather than leaving it to be
guessed at. Two things they taught, both of which shaped the code:

- **A host call costs roughly 220 µs**, because dispatch matches the canonical
  method name as a string. Per-pixel work in C# is out of the question, but so
  is a host call per cell — the fire draws *runs* of same-coloured cells.
- **A static method call costs roughly 65 µs**, only about three times less
  than a host call. That is not the ratio one expects. Calling a one-line
  shading helper per cell made the fire measurably *slower* than not merging
  runs at all; the arithmetic is now inlined into the loop that already runs.

And one bug those numbers caught, which no test would have: **a receive left
its frame count in `ctrlr1`, and the next transmit inherited it.** A single
four-byte panel read at boot doubled the cost of every frame drawn afterwards —
64 ms became 126 — and removing the read put it back. `begin()` now clears
`ctrlr1` for every transfer rather than only the reads that set it. The
measurement is what made it visible; it had been latent on SPI3 the whole time,
where reads and writes alternate constantly.

## Why this chip is a good fit

**6 MB of on-chip SRAM.** The STM32F401RE port spends real effort arguing
about kilobytes: 64 KB of heap out of 96 KB, a 12 KB ceiling on an inbound
`rustnet flash` frame, and a `compile_error!` stopping the bigger demo from
being linked against the smaller board. None of that applies here. The heap is
4 MB, `rustnet flash` accepts half-megabyte containers, and a 320×240 RGB565
framebuffer — 150 KB, the thing that forces PSRAM on an ESP32 — is a rounding
error.

Two consequences worth naming. There is **no internal flash at all**: the mask
ROM reads the image out of an external SPI NOR part, copies it to
`0x80000000` and jumps there, so this is a RAM-only link and the flash holds
no executing code. And the core is **RV64GC with a real FPU**, which the
interpreter's `f64` arithmetic uses directly instead of soft-float.

## The camera talks over I²C2, not over the SCCB master

The K210 has a small SCCB master built into the DVP block, the pads are
labelled `DVP_SDA`/`DVP_SCL`, and Kendryte's own `dvp.c` drives the sensor
through it. All three point one way, and all three are a dead end on this
board. This port wrote that driver first and got a sensor that acknowledged
its address, answered `0x26` from the id register exactly once, and `0xff` on
every boot afterwards — the signature of hardware that is present and a bus
that is not working.

A running MaixPy settles it, and announces it on the way up:

```text
init i2c:2 freq:100000
[MAIXPY]: find ov2640
```

Its `fpioa[41]` holds channel 130 and `fpioa[40]` holds 131 — `I2C2_SCLK` and
`I2C2_SDA`, not the `SCCB_*` functions. So the sensor is on an ordinary I²C
master at 100 kHz, and [`i2c.rs`](../rustnet-hal-k210/src/i2c.rs) drives it;
`camera.rs` keeps only what is genuinely DVP, which is the pixel clock, the
power and reset lines, and eventually the capture engine.

The DVP block still has to be brought up before any of that means anything.
`XCLK` comes from there, and the sensor's own logic runs off it — including
the part that acknowledges an address. A probe that runs before the clock
starts, or while reset is still asserted, reads as an absent camera.

### The bug underneath: disabling a busy controller

With the pads correct, the first scan of I²C2 reported acknowledges from
`0x30` *upwards, continuously* — which reads as a board covered in devices and
is really one device plus a wedged bus. `0x30` is the OV2640; every "device"
after it was the aftermath.

The cause was in this port's I²C driver, not in the camera. It ended a write
by waiting for the transmit FIFO to empty and then disabling the controller.
An empty FIFO means the last byte has been *popped*, not that it has been
clocked out, so the disable cut the transfer off mid-byte and left the sensor
holding SDA low — and a master reading a low SDA at the acknowledge bit sees
an acknowledge from everything. Kendryte's driver waits for `status.activity`
to go quiet instead, and so does this one now:

```text
[camera] scan: 1 acked [30]
[camera] 0x30 ff/0a/0b/1c/1d = [01, 26, 42, 7f, a2]
[camera] OV2640 on I2C2 at 0x30, id 0x2642
```

`0x7fa2` is OmniVision manufacturer id, and the reads repeat identically
across power cycles. The bank select in front matters too: the OV2640 splits
its register space in two, `0x0a` means different things in each, and reading
it without selecting the sensor bank returns a nearly-plausible id that is not
one.

### Capture: two surprises, and a way to see the frame

With the sensor talking, the pixel path took two more corrections, and both
came from the same place as the first.

**The shared data pins want to stay routed to the panel.** The eight DVP data
lines are the eight the LCD is driven through, and
`sysctl.misc.spi_dvp_data_enable` is what puts SPI0's data on them — so the
obvious design is to take the pins for a capture and hand them back for a blit.
That design captures a frame of uniform `0x0420`, forever. `0x0420` is not
arbitrary: it is exactly what the block's YUV-to-RGB conversion produces from
data lines reading zero, R and B clipping to nothing and G landing on 33. The
DVP needs the bit **set**. MaixPy never touches it at all — its `st7789.c` has
both `sysctl_set_spi0_dvp_data` calls commented out — and captures fine. So the
camera and the panel do not have to alternate, which is the opposite of what
this README said before the frame was tried.

**The sensor needs frames, not milliseconds.** The OV2640's automatic exposure
and white balance converge over frames and there is no register that says
"ready". Measured on this board: frame one arrives with most of the buffer
untouched, frame six is a complete image with a heavy green cast, and by frame
forty the cast is gone. `Sensor::open` throws away forty frames before handing
one back — a little over a second, the same order as the reset delays the
sensor already mandates.

Two diagnostics made this quick, and both avoid asking anyone to look at a
screen and describe it:

- `describe` reports min, max, mean, what fraction of pixels are lit, and
  **which rows have content**. A frame that stops a third of the way down is a
  size or burst-count mistake, not a dark room, and the two numbers separate
  those instantly.
- `thumbnail` dumps every fourth pixel of every fourth row as hex over the
  console — 80x60, small enough to fit down a serial line. `thumb.py` turns
  that back into a PNG. Statistics say a frame is plausible; a picture says it
  is a picture of the room.

The first frame that arrived complete was a lamp in a dark room, with a
saturated core, plausible falloff and lens vignetting — unmistakably a
photograph rather than a pattern, which is what the statistics alone could
never have settled.

## The radio has to be power-cycled, and 74880 was a symptom

The ESP8285 answers AT commands on UART1, and getting it to join an access
point took one correction that invalidated an earlier "finding" — worth
writing down precisely because the earlier version had been committed as fact.

**Resetting the K210 does not reset the ESP8285.** Nothing connects them but a
board and a UART. A module left mid-command answers `busy p...` to everything
and *stays that way* across any number of K210 resets and firmware reflashes —
and `AT+CWJAP` is exactly the command that leaves it there. So one failed join
wedged every session after it, which looks like a radio that has stopped
working rather than one that is waiting. The enable line on IO8 is the only way
to clear it, and the firmware now pulses it low for 100 ms at boot.

**74880 baud was never a fact about this board.** This port swept for a rate,
found the module answering at 74880, and recorded it as "the ESP8285 ROM's own
odd rate rather than the 115200 most AT builds ship with". That reads like a
discovery and was a symptom: 74880 is the ESP8266 *bootloader's* rate, and the
module was sitting in it because it had never been reset. Adding the
power-cycle moved it to 115200 — the rate the AT firmware documents — and the
joins started working in the same change. A rate that only ever appears on a
stuck module is not a specification.

Two smaller corrections came out of the same session. `busy p...` is *proof*
the baud rate is right — a wrong rate returns garbage, and only a correct one
returns `busy` — so it is a reason to wait rather than to try the next rate;
the probe had been treating it as failure and leaving UART1 configured at 9600,
where every later join failed for a reason that had nothing to do with joining.
And an AT reply ends with a terminator line, not with silence: `AT+CWJAP`
answers `WIFI DISCONNECT`, says nothing for several seconds while it
associates, then finishes, so a read that stops at the first quiet moment
reports a failure for a join still in progress — indistinguishable from a wrong
password.

### Credentials live in flash, and that is not the cautious choice

Keeping them in RAM was the first design, and it cannot work here: opening the
serial port asserts the K210's reset, so *every* tool invocation power-cycles
the device. `rustnet wifi --ssid ... --psk ...` would set the SSID and the next
`rustnet logs` would wipe it, and an application would retry forever against
credentials erased moments after they arrived.

So they go to `wifi.cfg` in the board's filesystem, beside the provisioning key
and the application already there. The exposure is real: anyone holding the
board can read the PSK out of its NOR. It is the same exposure as every other
ESP-AT device with a stored network, and it is the price of a reset line wired
to a serial handshake.

### The panel was not the problem, and proving that was cheap

While the join was failing, the leading hypothesis was that the radio's
transmit peaks were browning out a USB-powered board — and the panel is the
largest other consumer on that rail. The `no-panel` feature exists from that
experiment: it skips the panel's bring-up and holds its reset asserted, so a
build can be compared with the display genuinely dark rather than merely blank.
The join failed identically either way, which ruled the power hypothesis out in
one flash cycle and pointed at the wedged module instead. Kept because the next
power question will want the same lever.

## Two things that are not obvious

**The FPU has to be switched on, in software, before anything uses it.**
`mstatus.FS` comes out of reset as `Off` on this core, and in that state every
floating-point instruction — and every access to `fcsr` — raises an
illegal-instruction exception. `riscv-rt` does not do this for you; Kendryte's
`crt.S` does it in its third instruction. So `main.rs` has:

```rust
#[pre_init]
unsafe fn enable_fpu_and_accelerator() {
    // FS = mstatus[14:13], XS = mstatus[16:15]
    core::arch::asm!("csrs mstatus, {}", in(reg) 0x0001_E000usize, ...);
}
```

Skipping it does not fail to build and does not fail at boot. It fails the
first time a C# program multiplies two doubles, and the trap points at
whatever ordinary-looking arithmetic happened to be there — which is a long
way from the cause.

**The clock tree is read, not written.** The ROM has already brought PLL0 up
before our image runs, because its own ISP talks over UARTHS at speed.
Re-programming a PLL that is currently feeding the executing core either works
or hangs with nothing on the console to say which, so `Clocks::detect()`
recovers what is actually in force and the UART divisor, the SPI divisors and
the microsecond clock all scale off that. The boot banner prints the number,
which makes the first hardware run a measurement rather than a guess.

## Pinout

From Sipeed's `config_maix_go.py` in
[MaixPy-v1_scripts](https://github.com/sipeed/MaixPy-v1_scripts/blob/master/board/config_maix_go.py)
— the file MaixPy writes into a Maix Go's own `config.json`, so it is the
board's authoritative pin list rather than someone's reading of the schematic.
Worth knowing: several third-party pinouts have green and blue the other way
round.

| Pad | What | Note |
|---|---|---|
| IO4 / IO5 | UARTHS RX / TX | the on-board STM32F103 bridges USB to these; also the ROM's ISP port |
| IO12 | LED **blue** | what the app gets from `Board::UserLed()` |
| IO13 | LED **green** | boot progress |
| IO14 | LED red | failure signals |
| IO36 / IO37 / IO38 / IO39 | LCD chip select / reset / `dcx` / write strobe | **1.8 V domain** — see the panel section |
| IO16 | BOOT key | pulled up, shorts to ground |
| IO6 / IO7 / IO8 | ESP8285 TX / RX / enable | muxed onto UART1; the enable is **pulsed at boot** — see below |
| IO18 / IO19 / IO20 | microphone array (I2S0) | no driver |
| IO40 / IO41 | camera control — **I²C2** SDA / SCL | *not* the DVP's SCCB master; see below |
| IO42..IO47 | camera `RST` / `VSYNC` / `PWDN` / `HREF` / `XCLK` / `PCLK` | the DVP block proper |
| IO15 / IO16 / IO17 | buttons middle / down / up | IO16 doubles as the BOOT key |

The RGB LED is **common-anode**: pulling a pad low lights it. If that is ever
backwards the diagnostics still work — the LED would sit lit and blink dark,
and a group of blinks is just as countable inverted.

Nothing on this chip has a fixed pinout. Any of the 48 pads can carry any of
256 peripheral functions, which is why `Board::gpio(pin)` takes an *FPIOA pad*
number and has to allocate one of GPIOHS's 32 channels before there is
anything to drive, and why the UARTs and SPI buses in `rustnet-hal-k210` have
no default pins at all. A "sensible default" here would quietly steal a pad
from something else — IO8 as UART2's flow control would hold the WiFi module
in reset.

> Deploying an application step by step — tools, provisioning, flashing,
> troubleshooting — is [`docs/deploy-maixgo.md`](../../docs/deploy-maixgo.md)
> (Indonesian: [`deploy-maixgo.id.md`](../../docs/deploy-maixgo.id.md)). This
> file is the firmware's own internals.

## Build and flash

```bash
rustup target add riscv64gc-unknown-none-elf
pip install kflash          # or: pip install kendryte-flash

cd runtime/firmware-k210
cargo build --release                                   # Maix Go, blink demo
cargo build --release --no-default-features \
    --features board-maix-go,app-language-tour,rx-interrupt
```

`kflash` wants a raw binary, so objcopy first:

```bash
rustup component add llvm-tools
cargo install cargo-binutils --locked

rust-objcopy -O binary \
    target/riscv64gc-unknown-none-elf/release/rustnet-firmware-k210 fw.bin
kflash -p COM7 -b 1500000 fw.bin
```

> **Check the image before flashing it.** Every feature combination builds to
> the same ELF path, so whichever ran last wins. The STM32 port learned this
> the hard way — `cargo objcopy <flags> -- -O binary` silently emitted the
> *default* artifact rather than the requested one. Build first, then run
> `rust-objcopy` on the ELF explicitly. `fw.bin` should be roughly 320 KB and
> start with `00000097`, the `auipc` of `_start`:
>
> ```bash
> od -A x -t x4 -N 8 fw.bin
> ```

**A bad image is not a brick.** `kflash` talks to the mask ROM's ISP, which
does not depend on the flash contents being valid, and on a Maix Go the
on-board STM32F103 drives the reset and boot lines itself — no button
sequence. Whatever ends up in flash, the next `kflash` still works.

> Flashing this **replaces MaixPy** (or whatever was there). Sipeed publishes
> MaixPy images at <https://dl.sipeed.com/MAIX/MaixPy/release/>; keep a copy
> if you want to go back.

## Talking to it

The real tools work against this target over the console UART — the same USB
port `kflash` uses:

```bash
rustnet info      --device serial:COM7
rustnet apps list --device serial:COM7
rustnet logs -n 40 --device serial:COM7

rustnet provision --key keys/rustnet-signing.pub --device serial:COM7
rustnet flash app.dll --name myapp --key keys/rustnet-signing.key \
    --chip k210 --start --device serial:COM7
```

`--chip k210` matters: the container records the chip family and the device
refuses one sealed for anything else. `ChipFamily::K210` already existed on
both sides of the contract (`runtime/rustnet-secureboot` and
`dotnet/tools/RustNet.Deploy/Signing.cs`), so no protocol change was needed
for this port.

Console output is kept in a 128-line ring so `rustnet logs` returns it,
including whatever the C# application printed. An uploaded container is
signature-checked, confirmed to be an App image, and parsed as RNX *before* it
is accepted — a broken module must not replace a working application.

### Opening the port can reboot the board, and DTR picks the boot mode

The Maix Go's USB bridge (an FT2232H, `0403:6010`) wires its modem-control
lines to the K210's reset and boot pins, and **reset is asserted whenever DTR
and RTS differ**. Two consequences the tools now handle, both learned the hard
way on this board:

- Merely opening the port can restart the firmware, so the *first* request
  after connecting is written into a device that is still in reset and is
  simply lost. `RndpClient.Connect` therefore pings — the one command with no
  side effects — until the device answers, before letting a real command
  through. Retrying the real command instead would not do: re-sending a
  `FLASH_APP` chunk mid-stream corrupts the upload.
- **Which** line is pulsed selects the boot mode. Pulsing DTR with RTS idle
  boots the application; pulsing RTS is exactly how `kflash` enters the ROM
  loader, which answers nothing. A tool that "resets" with RTS leaves a
  perfectly healthy board looking dead.

Because a reset costs the running app, the client only pulses reset after a
short unanswered probe — a board that is already talking is never disturbed.
A device whose app has faulted still answers, so a bad app is replaced over
the wire rather than by reaching for `kflash`.

### Two things that made an upload time out, and are fixed

Both were found by flashing a graphics application over the wire, and neither
is visible with a light one.

**Compaction erased the whole window.** When the log fills, it is read into
RAM, erased, and written back. Erasing *the region* rather than *what is in it*
is the obvious spelling and it is quadratically wrong at this scale: a sector
erase costs tens to hundreds of milliseconds, and the filesystem window is
thousands of sectors. The device stopped answering for long enough that the
tools reported a timeout, with nothing to say it was busy rather than broken.
Both this port and the STM32's now erase the used span rounded out to whole
sectors, plus room for the record that triggered the compaction.

**The receive ring was 16 KB.** That is 1.4 seconds of continuous traffic at
115200, and the consumer only runs between interpreter fuel slices — a scene
spending 200 ms a frame leaves the ring filling for that whole time. Fine until
something stalls, and a stall shows up as a failed upload and nothing else. It
is 128 KB now; on a chip with 6 MB of SRAM there was never a reason to be
tight about it.

## Receive: a FIFO and a deadline

UARTHS has an 8-entry receive FIFO. At 115200 baud that is 694 µs of traffic,
and a gap longer than that between two reads **loses** bytes rather than
delaying them — one lost byte in the middle of a multi-kilobyte `rustnet
flash` payload fails the whole upload.

So the FIFO is emptied from two places, running the same code either way:

- the UARTHS interrupt, through the PLIC (feature `rx-interrupt`, on by
  default; source 33, hart 0's machine-mode context);
- polling — before every interpreter fuel slice, and every 100 µs while an
  application sleeps.

Belt and braces on purpose. The interrupt gives margin no polling interval can
guarantee. The polled drain means that if the PLIC setup turns out to be wrong
on real silicon, the port still talks to the tools instead of appearing dead —
and `cargo build --no-default-features --features board-maix-go,app-blink`
turns the interrupt off entirely if it misbehaves.

`info` reports `rx_dropped` and `max_poll_gap_us` (reset when read), so which
mechanism is actually carrying the traffic is a measurement.

The same reasoning is why `sleep_ms` uses two intervals rather than one.
Draining has a hard deadline; *servicing a frame* only costs response latency.
So the FIFO is emptied every 100 µs and RNDP is polled every 2 ms. An
application that blinks spends nearly all its wall-clock time asleep while
burning almost no fuel, so without this the tools would go unanswered for as
long as each sleep.

## Diagnostics without a debug probe

Boot progress blinks a rising count **in green**; failures repeat a group **in
red**. Execution stopping at green `n` names the step after `n` as the culprit.
Having two colours is the one place this port is better off than the
single-LED STM32: "it stalled" and "it failed" are different colours instead
of different counts.

| Signal | Meaning |
|---|---|
| green 1 | reached the entry point |
| green 2 | clock tree read; the spin-delay reference is now correct |
| green 3 | board, LED channels and console configured |
| green 4 | banner sent — UARTHS transmit completes, so it is not stuck |
| green 6 | RNDP listening; the application runs next |
| green 9 | the app's first call into the host |
| **red 5** | Rust panic, including allocation failure |
| **red 6** | an interrupt fired that nothing claimed |
| **red 7** | embedded RNX rejected |
| **red 8** | CPU exception — most likely an illegal instruction or a bad access |

The blink delay scales itself from the detected core clock, and starts from
the 26 MHz crystal — the slowest the core can possibly be. Erring slow means
the first blinks are *longer* than intended, which is still visible; erring
fast would make them too brief to see, which reads exactly like a hang.

## What the first hardware run settled

Ordered by where the risk actually sat, not by how much code was involved.
Answers from a Maix Go on 2026-07-31; the two open items are marked.

1. **Did the image run?** Yes — `_start` at `0x80000000`, no flash offset or
   objcopy surprises.
2. **Is the banner readable at 115200?** Yes, which is the clock test passing:
   the UARTHS divisor is `cpu_hz / baud - 1` over a *detected* `cpu_hz`, so
   legible text means the SYSCTL arithmetic is right. It reported **390 MHz**,
   not the ≈403 MHz the defaults assumed — reading the clock tree rather than
   trusting a constant paid for itself on the first boot.
3. **What did the `[storage]` line say?** `JEDEC c8:60:18, 16 MB` — a
   GigaDevice GD25Q128. So SPI3 is muxed, clocked and configured correctly,
   *including* the transfer-mode field that sits at `ctrlr0` bits 10..11 on
   SPI3 but 8..9 on SPI0/1. That was the single most-likely-wrong register
   write in the port, and it was right.
4. **The FPU.** No illegal-instruction trap: the `#[pre_init]` write to
   `mstatus.FS` does what it was written to do, and the interpreter's `f64`
   paths run.
5. **RNDP.** `ping`, `info`, `logs`, `provision`, `flash`, `start` all work,
   with `rx_dropped: 0` across repeated multi-kilobyte uploads. Steady-state
   `max_poll_gap_us` is ≈27 ms with a light application and ≈155 ms with the
   graphics demo; the ≈2.8 s maximum is a one-off, the boot LED signalling
   between `start_receiving()` and the first service loop. All well over the
   694 µs FIFO deadline — which is exactly why the interrupt matters:
6. **The UARTHS interrupt fires.** `info` now reports `rx_irqs`, and it climbs
   into the tens of thousands during an upload. This was the port's biggest
   open question, because the polled drain covers for a dead interrupt so
   completely that the difference only shows once an application is busy enough
   to widen the polling gap — and then uploads fail for no visible reason. It
   is a measurement now, not a hope.
6. **Persistence.** A signed C# app uploaded over the wire survives a power
   cycle and runs from flash — verified over three flash-and-power-cycle
   rounds. This is where the one real bug turned up; see below.
7. **LED colours and polarity** — *still open*. Nothing here depends on it,
   and no amount of register reading settles it.
8. **`Board::UserLed()`**: the demo is expected to blink *inverted* on this
   board, because the LED is common-anode and the demo writes `true` for
   "lit". Left alone rather than silently flipped — an app driving a relay off
   the same call would not thank us for the surprise. *Unconfirmed*, with 7.

### The bug: a blank check that was not blank enough

A Maix Go arrives with something already in its flash, and it is not uniform.
This window was erased for its first few kilobytes and written further in — so
`tail_is_blank`, which probed **four bytes** at the append site, said "blank",
and a 12 KB application was programmed straight over live data.

NOR flash does not report that. Programming only clears bits, so the second
write silently ANDs into the first, and the corruption here was four bytes
three pages deep: `73 2e 44 65` (`"s.De"`, inside a method name in the RNX
string table) became `23 04 00 00`. The app uploaded cleanly, parsed cleanly
and ran perfectly — from RAM. It came back from the next power cycle
complaining about an internal call named
`System.Runtime.CompilerService#\x04\x00\x00faultInterpolatedStringHandler`.

The tell was that every corrupted byte was a **bit-subset** of the byte that
should have been there. That is not noise on a wire and not a bad read; it is
the signature of a region being programmed twice.

Two fixes, both in `storage.rs`, and both applied to the STM32 port too since
it carries the same log format:

- `tail_is_blank` now checks the **whole span** the record will occupy, not
  its first four bytes, so a partly-used window triggers a compaction and a
  real erase.
- `write_record` reads the record back and compares, naming the offset and
  both bytes if they differ. A persistence layer that cannot detect its own
  bad write is exactly what made this take a while to find.

## What is still missing

A **filesystem**. What exists is a 256 KB window at the top of the board's
flash holding three fixed records — the provisioned key, the uploaded
application and its name — restored at boot. There are no paths and no
directories, so the C# `RustNet.IO.FileSystem` APIs are unimplemented here,
and OTA, secure config and the on-device debugger remain in the `std`-bound
`runtime/firmware`.

Unlike the STM32 port, storage needs no linker-script guard. There the same
flash array holds the executing code, so `memory.x` has to withhold the
storage sector or the firmware would erase itself, and an erase stalls the
core for about a second. Here the image runs from SRAM: an erase is an
ordinary SPI transaction, the core keeps running, and the protection is
arithmetic — the window starts megabytes above the image and `SpiFlash`
refuses any address outside it.

The obvious next drivers, in the order the hardware makes them worth doing:

- **TCP and MQTT over the radio.** The join works; sockets do not exist yet.
  AT 1.6.2 has no MQTT client — that arrived in AT 2.x — so MQTT has to be
  framed by hand over `AT+CIPSTART`/`AT+CIPSEND`, with `+IPD` on the way back.
  The protocol half belongs in `rustnet-espat` where the host tests reach it.
- **microSD** on SPI1, where the block-device work in
  `runtime/firmware-stm32/src/sdcard.rs` is directly reusable.
- **The microphone array**, and eventually the KPU — which is where this chip
  is actually unusual.

## The embedded applications

One app feature is linked alongside one board feature, and both are the *same
files* the STM32 port carries rather than copies:

```rust
include_bytes!("../../firmware-stm32/demo/Blink.rnx")
```

Reaching into a sibling crate's directory is unusual, and deliberate. Two
ports running the same demo is the point of having a demo; a duplicated binary
is a duplicate that can drift, and then "it works on ARM" stops meaning
anything. The C# sources live in `runtime/firmware-stm32/demo/`.

| Feature | App | Exercises | LED result |
|---|---|---|---|
| `app-blink` (default) | `Blink/` | one GPIO pin | two blips, long pause |
| `app-language-tour` | `LanguageTour/` | string interpolation, `List<T>` + foreach, `Dictionary`, LINQ, lambdas, interface dispatch with a `ToString` override, user generics, `try`/`catch` with a `when` filter | calm 1 Hz pulse = all passed |

Neither app hardcodes its LED pin: each calls a `Board::UserLed()` hook that
the firmware answers with the board's own pad, matched by shape rather than by
one demo's namespace, so either app runs on either port.

Two dev utilities size and shape a port before it is ever flashed:

```bash
cargo run -p rustnet-core --example heap_probe -- demo/LanguageTour.rnx  # peak heap
cargo run -p rustnet-core --example host_calls -- demo/LanguageTour.rnx  # what to implement
```

`heap_probe` matters much less here than on the STM32 — the tour's 49 KB peak
is 1% of this heap — but `host_calls` still earns its keep: it lists every
canonical name an app invokes **and the `HostValue` variants actually passed**.
Arguments arrive widened, and a C# `bool` reaches `RuntimeHost::invoke` as
`I32` rather than `HostValue::Bool`, because that is how it lives on the
evaluation stack. A too-narrow accessor passes on the host harnesses in
`examples/` — they match `RustNet.Hal.*` by prefix and never inspect
arguments — and then fails every call on the device.

## Layout

Separate Cargo workspace (like `firmware-esp32` and `firmware-stm32`),
excluded from the host workspace in the root `Cargo.toml`, because it targets
`riscv64gc-unknown-none-elf` and links `riscv-rt`. The HAL crate it drives,
`runtime/rustnet-hal-k210`, carries no dependencies beyond `rustnet-hal` and so
stays inside the host workspace and its test run — 38 tests covering the pad
table, the divisor arithmetic, channel allocation and the storage window's
bounds.

`memory-maixgo.x` is RAM-only and describes 6 MB, not 8: the general-purpose
SRAM is two contiguous banks (4 MB at `0x80000000`, 2 MB at `0x80400000`),
while the 2 MB above them is the KPU's AI RAM and is usable as ordinary memory
only once the AI clock domain is ungated. It also discards `.eh_frame`, which
otherwise fails the link outright — those relocations are 32-bit PC-relative
from address 0 and cannot reach code at `0x80000000`.
