# Chip support & real-silicon bring-up

## Chip families

| Chip | Family id | ISA | Firmware feature | Status |
|---|---|---|---|---|
| host-sim | 5 | native | `chip-host` (default) | **fully working** — virtual device used by all tools/tests |
| ESP32 | 1 | Xtensa LX6 | `runtime/firmware-esp32` (ESP-IDF std) | **RUNS RUSTNET** — verified on an ESP32-WROOM-32: RNDP over UART0, RSA-verified app flash, C# apps (incl. async/await) executing on-chip, live GPIO |
| ESP32-C3 | 6 | **RISC-V RV32IMC** | `chip-esp32c3` | **board crate started** (`rustnet-hal-esp32c3`): register-level GPIO + cycle-counted delay compile for `riscv32imc-unknown-none-elf`; other peripherals name their esp-hal integration points |
| Kendryte K210 | 7 | **RISC-V RV64GC** | `chip-k210` | variant builds; vendor PAC/SDK pending |
| STM32F4 | 2 | ARM Cortex-M4F | `chip-stm32` + `runtime/firmware-stm32` | **RUNS C# ON BARE METAL** — the `no_std` interpreter plus `rustnet-hal-stm32` (GPIO, USART, DWT delay), verified on a Nucleo-F401RE and a Netduino 3 WiFi (F427) |
| TI / NXP | 3/4 | ARM Cortex-M | `chip-ti` / `chip-nxp` | variant builds; vendor PAC/SDK pending |

```bash
cargo build -p rustnet-firmware --no-default-features --features chip-esp32c3
cargo build -p rustnet-firmware --no-default-features --features chip-k210
cargo build -p rustnet-hal-stm32 --target thumbv7em-none-eabihf
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
rustup target add riscv32imc-unknown-none-elf
cargo build -p rustnet-core        --no-default-features --target riscv32imc-unknown-none-elf
cargo build -p rustnet-hal         --no-default-features --target riscv32imc-unknown-none-elf
cargo build -p rustnet-hal-esp32c3                       --target riscv32imc-unknown-none-elf
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
- The RNSB container's chip byte is verified by secure boot on-device.

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
natural `SignalControl` implementation. K210 notes: no WiFi on chip
(pair with an ESP-AT module over UART → `NetIfKind::Cellular/Ppp` style),
display via DVP/SPI.
