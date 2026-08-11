# Deploying a C# App to a Wilderness Labs Meadow F7 Micro

End-to-end procedure for running a .NET application on a **Meadow F7 Micro v1.0**
(STM32F777, Cortex-M7 at 216 MHz) — over the board's own USB socket, with no
probe.

There are **two phases**:

| | How | When |
|---|---|---|
| **A. Flash the firmware** | USB DFU, button combination on the board | Once, and again whenever the firmware changes |
| **B. Replace the application** | `rustnet flash` over the same USB port | Every time you change your C# code |

Phase B is the one you do repeatedly, and it is cheap: the application lives in
the module's 32 MB QSPI flash, is replaced without a reboot, and survives both
a power cycle **and a firmware reflash** — because it is not in the image.

> **Flashing this replaces Meadow OS.** DFU into internal flash is the only way
> in without a probe, and it overwrites Wilderness Labs' own runtime. It is
> reversible — their `meadow` CLI puts it back.

## The board

| | |
|---|---|
| MCU | STM32F777, Cortex-M7 at **216 MHz** |
| RAM | 512 KB; this port uses SRAM1+SRAM2 (384 KB) and leaves DTCM out |
| Flash | 2 MB internal for the image; **32 MB QSPI** (S25FL256L) for storage |
| SDRAM | 32 MB on the module — not yet driven by this port |
| Clock | **25 MHz crystal** (X401, Abracon ABM12W-25) on PH0/PH1 |
| Console | USB CDC from the MCU, and UART4 on the `D0`/`D1` header pins |
| RGB LED | **PA2 red, PA1 green, PA0 blue** |
| Coprocessor | ESP32-PICO-D4 (WiFi/BLE) — not yet driven by this port |

Every one of those pin facts comes from Wilderness Labs' published schematic,
`Meadow_Hardware_Designs/Meadow_F7v1/Micro_Dev_Module/MeadowF7Micro_REVD.pdf`,
and none of them is in the developer-portal documentation.

### Why RAM starts at SRAM1

The 128 KB below `0x20020000` is DTCM: the fastest memory on the part, tightly
coupled to the core and **not reachable by DMA**. Handing it to the allocator
would work until the first driver DMAs into a buffer that happened to land
there. The Netduino's map leaves its CCM out for the same reason.

# Phase A — flash the firmware

## A1. One-time: tools

```bash
rustup target add thumbv7em-none-eabihf
cargo install cargo-binutils --locked
rustup component add llvm-tools
# plus dfu-util
```

## A2. Enter DFU and flash

Hold **BOOT**, tap **RESET**, release BOOT. The board enumerates as
`STM32 BOOTLOADER` (`0483:df11`). Then:

```bash
rustnet firmware build --board meadow-f7
rustnet firmware flash --board meadow-f7
```

Or the Workbench: **FIRMWARE ▸ BOARD FIRMWARE**, pick `meadow-f7`.

## A3. Confirm it is alive

The board comes back as an ordinary COM port:

```bash
rustnet info --device serial:COM17
{"chip":"stm32f7","board":"Meadow F7 Micro","protocol":1,"cpu_hz":216000000,
 "hse_hz":25000000,"transport":"usb-cdc+uart4",
 "chip_id":"chip: STM32F76x/F77x (dev 0x451 rev 0x1001), 2048 KB flash",
 "chip_expected":true}

rustnet logs -n 20 --device serial:COM17
--- RustNet on Meadow F7 ---
RustNet on Meadow F7 Micro @ 216 MHz, heap 256 KB
hse: 25 MHz crystal (X401, ABM12W-25)
chip: STM32F76x/F77x (dev 0x451 rev 0x1001), 2048 KB flash, uid 00400020
app: 67 methods, 19 types, 91 strings
[C#] blinking the user LED
```

The green LED responds — but **inverted**, and that is expected. This board's
RGB LED is common-anode: its shared pin sits at VCC and each colour returns
through a resistor to the MCU, so a pin driven **low** lights it. The Blink
demo is shared with the STM32F4 port, where the LED is active-high, and writes
`true` to mean "lit". On a Meadow that reads as mostly-on with two brief dark
flickers rather than two blinks and a pause.

The application is genuinely running either way — the pin changes on cue, and
`[C#] blinking the user LED` in the log comes from the interpreter. If you want
the intended pattern, invert the writes in your own app: `Gpio.Write` is a raw
pin API and deliberately does not know about board polarity.

`chip_expected` is worth reading. The firmware asks the silicon what it is
rather than asserting it, because being wrong there is quiet: a memory map
sized for the wrong part does not fail at link time or at boot, it fails later
in whatever code was running when the allocator first ran past real memory.

## A4. The serial console

Not required, and invaluable when something is wrong. A USB-serial adapter on
the header:

| Adapter | Meadow |
|---|---|
| RX | `D1` (UART4 TX, PH13) |
| TX | `D0` (UART4 RX, PI9) |
| GND | GND — **not optional**, and the most commonly forgotten wire |

115200 8N1. The board prints its banner at boot and a line every second after
that, so you can attach at any time and see something within a second — reading
a boot banner is otherwise a race you lose silently.

`D0`/`D1` are **UART4**, which the schematic labels `COM1` and the Meadow
documentation calls `COM4`. `COM2` carries USART1 on PB14/PB15. Guessing from
the name would put you on the wrong peripheral.

The link is a console until a tool speaks RNDP at it, and a protocol link
thereafter: one wire cannot carry human text and binary frames at the same
time without the frames being corrupted by the text.

# Phase B — your application

The application is compiled to RNX on the PC and embedded in the image:

```bash
dotnet build runtime/firmware-stm32/demo/Blink/Blink.csproj
rustnet build runtime/firmware-stm32/demo/Blink/bin/Debug/net10.0/Blink.dll \
    -o runtime/firmware-stm32/demo/Blink.rnx
rustnet firmware build --board meadow-f7   # then flash as above
```

The interpreter is the same one every other port runs, so the language surface
is the same — see [`dotnet-support.md`](dotnet-support.md). The heap is 256 KB,
which is the most generous of any bare-metal port here.

What the firmware exposes to C# today is GPIO (`RustNet.Hal.Gpio::SetMode`,
`Write`, `Read`, `Toggle`), the console and the clock. Flashing apps over the
wire, provisioning and a filesystem all need storage this port does not have
yet; the 32 MB QSPI flash on the module is the obvious home for it.

# What can go wrong

**Everything times out and the LED shows red once.** The clock tree did not come
up on the crystal. The red/green stage codes are in `src/main.rs`; red means
`HSERDY` never asserted.

**The board is flashed and behaves oddly, but a power cycle fixes it.**
`dfu-util --leave` makes the ROM bootloader *jump* to the application rather
than resetting the chip, so peripherals arrive still configured for the
bootloader's own session. The firmware handles the two cases this bit us on —
it returns PH0/PH1 to analog before starting the oscillator, and resets the OTG
core through `RCC_AHB2RSTR` rather than merely clocking it — but a power cycle
remains the cleanest way to reproduce a real boot.

**Nothing on the serial console at all.** Check GND first. If the adapter is a
Prolific PL2303, check that it opens: a counterfeit chip enumerates, reports no
problem in Device Manager, and fails every open with `ERROR_NO_SUCH_DEVICE`. A
CP210x or CH340 avoids the question.

# The bug that cost this port a day

Every wait returned instantly, USB never enumerated, timed blinks were
invisible, and the serial console produced not one byte. Four symptoms, one
cause: **the Cortex-M7's DWT ships locked.**

`DwtDelay::start` enabled `TRCENA` and `CYCCNTENA`, which is all a Cortex-M4
needs and had worked on the F4 boards for a year. The M7's DWT is CoreSight and
requires the key `0xC5ACCE55` in `DWT_LAR` first; without it the write to
`DWT_CTRL` is discarded and the cycle counter never leaves zero. Nothing
reports an error — the delay source simply answers that no time has passed.

The clue was that the console and the timed blinks went quiet *together*. One
dead peripheral cannot do that; one dead clock can. It is the same shape as the
K210's `mstatus.FS` trap: a core facility the previous architecture left
enabled and this one does not.
