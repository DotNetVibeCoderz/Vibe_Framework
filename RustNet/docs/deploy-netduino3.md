# Deploying a C# App to a Netduino 3 WiFi

End-to-end procedure for running a .NET application on a Netduino 3 WiFi
(STM32F427VIT6).

There are **two phases**, and they have very different costs:

| | How | When |
|---|---|---|
| **A. Flash the firmware** | USB DFU, button combination on the board | Once, and again whenever the firmware changes |
| **B. Replace the application** | `rustnet flash` over a serial console | Every time you change your C# code |

Phase B is the one you will do repeatedly, and it needs no button presses
and no extra hardware: the firmware serves RNDP over the board's own USB
socket as a CDC serial port, so one cable carries both phases.

An uploaded application is kept in flash and **starts again by itself
after a power cycle** — the board does not need a PC attached to run.

Indonesian translation: [`deploy-netduino3.id.md`](deploy-netduino3.id.md).

## The board

| | |
|---|---|
| MCU | STM32F427VIT6 — Cortex-M4F, 168 MHz |
| Flash / RAM | 2 MB / 256 KB (192 KB contiguous + 64 KB CCM) |
| Clock source | 25 MHz crystal on PH0/PH1 |
| User LED | PA10 (`USR_LED`) |
| Console | USART2 — PA2 TX / PA3 RX, on the digital header as **D3 / D2** |
| Programming | USB DFU (ST ROM bootloader), **RDP level 0** |
| Debug probe | none on board |

Pin and clock facts come from nanoFramework's own `NETDUINO3_WIFI` board
definition, at `nf-interpreter` tag `v1.7.2.5`.

UART7 on the goPort2 connector would work electrically, but that is a
GoBus socket needing a special cable, and the port has its own power and
TX-pull-up enables (PD10, PE10) to assert first. D2/D3 take jumper wires.

---

> ## Read this before you flash anything
>
> Flashing RustNet **erases the nanoFramework firmware** the board ships
> with. That is recoverable, but the recovery path is narrower than it
> looks: the `NETDUINO3_WIFI` target has been **retired** from
> `nf-interpreter`, and its last published build is **1.7.2.6, December
> 2021**. There will be no newer one.
>
> Download it and keep it somewhere durable *before* you start:
>
> ```
> https://dl.cloudsmith.io/public/net-nanoframework/nanoframework-images/raw/names/NETDUINO3_WIFI/versions/1.7.2.6/NETDUINO3_WIFI-1.7.2.6.zip
> ```
>
> The `nanobooter-nanoclr.dfu` inside restores the board:
>
> ```bash
> dfu-util -d 0483:df11 -a 0 -D nanobooter-nanoclr.dfu
> ```
>
> No `-s` — a DFU container carries its own addresses.
>
> A byte-level backup of the existing flash is *not* practical with
> `dfu-util` 0.11: it writes this bootloader fine but stalls reading past
> roughly 14 KB. Use STM32CubeProgrammer if you want one.

---

## Optional: a serial console on the header

The firmware serves RNDP over USB, so this is a fallback rather than a
requirement — useful when USB is not enumerating, or to watch the boot
banner before enumeration happens. Both transports stay configured; USB
takes precedence when it is present.

A USB-serial adapter at 3.3V logic, on three pins of the digital header.
**The data lines cross over** — the adapter's receive goes to the board's
transmit:

| USB-serial adapter | Netduino 3 header | STM32 |
|---|---|---|
| RX | **D3** | PA2 (board TX) |
| TX | **D2** | PA3 (board RX) |
| GND | any GND | — |

115200 8N1. Do not connect the adapter's VCC — the board is powered over
its own USB.

Getting RX and TX the same way round is the usual mistake: the board's
banner will never appear, and every `rustnet` command times out.

---

# Phase A — flash the firmware (DFU)

## A1. One-time: tools

```bash
rustup target add thumbv7em-none-eabihf
rustup component add llvm-tools
cargo install cargo-binutils --locked
```

Plus `dfu-util`, .NET SDK 10, and on Windows a WinUSB driver bound to the
DFU device (Zadig, or whatever `nanoff` already installed). Verify the
board is reachable:

```bash
dfu-util -l
```

Entering DFU is a button combination on the board itself. You should see
four alt settings and, on alt 0, the flash sector map:

```
Found DFU: [0483:df11] ... alt=0, name="@Internal Flash /0x08000000/04*016Kg,01*064Kg,07*128Kg,..."
```

That map repeating twice is the F427's dual-bank 2 MB flash — a useful
confirmation you are talking to the right part.

## A2. Build the firmware

The image carries one board feature and one application feature. The
application compiled in here is the fallback: the board runs it only when
nothing has been uploaded, or after storage is erased.

```bash
cd runtime/firmware-stm32
cargo build --release --no-default-features \
    --features board-netduino3-wifi,app-language-tour
```

Swap `app-language-tour` for `app-blink` for the minimal demo. Building a
different application into the image is covered in Phase B — for
day-to-day work you do not rebuild the firmware at all.

## A3. Convert to a raw binary — and check it

```bash
rust-objcopy -O binary \
    target/thumbv7em-none-eabihf/release/rustnet-firmware-stm32 fw.bin
od -A x -t x4 -N 8 fw.bin
```

> **Confirm the first word before flashing.** Both board variants build to
> the same ELF path, so whichever ran last wins. The initial stack pointer
> tells you which image you actually have:
>
> | First word | Image |
> |---|---|
> | `20030000` | 192 KB RAM — F427, the Netduino |
> | `20018000` | 96 KB RAM — F401RE, a Nucleo |
>
> Do not use `cargo objcopy <cargo flags> -- -O binary`; in practice it has
> emitted the default-feature artifact rather than the requested one.

## A4. Flash

Enter DFU mode on the board, then:

```bash
dfu-util -d 0483:df11 -a 0 -s 0x08000000:leave -D fw.bin
```

`:leave` restarts the board into the new firmware when the download
finishes. The "Invalid DFU suffix signature" warning is expected for a
raw binary and harmless.

---

# Phase B — replace the application over the wire

This is the fast loop. No DFU, no buttons, no firmware rebuild.

## B1. One-time per machine: a signing keypair

```bash
rustnet keys generate --out keys
```

`rustnet-signing.key` is private and signs your images;
`rustnet-signing.pub` goes to the device. Keep the private half out of
version control.

## B2. Once per device: provision

```bash
rustnet provision --key keys/rustnet-signing.pub --device serial:COM9
```

The key is written to a reserved sector of the internal flash, so it
survives a reset. Re-run this only to change keys, or after erasing
storage.

## B3. Build and flash your application

```bash
dotnet build MyApp/MyApp.csproj -c Debug
rustnet flash MyApp/bin/Debug/net10.0/MyApp.dll \
    --name myapp --key keys/rustnet-signing.key \
    --chip stm32 --start --device serial:COM9
```

```
flashed 'myapp' (6480 bytes, signed, chip=Stm32)
started
```

Build in **Debug**: Release-config async is not supported, and the entry
point must be `static void Main()`.

> **`--chip stm32` is mandatory.** The flag defaults to `host-sim`, and
> the device rejects a container sealed for the wrong chip. The signature
> is verified on the STM32 itself — about 67 ms for RSA-2048 at 84 MHz.

The device checks the signature, confirms the container is an App image,
and parses the RNX **before** accepting it, so a broken module cannot
replace a working application.

## B4. Watch it

```bash
rustnet info       --device serial:COM9
rustnet apps list  --device serial:COM9
rustnet logs -n 30 --device serial:COM9
```

`info` reports more than the basics, and the extra fields are diagnostic:

```json
{"chip":"stm32","board":"Netduino 3 WiFi","active_app":"myapp","running":true,
 "heap_used":22704,"rx_dropped":0,"max_poll_gap_us":78333,"last_verify_us":66709}
```

- **`rx_dropped`** — bytes the receive ring had no room for. Should be 0.
  Anything else means frames are arriving corrupted.
- **`max_poll_gap_us`** — the worst gap between two service-loop turns,
  reset each time you read `info`. The ring covers ~355 ms at 115200, so
  a gap approaching that is the warning sign.
- **`last_verify_us`** — how long the last on-chip signature check took.

## B5. Check on the host first

Every hardware round trip is slower than a local one, and two dev
utilities answer the common questions without touching the board:

```bash
cargo run -p rustnet-core --example run_rnx    -- app.rnx  # does it run at all?
cargo run -p rustnet-core --example heap_probe -- app.rnx  # will it fit?
cargo run -p rustnet-core --example host_calls -- app.rnx  # what must the firmware answer?
```

`heap_probe` reports peak heap — 49 KB for the language tour, against the
96 KB this board reserves. `host_calls` lists every canonical name the app
invokes **and the `HostValue` variants actually passed**; read that second
column, because a C# `bool` arrives as `I32`.

---

## Verifying without a serial adapter

The firmware blinks its boot progress on the user LED as a rising count,
with clear pauses between groups. **The counting sequence runs once;
groups that repeat forever are failures.**

| Signal | Meaning |
|---|---|
| 1 | reached the entry point |
| 2 | PLL locked to the 25 MHz crystal |
| 3 | board and console configured |
| 4 | banner sent — UART transmit completes |
| 6 | RNX parsed, interpreter built, RNDP listening |
| 9 | the app's first call into the host |
| 5 *repeating* | Rust panic, including an allocation failure |
| 7 *repeating* | the embedded RNX was rejected |
| 8 *repeating* | hard fault — most likely a stack overflow |
| 2 / 3 / 4 *repeating* | the interpreter returned Completed / Paused / Error |

Then the application takes over the LED:

- **`app-language-tour`** — a calm 1 Hz pulse means all checks passed.
  Anything else blinks the number of failures.
- **`app-blink`** — two quick blips, then a long pause.

> **Boot takes about ten seconds**, because those signals run before the
> service loop starts. The first `rustnet` command after a reset will time
> out; retry it.

## What you do not get yet

RNDP is served by the firmware itself, not by `runtime/firmware`, which is
still `std`-bound. Answered: `ping`, `info`, `logs`, `apps list`, `start`,
`stop`, `reboot`, `provision`, `flash`.

Persistence covers the provisioned key, the uploaded application and its
name — kept in a reserved 128 KB sector of the internal flash, restored at
boot. It is **not a filesystem**: no paths, no directories, and the C#
`RustNet.IO.FileSystem` APIs are still unimplemented here. OTA, secure
config and the on-device debugger are absent too; those live in the
`std`-bound `runtime/firmware`.

Storage is a log, so repeated uploads append rather than erase. When the
sector fills, one compaction erases and rewrites the live set — and that
erase stalls the core and every interrupt for roughly a second, because
the flash controller blocks all access to flash and that is where the code
lives.

The workflow was verified on a Nucleo-F401RE, which has a virtual COM port
built in. The Netduino runs the same firmware and speaks the same
protocol, over the adapter wired above.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Every command times out, no banner ever appears | RX/TX not crossed | The adapter's RX goes to **D3**, its TX to **D2** |
| First command after a reset times out | Boot signals still running | Wait ~10 s and retry |
| `flash` says *not provisioned* | Never provisioned, or storage was erased | Run `rustnet provision` |
| `flash` rejected for the wrong chip | `--chip` defaults to `host-sim` | Pass `--chip stm32` |
| `rx_dropped` climbing in `info` | Frames arriving corrupted | Check `max_poll_gap_us`; a gap near 355 ms means the service loop is starved |
| Sequence stops after 1 | PLL never locked | Check the board feature — the F401RE build clocks from HSI, this one needs the crystal |
| Sequence stops after 3 | UART transmit never completes | Wrong console index; USART2 is `CONSOLE = 1` |
| **5 repeating** | Panic, usually out of memory | Re-run `heap_probe`; raise `HEAP_SIZE` in `src/main.rs` |
| **4 repeating** | Interpreter returned an error | Read the message with `rustnet logs`, or re-check `host_calls` output against the firmware's `invoke` arms |
| **8 repeating** | Hard fault | Stack overflow — lower `HEAP_SIZE` to leave more stack |
| Nothing at all, not even 1 blink | Never reached `main` | Wrong image flashed; re-check the stack pointer from A3 |
| `dfu-util` cannot attach | Interface stalled by an aborted transfer | Unplug, replug, re-enter DFU |
| Board dark and unresponsive after flashing | — | The ROM bootloader is untouched by anything you write; re-enter DFU and reflash |

## See also

- `runtime/firmware-stm32/README.md` — firmware internals and hard-won facts
- `docs/chips.md` — support matrix across all chip variants
- [`deploy-esp32.md`](deploy-esp32.md) — the same goal on ESP32, where the
  firmware keeps a filesystem and provisioning survives a reset
- `docs/dotnet-support.md` — which C# features the runtime implements
