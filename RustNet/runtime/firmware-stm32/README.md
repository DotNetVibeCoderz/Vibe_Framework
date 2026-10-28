# RustNet firmware — STM32F4 (bare-metal Cortex-M4F)

**Runs a C# application on bare-metal ARM.** The IL interpreter
(`rustnet-core`) is `no_std + alloc`, so it links here directly; the app
is compiled to an RNX module on the PC and embedded in the image with
`include_bytes!`. Exactly one board feature is linked per image.

| Feature | Board | MCU | Clock | Flash / RAM | LED | Console |
|---|---|---|---|---|---|---|
| `board-nucleo-f401re` (default) | Nucleo-F401RE | STM32F401RET6 | 84 MHz from HSI | 512 KB / 96 KB | PA5 (LD2) | USART2, PA2/PA3 |
| `board-netduino3-wifi` | Netduino 3 WiFi | STM32F427VIT6 | 168 MHz from 25 MHz HSE | 2 MB / 192 KB usable | PA10 (`USR_LED`) | **USB CDC** (native socket); USART2 on D3/D2 as fallback |

**Both variants verified on hardware.** The Nucleo-F401RE over SWD with
an external ST-LINK/V2, with the GPIO and USART registers read back to
confirm the HAL wrote what it claimed. The **Netduino 3 WiFi over DFU,
running the C# application** — its `USR_LED` blinking the app's rhythm
is end-to-end proof: the PLL locked to the board's 25 MHz crystal (a
missing HSE hangs in `freeze()` first), the interpreter executes the
embedded module, and `Gpio.Write` from managed code reaches the pin. Its
pin and clock numbers come from nanoFramework's own `NETDUINO3_WIFI`
board definition (`nf-interpreter` tag `v1.7.2.5`, before the target was
retired from `main`).

## Status

| Surface | State |
|---|---|
| IL interpreter | **live** — `rustnet-core`, `no_std + alloc` |
| RNDP over the console UART | **live** — `ping`, `info`, `logs`, `apps list`, `start`, `stop`, `reboot`, `provision`, **`flash`** |
| Secure boot | **live** — RSA-2048 verified on-chip, ~67 ms at 84 MHz |
| GPIO (mode, read, write) | **live** — register-level, `rustnet-hal-stm32` |
| USART (config, tx, rx) | **live** — USART1/2/6 and UART7/8, pin muxing included |
| Delay / monotonic clock | **live** — DWT cycle counter |
| Everything else in the HAL | `NotSupported`, integration point named in the source |
| Persistent storage | **runs on both boards** — `rustnet-flashfs` in the reserved flash sector; key and app restored at boot, migrated from the old record log (seen on the F401RE: 3 records), and carried through compaction (`demo/FlashStress`: 3000 rewrites, sector compacted mid-run, then a reboot — Netduino in 69 s, F401RE in 117 s) |
| `RustNet.IO.FileSystem` | **runs on both boards** — write/read/delete over the same sector; budget 16 KB (F401RE) / 32 KB (F427). The GC collects after an eighth of the heap in new allocation; a managed `byte[]` is 16 bytes per element, so keep buffers small |
| Crash notes + safe mode | **runs on both boards** — a panic or hard fault notes where (and, for an allocation failure, how much was asked for and what was free) in `.uninit` RAM and resets; the next boot logs it and does not start the app (`src/crash.rs`) |
| DFU on command | **runs on the Netduino** — `rustnet reboot --bootloader` (and `rustnet firmware flash --board netduino3 --device serial:COMn`) enter the ROM bootloader with no button (`src/dfu.rs`, ported from the Meadow) |
| USB CDC transport | **live** on the Netduino — RNDP over the board's own USB, no adapter |
| SPI | **live** — SPI1/2/3, register level |
| microSD block device | **driver written, unproven** — see below |
| OTA, on-device debugger | **not here** |

## Talking to it

The real tools work against this target over the console UART:

```bash
rustnet info      --device serial:COM8
rustnet apps list --device serial:COM8
rustnet logs -n 20 --device serial:COM8
```

Verified on a Nucleo-F401RE, whose on-board ST-LINK exposes a virtual COM
port wired to USART2 — no extra adapter needed there. Console output is
kept in a 64-line ring so `rustnet logs` returns it, including whatever
the C# application printed.

> **Boot takes about ten seconds** before the service loop starts, because
> the LED signals below run first. A command issued during that window
> times out; retry it.

Two things are worth knowing about how this is built.

**Receive is interrupt-driven, and has to be.** The F4's USART has a
one-byte receive register and no FIFO, so at 115200 baud a byte must be
taken every ~87 µs while an interpreter fuel slice runs for tens of
milliseconds. A polled reader drops most of every frame. The ISR moves
bytes into a single-producer/single-consumer ring, and counts what it had
to drop — `rx_dropped` in the `info` response, rather than silence.

**`sleep_ms` serves RNDP too.** Fuel is counted in instructions, but an
application spends its wall-clock time asleep: the blink demo burns a few
hundred instructions per 1.7 seconds. Waiting a slice out in one blocking
delay leaves the tools unanswered for minutes. This cost a debugging round
to find, since the symptom — bytes arriving, being consumed, and no reply
— looks nothing like its cause.

### Replacing the application over the wire

```bash
rustnet provision --key keys/rustnet-signing.pub --device serial:COM8
rustnet flash app.dll --name myapp --key keys/rustnet-signing.key     --chip stm32 --start --device serial:COM8
```

The container is signature-checked on the device, confirmed to be an App
image, and parsed as RNX before it is accepted — a broken module must not
replace a working application. The main loop then rebuilds the interpreter
around it, which is what swapping requires: the interpreter borrows its
module for its lifetime.

## Storage and files

> **Compile-verified only.** This storage layer builds for both boards
> but has not yet been run on either one.

The reserved 128 KB flash sector holds a **`rustnet-flashfs`** filesystem,
the same named-blob log the K210, RP2040 and Meadow F7 ports use. The
firmware keeps its own state there as ordinary files under `/.sys/`, with
the same names as those ports:

| Path | Holds |
|---|---|
| `/.sys/signing.pub` | the key from `rustnet provision` |
| `/.sys/app.rnx` | the last application `rustnet flash` delivered, already verified |
| `/.sys/app.name` | its `--name` |

Everything else belongs to the application, through all ten
`RustNet.IO.FileSystem` calls (`WriteAllText`, `AppendText`,
`ReadAllText`, `WriteAllBytes`, `AppendBytes`, `ReadAllBytes`, `Exists`,
`Delete`, `CreateDirectory`, `List`). Applications cannot reach `/.sys`:
a call that names it fails, and `List("/")` does not show it. A writable
`signing.pub` would let an app swap the key and make signature checking
pointless.

flashfs handles the NOR rules (see the root `CLAUDE.md`). Before it writes
a record it checks that the whole span the record will take is still
erased, and after writing it reads the record back. Writes append to the
log. When the log fills, a compaction keeps only the live files, so an
erase happens once per sector-full of writes, not on every write.

**RAM is the real limit, not the 128 KB.** The whole store is one sector,
so a compaction has to keep every live file in RAM while the sector is
erased, because there is nowhere else to put them. On the F401RE that RAM
comes from a 64 KB heap the running app is already using. So:

- **Every write is planned first.** If the heap cannot cover the write,
  plus a compaction when one is needed, the write is refused and nothing
  is changed. The error message gives the live bytes, the record size and
  how much heap is free. flashfs has a second safeguard: it claims all
  compaction RAM before the erase, and can fail at that point, so a
  shortage gives an error. It never halts the board or loses data.
- **The app's files have a budget:** 16 KB on the F401RE, 32 KB on the
  F427, headers included. The budget leaves room to replace the app
  later. That write has to hold the running app (~21 KB for blink), the
  incoming frame and the new app's record (up to 12 KB each on the
  F401RE), plus every other live file while the sector compacts. Going
  over the budget gives `storage budget exceeded: ...`.
- If saving the key or the app fails (always for lack of RAM), the
  command still succeeds in RAM, and `[storage] could not persist ...`
  goes to the log, as before. The app name is only saved if the app was
  saved too.

A compaction erases the sector, which stalls the core for one to two
seconds (see below). The RNDP receive ring covers ~355 ms, so a tool
command sent during one may need to be retried.

### Upgrading from the old record log

Firmware before this kept three fixed records in that sector, in a format
of its own (magic `RNS1`). **The first boot of the new firmware migrates
them once**: it reads the newest key, app and name into RAM, erases the
sector, then writes them back as `/.sys/*` files, key first. The boot log
says `[storage] migrated N records from the old log`.

Migration is safe up to the erase: if power is lost before it, the next
boot tries again. If power is lost after the erase and before the last
write, whatever was not yet written is gone. If that happens, or the log
reports `old log not migrated`, **provision the board again** and flash
the app again (`rustnet provision`, then `rustnet flash`). A board that was
never provisioned needs nothing.

OTA, secure config and the on-device debugger still live only in the
`std`-bound `runtime/firmware`.

`memory.x` deliberately stops the FLASH region short of that sector. The
firmware erases it at runtime, so a linker free to place code there would
be arranging for the firmware to erase itself; keeping the sector out of
`MEMORY` turns that into a link error rather than a corruption.

An erase stalls the core and every interrupt handler for one to two
seconds. The flash controller blocks all access to flash, and the code
runs from flash. Because storage is a log, this only happens on
compaction, not on every write.

## microSD: where it stands

`sdcard.rs` is a block device over the card's SPI mode. On the Netduino it
gets as far as a card that identifies fully and then refuses to hand over
any block, so it is **not yet proven working** — with one card, which the
evidence says is the reason.

What that card demonstrated, and what is therefore sound:

- The wiring and pin mapping — SPI3 on PC10/11/12, chip select PB0.
- **The slot's enable line, PB1, is active low.** `board.h` leaves it high
  and that is not evidence: nanoFramework never drives a card over SPI on
  this target (`HAL_USE_MMC_SPI FALSE`), so high is its idle level, not its
  active one. Left floating, the card is simply silent.
- The identification sequence: CMD0 answers idle, the CMD8 voltage echo
  comes back exactly to spec, ACMD41 clears after 18 rounds, the OCR reads,
  and the CSD decodes to a sensible capacity.
- Receiving a data block, proven by CMD9: the CSD arrives through the same
  data-token path a block read uses.

What the card then does is accept CMD17 with `R1 = 0x00` and send `0x01`
where `0xFE` belongs, at every clock rate from 200 kHz to 4 MHz, with CMD13
reporting no error at all. Register access works and flash access does not,
independent of speed — a working controller in front of unreadable storage.
`read_csd`, `status` and `probe` are kept for exactly this kind of
question: they report bytes rather than interpretations.

Two things this cost, worth not repeating:

- **Unbounded waits on peripheral flags.** The SPI transfer had three. A bus
  that never clocks hung the firmware before the service loop started,
  which presents as a board that will not enumerate — nothing like a
  peripheral fault. They are bounded now.
- **Not draining the bus between operations.** A single throwaway byte in
  `deselect` never drained a partly-consumed data block, and the residue
  surfaced as the next command's response. Visible in a raw trace as
  `CMD17 echo=[eb, 6c, 2a]` — the tail of the previous CSD.

## The embedded applications

One app feature is linked alongside one board feature. Both apps are
compiled ahead of time and baked into the image.

| Feature | App | Exercises | Peak heap | LED result |
|---|---|---|---|---|
| `app-blink` (default) | `demo/Blink/` | one GPIO pin | 21 KB | two blips, long pause |
| `app-language-tour` | `demo/LanguageTour/` | string interpolation, `List<T>` + foreach, `Dictionary`, LINQ `Where`/`Select`/`Sum`/`OrderBy`, lambdas, interface dispatch with a `ToString` override, user generics, `try`/`catch` with a `when` filter | 49 KB | calm 1 Hz pulse = all passed; otherwise blinks the failure count |

**Both verified running on a Netduino 3 WiFi**, the tour with all nine of
its checks passing on-chip.

```bash
dotnet build demo/LanguageTour/LanguageTour.csproj -c Debug
rustnet build demo/LanguageTour/bin/Debug/net10.0/LanguageTour.dll -o demo/LanguageTour.rnx
cargo build --release --no-default-features \
    --features board-netduino3-wifi,app-language-tour
```

The firmware supplies the interpreter's whole `RuntimeHost` surface —
four methods — from `rustnet-hal-stm32`: console to the UART, clock and
sleeps to the DWT delay, and `RustNet.Hal.Gpio::*` to the pins.

Neither app hardcodes the LED pin. Each calls a `Board::UserLed()` hook
that the firmware answers with the board's own pin, matched by shape
rather than by one demo's namespace — so either app runs on either board.

Two dev utilities size and shape a port before it is ever flashed:

```bash
cargo run -p rustnet-core --example heap_probe -- demo/LanguageTour.rnx  # peak heap
cargo run -p rustnet-core --example host_calls -- demo/LanguageTour.rnx  # what to implement
```

`heap_probe` reports the high-water mark, and it earns its keep: the tour
peaks at 49 KB, comfortably inside the Netduino's 96 KB reserve but above
the 48 KB the F401RE build sets aside — so that combination is a
`compile_error` rather than an out-of-memory panic on hardware.
`host_calls` lists every
canonical name the app invokes **and the `HostValue` variants actually
passed** — see the argument-widening note under Hard-won facts for why
that second column matters.

## Diagnostics without a debug probe

The Netduino has no on-board probe, so the LED doubles as the debugging
channel. Boot blinks a rising count, and execution stopping at `n` names
the step after `n` as the culprit. Groups that repeat forever are
failures; the counting sequence runs once.

| Signal | Meaning |
|---|---|
| 1 | reached the entry point |
| 2 | PLL locked (a missing HSE hangs before this) |
| 3 | board and console configured |
| 4 | banner sent — UART transmit completes |
| 6 | RNX parsed and interpreter built |
| 9 | the app's first call into the host |
| 2, 3, 4 *repeating* | interpreter returned Completed / Paused / Error |
| 5 *repeating* | Rust panic, including allocation failure |
| 7 *repeating* | embedded RNX rejected |
| 8 *repeating* | hard fault — most likely a stack overflow |

The blink delay scales itself from the configured core clock. Without
that, every signal after the PLL comes up runs 10x fast and is too brief
to see — which reads exactly like a hang.

## Build and flash

```bash
rustup target add thumbv7em-none-eabihf
cargo install probe-rs-tools --locked

cd runtime/firmware-stm32
cargo build --release                                                    # Nucleo-F401RE
cargo build --release --no-default-features --features board-netduino3-wifi,app-blink

# `probe-rs run` waits for RTT, which this binary does not emit — use
# download + reset instead.
probe-rs download --chip STM32F401RE target/thumbv7em-none-eabihf/release/rustnet-firmware-stm32
probe-rs reset --chip STM32F401RE
```

`probe-rs info --verbose` enumerates the debug port even when it cannot
auto-identify the part — use it first if `download` cannot attach.

### Flashing the Netduino over DFU

The Netduino 3 WiFi has no on-board debug probe, but its ROM bootloader
is open (RDP level 0, verified). Enter DFU with the board's button
combination, then:

```bash
rustup component add llvm-tools
cargo install cargo-binutils --locked

cargo build --release --no-default-features --features board-netduino3-wifi,app-blink
rust-objcopy -O binary target/thumbv7em-none-eabihf/release/rustnet-firmware-stm32 fw.bin
```

> **Check the image before flashing it.** Both board variants build to the
> same ELF path, so whichever ran last wins — and in our run `cargo
> objcopy <cargo flags> -- -O binary` silently emitted the *default*
> (Nucleo) artifact instead of the requested one. Build first, then run
> `rust-objcopy` on the ELF explicitly, and confirm the initial stack
> pointer in the first word of `fw.bin`:
>
> ```bash
> od -A x -t x4 -N 8 fw.bin
> ```
>
> | First word | Means |
> |---|---|
> | `20030000` | 192 KB RAM — the F427 / Netduino image |
> | `20018000` | 96 KB RAM — the F401RE / Nucleo image |
>
> Flashing the Nucleo image to a Netduino is not destructive, but it
> configures PA5 — the CC3100 WiFi SPI clock — as a GPIO and drives it,
> and its HSI clock setup means nothing visible happens.

```bash
dfu-util -d 0483:df11 -a 0 -s 0x08000000:leave -D fw.bin
```

> **This erases nanoFramework.** Restore it by flashing the official image
> — `nanobooter-nanoclr.dfu` from the `NETDUINO3_WIFI-1.7.2.6` package on
> nanoFramework's Cloudsmith repo, the last build published for this board
> (December 2021). Keep a local copy: the target has been retired from
> `nf-interpreter`, so that package is the end of the line.
>
> Note that `dfu-util` 0.11 cannot reliably *read* large regions from this
> bootloader — uploads stall past roughly 14 KB — so a byte-level backup of
> the existing firmware is not practical with it. Use STM32CubeProgrammer
> if you want one.

## Hard-won facts

- **On the Nucleo, clocks come from HSI, not HSE.** Its 8 MHz HSE is fed
  from the on-board ST-LINK's MCO. Remove the CN2 jumpers to use an
  external probe and that clock stops; asking for HSE then hangs in
  `freeze()`. The Netduino is the opposite case — a real 25 MHz crystal on
  PH0/PH1, independent of any probe — so the two boards configure the PLL
  differently and the board feature has to pick.
- **Host arguments arrive widened, and a `bool` is not `HostValue::Bool`.**
  A C# `bool` reaches `RuntimeHost::invoke` as `I32`, because that is how
  it lives on the evaluation stack; integers may arrive as `I64` or `F64`
  too. Accessors must coerce, exactly as `runtime/firmware/src/apphost.rs`
  does (`arg_bool` is defined as `arg_i32(..)? != 0`). This bites only on
  real firmware: the host harnesses in `examples/` match `RustNet.Hal.*`
  by prefix and return `Void` without inspecting arguments, so a
  too-narrow accessor passes on the host and fails every call on the
  device. `cargo run -p rustnet-core --example host_calls` prints the
  variants actually passed.
- **An allocator is mandatory.** `rustnet-hal` depends on `alloc` —
  `GpioPin::on_edge` boxes its callback — so every bare-metal port must
  supply a `#[global_allocator]`. The 16 KB heap here is a placeholder;
  the interpreter will need far more than that out of 96 KB, and sizing it
  is open work (see the object-count-based GC threshold in
  `runtime/rustnet-core/src/heap.rs`).
- **A standalone ST-LINK/V2 has no VCP.** Unlike the Nucleo's on-board
  ST-LINK/V2-1, the dongle exposes no serial port — reading the USART
  output needs a separate USB-serial adapter on PA2/PA3/GND.
- **`memory.x` is F401RE-specific.** Other F4 parts change the RAM and
  flash lengths.

## Layout

Separate Cargo workspace (like `firmware-esp32`), excluded from the host
workspace in the root `Cargo.toml`, because it targets
`thumbv7em-none-eabihf` and links `cortex-m-rt`. The HAL crate it drives,
`runtime/rustnet-hal-stm32`, carries no dependencies beyond `rustnet-hal`
and so stays inside the host workspace and its test run.
