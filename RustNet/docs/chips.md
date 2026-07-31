# Chip support & real-silicon bring-up

## Chip families

| Chip | Family id | ISA | Firmware feature | Status |
|---|---|---|---|---|
| host-sim | 5 | native | `chip-host` (default) | **fully working** — virtual device used by all tools/tests |
| ESP32 | 1 | Xtensa LX6 | `runtime/firmware-esp32` (ESP-IDF std) | **RUNS RUSTNET** — verified on an ESP32-WROOM-32: RNDP over UART0, RSA-verified app flash, C# apps (incl. async/await) executing on-chip, live GPIO |
| ESP32-C3 | 6 | **RISC-V RV32IMC** | `chip-esp32c3` | **board crate started** (`rustnet-hal-esp32c3`): register-level GPIO + cycle-counted delay compile for `riscv32imc-unknown-none-elf`; other peripherals name their esp-hal integration points |
| Kendryte K210 | 7 | **RISC-V RV64GC** | `chip-k210` + `runtime/firmware-k210` | **runs C# on hardware** — `rustnet-hal-k210` (FPIOA/GPIOHS/UARTHS/UART1-3/SPI/`mcycle` clock/SPI-NOR storage) plus a firmware that links the interpreter, serves RNDP, drives the MaixLCD panel and keeps a filesystem in the board's flash; verified on a Sipeed Maix Go |
| STM32F4 | 2 | ARM Cortex-M4F | `chip-stm32` + `runtime/firmware-stm32` | **RUNS C# ON BARE METAL** — the `no_std` interpreter plus `rustnet-hal-stm32` (GPIO, USART, DWT delay), verified on a Nucleo-F401RE and a Netduino 3 WiFi (F427) |
| TI / NXP | 3/4 | ARM Cortex-M | `chip-ti` / `chip-nxp` | variant builds; vendor PAC/SDK pending |

```bash
cargo build -p rustnet-firmware --no-default-features --features chip-esp32c3
cargo build -p rustnet-firmware --no-default-features --features chip-k210
cargo build -p rustnet-hal-stm32 --target thumbv7em-none-eabihf
cargo build -p rustnet-hal-k210  --target riscv64gc-unknown-none-elf
```

## STM32F4 on bare metal (verified on real silicon)

`runtime/rustnet-hal-stm32` implements the HAL over the chip's registers
directly (RM0368), with no dependency beyond `rustnet-hal` — so it stays
in the host workspace and its test run while also building for
`thumbv7em-none-eabihf`. GPIO, USART1/2/6 (pin muxing included) and the
DWT-backed delay are live; everything else returns `NotSupported` with
its integration point named in the source.

`runtime/firmware-stm32` is the bare-metal binary that drives it —
a separate workspace, like `firmware-esp32` — with one board feature
linked per image. Two are verified on hardware:

- **Nucleo-F401RE** (84 MHz from HSI) over SWD with an external
  ST-LINK/V2: LD2 blinking, plus the USART2 configuration the HAL wrote
  (AF7 mux, `BRR` = 365 for 115200 at PCLK1 42 MHz) read back over SWD.
- **Netduino 3 WiFi** (STM32F427VIT6, 168 MHz from a 25 MHz crystal,
  2 MB flash, 192 KB usable SRAM) over DFU — its ROM bootloader is open
  at RDP level 0. Pin and clock numbers come from nanoFramework's own
  board definition; that target has been retired upstream, so keep a copy
  of its last firmware (1.7.2.6) before overwriting one.

On the Netduino that image **executes a C# application on-chip**: the
interpreter is `no_std + alloc`, so it links on bare metal directly, and
the app is compiled to RNX on the PC and embedded with `include_bytes!`.
Verified on silicon with a language-tour demo whose nine checks all pass
there — string interpolation, `List<T>` and foreach, `Dictionary`, LINQ
`Where`/`Select`/`Sum`/`OrderBy`, lambdas, interface dispatch with a
`ToString` override, user generics, and `try`/`catch` with a `when`
filter.
The firmware supplies the interpreter's whole `RuntimeHost` surface —
four methods — from the HAL.

Sizing a port before flashing it: `cargo run -p rustnet-core --example
heap_probe -- app.rnx` reports peak heap (21 KB for the blink demo), and
`--example host_calls` lists every canonical name the app invokes plus
the `HostValue` variants actually passed — a C# `bool` arrives as `I32`,
and an accessor that insists on `HostValue::Bool` fails every call on the
device while passing on a loosely-matching host harness.

**RNDP runs on this target too**, served over the console UART by a
cooperative service loop that alternates between answering the tools and
handing the interpreter a slice of fuel — no RTOS, no executor. `rustnet
info`, `apps list` and `logs` work against a Nucleo-F401RE over its
on-board ST-LINK's virtual COM port. `rustnet provision` and `rustnet flash` work too, with the
RSA-2048 signature verified on-chip in about 67 ms — `rustnet-crypto` and
`rustnet-secureboot` build for `thumbv7em-none-eabihf`. Receive is
interrupt-driven out of necessity: the F4 USART has no receive FIFO, so a
polled reader drops most of every frame while a fuel slice runs. The provisioned key and the uploaded
application persist in a reserved flash sector and are restored at boot, so
an uploaded application **starts again by itself after a power cycle** —
not a filesystem, but enough that the board runs standalone.

On the Netduino the transport is the board's own USB socket, enumerated as
a CDC serial port, so no adapter is needed; the header USART stays
configured as a fallback.

Step-by-step deployment walkthrough: **`docs/deploy-netduino3.md`**
(Indonesian: `docs/deploy-netduino3.id.md`). Firmware internals and the
build's hard-won facts: `runtime/firmware-stm32/README.md`.

Hard-won build facts: the Nucleo's HSE comes from the on-board ST-LINK's
MCO, so removing the CN2 jumpers for an external probe means the clock
tree must run from HSI or `freeze()` hangs; and `rustnet-hal` needs
`alloc` (`GpioPin::on_edge` boxes its callback), so a bare-metal port
must supply a `#[global_allocator]`.

What is still absent is a real filesystem — the reserved sector holds a
fixed set of records, not paths — so the C# `RustNet.IO.FileSystem` APIs,
OTA and the on-device debugger remain in the `std`-bound
`runtime/firmware`.

The groundwork for one exists: SPI is live in the HAL, and
`firmware-stm32/src/sdcard.rs` is a microSD block device over it. On the
Netduino it identifies a card completely — CMD8 echo, ACMD41, OCR, CSD —
and then cannot read a block from it at any clock rate, with the card
reporting no error. Register access works, flash access does not: a working
controller in front of unreadable storage. Needs a second card to confirm,
so the driver is written but unproven. `runtime/firmware-stm32/README.md`
has the traces.

## Kendryte K210 on bare metal (verified on hardware)

`runtime/rustnet-hal-k210` and `runtime/firmware-k210` are the second
bare-metal port, and the first 64-bit one: `riscv64gc-unknown-none-elf`
with `riscv-rt`. Both build clean, the HAL's 38 unit tests run in the host
workspace, and as of 2026-07-31 a **Sipeed Maix Go** boots the firmware,
answers `rustnet` over RNDP, accepts a signed C# application over the wire
and runs it from its SPI NOR after a power cycle. The part reports
390 MHz — the clock tree is read at boot rather than assumed, which is why
the wrong 403 MHz default cost nothing.

What is in place: FPIOA pad muxing, GPIOHS (32 channels, with software
open-drain), UARTHS and UART1..3, an `mcycle`-based delay and monotonic
clock, the DesignWare SSI masters, and the board's SPI NOR flash as
`extmem(0)` so a provisioned key and an uploaded app survive a power
cycle. The firmware links the interpreter, serves RNDP over UARTHS,
verifies RSA-2048 containers on-chip and swaps applications over the wire.

It also **draws**: the MaixLCD's 320×240 ST7789V runs off SPI0 in octal
frame format, `Display.Present()` reaches glass, and a graphics demo
animates at around 20 fps. And it **keeps files**: `rustnet-flashfs` backs
`RustNet.IO.FileSystem` over ~15 MB of the same part, in a second window
below the record one. Step-by-step: [`deploy-maixgo.md`](deploy-maixgo.md).

Four things about this chip shape the port, and none of them are like the
Cortex-M case:

- **6 MB of on-chip SRAM.** The heap is 4 MB and `rustnet flash` accepts
  half-megabyte containers. The STM32F401's constant negotiation over
  kilobytes — a `compile_error!` stopping the bigger demo from linking
  against the smaller board — simply does not arise, and a 320×240 RGB565
  framebuffer is a rounding error rather than the thing that forces PSRAM
  on an ESP32.
- **No internal flash.** The mask ROM copies the image out of an external
  SPI NOR part into SRAM and jumps there. So storage needs no
  linker-script guard (the flash holds no executing code, and an erase is
  an ordinary SPI transaction that does not stall the core), and a bad
  image is not a brick — `kflash` talks to the ROM's ISP regardless of
  what is in flash.
- **The panel's SPI config is per transfer, not per driver.** In the
  enhanced frame formats `spi_ctrlr0` says what unit the transfer carries,
  and it changes with every one: a command is an 8-bit instruction, its
  parameters are 8-bit units, pixels are 32-bit units. Set one value for
  all three and the panel wakes, lights its backlight, and shows a single
  flat colour forever while every call returns `Ok`. Mirroring MaixPy's
  `st7789.c` is what fixed it; reading the register out of a *running*
  MaixPy and copying that is worse than useless, because the value there
  is whatever its last pixel write left behind.
- **`mstatus.FS` is `Off` at reset**, so every floating-point instruction
  traps as illegal until software turns the FPU on. `riscv-rt` does not do
  it; Kendryte's `crt.S` does. Without the `csrs mstatus` in the port's
  `#[pre_init]`, a C# program dies the first time it multiplies two
  doubles and the trap points at whatever arithmetic happened to be
  first — a long way from the cause. This is the single most important
  non-obvious fact in the port.

**There is no pinout.** Any of the 48 pads can carry any of 256
peripheral functions, so `Board::gpio(pin)` takes an *FPIOA pad* number and
allocates one of GPIOHS's 32 channels on first use, and the UARTs and SPI
buses carry no default pins at all — on a Maix Go a "sensible default" of
IO8 for UART2 flow control would hold the on-board ESP8285 in reset. Pad
assignments come from Sipeed's own `config_maix_go.py`, the file MaixPy
writes into the board's `config.json`.

The clock tree is **read, not written**: the ROM has already brought PLL0
up (its ISP talks over UARTHS at speed), so `Clocks::detect()` recovers
what is in force and the boot banner prints it. That makes the first
hardware run a measurement — including of whether the core is at ~403 MHz
or still on the 26 MHz crystal.

Receive is drained from both the PLIC interrupt (source 33) and by
polling, running the same code either way, because UARTHS's 8-entry FIFO
is a hard 694 µs deadline at 115200 while a polling interval is only a
latency choice. `info` reports `rx_dropped` and `max_poll_gap_us` so which
one is carrying the traffic is measurable, and the interrupt can be
switched off with `--no-default-features` if it misbehaves on first
bring-up.

`runtime/firmware-k210/README.md` has the pinout, the `kflash` recipe, and
a **what to check first** list ordered by where the risk actually sits —
starting with "is the banner readable", which is the clock test, and "what
does the `[storage]` JEDEC line say", which is the SPI3 test.

## ESP32 on ESP-IDF (verified on real silicon)

`runtime/firmware-esp32` runs the full `DeviceService` as Rust **std** on
ESP-IDF, serving RNDP over UART0 through the raw UART driver (the
console VFS would CR/LF-mangle binary frames). Verified end-to-end on an
ESP32-WROOM-32 devkit: `rustnet provision/flash/logs --device
serial:COM4`, RSA-2048 signature verification on-chip, and C# apps —
including the full v0.4 language matrix with async/await — executing on
the chip; GPIO is live (LED blink from managed code). See
`runtime/firmware-esp32/README.md` for the toolchain and flash steps, and
**`docs/deploy-esp32.md`** (Indonesian: `docs/deploy-esp32.id.md`) for the
full step-by-step deployment walkthrough. For the M5Stack Tough — the
first board with a live panel — see **`docs/deploy-m5tough.md`**
(Indonesian: `docs/deploy-m5tough.id.md`).

Hard-won build facts: `ESP_IDF_SDKCONFIG_DEFAULTS` must be set
explicitly or the defaults file is silently ignored (3.5 KB main-task
stack → instant overflow); esp-idf-sys needs a short `target-dir` on
Windows; 32-bit targets have no `AtomicU64`.

## no_std profile (bare metal)

The interpreter core and the HAL build **without std** (alloc only) —
the profile bare-metal firmware links against. Verified in CI on the
bare-metal RISC-V target:

```bash
rustup target add riscv32imc-unknown-none-elf riscv64gc-unknown-none-elf
cargo build -p rustnet-core        --no-default-features --target riscv32imc-unknown-none-elf
cargo build -p rustnet-hal         --no-default-features --target riscv32imc-unknown-none-elf
cargo build -p rustnet-hal-esp32c3                       --target riscv32imc-unknown-none-elf
cargo build -p rustnet-hal-k210                          --target riscv64gc-unknown-none-elf
```

Float math routes through `libm` on no_std; std hosts use native
intrinsics. The `std` feature (default) keeps desktop behavior
unchanged. The firmware *binary* (services, RNDP server, threads) still
uses std on the host; the bare-metal executor is the remaining v0.5
work.

## Byte-pipe transport (USB-CDC/UART shape)

`rustnet-firmware --stdio` serves RNDP over stdin/stdout — exactly the
byte-pipe shape a USB-CDC or UART transport has on real silicon, and the
integration point where a chip's serial driver plugs in (`serve_pipe`
in `runtime/firmware/src/main.rs` accepts any `Read + Write`). Covered
by the `stdio_transport` integration test. The C# tools already speak
`serial:COMx[:baud]`.

## Hardware-in-the-loop smoke

`tools/hil-smoke.sh` runs in stages. It is a no-op until
`RUSTNET_HIL_PORT` is set, so CI runs it safely without hardware:

```bash
RUSTNET_HIL_PORT=COM4 ./tools/hil-smoke.sh          # stage 0 only
RUSTNET_HIL_PORT=COM4 RUSTNET_HIL_RNDP=1 ./tools/hil-smoke.sh  # + RNDP smoke
```

**Stage 0 — ROM probe (works today, verified on real silicon).**
`rustnet probe --port COM4` resets the board into its ROM serial
bootloader (the DTR/RTS download-mode circuit), speaks the esptool
protocol (SLIP + SYNC + READ_REG) and reports the chip model from the
chip-detect magic register plus the base MAC from eFuse, then reboots
the board to its application. `--log` instead resets and captures the
boot banner. Verified against an ESP32-WROOM-32 devkit: chip identified
(magic `0x00F01D83`), MAC read, clean reboot.

**Stage 1 — RNDP smoke (needs RustNet firmware on-chip).** Provisions,
flashes and verifies a sample app over `serial:COMx` — gated behind
`RUSTNET_HIL_RNDP=1` until the bare-metal firmware executor lands.

Every variant compiles the full service stack (interpreter, RNDP, OTA,
secure boot, all v0.3 HAL surfaces) against the simulator board, so the
whole firmware stays exercisable per-variant; signed images are stamped
with the chip family and the device rejects mismatches.

## Deploying to RISC-V chips

The toolchain is chip-aware end to end:

```bash
rustnet build app.dll -o app.rnx
rustnet flash app.dll --chip esp32c3 --key priv.key --device serial:COM5:115200
rustnet flash app.dll --chip k210   --key priv.key --device serial:COM4
```

- CLI/Workbench/VSCode accept `esp32c3` and `k210` chip ids everywhere a
  chip is selectable; `serial:COMx[:baud]` transports RNDP over USB-CDC/UART.
- The RNSB container's chip byte is verified by secure boot on-device, and
  `--chip` is not cosmetic: sealing for the wrong family produces a
  perfectly good image the device then refuses.
- `--chip k210` reaches the firmware in `runtime/firmware-k210`, which
  verifies the container on-chip, confirms it is an App image and parses it
  as RNX *before* accepting it — a broken module must not replace a working
  application.

## Real-silicon bring-up checklist

Bring-up = implementing the `rustnet_hal::Board` traits with the vendor
PAC/SDK and swapping the RNDP transport to USB-CDC/UART:

1. **Board traits** — implement `gpio/i2c/spi/uart/i2s/pwm/adc/power/
   delay/can/onewire/rtc/watchdog/extmem/netif/signal` from
   `rustnet-hal` for the chip (start from `rustnet-hal-host` as the
   reference implementation). Plug it into `runtime/firmware/src/chip.rs`
   `make_board()` under the chip feature.
2. **Transport** — the firmware's RNDP server reads/writes frames over any
   byte pipe; replace the TCP listener with the chip's USB-CDC or UART
   driver. The C# tools already speak `serial:COMx`.
3. **no_std** — the interpreter core avoids OS dependencies by
   construction; the firmware binary currently uses `std` (threads,
   TCP) on the host. Real-silicon targets take the embedded runtime path
   (RTOS tasks or a single-threaded executor) — tracked in PLAN.md.
4. **Verify** — `cargo test -p rustnet-core` on the host, then the manual
   smoke flow over serial: `rustnet provision`, `flash --start`, `logs`.

ESP32/ESP32-C3 notes: lwIP backs `NetInterface`, mbedTLS plugs into
`rustnet-net::tls::TlsProvider`, TWAI is the CAN peripheral, RMT is the
natural `SignalControl` implementation.

K210 notes: steps 1 and 2 are done (`rustnet-hal-k210` +
`runtime/firmware-k210`), and step 3 does not apply — the port is
bare-metal, single-threaded and cooperative rather than std. What remains
is step 4, on real hardware. No WiFi on the chip itself, so `NetInterface`
means an ESP-AT companion over a UART (a Maix Go has an ESP8285 wired to
IO6/IO7 already, running AT 1.6.2 at 115200 and joining an access point — its enable line has to be pulsed at boot, because a K210 reset does not reach it and a wedged module stays wedged); the display is an ST7789V on SPI0 and
works, and the camera is an OV2640 whose control channel is on **I²C2** — not
on the SCCB master inside the DVP block, which is the trap the pad labels set.
Its pixel path comes in over DVP and delivers 320x240 RGB565, so
`RustNet.Media.Camera` photographs on device. The eight DVP data pins are the
panel's eight, and counter-intuitively they stay routed to the panel during a
capture: clearing `spi_dvp_data_enable` gives a uniform frame. There is no ADC on this part at all — that one is not a missing
driver, it is missing silicon.
